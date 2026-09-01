// VoiceLoopTests.swift
//
// The circle: wake -> ASR -> brain -> TTS -> speaker -> back to listening.
// Each half already worked in isolation; these tests are about the joins.

import XCTest
@testable import CircleAI

/// A wake detector a test can fire by hand.
private final class ManualWake: IWakeWordDetector, @unchecked Sendable {
    let wakeWord = "Hey B"
    private let lock = NSLock()
    private var listening = false
    private var sinks: [AsyncStream<WakeWordDetectedEventArgs>.Continuation] = []

    var isListening: Bool { lock.lock(); defer { lock.unlock() }; return listening }

    func detections() -> AsyncStream<WakeWordDetectedEventArgs> {
        AsyncStream { c in
            lock.lock(); sinks.append(c); lock.unlock()
        }
    }

    func fire() {
        lock.lock(); let all = sinks; lock.unlock()
        for c in all { c.yield(WakeWordDetectedEventArgs(wakeWord: wakeWord, confidence: 1)) }
    }

    func start() async throws { lock.lock(); listening = true; lock.unlock() }
    func stop() async throws { lock.lock(); listening = false; lock.unlock() }
    func dispose() async {
        lock.lock(); let all = sinks; sinks = []; lock.unlock()
        for c in all { c.finish() }
    }
}

private final class StubTts: ITtsEngine, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var spoken: [String] = []
    var bytes = 64
    var failWith: Error?

    func synthesise(text: String) async throws -> TtsSynthesisResult {
        if let failWith { throw failWith }
        lock.lock(); spoken.append(text); lock.unlock()
        return TtsSynthesisResult(audioData: Data(count: bytes), sampleRate: 16_000,
                                  channels: 1, bitsPerSample: 16)
    }

    func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { $0.finish() }
    }

    var said: [String] { lock.lock(); defer { lock.unlock() }; return spoken }
}

private final class SpyPlayer: IAudioPlayer, @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    /// Set to hold playback open so a barge-in has something to interrupt.
    var holdSeconds: Double = 0

    var played: Int { lock.lock(); defer { lock.unlock() }; return count }

    func play(pcm: Data, sampleRate: Int, channels: Int, bitsPerSample: Int) async throws {
        if holdSeconds > 0 {
            try await Task.sleep(nanoseconds: UInt64(holdSeconds * 1_000_000_000))
        }
        lock.lock(); count += 1; lock.unlock()
    }

    func close() async {}
}

/// Collects callbacks off whatever task fires them.
private final class Box<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T] = []
    func add(_ t: T) { lock.lock(); items.append(t); lock.unlock() }
    var all: [T] { lock.lock(); defer { lock.unlock() }; return items }
    var count: Int { all.count }
}

final class VoiceLoopTests: XCTestCase {

    private func pipeline(_ wake: ManualWake) -> VoicePipeline {
        VoicePipeline(wake: wake, transcriber: NullVoiceTranscriber())
    }

    private func waitFor(_ timeout: Double = 2.0,
                         _ condition: @escaping @Sendable () -> Bool) async {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if condition() { return }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
    }

    private func heard(_ ears: VoicePipeline, _ text: String) {
        ears.onTranscribed?(TranscribedEvent(
            result: TranscriptionResult(text: text, confidence: 1, languageCode: "en")))
    }

    // MARK: - The circle closes

    func testATranscriptReachesTheBrainAndTheReplyReachesTheMouth() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let mouth = StubTts()
        let player = SpyPlayer()
        let asked = Box<String>()

        let loop = VoiceLoop(ears: ears,
                             brain: { t in asked.add(t); return "you said \(t)" },
                             mouth: mouth, speaker: player)
        try await loop.start()
        heard(ears, "hello")

        await waitFor { player.played == 1 }
        XCTAssertEqual(asked.all, ["hello"])
        XCTAssertEqual(mouth.said, ["you said hello"])
        XCTAssertEqual(player.played, 1)
        await loop.close()
    }

    func testACompletedTurnIsReportedWithBothHalves() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let turns = Box<VoiceExchange>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "hi there" }, mouth: StubTts())
        loop.onExchanged = { turns.add($0) }
        try await loop.start()
        heard(ears, "hello")

        await waitFor { turns.count == 1 }
        XCTAssertEqual(turns.all.first?.heard, "hello")
        XCTAssertEqual(turns.all.first?.replied, "hi there")
        await loop.close()
    }

    // MARK: - One turn at a time

    func testTurnsAreProcessedOneAtATimeNotInterleaved() async throws {
        // A callback cannot await the brain, and letting turns overlap would
        // interleave two replies through one speaker.
        let wake = ManualWake()
        let ears = pipeline(wake)
        let order = Box<String>()

        let loop = VoiceLoop(ears: ears, brain: { t in
            order.add("start \(t)")
            try await Task.sleep(nanoseconds: 30_000_000)
            order.add("end \(t)")
            return t
        }, mouth: StubTts())
        try await loop.start()

        heard(ears, "one")
        heard(ears, "two")

        await waitFor { order.count == 4 }
        XCTAssertEqual(order.all, ["start one", "end one", "start two", "end two"])
        await loop.close()
    }

    // MARK: - Nothing to say

    func testABlankTranscriptIsNotATurn() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let asked = Box<String>()

        let loop = VoiceLoop(ears: ears,
                             brain: { t in asked.add(t); return "x" },
                             mouth: StubTts())
        try await loop.start()
        heard(ears, "   ")
        heard(ears, "real")

        await waitFor { asked.count == 1 }
        XCTAssertEqual(asked.all, ["real"])
        await loop.close()
    }

    func testABlankReplyIsStillACompletedTurnButNothingIsSpoken() async throws {
        // The turn HAPPENED, and a host counting exchanges must see it. There
        // was simply nothing to say.
        let wake = ManualWake()
        let ears = pipeline(wake)
        let mouth = StubTts()
        let turns = Box<VoiceExchange>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "" }, mouth: mouth)
        loop.onExchanged = { turns.add($0) }
        try await loop.start()
        heard(ears, "hello")

        await waitFor { turns.count == 1 }
        XCTAssertTrue(mouth.said.isEmpty)
        XCTAssertEqual(turns.all.first?.replied, "")
        await loop.close()
    }

    // MARK: - Failures do not kill the loop

    func testAFailedBrainDropsOneReplyAndKeepsListening() async throws {
        // Going permanently deaf is far worse than dropping one reply.
        struct Hiccup: Error {}
        let wake = ManualWake()
        let ears = pipeline(wake)
        let faults = Box<String>()
        let turns = Box<VoiceExchange>()
        let attempt = Box<String>()

        let loop = VoiceLoop(ears: ears, brain: { t in
            attempt.add(t)
            if t == "bad" { throw Hiccup() }
            return "ok"
        }, mouth: StubTts())
        loop.onFaulted = { faults.add("\($0)") }
        loop.onExchanged = { turns.add($0) }
        try await loop.start()

        heard(ears, "bad")
        heard(ears, "good")

        await waitFor { turns.count == 1 }
        XCTAssertEqual(faults.count, 1)
        XCTAssertEqual(attempt.all, ["bad", "good"])
        XCTAssertEqual(turns.all.first?.heard, "good")
        await loop.close()
    }

    func testAFailedTtsIsAFaultNotAStop() async throws {
        struct TtsDown: Error {}
        let wake = ManualWake()
        let ears = pipeline(wake)
        let mouth = StubTts()
        mouth.failWith = TtsDown()
        let faults = Box<String>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "reply" }, mouth: mouth)
        loop.onFaulted = { faults.add("\($0)") }
        try await loop.start()
        heard(ears, "hello")

        await waitFor { faults.count == 1 }
        XCTAssertEqual(faults.count, 1)

        // Still alive: a second turn goes through once the engine recovers.
        mouth.failWith = nil
        let turns = Box<VoiceExchange>()
        loop.onExchanged = { turns.add($0) }
        heard(ears, "again")
        await waitFor { turns.count == 1 }
        XCTAssertEqual(turns.count, 1)
        await loop.close()
    }

    func testSilentAudioIsNotSentToTheSpeaker() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let mouth = StubTts()
        mouth.bytes = 0
        let player = SpyPlayer()
        let turns = Box<VoiceExchange>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "reply" },
                             mouth: mouth, speaker: player)
        loop.onExchanged = { turns.add($0) }
        try await loop.start()
        heard(ears, "hello")

        await waitFor { turns.count == 1 }
        XCTAssertEqual(player.played, 0)
        await loop.close()
    }

    // MARK: - Barge-in

    func testTheWakeWordDuringAReplyInterruptsTheSpeakingOnly() async throws {
        // Cancelling the loop here would make interrupting the assistant also
        // switch it off, which is the opposite of what the person wanted.
        let wake = ManualWake()
        let ears = pipeline(wake)
        let player = SpyPlayer()
        player.holdSeconds = 5
        let barged = Box<Int>()
        let turns = Box<VoiceExchange>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "a long reply" },
                             mouth: StubTts(), speaker: player)
        loop.onBargedIn = { barged.add(1) }
        loop.onExchanged = { turns.add($0) }
        try await loop.start()

        heard(ears, "hello")
        try await Task.sleep(nanoseconds: 60_000_000)   // let playback begin
        wake.fire()

        await waitFor { barged.count == 1 && turns.count == 1 }
        XCTAssertEqual(barged.count, 1)
        XCTAssertEqual(turns.count, 1, "the turn still completes; only the audio stopped")

        // And the loop is still alive.
        player.holdSeconds = 0
        heard(ears, "again")
        await waitFor { turns.count == 2 }
        XCTAssertEqual(turns.count, 2)
        await loop.close()
    }

    func testTheWakeWordWithNothingPlayingIsNotABargeIn() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let barged = Box<Int>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "x" }, mouth: StubTts())
        loop.onBargedIn = { barged.add(1) }
        try await loop.start()

        wake.fire()
        try await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(barged.count, 0)
        await loop.close()
    }

    func testBargeInCanBeTurnedOff() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let player = SpyPlayer()
        player.holdSeconds = 0.3
        let barged = Box<Int>()

        let loop = VoiceLoop(ears: ears, brain: { _ in "reply" },
                             mouth: StubTts(), speaker: player, allowBargeIn: false)
        loop.onBargedIn = { barged.add(1) }
        try await loop.start()

        heard(ears, "hello")
        try await Task.sleep(nanoseconds: 60_000_000)
        wake.fire()

        await waitFor(1.0) { player.played == 1 }
        XCTAssertEqual(barged.count, 0)
        XCTAssertEqual(player.played, 1, "playback ran to the end")
        await loop.close()
    }

    // MARK: - Lifecycle

    func testStartingTwiceIsHarmless() async throws {
        let wake = ManualWake()
        let loop = VoiceLoop(ears: pipeline(wake), brain: { _ in "x" }, mouth: StubTts())
        try await loop.start()
        try await loop.start()
        XCTAssertTrue(wake.isListening)
        await loop.close()
    }

    func testStopEndsTheEarsAndTheConsumer() async throws {
        let wake = ManualWake()
        let ears = pipeline(wake)
        let loop = VoiceLoop(ears: ears, brain: { _ in "x" }, mouth: StubTts())
        try await loop.start()
        await loop.stop()
        XCTAssertFalse(wake.isListening)
    }

    func testStoppingTwiceIsHarmless() async throws {
        let wake = ManualWake()
        let loop = VoiceLoop(ears: pipeline(wake), brain: { _ in "x" }, mouth: StubTts())
        try await loop.start()
        await loop.stop()
        await loop.stop()
        XCTAssertFalse(wake.isListening)
    }

    func testATranscriptAfterStopIsNotSpoken() async throws {
        // The reason the queue is hand-rolled rather than an AsyncStream: the
        // symptom of losing that race is a turn that plays after stop().
        let wake = ManualWake()
        let ears = pipeline(wake)
        let asked = Box<String>()

        let loop = VoiceLoop(ears: ears,
                             brain: { t in asked.add(t); return "x" },
                             mouth: StubTts())
        try await loop.start()
        await loop.stop()
        heard(ears, "too late")

        try await Task.sleep(nanoseconds: 80_000_000)
        XCTAssertTrue(asked.all.isEmpty)
    }

    func testStartingAfterCloseIsRefusedRatherThanSilentlyDoingNothing() async throws {
        let wake = ManualWake()
        let loop = VoiceLoop(ears: pipeline(wake), brain: { _ in "x" }, mouth: StubTts())
        try await loop.start()
        await loop.close()

        do {
            try await loop.start()
            XCTFail("a closed loop must not restart")
        } catch {
            XCTAssertEqual(error as? VoiceError, .disposed)
        }
    }

    func testClosingTwiceIsHarmless() async throws {
        let wake = ManualWake()
        let loop = VoiceLoop(ears: pipeline(wake), brain: { _ in "x" }, mouth: StubTts())
        try await loop.start()
        await loop.close()
        await loop.close()
    }
}
