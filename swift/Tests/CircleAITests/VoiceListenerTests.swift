// VoiceListenerTests.swift
//
// Verifies VoiceCompanionListener (VoiceListener.swift): a transcription from the
// pipeline fires onUtteranceDetected, is forwarded to the ICompanionSession, and
// the reply is surfaced via onResponseReady. Also covers start/stop delegation
// and disposal.

import XCTest
@testable import CircleAI

final class VoiceListenerTests: XCTestCase {

    /// A pipeline whose transcription stream we drive manually.
    final class FakePipeline: IVoicePipeline, @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: AsyncStream<TranscribedEvent>.Continuation?
        private(set) var started = false
        private(set) var stopped = false

        func transcribed() -> AsyncStream<TranscribedEvent> {
            AsyncStream { cont in
                self.lock.lock(); self.continuation = cont; self.lock.unlock()
            }
        }
        func emit(_ text: String, confidence: Float = 0.95) {
            lock.lock(); let c = continuation; lock.unlock()
            c?.yield(TranscribedEvent(result: TranscriptionResult(text: text, confidence: confidence, languageCode: "en")))
        }
        func start() async throws { lock.lock(); started = true; lock.unlock() }
        func stop() async throws { lock.lock(); stopped = true; lock.unlock() }
    }

    /// Minimal ICompanionSession returning a canned reply.
    final class FakeSession: ICompanionSession, @unchecked Sendable {
        let sessionId = "s"
        let identityId = "u"
        let interface: InterfaceKind = .ambient
        let reply: String
        private(set) var received: [String] = []
        init(reply: String) { self.reply = reply }

        func send(_ message: String) async throws -> String { received.append(message); return reply }
        func stream(_ message: String) -> AsyncStream<String> { AsyncStream { $0.finish() } }
        func agent(_ instruction: String) async throws -> String { reply }
        func getContext() -> CompanionContext {
            CompanionContext(identityId: identityId, displayName: "U", interface: interface,
                             personaHints: "", affectSummary: "", recentMemorySnippets: [], activeGoals: [])
        }
        func refreshContext() async throws {}
        var history: [CompanionTurn] { [] }
        func signalFeedback(positive: Bool, note: String?) async throws {}
        var proactiveEvents: AsyncStream<CompanionProactiveEvent> { AsyncStream { $0.finish() } }
    }

    /// Thread-safe capture box for the two callbacks (they fire on background tasks).
    final class Box: @unchecked Sendable {
        private let lock = NSLock()
        private var _utterances: [String] = []
        private var _responses: [ResponseReadyEvent] = []
        func addUtterance(_ s: String) { lock.lock(); _utterances.append(s); lock.unlock() }
        func addResponse(_ r: ResponseReadyEvent) { lock.lock(); _responses.append(r); lock.unlock() }
        var utterances: [String] { lock.lock(); defer { lock.unlock() }; return _utterances }
        var responses: [ResponseReadyEvent] { lock.lock(); defer { lock.unlock() }; return _responses }
    }

    func testForwardsUtteranceAndSurfacesReply() async throws {
        let pipeline = FakePipeline()
        let session = FakeSession(reply: "Sure, playing jazz.")
        let listener = VoiceCompanionListener(pipeline: pipeline, session: session)

        let box = Box()
        listener.onUtteranceDetected = { ev in box.addUtterance(ev.text) }
        listener.onResponseReady = { ev in box.addResponse(ev) }

        try await listener.start()
        pipeline.emit("play some jazz")

        // Poll until the fire-and-forget forwarding completes (or time out).
        for _ in 0..<100 where box.responses.isEmpty {
            try await Task.sleep(nanoseconds: 20_000_000)
        }

        XCTAssertEqual(box.utterances, ["play some jazz"])
        XCTAssertEqual(box.responses.count, 1)
        XCTAssertEqual(box.responses.first?.originalUtterance, "play some jazz")
        XCTAssertEqual(box.responses.first?.text, "Sure, playing jazz.")
        XCTAssertEqual(session.received, ["play some jazz"])
        await listener.dispose()
    }

    func testStartStopDelegateToPipeline() async throws {
        let pipeline = FakePipeline()
        let listener = VoiceCompanionListener(pipeline: pipeline, session: FakeSession(reply: "ok"))
        try await listener.start()
        XCTAssertTrue(pipeline.started)
        try await listener.stop()
        XCTAssertTrue(pipeline.stopped)
        await listener.dispose()
    }

    func testStartAfterDisposeThrows() async throws {
        let pipeline = FakePipeline()
        let listener = VoiceCompanionListener(pipeline: pipeline, session: FakeSession(reply: "ok"))
        await listener.dispose()
        do {
            try await listener.start()
            XCTFail("expected disposed error")
        } catch {
            XCTAssertEqual(error as? VoiceListenerError, .disposed)
        }
    }
}
