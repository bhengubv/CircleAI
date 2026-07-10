// TelephonyTestSession.swift
//
// Port of CircleAI.Telephony.TestCallSession — build voice loops without
// paying for a real carrier minute. TestCallSession is an in-memory
// ICallSession that lets a test harness inject inbound audio + DTMF, capture
// outbound audio, and drive lifecycle events on demand.
//
// CONCURRENCY: the C# reference backs the inbound audio/DTMF with UNBOUNDED,
// single-reader channels (`Channel.CreateUnbounded { SingleReader = true }`).
// Crucially, an unbounded channel RETAINS writes made before a reader attaches
// and replays them on `ReadAllAsync`. `BufferedSingleConsumerStream` reproduces
// that exactly: injections before `receiveAudio()`/`receiveDtmf()` is called are
// buffered and drained into the (single) consumer's AsyncStream when it
// attaches; injections after go straight through. `finish()` is always called
// with the lock RELEASED (snapshot-release-finish) so a continuation's
// onTermination re-acquiring the lock cannot self-deadlock.

import Foundation

// MARK: - BufferedSingleConsumerStream

/// An unbounded, single-consumer buffered stream that mirrors a .NET unbounded
/// `Channel` with `SingleReader = true`: values written before the reader
/// attaches are retained and replayed to the reader when it subscribes; values
/// written after flow straight through. Completing the writer finishes the
/// reader's stream after any buffered values are delivered.
final class BufferedSingleConsumerStream<Element: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var backlog: [Element] = []
    private var continuation: AsyncStream<Element>.Continuation?
    private var attached = false
    private var completed = false

    init() {}

    /// Write one value. If a reader is attached it is yielded immediately;
    /// otherwise it is buffered until a reader attaches. No-op once completed
    /// (mirrors `Writer.TryWrite` failing after `TryComplete`).
    @discardableResult
    func write(_ element: Element) -> Bool {
        lock.lock()
        if completed { lock.unlock(); return false }
        if let cont = continuation {
            lock.unlock()
            cont.yield(element)
            return true
        }
        backlog.append(element)
        lock.unlock()
        return true
    }

    /// Complete the writer. After buffered values are drained the reader's
    /// stream finishes. Idempotent (mirrors `TryComplete`).
    func complete() {
        lock.lock()
        if completed { lock.unlock(); return }
        completed = true
        // If a reader is attached, finish OUTSIDE the lock.
        let cont = continuation
        lock.unlock()
        cont?.finish()
    }

    /// Attach the single reader. Drains the backlog into the new continuation
    /// synchronously, then keeps the continuation for straight-through delivery.
    /// A second attach returns an already-finished stream (single-reader).
    func stream() -> AsyncStream<Element> {
        AsyncStream { continuation in
            lock.lock()
            if attached {
                // Single-reader: a second subscriber gets nothing.
                lock.unlock()
                continuation.finish()
                return
            }
            attached = true
            // Drain the backlog into the continuation WHILE HOLDING THE LOCK, so
            // a concurrent write() cannot interleave a straight-through yield
            // ahead of a buffered one (it would block on the lock and be
            // delivered in order once we publish the continuation). Yielding
            // under the lock is safe here: AsyncStream.Continuation.yield does
            // not re-enter this type synchronously — only onTermination does,
            // and that is wired AFTER the lock is released.
            for value in backlog { continuation.yield(value) }
            backlog.removeAll()
            let wasCompleted = completed
            if wasCompleted {
                lock.unlock()
                continuation.finish()
                return
            }
            self.continuation = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                self.continuation = nil
                self.lock.unlock()
            }
        }
    }
}

// MARK: - StatusChangeBroker

/// Fan-out broker for `CallStatus` change events (models the C#
/// `event EventHandler<CallStatus>? StatusChanged`). Multiple subscribers each
/// get their own AsyncStream. Uses the snapshot-release-finish discipline.
final class StatusChangeBroker: @unchecked Sendable {
    private let lock = NSLock()
    private var continuations: [UUID: AsyncStream<CallStatus>.Continuation] = [:]
    private var completed = false

    init() {}

    /// Publish a status change to all live subscribers.
    func publish(_ status: CallStatus) {
        lock.lock(); defer { lock.unlock() }
        guard !completed else { return }
        for cont in continuations.values { cont.yield(status) }
    }

    /// Finish all subscriber streams. Snapshot + clear under the lock, finish
    /// OUTSIDE it (non-reentrant lock; onTermination re-acquires it).
    func complete() {
        lock.lock()
        completed = true
        let conts = Array(continuations.values)
        continuations.removeAll()
        lock.unlock()
        for cont in conts { cont.finish() }
    }

    func stream() -> AsyncStream<CallStatus> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            if completed { lock.unlock(); continuation.finish(); return }
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.continuations[id] = nil; self.lock.unlock()
            }
        }
    }
}

// MARK: - TestCallSession

/// In-memory `ICallSession` for harnesses + unit tests. Port of
/// `CircleAI.Telephony.TestCallSession`.
public final class TestCallSession: ICallSession, @unchecked Sendable {
    private let inboundAudio = BufferedSingleConsumerStream<AudioFrame>()
    private let inboundDtmf = BufferedSingleConsumerStream<DtmfEvent>()
    private let statusBroker = StatusChangeBroker()

    private let gate = NSLock()
    private var _status: CallStatus = .active
    private var _outboundAudio: [AudioFrame] = []
    private var _outboundDtmf: [String] = []

    public let info: CallInfo

    public init(info: CallInfo? = nil) {
        self.info = info ?? CallInfo(
            callId: UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
            direction: .inbound,
            from: "+15555550100",
            to: "+15555550200",
            carrierId: "test",
            mediaFormat: .pcm16000,
            startedAtUtc: Date())
    }

    public var status: CallStatus {
        gate.lock(); defer { gate.unlock() }
        return _status
    }

    /// Outbound audio frames the AI has emitted, captured for assertions.
    public var sentAudioFrames: [AudioFrame] {
        gate.lock(); defer { gate.unlock() }
        return _outboundAudio
    }

    /// Outbound DTMF strings the AI has emitted.
    public var sentDtmf: [String] {
        gate.lock(); defer { gate.unlock() }
        return _outboundDtmf
    }

    /// Inject one inbound audio frame for the AI to consume via `receiveAudio`.
    public func injectInboundAudio(_ frame: AudioFrame) {
        inboundAudio.write(frame)
    }

    /// Inject one inbound DTMF event.
    public func injectInboundDtmf(_ ev: DtmfEvent) {
        inboundDtmf.write(ev)
    }

    /// Stop the inbound streams cleanly.
    public func endInboundStreams() {
        inboundAudio.complete()
        inboundDtmf.complete()
    }

    /// Trigger a status change (e.g. caller hangs up).
    public func triggerStatusChange(_ newStatus: CallStatus) {
        gate.lock()
        _status = newStatus
        gate.unlock()
        statusBroker.publish(newStatus)
    }

    public func receiveAudio() -> AsyncStream<AudioFrame> {
        inboundAudio.stream()
    }

    public func receiveDtmf() -> AsyncStream<DtmfEvent> {
        inboundDtmf.stream()
    }

    public func sendAudio(_ frame: AudioFrame) async throws {
        gate.lock(); _outboundAudio.append(frame); gate.unlock()
    }

    public func sendDtmf(_ digits: String) async throws {
        gate.lock(); _outboundDtmf.append(digits); gate.unlock()
    }

    public func transfer(targetNumber: String, mode: TransferMode, briefing: String?) async throws {
        triggerStatusChange(.transferred)
    }

    public func hangUp() async throws {
        triggerStatusChange(.endedByAgent)
        endInboundStreams()
    }

    public func statusChanges() -> AsyncStream<CallStatus> {
        statusBroker.stream()
    }

    public func dispose() async {
        endInboundStreams()
        statusBroker.complete()
    }
}
