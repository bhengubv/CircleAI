// VoiceTtsDecoratorsTests.swift

import XCTest
@testable import CircleAI

/// Records what it was asked to say and hands back audio of a known length, so
/// the joining and the padding can be counted exactly.
private final class RecordingTts: ITtsEngine, ITtsFrontEndDiagnostics, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var asked: [String] = []

    var bytesPerCall = 100
    var sampleRate = 16_000
    var channels = 1
    var bitsPerSample = 16

    /// Text that produces no audio at all, standing in for a sentence the front
    /// end could not phonemise.
    var silentFor: Set<String> = []

    var lastSkippedCount = 0
    var lastSkippedSymbols: [String] = []
    var lastApproximatedSymbols: [String] = []

    /// Armed by a test that needs the producer held after its first render.
    ///
    /// Without it, `AsyncThrowingStream`'s unbounded buffer lets the producing
    /// task render the whole passage before the consumer is scheduled to see
    /// chunk one — so a test about time-to-first-audio measures the scheduler.
    var holdAfterFirst: (@Sendable () async -> Void)?

    func synthesise(text: String) async throws -> TtsSynthesisResult {
        // The gate is taken BEFORE the ask is recorded. Recording first would
        // count a render that has not happened — the point of the assertion is
        // how many sentences were actually rendered when chunk one arrived.
        lock.lock()
        let alreadyAsked = asked.count
        let gate = holdAfterFirst
        lock.unlock()
        if alreadyAsked >= 1, let gate { await gate() }

        lock.lock(); asked.append(text); lock.unlock()
        let n = silentFor.contains(text) ? 0 : bytesPerCall
        return TtsSynthesisResult(audioData: Data(count: n), sampleRate: sampleRate,
                                  channels: channels, bitsPerSample: bitsPerSample)
    }

    func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { c in
            Task {
                let r = try await self.synthesise(text: text)
                c.yield(r.audioData)
                c.finish()
            }
        }
    }
}

final class VoiceTtsDecoratorsTests: XCTestCase {

    // MARK: - Phrasing

    func testEachSentenceIsSynthesisedSeparately() async throws {
        // The whole point: the model cannot encode a pause, so the pause has to
        // be made out of separate renders with silence between them.
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        _ = try await e.synthesise(text: "One. Two. Three.")
        XCTAssertEqual(inner.asked.count, 3)
        XCTAssertEqual(e.lastSegmentCount, 3)
    }

    func testASingleSentenceIsHandedBackUntouched() async throws {
        // Byte-identical to the unwrapped engine: no join, no padding, nothing.
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        let r = try await e.synthesise(text: "Just the one.")
        XCTAssertEqual(r.audioData.count, 100)
        XCTAssertEqual(inner.asked, ["Just the one."])
    }

    func testASingleSentenceStillGetsPaddingWhenPaddingWasAskedFor() async throws {
        // This path is easy to forget and easy to hit: grouping collapses a whole
        // paragraph to one segment, so the common case lands here, and skipping
        // the padding would apply it to short text and not to long.
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        e.leadInSilenceMs = 100    // 16000 * 0.1 * 2 bytes = 3200
        let r = try await e.synthesise(text: "Just the one.")
        XCTAssertEqual(r.audioData.count, 3200 + 100)
    }

    func testSilenceIsInsertedWhereTheFullStopsWere() async throws {
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        let segments = SentenceSplitter.split("One. Two.")
        let expectedGap = segments.map(\.trailingPauseMs).reduce(0, +) * 32   // 16 kHz, 16-bit mono

        let r = try await e.synthesise(text: "One. Two.")
        XCTAssertEqual(r.audioData.count, 200 + expectedGap)
    }

    func testSilenceMatchesTheFormatOfTheAudioItSitsAgainst() {
        // Silence at the wrong rate or width is a click, not a pause.
        let stereo24 = TtsSynthesisResult(audioData: Data(), sampleRate: 24_000,
                                          channels: 2, bitsPerSample: 16)
        XCTAssertEqual(PhrasedTtsEngine.silence(stereo24, milliseconds: 100).count,
                       2400 * 2 * 2)

        let mono8 = TtsSynthesisResult(audioData: Data(), sampleRate: 8_000,
                                       channels: 1, bitsPerSample: 8)
        XCTAssertEqual(PhrasedTtsEngine.silence(mono8, milliseconds: 100).count, 800)
    }

    func testNoSilenceIsAskedForNoSilenceIsMade() {
        let f = TtsSynthesisResult(audioData: Data(), sampleRate: 16_000,
                                   channels: 1, bitsPerSample: 16)
        XCTAssertTrue(PhrasedTtsEngine.silence(f, milliseconds: 0).isEmpty)
        XCTAssertTrue(PhrasedTtsEngine.silence(f, milliseconds: -5).isEmpty)
    }

    func testSignedPcmSilenceIsAllZeroes() {
        let f = TtsSynthesisResult(audioData: Data(), sampleRate: 16_000,
                                   channels: 1, bitsPerSample: 16)
        XCTAssertTrue(PhrasedTtsEngine.silence(f, milliseconds: 10).allSatisfy { $0 == 0 })
    }

    func testTailSilenceLetsTheLastSyllableDecay() async throws {
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        e.tailSilenceMs = 50       // 16000 * 0.05 * 2 = 1600
        let r = try await e.synthesise(text: "Just the one.")
        XCTAssertEqual(r.audioData.count, 100 + 1600)
    }

    func testEmptyTextIsEmptyAudioNotACrash() async throws {
        let e = PhrasedTtsEngine(inner: RecordingTts())
        let r = try await e.synthesise(text: "")
        XCTAssertTrue(r.audioData.isEmpty)
        XCTAssertEqual(e.lastSegmentCount, 0)
    }

    func testASentenceThatProducesNoAudioIsSkippedNotJoinedAsNothing() async throws {
        let inner = RecordingTts()
        inner.silentFor = ["Two."]
        let e = PhrasedTtsEngine(inner: inner)
        let r = try await e.synthesise(text: "One. Two. Three.")
        XCTAssertEqual(inner.asked.count, 3, "the silent one is still attempted")
        XCTAssertGreaterThanOrEqual(r.audioData.count, 200, "both real renders survived")
    }

    func testEveryThingSilentGivesEmptyAudioRatherThanAFabricatedFormat() async throws {
        let inner = RecordingTts()
        inner.silentFor = ["One.", "Two."]
        let e = PhrasedTtsEngine(inner: inner)
        let r = try await e.synthesise(text: "One. Two.")
        XCTAssertTrue(r.audioData.isEmpty)
    }

    // MARK: - Grouping

    func testGroupingJoinsSentencesIntoOneUtterance() {
        let segments = [
            SpeechSegment(text: "One.", trailingPauseMs: 10),
            SpeechSegment(text: "Two.", trailingPauseMs: 20),
            SpeechSegment(text: "Three.", trailingPauseMs: 30),
        ]
        let g = PhrasedTtsEngine.group(segments, size: 2)
        XCTAssertEqual(g.count, 2)
        XCTAssertEqual(g[0].text, "One. Two.")
        // The GROUP's pause is the LAST member's: the pauses inside the group are
        // now spoken as one utterance and the only boundary left is at the end.
        XCTAssertEqual(g[0].trailingPauseMs, 20)
        XCTAssertEqual(g[1].text, "Three.")
        XCTAssertEqual(g[1].trailingPauseMs, 30)
    }

    func testGroupingOfOneChangesNothing() {
        let segments = [SpeechSegment(text: "One.", trailingPauseMs: 10)]
        XCTAssertEqual(PhrasedTtsEngine.group(segments, size: 1), segments)
    }

    func testGroupingLargerThanTheTextIsOneGroup() {
        let segments = [
            SpeechSegment(text: "One.", trailingPauseMs: 10),
            SpeechSegment(text: "Two.", trailingPauseMs: 0),
        ]
        let g = PhrasedTtsEngine.group(segments, size: 10)
        XCTAssertEqual(g.count, 1)
        XCTAssertEqual(g[0].text, "One. Two.")
    }

    func testSentencesPerUtteranceReachesTheInnerEngine() async throws {
        let inner = RecordingTts()
        let e = PhrasedTtsEngine(inner: inner)
        e.sentencesPerUtterance = 2
        _ = try await e.synthesise(text: "One. Two. Three. Four.")
        XCTAssertEqual(inner.asked.count, 2)
        XCTAssertEqual(e.lastSegmentCount, 2)
    }

    // MARK: - Streaming

    func testStreamingEmitsSentenceOneWithoutWaitingForTheRest() async throws {
        // The latency half of the problem: a whole paragraph means every word
        // renders before the first word plays.
        //
        // The producer is HELD after its first render until the consumer has
        // chunk one. Without that gate the stream's unbounded buffer lets the
        // producer finish the whole passage first on a loaded machine, and the
        // test reports the scheduler rather than the engine.
        let inner = RecordingTts()
        let firstChunkSeen = Gate()
        inner.holdAfterFirst = { await firstChunkSeen.wait() }
        let e = PhrasedTtsEngine(inner: inner)

        var chunks = 0
        var firstArrivedAfterAsks = -1
        for try await _ in e.streamSynthesise(text: "One. Two. Three.") {
            if chunks == 0 {
                firstArrivedAfterAsks = inner.asked.count
                firstChunkSeen.open()
            }
            chunks += 1
        }
        XCTAssertEqual(firstArrivedAfterAsks, 1,
                       "chunk one arrives having rendered only sentence one")
        XCTAssertGreaterThanOrEqual(chunks, 3)
    }

    /// A one-shot latch. Opening before anyone waits is fine — a waiter then
    /// returns immediately rather than blocking on an event already past.
    private actor Gate {
        private var isOpen = false
        private var waiters: [CheckedContinuation<Void, Never>] = []

        nonisolated func open() {
            Task { await self.release() }
        }

        private func release() {
            isOpen = true
            let waiting = waiters
            waiters = []
            for w in waiting { w.resume() }
        }

        func wait() async {
            if isOpen { return }
            await withCheckedContinuation { waiters.append($0) }
        }
    }

    func testStreamingEmptyTextEmitsNothingAndFinishes() async throws {
        let e = PhrasedTtsEngine(inner: RecordingTts())
        var chunks = 0
        for try await _ in e.streamSynthesise(text: "") { chunks += 1 }
        XCTAssertEqual(chunks, 0)
    }

    // MARK: - Diagnostics

    func testDiagnosticsAccumulateAcrossTheWholePassage() async throws {
        // Reading only the last segment's would report a clean render for a
        // paragraph whose FIRST sentence lost every 'š' in it.
        let inner = RecordingTts()
        inner.lastSkippedCount = 2
        inner.lastSkippedSymbols = ["š"]
        inner.lastApproximatedSymbols = ["ṱ"]

        let e = PhrasedTtsEngine(inner: inner)
        _ = try await e.synthesise(text: "One. Two. Three.")

        XCTAssertEqual(e.lastSkippedCount, 6)
        XCTAssertEqual(e.lastSkippedSymbols, ["š"], "deduplicated, not repeated per sentence")
        XCTAssertEqual(e.lastApproximatedSymbols, ["ṱ"])
    }

    func testDiagnosticsResetBetweenPassages() async throws {
        let inner = RecordingTts()
        inner.lastSkippedCount = 1
        let e = PhrasedTtsEngine(inner: inner)
        _ = try await e.synthesise(text: "One. Two.")
        XCTAssertEqual(e.lastSkippedCount, 2)
        _ = try await e.synthesise(text: "Three. Four.")
        XCTAssertEqual(e.lastSkippedCount, 2, "not 4")
    }

    func testAnInnerEngineWithNoDiagnosticsIsNotAnError() async throws {
        let e = PhrasedTtsEngine(inner: NullTtsEngine())
        _ = try await e.synthesise(text: "One. Two.")
        XCTAssertEqual(e.lastSkippedCount, 0)
        XCTAssertTrue(e.lastSkippedSymbols.isEmpty)
    }

    // MARK: - Respelling

    func testTheRespellerRewritesBeforeTheEngineEverSeesTheText() async throws {
        let inner = RecordingTts()
        let e = RespellingTtsEngine(inner: inner,
                                    respeller: Respeller(hostLanguage: "zu"))
        // "SMS", not "internet": the splitter deliberately does not flag ordinary
        // lowercase English, because mispronouncing a native word to "fix" a
        // foreign one insults the speaker in their own language.
        _ = try await e.synthesise(text: "SMS")
        XCTAssertEqual(inner.asked, ["esemese"])
    }

    func testALanguageTheseSpellingsWereNeverWrittenForIsLeftCompletelyAlone() async throws {
        // Afrikaans has its own forms for these words; "S.M.S." is our idea of
        // helpful imposed on a language that did not ask for it.
        let inner = RecordingTts()
        let e = RespellingTtsEngine(inner: inner,
                                    respeller: Respeller(hostLanguage: "af"))
        _ = try await e.synthesise(text: "SMS")
        XCTAssertEqual(inner.asked, ["SMS"])
    }

    func testRewriteSaysWhatItChanged() {
        // A respelling that fires silently is indistinguishable from one that
        // never ran, and both sound like a voice that cannot say the word.
        let logged = LogBox()
        let r = Respeller(hostLanguage: "zu", log: { logged.add($0) })
        _ = r.rewrite("SMS")
        XCTAssertTrue(logged.lines.contains { $0.contains("SMS") })
    }

    func testRewriteOfNothingIsNothing() {
        let r = Respeller(hostLanguage: "zu")
        XCTAssertEqual(r.rewrite(nil), "")
        XCTAssertEqual(r.rewrite(""), "")
        XCTAssertEqual(r.rewrite("   "), "   ")
    }

    func testAPersonalCorrectionReachesTheEngine() async throws {
        let personal = PersonalRespellings()
        personal.learn(word: "SMS", respelling: "MY-WAY")
        let inner = RecordingTts()
        let e = RespellingTtsEngine(
            inner: inner,
            respeller: Respeller(hostLanguage: "zu", personal: personal))
        _ = try await e.synthesise(text: "SMS")
        XCTAssertEqual(inner.asked, ["MY-WAY"])
    }

    func testStreamingIsRespeltToo() async throws {
        // The live conversation streams. A decorator that only rewrites the
        // single-shot path improves nothing anybody actually hears.
        let inner = RecordingTts()
        let e = RespellingTtsEngine(inner: inner,
                                    respeller: Respeller(hostLanguage: "zu"))
        for try await _ in e.streamSynthesise(text: "SMS") {}
        XCTAssertEqual(inner.asked, ["esemese"])
    }
}

/// A tiny sink so the log closure can stay `@Sendable`.
private final class LogBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func add(_ s: String) { lock.lock(); stored.append(s); lock.unlock() }
    var lines: [String] { lock.lock(); defer { lock.unlock() }; return stored }
}
