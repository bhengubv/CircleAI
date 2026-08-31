import XCTest
@testable import CircleAI

/// The confirmers. Most of this file is about the two ways a wake word goes
/// wrong: firing mid-sentence, and refusing to fire at all.
final class VoiceWakeConfirmTests: XCTestCase {

    private let rate = 16_000

    /// A window of `seconds` where `speechFrom` onwards is loud.
    private func window(seconds: Double, speechFrom: Double) -> [Float] {
        let n = Int(seconds * Double(rate))
        let start = Int(speechFrom * Double(rate))
        return (0..<n).map { i in
            guard i >= start else { return 0 }
            // A steady tone: what matters is the RMS envelope, not the shape.
            return i % 2 == 0 ? 0.5 : -0.5
        }
    }

    private func candidate(_ w: [Float], phrase: String = "hey b") -> WakeCandidate {
        WakeCandidate(detection: KwsDetection(phrase: phrase, atFrame: 10, probability: 0.9),
                      window: w, keywordStart: max(0, w.count - rate / 2), keywordEnd: w.count - 1)
    }

    // MARK: - Frames

    func testAFrameIsFortyMilliseconds() {
        let d = KwsDetection(phrase: "x", atFrame: 25, probability: 0.5)
        XCTAssertEqual(d.endMs, 1000)
        // With no start frame, the start falls back to the end.
        XCTAssertEqual(d.startMs, 1000)
        XCTAssertEqual(KwsDetection(phrase: "x", atFrame: 25, probability: 0.5, startFrame: 10).startMs, 400)
    }

    // MARK: - Onset

    // Somebody addressing a device pauses first.
    func testAPhraseAfterASilenceIsConfirmed() async {
        let c = candidate(window(seconds: 2.0, speechFrom: 1.7))
        let ok = await UtteranceOnsetConfirmer().confirm(c)
        XCTAssertTrue(ok)
    }

    // The same words INSIDE a running sentence are almost always about the
    // device, not to it.
    func testAPhraseInTheMiddleOfAnUtteranceIsRejected() async {
        let c = candidate(window(seconds: 2.0, speechFrom: 0.0))
        let confirmer = UtteranceOnsetConfirmer()
        let ok = await confirmer.confirm(c)
        XCTAssertFalse(ok)
        XCTAssertTrue(confirmer.lastReason!.contains("had been speaking"))
    }

    func testTheLeadInAllowanceIsConfigurable() async {
        let c = candidate(window(seconds: 2.0, speechFrom: 0.0))
        let lenient = UtteranceOnsetConfirmer(maxLeadInMs: 5000)
        let v0 = await lenient.confirm(c)
        XCTAssertTrue(v0)
        XCTAssertNil(lenient.lastReason)
    }

    func testSilenceIsRejectedAndNamed() async {
        let confirmer = UtteranceOnsetConfirmer()
        let ok = await confirmer.confirm(candidate([Float](repeating: 0, count: rate)))
        XCTAssertFalse(ok)
        XCTAssertEqual(confirmer.lastReason, "silence")
    }

    // A confirmer that rejects when it cannot see is a device that stops
    // answering.
    func testTooLittleAudioFailsOpen() async {
        let confirmer = UtteranceOnsetConfirmer()
        let v1 = await confirmer.confirm(candidate([]))
        XCTAssertTrue(v1)
        let v2 = await confirmer.confirm(candidate([Float](repeating: 0.5, count: 100)))
        XCTAssertTrue(v2)
        XCTAssertNil(confirmer.lastReason)
    }

    // MARK: - Always

    func testAlwaysConfirmConfirms() async {
        let c = AlwaysConfirm()
        let v3 = await c.confirm(candidate([]))
        XCTAssertTrue(v3)
        XCTAssertNil(c.lastReason)
    }

    // MARK: - Transcript

    func testAPhraseAtTheStartOfTheTranscriptIsConfirmed() async {
        let c = TranscriptConfirmer(transcribe: { _ in "Circle wake, what is the time?" })
        let ok = await c.confirm(candidate([0.1, 0.2], phrase: "circle wake"))
        XCTAssertTrue(ok)
        XCTAssertNil(c.lastReason)
    }

    // A DEFECT IN THE C#, PORTED FAITHFULLY RATHER THAN DIVERGED.
    //
    // The lead-in skip is greedy, and "hey" is BOTH an allowed filler and the
    // first word of the shipped default phrase "Hey B". The skip eats it, the
    // comparison then starts at "b", and the match fails - so this confirmer
    // can never confirm the product default. Pinned here so the behaviour is
    // known, and so a fix lands in BOTH language bases at once rather than
    // silently in one.
    func testGreedyLeadInSkipEatsAPhraseThatStartsWithAFiller() async {
        let c = TranscriptConfirmer(transcribe: { _ in "Hey B, what is the time?" })
        let ok = await c.confirm(candidate([0.1], phrase: "hey b"))
        XCTAssertFalse(ok, "documents the defect; flip this when the C# is fixed too")
        XCTAssertTrue(c.lastReason!.contains("not how it starts"))
    }

    // Fillers a person really does say before addressing a device.
    func testLeadInFillersAreAllowedBeforeThePhrase() async {
        let c = TranscriptConfirmer(transcribe: { _ in "um, uh, circle wake, play music" })
        let ok = await c.confirm(candidate([0.1], phrase: "circle wake"))
        XCTAssertTrue(ok)
    }

    func testAPhraseBuriedMidSentenceIsRejected() async {
        let c = TranscriptConfirmer(transcribe: { _ in "I was telling her about circle wake yesterday" })
        let ok = await c.confirm(candidate([0.1], phrase: "circle wake"))
        XCTAssertFalse(ok)
        XCTAssertTrue(c.lastReason!.contains("not how it starts"))
    }

    func testPunctuationAndCasingDoNotBreakTheMatch() async {
        let c = TranscriptConfirmer(transcribe: { _ in "CIRCLE, WAKE! ... what now" })
        let ok = await c.confirm(candidate([0.1], phrase: "circle wake"))
        XCTAssertTrue(ok)
    }

    func testAnEmptyTranscriptFailsOpen() async {
        let c = TranscriptConfirmer(transcribe: { _ in "" })
        let v7 = await c.confirm(candidate([0.1], phrase: "hey b"))
        XCTAssertTrue(v7)
        XCTAssertNil(c.lastReason)
    }

    // A confirmer that is unavailable must not silence the device.
    func testATranscriberThatThrowsFailsOpenAndSaysSo() async {
        struct Boom: Error {}
        let c = TranscriptConfirmer(transcribe: { _ in throw Boom() })
        let v8 = await c.confirm(candidate([0.1], phrase: "hey b"))
        XCTAssertTrue(v8)
        XCTAssertTrue(c.lastReason!.contains("unavailable"))
    }

    func testPcm16IsLittleEndian() {
        let pcm = TranscriptConfirmer.toPcm16([1.0, -1.0, 0.0])
        XCTAssertEqual(pcm.count, 6)
        XCTAssertEqual([pcm[0], pcm[1]], [0xFF, 0x7F])   // +32767
        XCTAssertEqual([pcm[2], pcm[3]], [0x01, 0x80])   // -32767
        XCTAssertEqual([pcm[4], pcm[5]], [0x00, 0x00])
    }

    func testPcm16ClampsRatherThanWrapping() {
        let pcm = TranscriptConfirmer.toPcm16([9.0, -9.0])
        XCTAssertEqual([pcm[0], pcm[1]], [0xFF, 0x7F])
        XCTAssertEqual([pcm[2], pcm[3]], [0x00, 0x80])
    }

    // MARK: - Either

    // The name says either, but the C# requires BOTH. The cheap check runs
    // first so the expensive one is not paid for on candidates it can reject
    // outright.
    func testEitherConfirmerRequiresBothToAgree() async {
        let strict = TranscriptConfirmer(transcribe: { _ in "nothing like it" })
        let either = EitherConfirmer(AlwaysConfirm(), strict)
        let ok = await either.confirm(candidate([0.1], phrase: "circle wake"))
        XCTAssertFalse(ok)
        XCTAssertTrue(either.lastReason!.contains("not how it starts"))
    }

    func testEitherConfirmerConfirmsWhenBothDo() async {
        let lenient = TranscriptConfirmer(transcribe: { _ in "circle wake now" })
        let either = EitherConfirmer(AlwaysConfirm(), lenient)
        let ok = await either.confirm(candidate([0.1], phrase: "circle wake"))
        XCTAssertTrue(ok)
        XCTAssertNil(either.lastReason)
    }

    // The CHEAP one short-circuits: when it rejects, the precise one is never
    // reached, and the reason reported is the cheap one.
    func testACheapRejectionShortCircuitsTheExpensiveCheck() async {
        let expensive = CountingConfirmer()
        let onset = UtteranceOnsetConfirmer()
        let either = EitherConfirmer(onset, expensive)

        // Silence: the onset confirmer rejects outright.
        let ok = await either.confirm(candidate([Float](repeating: 0, count: rate)))
        XCTAssertFalse(ok)
        XCTAssertEqual(either.lastReason, "silence")
        XCTAssertEqual(expensive.calls, 0, "the expensive confirmer must not be reached")
    }

    private final class CountingConfirmer: IWakeConfirmer, @unchecked Sendable {
        private let lock = NSLock()
        private var count = 0
        var calls: Int { lock.lock(); defer { lock.unlock() }; return count }
        var lastReason: String? { nil }
        func confirm(_ candidate: WakeCandidate) async -> Bool {
            lock.lock(); count += 1; lock.unlock()
            return true
        }
    }
}
