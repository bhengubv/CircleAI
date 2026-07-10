// Telephony.swift
//
// Port of the CircleAI.Telephony contract surface (carrier-agnostic core):
//   • Primitives.cs      — CallDirection, CallStatus, CallMediaFormat,
//                          TransferMode, CallInfo, CallSnapshot, AudioFrame,
//                          DtmfEvent, ProvisionedNumber.
//   • Contracts.cs       — ITelephonyCarrier, OutboundDialOptions, ICallSession,
//                          IInboundCallDispatcher.
//   • IMediaStream.cs    — IMediaStream.
//   • IDtmfSendable.cs   — IDtmfSendable.
//   • ToolCalling.cs     — Tool definition/invocation/result, IToolCallRegistry,
//                          DefaultToolCallRegistry.
//   • WarmTransferOrchestrator.cs — WarmTransferRequest/Result,
//                          IWarmTransferOrchestrator, BriefingSynthesiser,
//                          DefaultWarmTransferOrchestrator.
//   • DtmfToneGenerator.cs — DtmfToneGenerator.
//   • NullImplementations.cs — NullTelephonyCarrier, NullInboundCallDispatcher.
//   • TestCallSession.cs — TestCallSession (in-memory harness session).
//   • ServiceCollectionExtensions.cs — CarrierFallback.
//   • FakeTelephonyCarrier — deterministic in-memory carrier (Swift addition
//                          standing in for the real HTTP carriers in tests /
//                          dry-runs; no network).
//
// NAMING: Swift flattens all C# namespaces into the single `CircleAI` module.
// The tool-calling DTOs (`ToolDefinition` / `ToolInvocation` / `ToolResult`)
// already exist in Tools.swift with a DIFFERENT shape (LLM function-call
// bridge). To avoid a hard collision the telephony tool DTOs are prefixed
// `Telephony…` here; the registry keeps its distinct names (`IToolCallRegistry`,
// `DefaultToolCallRegistry`). Everything else in the telephony surface has a
// unique name and is ported verbatim.
//
// CONCURRENCY: `IAsyncDisposable` maps to `func dispose() async` (established
// convention). C# events (`event EventHandler<CallStatus>? StatusChanged`) map
// to an `AsyncStream` fan-out subscription (`statusChanges()`), with the
// snapshot-release-finish lock discipline used across this codebase.

import Foundation

// =====================================================================
// Primitives.cs
// =====================================================================

/// Call direction. Port of `CircleAI.Telephony.CallDirection`.
///
/// C# ordinals: Inbound = 0, Outbound = 1.
public enum CallDirection: Int, Sendable, Codable, CaseIterable {
    case inbound = 0
    case outbound = 1
}

/// Call lifecycle states. Port of `CircleAI.Telephony.CallStatus`.
///
/// C# ordinals in declaration order: Ringing = 0, Active = 1, EndedByCaller = 2,
/// EndedByCallee = 3, EndedByAgent = 4, Voicemail = 5, Failed = 6,
/// Transferred = 7.
public enum CallStatus: Int, Sendable, Codable, CaseIterable {
    /// Carrier accepted the dial but the other end has not picked up yet.
    case ringing = 0
    /// Both sides connected; media flowing.
    case active = 1
    /// Caller hung up.
    case endedByCaller = 2
    /// Callee hung up.
    case endedByCallee = 3
    /// AI agent (us) ended the call.
    case endedByAgent = 4
    /// Carrier-detected voicemail / answering machine on outbound dial.
    case voicemail = 5
    /// Call did not connect (busy, no answer, network).
    case failed = 6
    /// Call transferred to a human or a different agent.
    case transferred = 7
}

/// Audio wire formats supported across carriers. Port of
/// `CircleAI.Telephony.CallMediaFormat`.
///
/// C# ordinals: Mulaw8000 = 0, Alaw8000 = 1, Pcm16000 = 2, Pcm24000 = 3.
public enum CallMediaFormat: Int, Sendable, Codable, CaseIterable {
    /// µ-law 8 kHz mono — Twilio default, Plivo default, fallback Telnyx.
    case mulaw8000 = 0
    /// A-law 8 kHz mono — some European carriers.
    case alaw8000 = 1
    /// Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path.
    case pcm16000 = 2
    /// Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime.
    case pcm24000 = 3
}

/// Transfer mode the AI requests from the carrier. Port of
/// `CircleAI.Telephony.TransferMode`.
///
/// C# ordinals: Cold = 0, Warm = 1.
public enum TransferMode: Int, Sendable, Codable, CaseIterable {
    /// Drop the caller into the new line and hang up — fast, no context handover.
    case cold = 0
    /// Park caller, dial human, brief human verbally, then bridge both — context preserved.
    case warm = 1
}

/// Information about one call. Captured once at call start, immutable.
/// Port of the C# record `CircleAI.Telephony.CallInfo`.
public struct CallInfo: Sendable, Equatable, Codable {
    /// Carrier-supplied unique id (Twilio CallSid, Telnyx call_control_id, etc.).
    public let callId: String
    /// Direction — who initiated.
    public let direction: CallDirection
    /// Caller's phone number in E.164 format (e.g. +27821234567).
    public let from: String
    /// Called party's phone number in E.164 format.
    public let to: String
    /// Carrier id (e.g. "twilio", "telnyx", "plivo").
    public let carrierId: String
    /// Audio wire format the carrier is streaming.
    public let mediaFormat: CallMediaFormat
    /// When the call started.
    public let startedAtUtc: Date

    public init(
        callId: String,
        direction: CallDirection,
        from: String,
        to: String,
        carrierId: String,
        mediaFormat: CallMediaFormat,
        startedAtUtc: Date
    ) {
        self.callId = callId
        self.direction = direction
        self.from = from
        self.to = to
        self.carrierId = carrierId
        self.mediaFormat = mediaFormat
        self.startedAtUtc = startedAtUtc
    }
}

/// A snapshot of a call's current state. Returned by lifecycle queries.
/// Port of the C# record `CircleAI.Telephony.CallSnapshot`.
public struct CallSnapshot: Sendable, Equatable, Codable {
    /// Carrier-captured call metadata.
    public let info: CallInfo
    /// Current lifecycle state.
    public let status: CallStatus
    /// How long since the call connected.
    public let duration: TimeInterval
    /// Per-second cost so far (carrier minutes + any LLM/STT/TTS attached).
    public let costSoFar: Decimal
    /// If `.transferred`, the E.164 number we transferred to.
    public let transferTarget: String?

    public init(
        info: CallInfo,
        status: CallStatus,
        duration: TimeInterval,
        costSoFar: Decimal,
        transferTarget: String? = nil
    ) {
        self.info = info
        self.status = status
        self.duration = duration
        self.costSoFar = costSoFar
        self.transferTarget = transferTarget
    }
}

/// Audio chunk flowing from caller → AI or AI → caller. Port of the C# record
/// `CircleAI.Telephony.AudioFrame`. `ReadOnlyMemory<byte>` maps to `Data`;
/// `TimeSpan Offset` maps to `TimeInterval` (seconds).
public struct AudioFrame: Sendable, Equatable {
    /// Raw PCM (or encoded) bytes for this frame.
    public let pcm: Data
    /// Wire format of `pcm`.
    public let format: CallMediaFormat
    /// Offset from call start.
    public let offset: TimeInterval

    public init(pcm: Data, format: CallMediaFormat, offset: TimeInterval) {
        self.pcm = pcm
        self.format = format
        self.offset = offset
    }
}

/// DTMF tone from the caller. Port of the C# record
/// `CircleAI.Telephony.DtmfEvent`.
public struct DtmfEvent: Sendable, Equatable {
    /// The digit (0-9, *, #).
    public let digit: Character
    /// How long the caller held it.
    public let duration: TimeInterval
    /// When (relative to call start).
    public let offset: TimeInterval

    public init(digit: Character, duration: TimeInterval, offset: TimeInterval) {
        self.digit = digit
        self.duration = duration
        self.offset = offset
    }
}

/// Result of a number-provisioning request. Port of the C# record
/// `CircleAI.Telephony.ProvisionedNumber`.
public struct ProvisionedNumber: Sendable, Equatable, Codable {
    public let phoneNumber: String
    public let carrierId: String
    public let provisionedAtUtc: Date
    public let monthlyRecurringCost: Decimal

    public init(
        phoneNumber: String,
        carrierId: String,
        provisionedAtUtc: Date,
        monthlyRecurringCost: Decimal
    ) {
        self.phoneNumber = phoneNumber
        self.carrierId = carrierId
        self.provisionedAtUtc = provisionedAtUtc
        self.monthlyRecurringCost = monthlyRecurringCost
    }
}

// =====================================================================
// Contracts.cs — OutboundDialOptions
// =====================================================================

/// Optional knobs for an outbound dial. Port of the C# record
/// `CircleAI.Telephony.OutboundDialOptions`.
public struct OutboundDialOptions: Sendable, Equatable {
    /// If true, detect voicemail and surface `CallStatus.voicemail`.
    public var detectAnsweringMachine: Bool
    /// How long to ring before treating it as no-answer. Default 30 s.
    public var ringTimeoutSeconds: Int
    /// Optional caller-id override (must be a number you own).
    public var callerIdOverride: String?
    /// Optional list of E.164 numbers to also dial if the primary doesn't answer (round-robin).
    public var followMeNumbers: [String]?

    public init(
        detectAnsweringMachine: Bool = false,
        ringTimeoutSeconds: Int = 30,
        callerIdOverride: String? = nil,
        followMeNumbers: [String]? = nil
    ) {
        self.detectAnsweringMachine = detectAnsweringMachine
        self.ringTimeoutSeconds = ringTimeoutSeconds
        self.callerIdOverride = callerIdOverride
        self.followMeNumbers = followMeNumbers
    }
}

// =====================================================================
// Contracts.cs — ITelephonyCarrier
// =====================================================================

/// Carrier integration — the place where CircleAI talks to a phone-network
/// operator (Twilio, Telnyx, Plivo, or a SIP gateway). Port of
/// `CircleAI.Telephony.ITelephonyCarrier`.
///
/// Inbound: carrier delivers a call to us → carrier emits an `ICallSession`
/// via the host's webhook plumbing. Outbound: caller asks us to dial → we call
/// `dial(...)`.
public protocol ITelephonyCarrier: Sendable {
    /// Stable carrier id — "twilio" / "telnyx" / "plivo" / "null".
    var carrierId: String { get }

    /// True when the carrier has the credentials + base addresses it needs.
    var isConfigured: Bool { get }

    /// Buy a new phone number from this carrier for the given country code
    /// (ISO 3166-1 alpha-2, e.g. "ZA"). Caller chooses one of the offered area
    /// codes via `areaCode`; pass nil for "any".
    func provisionNumber(
        countryCode: String,
        areaCode: String?
    ) async throws -> ProvisionedNumber

    /// Configure a number we already own to route inbound calls to our
    /// host-provided WebSocket endpoint.
    func configureInboundWebhook(
        phoneNumber: String,
        inboundWebhook: URL
    ) async throws

    /// Place an outbound call. `streamUrl` is where the carrier should stream
    /// the live media (WebSocket URL on our host). Returns a session the caller
    /// can attach an agent to.
    func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options: OutboundDialOptions?
    ) async throws -> ICallSession

    /// List the numbers we own on this carrier.
    func listNumbers() async throws -> [ProvisionedNumber]
}

public extension ITelephonyCarrier {
    /// Overload mirroring the C# default `areaCode = null`.
    func provisionNumber(countryCode: String) async throws -> ProvisionedNumber {
        try await provisionNumber(countryCode: countryCode, areaCode: nil)
    }

    /// Overload mirroring the C# default `options = null`.
    func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL
    ) async throws -> ICallSession {
        try await dial(fromNumber: fromNumber, toNumber: toNumber, streamUrl: streamUrl, options: nil)
    }
}

// =====================================================================
// Contracts.cs — ICallSession
// =====================================================================

/// Live call session. The agent talks to this — it doesn't know or care which
/// carrier is on the other side. Audio in / audio out / hang up / transfer /
/// DTMF. Port of `CircleAI.Telephony.ICallSession` (`IAsyncDisposable` →
/// `dispose() async`; C# `event StatusChanged` → `statusChanges()` AsyncStream).
public protocol ICallSession: AnyObject, Sendable {
    /// Stable carrier-supplied info captured at call start.
    var info: CallInfo { get }

    /// Current lifecycle status (Active / EndedByCaller / Transferred / ...).
    var status: CallStatus { get }

    /// Audio frames arriving from the caller.
    func receiveAudio() -> AsyncStream<AudioFrame>

    /// Send an audio frame to the caller.
    func sendAudio(_ frame: AudioFrame) async throws

    /// DTMF tones the caller is pressing.
    func receiveDtmf() -> AsyncStream<DtmfEvent>

    /// Send DTMF tones from the AI side (for navigating other people's menus).
    func sendDtmf(_ digits: String) async throws

    /// Transfer the call to `targetNumber`. Cold = drop and forget. Warm =
    /// park the caller, dial the human, brief them, bridge both.
    func transfer(
        targetNumber: String,
        mode: TransferMode,
        briefing: String?
    ) async throws

    /// End the call from our side.
    func hangUp() async throws

    /// Subscribe to lifecycle status changes.
    func statusChanges() -> AsyncStream<CallStatus>

    /// Releases the session. Mirrors C# `IAsyncDisposable.DisposeAsync`.
    func dispose() async
}

public extension ICallSession {
    /// Overload mirroring the C# default `briefing = null`.
    func transfer(targetNumber: String, mode: TransferMode) async throws {
        try await transfer(targetNumber: targetNumber, mode: mode, briefing: nil)
    }
}

// =====================================================================
// Contracts.cs — IInboundCallDispatcher
// =====================================================================

/// A cancellation handle. Maps C# `IDisposable` (returned by `Subscribe`) to a
/// Swift value the caller retains to keep the subscription alive and calls
/// `dispose()` on to cancel.
public protocol ISubscription: AnyObject, Sendable {
    func dispose()
}

/// Inbound webhook dispatcher — the carrier-provided HTTP handler (host wires
/// this into ASP.NET routing) calls into the dispatcher to materialise an
/// `ICallSession` the agent can attach to. Port of
/// `CircleAI.Telephony.IInboundCallDispatcher`.
public protocol IInboundCallDispatcher: Sendable {
    /// Stable id of the carrier feeding inbound calls into this dispatcher.
    var carrierId: String { get }

    /// Subscribe to inbound call sessions. Each new call yields a session the
    /// consumer attaches their agent to. Retain the returned `ISubscription`;
    /// call `dispose()` to stop receiving.
    func subscribe(_ handler: @escaping @Sendable (ICallSession) async -> Void) -> ISubscription
}

// =====================================================================
// IMediaStream.cs
// =====================================================================

/// A live media channel for one call. The carrier host's WebSocket handler
/// implements this; the carrier session consumes it. Port of
/// `CircleAI.Telephony.IMediaStream` (`IAsyncDisposable` → `dispose() async`;
/// C# `event StatusChanged` → `statusChanges()` AsyncStream).
public protocol IMediaStream: AnyObject, Sendable {
    /// The carrier call id + metadata captured at connect.
    var callInfo: CallInfo { get }

    /// Inbound audio frames from the caller.
    func receiveAudio() -> AsyncStream<AudioFrame>

    /// Outbound audio frames to the caller.
    func sendAudio(_ frame: AudioFrame) async throws

    /// Inbound DTMF events.
    func receiveDtmf() -> AsyncStream<DtmfEvent>

    /// Mark the call ended from our side. Closes the WebSocket.
    func end() async throws

    /// Fires when the carrier reports the call status changed.
    func statusChanges() -> AsyncStream<CallStatus>

    /// The current lifecycle state.
    var currentStatus: CallStatus { get }

    /// Releases the media stream. Mirrors C# `IAsyncDisposable.DisposeAsync`.
    func dispose() async
}

// =====================================================================
// IDtmfSendable.cs
// =====================================================================

/// Optional sister interface a host can layer on its `IMediaStream`
/// implementation to support carrier-native out-of-band DTMF. When the media
/// stream doesn't implement this, the session falls back to in-band tones via
/// `DtmfToneGenerator`. Port of `CircleAI.Telephony.IDtmfSendable`.
public protocol IDtmfSendable: AnyObject, Sendable {
    func sendDtmf(_ digits: String) async throws
}

// =====================================================================
// Errors
// =====================================================================

/// Error surface for the telephony vertical. Mirrors the C#
/// `InvalidOperationException` / `ArgumentException` messages that callers /
/// tests assert against.
public enum TelephonyError: Error, Equatable, CustomStringConvertible {
    /// A carrier operation was attempted while `isConfigured == false`, or a
    /// required option was missing.
    case invalidOperation(String)
    /// An argument was null/empty/out-of-range.
    case argument(String)

    public var description: String {
        switch self {
        case .invalidOperation(let m): return m
        case .argument(let m): return m
        }
    }
}
