// TelephonyOrchestration.swift
//
// Port of:
//   • CircleAI.Telephony.WarmTransferOrchestrator — WarmTransferRequest,
//     WarmTransferResult, IWarmTransferOrchestrator, BriefingSynthesiser,
//     DefaultWarmTransferOrchestrator.
//   • CircleAI.Telephony.NullImplementations — NullTelephonyCarrier,
//     NullInboundCallDispatcher.
//   • CircleAI.Telephony.ServiceCollectionExtensions — CarrierFallback (the
//     multi-carrier failover ITelephonyCarrier).

import Foundation

// =====================================================================
// WarmTransferOrchestrator.cs
// =====================================================================

/// One warm-transfer request. Port of the C# record
/// `CircleAI.Telephony.WarmTransferRequest`.
public struct WarmTransferRequest: @unchecked Sendable {
    /// The active call we want to transfer.
    public let sourceSession: ICallSession
    /// E.164 number of the person we're transferring to.
    public let targetNumber: String
    /// What the AI should say to the target before the bridge.
    public let briefingText: String
    /// WSS endpoint the carrier will hand the target leg to.
    public let bridgeStreamUrl: URL

    public init(
        sourceSession: ICallSession,
        targetNumber: String,
        briefingText: String,
        bridgeStreamUrl: URL
    ) {
        self.sourceSession = sourceSession
        self.targetNumber = targetNumber
        self.briefingText = briefingText
        self.bridgeStreamUrl = bridgeStreamUrl
    }
}

/// Outcome of a warm transfer. Port of the C# record
/// `CircleAI.Telephony.WarmTransferResult`.
public struct WarmTransferResult: @unchecked Sendable {
    public let succeeded: Bool
    public let failureReason: String?
    public let bridgeSession: ICallSession?

    public init(succeeded: Bool, failureReason: String?, bridgeSession: ICallSession?) {
        self.succeeded = succeeded
        self.failureReason = failureReason
        self.bridgeSession = bridgeSession
    }
}

/// Park caller, dial target, brief, bridge. Port of
/// `CircleAI.Telephony.IWarmTransferOrchestrator`.
public protocol IWarmTransferOrchestrator: Sendable {
    func execute(_ request: WarmTransferRequest) async -> WarmTransferResult
}

/// Synthesise the briefing text to PCM-16 mono. Port of the C# delegate
/// `CircleAI.Telephony.BriefingSynthesiser`
/// (`ValueTask<ReadOnlyMemory<byte>>(string text, CancellationToken)`).
public typealias BriefingSynthesiser = @Sendable (_ text: String) async throws -> Data

/// Carrier-agnostic warm-transfer driver. Port of
/// `CircleAI.Telephony.DefaultWarmTransferOrchestrator`.
public final class DefaultWarmTransferOrchestrator: IWarmTransferOrchestrator, @unchecked Sendable {
    private let carrier: ITelephonyCarrier
    private let briefingTts: BriefingSynthesiser

    public init(carrier: ITelephonyCarrier, briefingTts: @escaping BriefingSynthesiser) {
        self.carrier = carrier
        self.briefingTts = briefingTts
    }

    public func execute(_ request: WarmTransferRequest) async -> WarmTransferResult {
        if request.targetNumber.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return WarmTransferResult(succeeded: false, failureReason: "TargetNumber is required", bridgeSession: nil)
        }

        // 1) Dial target on a fresh leg.
        let bridgeLeg: ICallSession
        do {
            bridgeLeg = try await carrier.dial(
                fromNumber: request.sourceSession.info.to,
                toNumber: request.targetNumber,
                streamUrl: request.bridgeStreamUrl,
                options: nil)
        } catch {
            return WarmTransferResult(
                succeeded: false,
                failureReason: "Failed to dial target: \(Self.message(error))",
                bridgeSession: nil)
        }

        // 2) Speak briefing to target.
        do {
            let briefingAudio = try await briefingTts(request.briefingText)
            if !briefingAudio.isEmpty {
                try await bridgeLeg.sendAudio(
                    AudioFrame(pcm: briefingAudio, format: .pcm24000, offset: 0))
            }
        } catch {
            await bridgeLeg.hangUpSafely()
            return WarmTransferResult(
                succeeded: false,
                failureReason: "Failed to brief target: \(Self.message(error))",
                bridgeSession: nil)
        }

        // 3) Hand caller off to target — this is the bridge moment.
        do {
            try await request.sourceSession.transfer(
                targetNumber: request.targetNumber, mode: .cold, briefing: nil)
        } catch {
            await bridgeLeg.hangUpSafely()
            return WarmTransferResult(
                succeeded: false,
                failureReason: "Failed to bridge caller: \(Self.message(error))",
                bridgeSession: nil)
        }

        // 4) AI leg ends; caller and target stay connected.
        await bridgeLeg.hangUpSafely()
        return WarmTransferResult(succeeded: true, failureReason: nil, bridgeSession: bridgeLeg)
    }

    private static func message(_ error: Error) -> String {
        (error as? TelephonyError)?.description ?? "\(error)"
    }
}

private extension ICallSession {
    /// Best-effort hang up (the C# orchestrator awaits HangUpAsync without a
    /// try/catch in the failure branches, but a failing hang-up must not mask
    /// the original failure reason).
    func hangUpSafely() async {
        do { try await hangUp() } catch { /* ignore */ }
    }
}

// =====================================================================
// NullImplementations.cs
// =====================================================================

/// Null carrier — fail-soft on every operation. Port of
/// `CircleAI.Telephony.NullTelephonyCarrier`.
public final class NullTelephonyCarrier: ITelephonyCarrier, @unchecked Sendable {
    public static let instance = NullTelephonyCarrier()

    public init() {}

    public var carrierId: String { "null" }
    public var isConfigured: Bool { false }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        throw TelephonyError.invalidOperation(
            "Null carrier cannot provision phone numbers. Register a real ITelephonyCarrier (CircleAI.Telephony.Twilio / .Telnyx / .Plivo).")
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        // ValueTask.CompletedTask — no-op.
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options: OutboundDialOptions?
    ) async throws -> ICallSession {
        throw TelephonyError.invalidOperation(
            "Null carrier cannot place outbound calls. Register a real ITelephonyCarrier.")
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        []
    }
}

/// Null inbound dispatcher — never fires. Port of
/// `CircleAI.Telephony.NullInboundCallDispatcher`.
public final class NullInboundCallDispatcher: IInboundCallDispatcher, @unchecked Sendable {
    public static let instance = NullInboundCallDispatcher()

    public init() {}

    public var carrierId: String { "null" }

    public func subscribe(_ handler: @escaping @Sendable (ICallSession) async -> Void) -> ISubscription {
        NoopCallSubscription.instance
    }
}

/// A subscription that does nothing on dispose. Port of the C#
/// `NullInboundCallDispatcher.NoopDisposable`. (Named `NoopCallSubscription`
/// to avoid confusion with the state-sync vertical's private `NoopSubscription`.)
public final class NoopCallSubscription: ISubscription, @unchecked Sendable {
    public static let instance = NoopCallSubscription()
    public init() {}
    public func dispose() {}
}

// =====================================================================
// ServiceCollectionExtensions.cs — CarrierFallback
// =====================================================================

/// Multi-carrier failover — picks the first configured carrier. Port of the
/// internal C# `CarrierFallback` (registered by `AddCarrierFallback`).
public final class CarrierFallback: ITelephonyCarrier, @unchecked Sendable {
    private let carriers: [ITelephonyCarrier]

    public init(_ carriers: [ITelephonyCarrier]) {
        self.carriers = carriers
    }

    public var carrierId: String { "fallback(\(carriers.count))" }
    public var isConfigured: Bool { carriers.contains { $0.isConfigured } }

    private func pick() -> ITelephonyCarrier {
        carriers.first { $0.isConfigured } ?? NullTelephonyCarrier.instance
    }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        try await pick().provisionNumber(countryCode: countryCode, areaCode: areaCode)
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        try await pick().configureInboundWebhook(phoneNumber: phoneNumber, inboundWebhook: inboundWebhook)
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options: OutboundDialOptions?
    ) async throws -> ICallSession {
        try await pick().dial(fromNumber: fromNumber, toNumber: toNumber, streamUrl: streamUrl, options: options)
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        try await pick().listNumbers()
    }
}
