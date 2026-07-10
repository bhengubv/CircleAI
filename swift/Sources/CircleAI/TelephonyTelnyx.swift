// TelephonyTelnyx.swift
//
// Port of CircleAI.Telephony.Telnyx — the Telnyx ITelephonyCarrier binding.
//   • TelnyxOptions.cs       — TelnyxOptions.
//   • TelnyxCarrier.cs       — TelnyxCarrier (v2 REST adapter, Bearer auth).
//   • TelnyxCallSession.cs   — TelnyxCallSession, TelnyxPendingMediaStream.
//   • ServiceCollectionExtensions.cs — TelnyxCarrierFactory (DI helper analogue).
//
// Telnyx speaks Bearer-token auth, the /v2 namespace, and the Call Control
// surface. As with Twilio, the raw HTTP is the injected
// `ITelephonyHttpTransport`; the JSON bodies, query strings, and response
// parsing (`data` envelope) are ported verbatim.

import Foundation

// MARK: - TelnyxOptions

/// Telnyx account credentials + endpoint. Port of
/// `CircleAI.Telephony.Telnyx.TelnyxOptions`.
public struct TelnyxOptions: Sendable, Equatable {
    /// Telnyx v2 API base address. Default `https://api.telnyx.com`.
    public var baseAddress: URL
    /// Telnyx v2 API key (Bearer). Found in the portal under "API Keys".
    public var apiKey: String?
    /// (Optional) Telnyx Call Control Application id used as the Connection for
    /// outbound calls and as the webhook owner for inbound calls. Required to dial.
    public var callControlConnectionId: String?

    public init(
        baseAddress: URL = URL(string: "https://api.telnyx.com")!,
        apiKey: String? = nil,
        callControlConnectionId: String? = nil
    ) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.callControlConnectionId = callControlConnectionId
    }
}

// MARK: - TelnyxCarrier

/// `ITelephonyCarrier` backed by Telnyx's v2 REST API. Fail-soft when
/// credentials are missing. Port of `CircleAI.Telephony.Telnyx.TelnyxCarrier`.
public final class TelnyxCarrier: ITelephonyCarrier, @unchecked Sendable {
    private let http: ITelephonyHttpTransport
    private let options: TelnyxOptions

    public init(http: ITelephonyHttpTransport, options: TelnyxOptions) {
        self.http = http
        self.options = options

        if http.baseAddress == nil {
            http.baseAddress = options.baseAddress
        }
        if isConfigured {
            var headers = http.defaultHeaders
            headers["Authorization"] = "Bearer \(options.apiKey ?? "")"
            http.defaultHeaders = headers
        }
    }

    public var carrierId: String { "telnyx" }

    public var isConfigured: Bool {
        !(options.apiKey ?? "").isBlank
    }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        try ensureConfigured()

        // 1) Search availability.
        var searchPath = "/v2/available_phone_numbers?filter[country_code]=\(countryCode)&filter[limit]=1"
        if let areaCode, !areaCode.isBlank {
            searchPath += "&filter[national_destination_code]=\(TelephonyUri.escapeDataString(areaCode))"
        }

        let searchResp = try await http.send(TelephonyHttpRequest(method: .get, path: searchPath))
        try searchResp.ensureSuccess()
        let searchDoc = try TelephonyJson.parse(searchResp.body)

        let data = searchDoc["data"] as? [Any] ?? []
        guard let first = data.first as? [String: Any] else {
            throw TelephonyError.invalidOperation(
                "Telnyx has no available numbers in country='\(countryCode)', areaCode='\(areaCode ?? "")'.")
        }

        let phoneNumber = first["phone_number"] as? String ?? ""

        // 2) Place a Number Order to purchase it.
        let orderBody = "{\"phone_numbers\":[{\"phone_number\":\"\(phoneNumber)\"}]}"
        let orderResp = try await http.send(TelephonyHttpRequest(
            method: .post, path: "/v2/number_orders", body: Data(orderBody.utf8), contentType: .json))
        try orderResp.ensureSuccess()

        return ProvisionedNumber(
            phoneNumber: phoneNumber,
            carrierId: carrierId,
            provisionedAtUtc: Date(),
            monthlyRecurringCost: TelephonyJson.parseNestedDecimal(first, "cost_information", "monthly_cost") ?? 0)
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        try ensureConfigured()
        guard let connId = options.callControlConnectionId, !connId.isBlank else {
            throw TelephonyError.invalidOperation(
                "Telnyx ConfigureInboundWebhook requires CallControlConnectionId on TelnyxOptions.")
        }

        // Update the Call Control Application's webhook URL.
        let path = "/v2/call_control_applications/\(connId)"
        let body = "{\"webhook_event_url\":\"\(inboundWebhook.absoluteString)\"}"
        let resp = try await http.send(TelephonyHttpRequest(
            method: .patch, path: path, body: Data(body.utf8), contentType: .json))
        try resp.ensureSuccess()

        // Ensure the number is assigned to this connection.
        let assignBody = "{\"connection_id\":\"\(connId)\"}"
        let assignPath = "/v2/phone_numbers/\(TelephonyUri.escapeDataString(phoneNumber))"
        let assignResp = try await http.send(TelephonyHttpRequest(
            method: .patch, path: assignPath, body: Data(assignBody.utf8), contentType: .json))
        _ = assignResp.isSuccessStatusCode // C# only logs on failure.
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options dialOptions: OutboundDialOptions?
    ) async throws -> ICallSession {
        try ensureConfigured()
        guard let connId = options.callControlConnectionId, !connId.isBlank else {
            throw TelephonyError.invalidOperation(
                "Telnyx DialAsync requires CallControlConnectionId on TelnyxOptions.")
        }
        let opts = dialOptions ?? OutboundDialOptions()

        // Build the JSON body in the exact field order the C# StringBuilder uses.
        var body = "{"
        body += "\"connection_id\":\"\(connId)\","
        body += "\"to\":\"\(toNumber)\","
        body += "\"from\":\"\(opts.callerIdOverride ?? fromNumber)\","
        body += "\"stream_url\":\"\(streamUrl.absoluteString)\","
        body += "\"stream_track\":\"both_tracks\","
        body += "\"timeout_secs\":\(opts.ringTimeoutSeconds)"
        if opts.detectAnsweringMachine {
            body += ",\"answering_machine_detection\":\"detect\""
        }
        body += "}"

        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: "/v2/calls", body: Data(body.utf8), contentType: .json))
        try resp.ensureSuccess()
        let doc = try TelephonyJson.parse(resp.body)

        let callControlId = (doc["data"] as? [String: Any])?["call_control_id"] as? String ?? ""

        let pending = TelnyxPendingMediaStream(callInfo: CallInfo(
            callId: callControlId,
            direction: .outbound,
            from: fromNumber,
            to: toNumber,
            carrierId: carrierId,
            mediaFormat: .pcm16000,
            startedAtUtc: Date()))
        return TelnyxCallSession(media: pending, carrier: self)
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        if !isConfigured { return [] }

        let path = "/v2/phone_numbers?page[size]=100"
        let resp = try await http.send(TelephonyHttpRequest(method: .get, path: path))
        if !resp.isSuccessStatusCode {
            return []
        }

        let doc = try TelephonyJson.parse(resp.body)
        var list: [ProvisionedNumber] = []
        if let arr = doc["data"] as? [Any] {
            for item in arr {
                guard let obj = item as? [String: Any] else { continue }
                let pn = obj["phone_number"] as? String ?? ""
                list.append(ProvisionedNumber(
                    phoneNumber: pn,
                    carrierId: carrierId,
                    provisionedAtUtc: Date(),
                    monthlyRecurringCost: 0))
            }
        }
        return list
    }

    /// Hang up an in-progress call. Used by sessions on HangUp. Port of the
    /// internal `EndCallAsync` (fail-soft).
    func endCall(callControlId: String) async throws {
        if !isConfigured { return }
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post,
            path: "/v2/calls/\(callControlId)/actions/hangup",
            body: Data("{}".utf8),
            contentType: .json))
        _ = resp.isSuccessStatusCode
    }

    /// Transfer an in-progress call to a new destination. Port of the internal
    /// `TransferCallAsync`.
    func transferCall(callControlId: String, targetNumber: String) async throws {
        try ensureConfigured()
        let body = "{\"to\":\"\(targetNumber)\"}"
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post,
            path: "/v2/calls/\(callControlId)/actions/transfer",
            body: Data(body.utf8),
            contentType: .json))
        _ = resp.isSuccessStatusCode
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw TelephonyError.invalidOperation(
                "Telnyx carrier is not configured. Set TelnyxOptions.ApiKey before calling REST operations.")
        }
    }
}

// MARK: - TelnyxCallSession

/// `ICallSession` wrapping a Telnyx media stream. Port of
/// `CircleAI.Telephony.Telnyx.TelnyxCallSession`.
public final class TelnyxCallSession: ICallSession, @unchecked Sendable {
    private let media: IMediaStream
    private let carrier: TelnyxCarrier
    private let briefingTts: BriefingSynthesiser?
    private let bridgeStreamUrl: URL?

    private let statusBroker = StatusChangeBroker()
    private let gate = NSLock()
    private var _status: CallStatus = .ringing
    private var mediaStatusTask: Task<Void, Never>?

    public convenience init(media: IMediaStream, carrier: TelnyxCarrier) {
        self.init(media: media, carrier: carrier, briefingTts: nil, bridgeStreamUrl: nil)
    }

    /// Construct with warm-transfer support — see TwilioCallSession for semantics.
    public init(
        media: IMediaStream,
        carrier: TelnyxCarrier,
        briefingTts: BriefingSynthesiser?,
        bridgeStreamUrl: URL?
    ) {
        self.media = media
        self.carrier = carrier
        self.briefingTts = briefingTts
        self.bridgeStreamUrl = bridgeStreamUrl
        let stream = media.statusChanges()
        mediaStatusTask = Task { [weak self] in
            for await status in stream { self?.setStatus(status) }
        }
    }

    public var info: CallInfo { media.callInfo }

    public var status: CallStatus {
        gate.lock(); let local = _status; gate.unlock()
        let mediaStatus = media.currentStatus
        return (mediaStatus == .ringing && local != .ringing) ? local : mediaStatus
    }

    public func receiveAudio() -> AsyncStream<AudioFrame> { media.receiveAudio() }
    public func sendAudio(_ frame: AudioFrame) async throws { try await media.sendAudio(frame) }
    public func receiveDtmf() -> AsyncStream<DtmfEvent> { media.receiveDtmf() }

    public func sendDtmf(_ digits: String) async throws {
        if digits.isEmpty { return }
        if let native = media as? IDtmfSendable {
            try await native.sendDtmf(digits)
            return
        }
        let sampleRate: Int
        switch info.mediaFormat {
        case .pcm16000: sampleRate = 16000
        case .pcm24000: sampleRate = 24000
        case .mulaw8000: sampleRate = 8000
        default: sampleRate = 8000
        }
        try await DtmfToneGenerator.sendThroughSession(self, digits: digits, sampleRateHz: sampleRate)
    }

    public func transfer(targetNumber: String, mode: TransferMode, briefing: String?) async throws {
        if mode == .warm {
            if let briefingTts, let bridgeStreamUrl,
               let briefing, !briefing.isBlank {
                let orchestrator = DefaultWarmTransferOrchestrator(carrier: carrier, briefingTts: briefingTts)
                let result = await orchestrator.execute(
                    WarmTransferRequest(
                        sourceSession: self,
                        targetNumber: targetNumber,
                        briefingText: briefing,
                        bridgeStreamUrl: bridgeStreamUrl))
                if !result.succeeded {
                    throw TelephonyError.invalidOperation("Warm transfer failed: \(result.failureReason ?? "")")
                }
                return
            }
        }

        try await carrier.transferCall(callControlId: info.callId, targetNumber: targetNumber)
        setStatus(.transferred)
    }

    public func hangUp() async throws {
        setStatus(.endedByAgent)
        do { try await media.end() } catch { /* media may already be closed */ }
        try await carrier.endCall(callControlId: info.callId)
    }

    public func statusChanges() -> AsyncStream<CallStatus> { statusBroker.stream() }

    public func dispose() async {
        mediaStatusTask?.cancel()
        await media.dispose()
        statusBroker.complete()
    }

    private func setStatus(_ status: CallStatus) {
        gate.lock()
        if _status == status { gate.unlock(); return }
        _status = status
        gate.unlock()
        statusBroker.publish(status)
    }
}

// MARK: - TelnyxPendingMediaStream

/// Pending media stream returned while the host's WebSocket attaches. Port of
/// the internal `CircleAI.Telephony.Telnyx.TelnyxPendingMediaStream`.
public final class TelnyxPendingMediaStream: IMediaStream, @unchecked Sendable {
    private let statusBroker = StatusChangeBroker()
    private let gate = NSLock()
    private var _currentStatus: CallStatus = .ringing

    public let callInfo: CallInfo

    public init(callInfo: CallInfo) {
        self.callInfo = callInfo
    }

    public var currentStatus: CallStatus {
        gate.lock(); defer { gate.unlock() }
        return _currentStatus
    }

    public func receiveAudio() -> AsyncStream<AudioFrame> {
        AsyncStream { $0.finish() }
    }

    public func sendAudio(_ frame: AudioFrame) async throws {
        throw TelephonyError.invalidOperation(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream.")
    }

    public func receiveDtmf() -> AsyncStream<DtmfEvent> {
        AsyncStream { $0.finish() }
    }

    public func end() async throws {
        gate.lock(); _currentStatus = .endedByAgent; gate.unlock()
        statusBroker.publish(.endedByAgent)
    }

    public func statusChanges() -> AsyncStream<CallStatus> { statusBroker.stream() }

    public func dispose() async {
        statusBroker.complete()
    }
}

// MARK: - DI helper

/// Composition helper mirroring `AddTelnyxCarrier`.
public enum TelnyxCarrierFactory {
    public static func make(http: ITelephonyHttpTransport, options: TelnyxOptions) -> ITelephonyCarrier {
        TelnyxCarrier(http: http, options: options)
    }
}
