// TelephonyTwilio.swift
//
// Port of CircleAI.Telephony.Twilio — the Twilio ITelephonyCarrier binding.
//   • TwilioOptions.cs       — TwilioOptions.
//   • TwilioCarrier.cs       — TwilioCarrier (REST adapter).
//   • TwilioCallSession.cs   — TwilioCallSession, PendingMediaStream.
//   • ServiceCollectionExtensions.cs — TwilioCarrierFactory (DI helper analogue).
//
// The C# adapter drives Twilio's REST API via HttpClient
// (https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/...), authenticating
// with HTTP Basic (AccountSid + AuthToken). Here the raw HTTP is the injected
// `ITelephonyHttpTransport`; every path, form/JSON body, auth header, and
// response-parsing step is ported verbatim so the wire format is preserved and
// asserted against `FakeHttpTransport` (no real calls).

import Foundation

// MARK: - TwilioOptions

/// Twilio account credentials + endpoint. Port of
/// `CircleAI.Telephony.Twilio.TwilioOptions`.
public struct TwilioOptions: Sendable, Equatable {
    /// Twilio REST API base address. Default `https://api.twilio.com`.
    public var baseAddress: URL
    /// Twilio Account SID (starts with "AC...").
    public var accountSid: String?
    /// Twilio Auth Token.
    public var authToken: String?

    public init(
        baseAddress: URL = URL(string: "https://api.twilio.com")!,
        accountSid: String? = nil,
        authToken: String? = nil
    ) {
        self.baseAddress = baseAddress
        self.accountSid = accountSid
        self.authToken = authToken
    }
}

// MARK: - TwilioCarrier

/// `ITelephonyCarrier` backed by Twilio's REST API. Fail-soft when credentials
/// are missing. Port of `CircleAI.Telephony.Twilio.TwilioCarrier`.
public final class TwilioCarrier: ITelephonyCarrier, @unchecked Sendable {
    private let http: ITelephonyHttpTransport
    private let options: TwilioOptions

    public init(http: ITelephonyHttpTransport, options: TwilioOptions) {
        self.http = http
        self.options = options

        if http.baseAddress == nil {
            http.baseAddress = options.baseAddress
        }
        if isConfigured {
            let raw = "\(options.accountSid ?? ""):\(options.authToken ?? "")"
            let creds = Data(raw.utf8).base64EncodedString()
            var headers = http.defaultHeaders
            headers["Authorization"] = "Basic \(creds)"
            http.defaultHeaders = headers
        }
    }

    public var carrierId: String { "twilio" }

    public var isConfigured: Bool {
        !(options.accountSid ?? "").isBlank && !(options.authToken ?? "").isBlank
    }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        try ensureConfigured()
        let sid = options.accountSid ?? ""

        // GET AvailablePhoneNumbers/{Country}/Local.json[?AreaCode=..&Limit=1]|[?Limit=1]
        var path = "/2010-04-01/Accounts/\(sid)/AvailablePhoneNumbers/\(countryCode)/Local.json"
        if let areaCode, !areaCode.isBlank {
            path += "?AreaCode=\(TelephonyUri.escapeDataString(areaCode))&Limit=1"
        } else {
            path += "?Limit=1"
        }

        let availableResp = try await http.send(TelephonyHttpRequest(method: .get, path: path))
        try availableResp.ensureSuccess()
        let availableDoc = try TelephonyJson.parse(availableResp.body)

        let numbers = availableDoc["available_phone_numbers"] as? [Any] ?? []
        guard let first = numbers.first as? [String: Any] else {
            throw TelephonyError.invalidOperation(
                "Twilio has no available numbers in country='\(countryCode)', areaCode='\(areaCode ?? "")'.")
        }

        let phoneNumber = first["phone_number"] as? String ?? ""

        // Reserve it on the account.
        let reservePath = "/2010-04-01/Accounts/\(sid)/IncomingPhoneNumbers.json"
        let form = FormUrlEncoded([("PhoneNumber", phoneNumber)])
        let reserveResp = try await http.send(TelephonyHttpRequest(
            method: .post, path: reservePath, body: form.data, contentType: .form))
        try reserveResp.ensureSuccess()

        return ProvisionedNumber(
            phoneNumber: phoneNumber,
            carrierId: carrierId,
            provisionedAtUtc: Date(),
            monthlyRecurringCost: TelephonyJson.parseDecimal(first, "price") ?? 0)
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        try ensureConfigured()
        let sid = options.accountSid ?? ""

        // Find the SID of the IncomingPhoneNumber resource for this E.164 number.
        let listPath = "/2010-04-01/Accounts/\(sid)/IncomingPhoneNumbers.json?PhoneNumber=\(TelephonyUri.escapeDataString(phoneNumber))"
        let listResp = try await http.send(TelephonyHttpRequest(method: .get, path: listPath))
        try listResp.ensureSuccess()
        let listDoc = try TelephonyJson.parse(listResp.body)

        let entries = listDoc["incoming_phone_numbers"] as? [Any] ?? []
        guard let numberEntry = entries.first as? [String: Any] else {
            throw TelephonyError.invalidOperation(
                "Phone number '\(phoneNumber)' is not owned on this Twilio account.")
        }

        let numberSid = numberEntry["sid"] as? String ?? ""
        let configPath = "/2010-04-01/Accounts/\(sid)/IncomingPhoneNumbers/\(numberSid).json"

        let form = FormUrlEncoded([
            ("VoiceUrl", inboundWebhook.absoluteString),
            ("VoiceMethod", "POST"),
        ])
        let updateResp = try await http.send(TelephonyHttpRequest(
            method: .post, path: configPath, body: form.data, contentType: .form))
        try updateResp.ensureSuccess()
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options dialOptions: OutboundDialOptions?
    ) async throws -> ICallSession {
        try ensureConfigured()
        let sid = options.accountSid ?? ""
        let opts = dialOptions ?? OutboundDialOptions()

        // Inline TwiML: <Connect><Stream url='wss://...'/></Connect>. The URL is
        // HTML-encoded exactly like `System.Net.WebUtility.HtmlEncode`.
        let twiml = "<Response><Connect><Stream url='\(TelephonyUri.htmlEncode(streamUrl.absoluteString))'/></Connect></Response>"

        var pairs: [(String, String)] = [
            ("From", opts.callerIdOverride ?? fromNumber),
            ("To", toNumber),
            ("Twiml", twiml),
            ("Timeout", String(opts.ringTimeoutSeconds)),
        ]
        if opts.detectAnsweringMachine {
            pairs.append(("MachineDetection", "Enable"))
        }
        let form = FormUrlEncoded(pairs)

        let callsPath = "/2010-04-01/Accounts/\(sid)/Calls.json"
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: callsPath, body: form.data, contentType: .form))
        try resp.ensureSuccess()
        let doc = try TelephonyJson.parse(resp.body)

        let callSid = doc["sid"] as? String ?? ""

        let pending = PendingMediaStream(callInfo: CallInfo(
            callId: callSid,
            direction: .outbound,
            from: fromNumber,
            to: toNumber,
            carrierId: carrierId,
            mediaFormat: .mulaw8000,
            startedAtUtc: Date()))
        return TwilioCallSession(media: pending, carrier: self)
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        if !isConfigured { return [] }
        let sid = options.accountSid ?? ""

        let path = "/2010-04-01/Accounts/\(sid)/IncomingPhoneNumbers.json?PageSize=100"
        let resp = try await http.send(TelephonyHttpRequest(method: .get, path: path))
        if !resp.isSuccessStatusCode {
            return []
        }

        let doc = try TelephonyJson.parse(resp.body)
        var list: [ProvisionedNumber] = []
        if let arr = doc["incoming_phone_numbers"] as? [Any] {
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

    /// Redirect an in-progress call to fresh TwiML. Used by sessions on cold
    /// transfer. Port of the internal `RedirectCallAsync`.
    func redirectCall(callSid: String, twiml: String) async throws {
        try ensureConfigured()
        let sid = options.accountSid ?? ""
        let path = "/2010-04-01/Accounts/\(sid)/Calls/\(callSid).json"
        let form = FormUrlEncoded([("Twiml", twiml)])
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: path, body: form.data, contentType: .form))
        _ = resp.isSuccessStatusCode // C# only logs on failure; no throw.
    }

    /// End a call by Twilio CallSid via the REST API. Used by sessions on
    /// HangUp. Port of the internal `EndCallAsync` (fail-soft).
    func endCall(callSid: String) async throws {
        if !isConfigured { return }
        let sid = options.accountSid ?? ""
        let path = "/2010-04-01/Accounts/\(sid)/Calls/\(callSid).json"
        let form = FormUrlEncoded([("Status", "completed")])
        let resp = try await http.send(TelephonyHttpRequest(
            method: .post, path: path, body: form.data, contentType: .form))
        _ = resp.isSuccessStatusCode
    }

    private func ensureConfigured() throws {
        if !isConfigured {
            throw TelephonyError.invalidOperation(
                "Twilio carrier is not configured. Set TwilioOptions.AccountSid and AuthToken before calling REST operations.")
        }
    }
}

// MARK: - TwilioCallSession

/// `ICallSession` wrapping a Twilio media stream. Port of
/// `CircleAI.Telephony.Twilio.TwilioCallSession`.
public final class TwilioCallSession: ICallSession, @unchecked Sendable {
    private let media: IMediaStream
    private let carrier: TwilioCarrier
    private let briefingTts: BriefingSynthesiser?
    private let bridgeStreamUrl: URL?

    private let statusBroker = StatusChangeBroker()
    private let gate = NSLock()
    private var _status: CallStatus = .ringing
    private var mediaStatusTask: Task<Void, Never>?

    public convenience init(media: IMediaStream, carrier: TwilioCarrier) {
        self.init(media: media, carrier: carrier, briefingTts: nil, bridgeStreamUrl: nil)
    }

    /// Construct with warm-transfer support. When `briefingTts` and
    /// `bridgeStreamUrl` are supplied, `transfer(mode: .warm)` runs the full
    /// dial-brief-bridge flow via `DefaultWarmTransferOrchestrator`.
    public init(
        media: IMediaStream,
        carrier: TwilioCarrier,
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
            // Warm requested but no briefing pipeline configured — fall through
            // to cold transfer (best-effort).
        }

        let transferTwiml = "<Response><Dial>\(TelephonyUri.htmlEncode(targetNumber))</Dial></Response>"
        try await carrier.redirectCall(callSid: info.callId, twiml: transferTwiml)
        setStatus(.transferred)
    }

    public func hangUp() async throws {
        setStatus(.endedByAgent)
        do { try await media.end() } catch { /* media may already be closed */ }
        try await carrier.endCall(callSid: info.callId)
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

// MARK: - PendingMediaStream

/// `IMediaStream` for the moment between "carrier accepted dial" and "host's
/// WebSocket attached." Yields no audio. Calling Send before attach raises a
/// friendly error. Port of the internal
/// `CircleAI.Telephony.Twilio.PendingMediaStream`.
public final class PendingMediaStream: IMediaStream, @unchecked Sendable {
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
        // Yield nothing until the host attaches.
        AsyncStream { $0.finish() }
    }

    public func sendAudio(_ frame: AudioFrame) async throws {
        throw TelephonyError.invalidOperation(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream. Wire CircleAI.Hosting.Telephony.Twilio to complete the connection.")
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

/// Composition helper mirroring `AddTwilioCarrier` — builds a `TwilioCarrier`
/// as the `ITelephonyCarrier`, wiring the injected transport + options.
public enum TwilioCarrierFactory {
    public static func make(http: ITelephonyHttpTransport, options: TwilioOptions) -> ITelephonyCarrier {
        TwilioCarrier(http: http, options: options)
    }
}
