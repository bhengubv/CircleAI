// TelephonyFakeCarrier.swift
//
// Deterministic in-memory ITelephonyCarrier + IMediaStream + inbound dispatcher.
//
// The three real carriers (Twilio / Telnyx / Plivo) talk to remote HTTP APIs.
// Per the port brief — "Port the carrier abstraction + a deterministic
// in-memory fake carrier; the real HTTP carrier is an injected dependency (no
// real calls/network)" — this file supplies:
//   • InMemoryMediaStream       — a full IMediaStream with no socket, driven by
//                                 test/host code (inject audio+DTMF, capture
//                                 outbound, push status).
//   • InMemoryCallSession       — an ICallSession over an IMediaStream that
//                                 records transfer/hangup instead of hitting a
//                                 carrier REST API (deterministic, offline).
//   • FakeTelephonyCarrier      — provisions numbers deterministically, tracks
//                                 configured webhooks, dials into an
//                                 InMemoryCallSession, lists owned numbers.
//   • FakeInboundCallDispatcher — materialises inbound sessions to subscribers.
//
// All state is guarded by NSLock; fan-out streams use the snapshot-release-
// finish discipline via the brokers defined in TelephonyTestSession.swift.

import Foundation

// MARK: - InMemoryMediaStream

/// A full in-memory `IMediaStream` with no WebSocket behind it. Host/test code
/// injects inbound audio + DTMF, reads captured outbound audio, and pushes
/// status transitions. Reproduces the "unbounded channel retains pre-reader
/// writes" semantics via `BufferedSingleConsumerStream`.
public final class InMemoryMediaStream: IMediaStream, @unchecked Sendable {
    private let inboundAudio = BufferedSingleConsumerStream<AudioFrame>()
    private let inboundDtmf = BufferedSingleConsumerStream<DtmfEvent>()
    private let statusBroker = StatusChangeBroker()

    private let gate = NSLock()
    private var _status: CallStatus
    private var _outboundAudio: [AudioFrame] = []

    public let callInfo: CallInfo

    public init(callInfo: CallInfo, initialStatus: CallStatus = .active) {
        self.callInfo = callInfo
        self._status = initialStatus
    }

    public var currentStatus: CallStatus {
        gate.lock(); defer { gate.unlock() }
        return _status
    }

    /// Outbound audio the carrier session has sent, captured for assertions.
    public var sentAudioFrames: [AudioFrame] {
        gate.lock(); defer { gate.unlock() }
        return _outboundAudio
    }

    /// Inject one inbound audio frame.
    public func injectInboundAudio(_ frame: AudioFrame) {
        inboundAudio.write(frame)
    }

    /// Inject one inbound DTMF event.
    public func injectInboundDtmf(_ ev: DtmfEvent) {
        inboundDtmf.write(ev)
    }

    /// Push a status transition to subscribers and update `currentStatus`.
    public func pushStatus(_ status: CallStatus) {
        gate.lock(); _status = status; gate.unlock()
        statusBroker.publish(status)
    }

    public func receiveAudio() -> AsyncStream<AudioFrame> { inboundAudio.stream() }
    public func receiveDtmf() -> AsyncStream<DtmfEvent> { inboundDtmf.stream() }

    public func sendAudio(_ frame: AudioFrame) async throws {
        gate.lock(); _outboundAudio.append(frame); gate.unlock()
    }

    public func end() async throws {
        gate.lock(); _status = .endedByAgent; gate.unlock()
        statusBroker.publish(.endedByAgent)
        inboundAudio.complete()
        inboundDtmf.complete()
    }

    public func statusChanges() -> AsyncStream<CallStatus> { statusBroker.stream() }

    public func dispose() async {
        inboundAudio.complete()
        inboundDtmf.complete()
        statusBroker.complete()
    }
}

// MARK: - InMemoryCallSession

/// An `ICallSession` over an `IMediaStream` that records control-plane actions
/// (transfer / hang-up) in memory instead of calling a carrier REST API.
/// Follows the same status-derivation rule as the real carrier sessions:
/// while the media stream still reports `.ringing`, a locally-set status wins.
public final class InMemoryCallSession: ICallSession, @unchecked Sendable {
    private let media: IMediaStream
    private let statusBroker = StatusChangeBroker()
    private let gate = NSLock()
    private var _status: CallStatus = .ringing
    private var _transferTargets: [String] = []
    private var _hungUp = false
    private var mediaStatusTask: Task<Void, Never>?

    public init(media: IMediaStream) {
        self.media = media
        // Bridge media status changes into this session's own broker, mirroring
        // the C# `_media.StatusChanged += OnMediaStatusChanged`.
        let stream = media.statusChanges()
        mediaStatusTask = Task { [weak self] in
            for await status in stream {
                self?.setStatus(status)
            }
        }
    }

    public var info: CallInfo { media.callInfo }

    public var status: CallStatus {
        gate.lock(); let local = _status; gate.unlock()
        let mediaStatus = media.currentStatus
        // Mirror: `media.CurrentStatus == Ringing && _status != Ringing ? _status : media.CurrentStatus`.
        return (mediaStatus == .ringing && local != .ringing) ? local : mediaStatus
    }

    /// Targets passed to `transfer`, captured for assertions.
    public var transferTargets: [String] {
        gate.lock(); defer { gate.unlock() }
        return _transferTargets
    }

    /// Whether `hangUp` was called.
    public var didHangUp: Bool {
        gate.lock(); defer { gate.unlock() }
        return _hungUp
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
        gate.lock(); _transferTargets.append(targetNumber); gate.unlock()
        setStatus(.transferred)
    }

    public func hangUp() async throws {
        setStatus(.endedByAgent)
        gate.lock(); _hungUp = true; gate.unlock()
        do { try await media.end() } catch { /* media may already be closed */ }
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

// MARK: - FakeInboundCallDispatcher

/// Materialises inbound call sessions to subscribers. Port-adjacent analogue of
/// the host's inbound plumbing, fully in-memory. Publishing an inbound session
/// fans it out to every live subscriber.
public final class FakeInboundCallDispatcher: IInboundCallDispatcher, @unchecked Sendable {
    private let lock = NSLock()
    private var handlers: [UUID: @Sendable (ICallSession) async -> Void] = [:]
    public let carrierId: String

    public init(carrierId: String = "fake") {
        self.carrierId = carrierId
    }

    public func subscribe(_ handler: @escaping @Sendable (ICallSession) async -> Void) -> ISubscription {
        let id = UUID()
        lock.lock(); handlers[id] = handler; lock.unlock()
        return Handle(id: id) { [weak self] in
            guard let self else { return }
            self.lock.lock(); self.handlers[id] = nil; self.lock.unlock()
        }
    }

    /// Deliver an inbound session to all current subscribers, awaiting each.
    public func deliver(_ session: ICallSession) async {
        lock.lock()
        let snapshot = Array(handlers.values)
        lock.unlock()
        for handler in snapshot {
            await handler(session)
        }
    }

    private final class Handle: ISubscription, @unchecked Sendable {
        private let id: UUID
        private let onDispose: @Sendable () -> Void
        private let lock = NSLock()
        private var disposed = false
        init(id: UUID, onDispose: @escaping @Sendable () -> Void) {
            self.id = id
            self.onDispose = onDispose
        }
        func dispose() {
            lock.lock()
            if disposed { lock.unlock(); return }
            disposed = true
            lock.unlock()
            onDispose()
        }
    }
}

// MARK: - FakeTelephonyCarrier

/// Deterministic, offline `ITelephonyCarrier`. Provisions numbers from a
/// predictable sequence, tracks the webhooks configured against owned numbers,
/// dials into an `InMemoryCallSession`, and lists owned numbers — all without a
/// socket. Useful for driving the full carrier contract in tests and dry-runs.
///
/// Determinism: provisioned numbers are minted as
/// `"+" + countryDigits + areaCodeOrPad + zero-padded-sequence`, incrementing a
/// counter. `configured=false` (default) makes `isConfigured` report false so
/// callers can exercise the fail-soft branches; construct with
/// `configured: true` (the default) for the happy path.
public final class FakeTelephonyCarrier: ITelephonyCarrier, @unchecked Sendable {

    /// A recorded webhook configuration.
    public struct ConfiguredWebhook: Sendable, Equatable {
        public let phoneNumber: String
        public let webhook: URL
    }

    /// A recorded outbound dial.
    public struct DialRecord: Sendable, Equatable {
        public let fromNumber: String
        public let toNumber: String
        public let streamUrl: URL
        public let options: OutboundDialOptions?
    }

    private let lock = NSLock()
    private let _carrierId: String
    private var _configured: Bool
    private var _monthlyCost: Decimal
    private var counter: Int = 0
    private var owned: [ProvisionedNumber] = []
    private var _webhooks: [ConfiguredWebhook] = []
    private var _dials: [DialRecord] = []
    /// The media format the dialled sessions report. Defaults to Mulaw8000
    /// (Twilio/Plivo default).
    private let dialMediaFormat: CallMediaFormat
    /// Clock so `provisionedAtUtc` / `startedAtUtc` are injectable/deterministic.
    private let clock: @Sendable () -> Date

    public init(
        carrierId: String = "fake",
        configured: Bool = true,
        monthlyRecurringCost: Decimal = 1,
        dialMediaFormat: CallMediaFormat = .mulaw8000,
        clock: @escaping @Sendable () -> Date = { Date(timeIntervalSince1970: 0) }
    ) {
        self._carrierId = carrierId
        self._configured = configured
        self._monthlyCost = monthlyRecurringCost
        self.dialMediaFormat = dialMediaFormat
        self.clock = clock
    }

    public var carrierId: String { _carrierId }

    public var isConfigured: Bool {
        lock.lock(); defer { lock.unlock() }
        return _configured
    }

    /// Flip configured state (to exercise fail-soft branches deterministically).
    public func setConfigured(_ value: Bool) {
        lock.lock(); _configured = value; lock.unlock()
    }

    /// Webhooks configured so far, in call order.
    public var configuredWebhooks: [ConfiguredWebhook] {
        lock.lock(); defer { lock.unlock() }
        return _webhooks
    }

    /// Outbound dials recorded so far, in call order.
    public var dials: [DialRecord] {
        lock.lock(); defer { lock.unlock() }
        return _dials
    }

    public func provisionNumber(countryCode: String, areaCode: String?) async throws -> ProvisionedNumber {
        try ensureConfigured()
        lock.lock()
        counter += 1
        let seq = String(format: "%07d", counter)
        let area = areaCode ?? "000"
        // Keep only ASCII digits from the country code (e.g. "27" → "27",
        // "ZA" → "" → fall back to the code's length so the number is still a
        // deterministic, well-formed string).
        let digitChars = countryCode.filter { $0.isNumber && $0.isASCII }
        let cc = digitChars.isEmpty ? String(countryCode.count) : digitChars
        let number = "+\(cc)\(area)\(seq)"
        let cost = _monthlyCost
        let now = clock()
        let provisioned = ProvisionedNumber(
            phoneNumber: number,
            carrierId: _carrierId,
            provisionedAtUtc: now,
            monthlyRecurringCost: cost)
        owned.append(provisioned)
        lock.unlock()
        return provisioned
    }

    public func configureInboundWebhook(phoneNumber: String, inboundWebhook: URL) async throws {
        try ensureConfigured()
        lock.lock()
        _webhooks.append(ConfiguredWebhook(phoneNumber: phoneNumber, webhook: inboundWebhook))
        lock.unlock()
    }

    public func dial(
        fromNumber: String,
        toNumber: String,
        streamUrl: URL,
        options: OutboundDialOptions?
    ) async throws -> ICallSession {
        try ensureConfigured()
        lock.lock()
        counter += 1
        let callId = "fake-call-\(counter)"
        let now = clock()
        let fmt = dialMediaFormat
        _dials.append(DialRecord(
            fromNumber: fromNumber, toNumber: toNumber, streamUrl: streamUrl, options: options))
        lock.unlock()

        let info = CallInfo(
            callId: callId,
            direction: .outbound,
            from: fromNumber,
            to: toNumber,
            carrierId: _carrierId,
            mediaFormat: fmt,
            startedAtUtc: now)
        // Return a live in-memory session (not a pending one): the fake carrier
        // needs no host WebSocket. Start it in `.active` so the caller can send
        // audio immediately.
        let media = InMemoryMediaStream(callInfo: info, initialStatus: .active)
        return InMemoryCallSession(media: media)
    }

    public func listNumbers() async throws -> [ProvisionedNumber] {
        // Mirror the real carriers: fail-soft (empty) when not configured.
        lock.lock(); defer { lock.unlock() }
        if !_configured { return [] }
        return owned
    }

    private func ensureConfigured() throws {
        lock.lock(); let ok = _configured; lock.unlock()
        if !ok {
            throw TelephonyError.invalidOperation(
                "Fake carrier is not configured. Construct FakeTelephonyCarrier(configured: true) before calling REST operations.")
        }
    }
}
