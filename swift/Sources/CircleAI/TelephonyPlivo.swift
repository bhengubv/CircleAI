// TelephonyPlivo.swift
//
// Port of CircleAI.Telephony.Plivo — the Plivo ITelephonyCarrier binding.
//   • PlivoOptions.cs        — PlivoOptions.
//   • PlivoCarrier.cs        — PlivoCarrier (v1 REST adapter, Basic auth).
//   • PlivoCallSession.cs    — PlivoCallSession, PlivoPendingMediaStream.
//   • ServiceCollectionExtensions.cs — PlivoCarrierFactory (DI helper analogue).
//
// Plivo speaks Basic auth (AuthId + AuthToken), the /v1/Account/{AuthId}/
// namespace, and the AnswerUrl-driven Audio Streaming flow. As with the other
// carriers, the raw HTTP is the injected `ITelephonyHttpTransport`; the form
// bodies, query composition (`UriBuilder`), and response parsing (`objects`
// envelope / `request_uuid`) are ported verbatim.

import Foundation

// MARK: - PlivoOptions

/// Plivo account credentials + endpoint. Port of
/// `CircleAI.Telephony.Plivo.PlivoOptions`.
public struct PlivoOptions: Sendable, Equatable {
    /// Plivo v1 API base address. Default `https://api.plivo.com`.
    public var baseAddress: URL
    /// Plivo Auth ID (starts with "MA..." or similar).
    public var authId: String?
    /// Plivo Auth Token.
    public var authToken: String?
    /// (Required for dial) HTTPS URL the host serves that, given a
    /// `?stream=<url-encoded wss://...>` query parameter, returns Plivo XML
    /// containing the matching `<Stream/>` verb.
    public var answerUrlBase: URL?

    public init(
        baseAddress: URL = URL(string: "https://api.plivo.com")!,
        authId: String? = nil,
        authToken: String? = nil,
        answerUrlBase: URL? = nil
    ) {
        self.baseAddress = baseAddress
        self.authId = authId
        self.authToken = authToken
        self.answerUrlBase = answerUrlBase
    }
}

// MARK: - PlivoCarrier

/// `ITelephonyCarrier` backed by Plivo's v1 REST API. Fail-soft when
/// credentials missing. Port of `CircleAI.Telephony.Plivo.PlivoCarrier`.
public final class PlivoCarrier: ITelephonyCarrier, @unchecked Sendable {
    private let http: ITelephonyHttpTransport
    private let options: PlivoOptions

    public init(http: ITelephonyHttpTransport, options: PlivoOptions) {
        self.http = http
        self.options = options

        if http.baseAddress == nil {
            http.baseAddress = options.baseAddress
        }
        if isConfigured {
            let raw = "\(options.authId ?? ""):\(options.authToken ?? "")"
            let creds = Data(raw.utf8).base64EncodedString()
            var headers = http.defaultHeaders
            headers["Authorization"] = "Basic \(creds)"
            http.defaultHeaders = headers
        }
    }

    public var carrierId: String { "plivo" }

    public var isConfigured: Bool {
        !(options.authId ?? "").isBlank && !(options.authToken ?? "").isBlank
    }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        try ensureConfigured()
        let authId = options.authId ?? ""

        // GET PhoneNumber/?country_iso={cc}&limit=1[&pattern={area}]
        var path = "/v1/Account/\(authId)/PhoneNumber/?country_iso=\(countryCode)&limit=1"
        if let areaCode, !areaCode.isBlank {
            path += "&pattern=\(TelephonyUri.escapeDataString(areaCode))"
        }

        let searchResp = try await http.send(TelephonyHttpRequest(method: .get, path: path))
        try searchResp.ensureSuccess()
        let searchDoc = try TelephonyJson.parse(searchResp.body)

        let objects = searchDoc["objects"] as? [Any] ?? []
        guard let first = objects.first as? [String: Any] else {
            throw TelephonyError.invalidOperation(
                "Plivo has no available numbers in country='\(countryCode)', areaCode='\(areaCode ?? "")'.")
        }

        let phoneNumber = first["number"] as? String ?? ""

        // POST PhoneNumber/{number}/ — buy it.
        let buyPath = "/v1/Account/\(authId)/PhoneNumber/\(phoneNumber)/"
        let buyForm = FormUrlEncoded([("app_id", "")])
        let buyResp = try await http.send(TelephonyHttpRequest(
            method: .post, path: buyPath, body: buyForm.data, contentType: .form))
        try buyResp.ensureSuccess()

        return ProvisionedNumber(
            phoneNumber: phoneNumber,
            carrierId: carrierId,
            provisionedAtUtc: Date(),
            monthlyRecurringCost: TelephonyJson.parseDecimal(first, "monthly_rental_rate") ?? 0)
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        try ensureConfigured()
        let authId = options.authId ?? ""

        // POST Number/{number}/ (Plivo uses POST for updates on Number/).
        let path = "/v1/Account/\(authId)/Number/\(phoneNumber)/"
        let form = FormUrlEncoded([
            ("answer_url", inboundWebhook.absoluteString),
            ("answer_method", "POST"),
        ])
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: path, body: form.data, contentType: .form))
        try resp.ensureSuccess()
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options dialOptions: OutboundDialOptions?
    ) async throws -> ICallSession {
        try ensureConfigured()
        guard let answerUrlBase = options.answerUrlBase else {
            throw TelephonyError.invalidOperation(
                "Plivo DialAsync requires PlivoOptions.AnswerUrlBase. The host must serve XML containing a <Stream/> verb pointing to the streamUrl.")
        }
        let authId = options.authId ?? ""
        let opts = dialOptions ?? OutboundDialOptions()

        // Compose the answer URL with the stream wss:// embedded as a query
        // param, mirroring the C# UriBuilder logic:
        //   existing = query.TrimStart('?'); sep = existing.empty ? "" : "&";
        //   query = existing + sep + "stream=" + Uri.EscapeDataString(streamUrl).
        let answerUrl = Self.composeAnswerUrl(answerUrlBase, streamUrl: streamUrl)

        var pairs: [(String, String)] = [
            ("from", opts.callerIdOverride ?? fromNumber),
            ("to", toNumber),
            ("answer_url", answerUrl),
            ("answer_method", "POST"),
            ("ring_timeout", String(opts.ringTimeoutSeconds)),
        ]
        if opts.detectAnsweringMachine {
            pairs.append(("machine_detection", "true"))
        }

        let path = "/v1/Account/\(authId)/Call/"
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: path, body: FormUrlEncoded(pairs).data, contentType: .form))
        try resp.ensureSuccess()
        let doc = try TelephonyJson.parse(resp.body)

        let requestUuid = doc["request_uuid"] as? String ?? ""

        let pending = PlivoPendingMediaStream(callInfo: CallInfo(
            callId: requestUuid,
            direction: .outbound,
            from: fromNumber,
            to: toNumber,
            carrierId: carrierId,
            mediaFormat: .mulaw8000,
            startedAtUtc: Date()))
        return PlivoCallSession(media: pending, carrier: self)
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        if !isConfigured { return [] }
        let authId = options.authId ?? ""

        let path = "/v1/Account/\(authId)/Number/?limit=100"
        let resp = try await http.send(TelephonyHttpRequest(method: .get, path: path))
        if !resp.isSuccessStatusCode {
            return []
        }

        let doc = try TelephonyJson.parse(resp.body)
        var list: [ProvisionedNumber] = []
        if let arr = doc["objects"] as? [Any] {
            for item in arr {
                guard let obj = item as? [String: Any] else { continue }
                let pn = obj["number"] as? String ?? ""
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
    /// internal `EndCallAsync` (fail-soft, DELETE).
    func endCall(callUuid: String) async throws {
        if !isConfigured { return }
        let authId = options.authId ?? ""
        let resp = try await http.send(TelephonyHttpRequest(
            method: .delete, path: "/v1/Account/\(authId)/Call/\(callUuid)/"))
        _ = resp.isSuccessStatusCode
    }

    /// Transfer an in-progress call by replaying the answer XML. Port of the
    /// internal `TransferCallAsync`.
    func transferCall(callUuid: String, targetNumber: String) async throws {
        try ensureConfigured()
        let authId = options.authId ?? ""
        let xml = "<Response><Dial><Number>\(targetNumber)</Number></Dial></Response>"
        let form = FormUrlEncoded([
            ("aleg_url", "data:application/xml,\(TelephonyUri.escapeDataString(xml))"),
            ("aleg_method", "POST"),
        ])
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: "/v1/Account/\(authId)/Call/\(callUuid)/", body: form.data, contentType: .form))
        _ = resp.isSuccessStatusCode
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw TelephonyError.invalidOperation(
                "Plivo carrier is not configured. Set PlivoOptions.AuthId and AuthToken before calling REST operations.")
        }
    }

    /// Reproduces the C# `UriBuilder`-based answer-URL composition.
    static func composeAnswerUrl(_ base: URL, streamUrl: URL) -> String {
        let full = base.absoluteString
        // Split into "before ?" and "existing query" (query excludes any
        // fragment; the carriers' answer URLs carry no fragment).
        let existingQuery: String
        let prefix: String
        if let qIndex = full.firstIndex(of: "?") {
            prefix = String(full[full.startIndex..<qIndex])
            existingQuery = String(full[full.index(after: qIndex)...])
        } else {
            prefix = full
            existingQuery = ""
        }
        let separator = existingQuery.isEmpty ? "" : "&"
        let newQuery = existingQuery + separator + "stream=" + TelephonyUri.escapeDataString(streamUrl.absoluteString)
        return prefix + "?" + newQuery
    }
}

// MARK: - PlivoCallSession

/// `ICallSession` wrapping a Plivo media stream. Port of
/// `CircleAI.Telephony.Plivo.PlivoCallSession`.
public final class PlivoCallSession: ICallSession, @unchecked Sendable {
    private let media: IMediaStream
    private let carrier: PlivoCarrier
    private let briefingTts: BriefingSynthesiser?
    private let bridgeStreamUrl: URL?

    private let statusBroker = StatusChangeBroker()
    private let gate = NSLock()
    private var _status: CallStatus = .ringing
    private var mediaStatusTask: Task<Void, Never>?

    public convenience init(media: IMediaStream, carrier: PlivoCarrier) {
        self.init(media: media, carrier: carrier, briefingTts: nil, bridgeStreamUrl: nil)
    }

    /// Construct with warm-transfer support — see TwilioCallSession for semantics.
    public init(
        media: IMediaStream,
        carrier: PlivoCarrier,
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

        try await carrier.transferCall(callUuid: info.callId, targetNumber: targetNumber)
        setStatus(.transferred)
    }

    public func hangUp() async throws {
        setStatus(.endedByAgent)
        do { try await media.end() } catch { /* media may already be closed */ }
        try await carrier.endCall(callUuid: info.callId)
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

// MARK: - PlivoPendingMediaStream

/// Pending stream returned while the host's WebSocket attaches. Port of the
/// internal `CircleAI.Telephony.Plivo.PlivoPendingMediaStream`.
public final class PlivoPendingMediaStream: IMediaStream, @unchecked Sendable {
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

/// Composition helper mirroring `AddPlivoCarrier`.
public enum PlivoCarrierFactory {
    public static func make(http: ITelephonyHttpTransport, options: PlivoOptions) -> ITelephonyCarrier {
        PlivoCarrier(http: http, options: options)
    }
}
