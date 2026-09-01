// VoiceConfirmedSpotterTests.swift
//
// The measured problem these tests stand in for: 12 of 12 genuine wakes heard,
// and 21 false accepts across 30 clips of ordinary speech — every one of them a
// sentence with the word INSIDE it. So the tests are about whether stage two
// separates "Circle, what's the weather" from "let us circle back", not about
// whether the model scores well.

import XCTest
@testable import CircleAI

/// A stage one that fires exactly when told to. Standing in for the zipformer
/// so the two-stage policy can be tested without onnxruntime.
private final class ScriptedSpotter: IKeywordSpotter {
    var keywords: [String] = ["circle"]
    var shadowedKeywords: [(phrase: String, shadowedBy: String)] = []
    var onDetected: ((KwsDetection) -> Void)?

    private(set) var accepted = 0
    private(set) var flushed = 0
    private(set) var resets = 0

    /// Detections to emit on the next acceptWaveform, in order.
    var script: [KwsDetection] = []

    func acceptWaveform(_ samples: [Float]) {
        accepted += samples.count
        let batch = script
        script = []
        for d in batch { onDetected?(d) }
    }

    func flush() {
        flushed += 1
        let batch = script
        script = []
        for d in batch { onDetected?(d) }
    }

    func reset() {
        resets += 1
        script = []
    }
}

private final class RecordingConfirmer: IWakeConfirmer, @unchecked Sendable {
    let verdict: Bool
    let reason: String?
    private(set) var seen: [WakeCandidate] = []

    init(verdict: Bool, reason: String? = nil) {
        self.verdict = verdict
        self.reason = reason
    }

    var lastReason: String? { verdict ? nil : reason }

    func confirm(_ candidate: WakeCandidate) async -> Bool {
        seen.append(candidate)
        return verdict
    }
}

final class VoiceConfirmedSpotterTests: XCTestCase {

    /// One second of audio at 16 kHz. Loud from `speechFromMs` onward so the
    /// onset confirmer has something real to measure.
    private func audio(speechFromMs: Double, lengthMs: Double = 1000) -> [Float] {
        let n = Int(lengthMs * 16)
        let from = Int(speechFromMs * 16)
        return (0..<n).map { i in
            i >= from ? sinf(Float(i) * 0.2) * 0.5 : 0
        }
    }

    private func detection(_ phrase: String, startMs: Double, endMs: Double) -> KwsDetection {
        // KwsDetection counts in 40 ms frames.
        KwsDetection(phrase: phrase,
                     atFrame: Int(endMs / KwsDetection.msPerFrame),
                     probability: 0.802,
                     startFrame: Int(startMs / KwsDetection.msPerFrame))
    }

    // MARK: - The point of the whole file

    func testAPhraseAtTheStartOfSpeechWakes() async {
        // "Circle, what's the weather" — nothing before it.
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(spotter: stage1)

        var woke: [KwsDetection] = []
        spotter.onWoke = { woke.append($0) }

        stage1.script = [detection("circle", startMs: 40, endMs: 440)]
        await spotter.acceptWaveform(audio(speechFromMs: 40))

        XCTAssertEqual(woke.count, 1)
        XCTAssertEqual(woke.first?.phrase, "circle")
    }

    func testAPhraseWithHalfASentenceInFrontOfItIsVetoed() async {
        // "let us circle back" — the score is 0.802, HIGHER than most genuine
        // wakes, which is exactly why no threshold can separate these.
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(spotter: stage1)

        var woke = 0
        var rejected: [(KwsDetection, String?)] = []
        spotter.onWoke = { _ in woke += 1 }
        spotter.onRejected = { rejected.append(($0, $1)) }

        // Talking from 0 ms; the phrase only ends at 900 ms.
        stage1.script = [detection("circle", startMs: 600, endMs: 900)]
        await spotter.acceptWaveform(audio(speechFromMs: 0))

        XCTAssertEqual(woke, 0)
        XCTAssertEqual(rejected.count, 1)
        XCTAssertNotNil(rejected.first?.1, "a veto must say why")
    }

    // MARK: - Wiring

    func testTheDefaultSecondStageIsTheOnsetConfirmer() async {
        // Constructing this WITHOUT a second stage must not silently give back
        // the generous stage one on its own.
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(spotter: stage1)

        var rejected = 0
        spotter.onRejected = { _, _ in rejected += 1 }
        spotter.onWoke = { _ in }

        stage1.script = [detection("circle", startMs: 600, endMs: 900)]
        await spotter.acceptWaveform(audio(speechFromMs: 0))
        XCTAssertEqual(rejected, 1)
    }

    func testTheConfirmerSeesTheAudioAroundTheDetectionNotJustBeforeIt() async {
        // The detection arrives mid-decode and stage two wants what came after
        // as well; judging inside the callback would look only backwards.
        let stage1 = ScriptedSpotter()
        let confirmer = RecordingConfirmer(verdict: true)
        let spotter = ConfirmedKeywordSpotter(spotter: stage1, confirmer: confirmer)
        spotter.onWoke = { _ in }

        stage1.script = [detection("circle", startMs: 200, endMs: 400)]
        await spotter.acceptWaveform(audio(speechFromMs: 0))

        XCTAssertEqual(confirmer.seen.count, 1)
        let c = confirmer.seen[0]
        XCTAssertEqual(c.window.count, 16_000)
        XCTAssertEqual(c.keywordStart, 200 * 16)
        XCTAssertEqual(c.keywordEnd, 400 * 16)
        XCTAssertLessThan(c.keywordEnd, c.window.count, "audio after the phrase is included")
    }

    func testAVetoReportsTheConfirmersOwnReason() async {
        // "it never fires" and "it fires and is vetoed every time" are completely
        // different problems and look identical from outside.
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(
            spotter: stage1,
            confirmer: RecordingConfirmer(verdict: false, reason: "had been speaking"))

        var reason: String?
        spotter.onRejected = { reason = $1 }
        spotter.onWoke = { _ in }

        stage1.script = [detection("circle", startMs: 0, endMs: 400)]
        await spotter.acceptWaveform(audio(speechFromMs: 0))
        XCTAssertEqual(reason, "had been speaking")
    }

    func testADetectionThatHasScrolledOutOfTheRingIsLetThroughNotDropped() async {
        // Only possible if a caller pushes seconds at a time. There is nothing
        // left to judge, and silently dropping a wake is the worse failure.
        let stage1 = ScriptedSpotter()
        let confirmer = RecordingConfirmer(verdict: false, reason: "should never be asked")
        let spotter = ConfirmedKeywordSpotter(
            spotter: stage1, confirmer: confirmer, historySeconds: 0.5)

        var woke = 0
        spotter.onWoke = { _ in woke += 1 }
        spotter.onRejected = { _, _ in XCTFail("must not be vetoed") }

        // 3 s of audio into a 0.5 s ring; the phrase at 100 ms is long gone.
        stage1.script = [detection("circle", startMs: 100, endMs: 300)]
        await spotter.acceptWaveform(audio(speechFromMs: 0, lengthMs: 3000))

        XCTAssertEqual(woke, 1)
        XCTAssertTrue(confirmer.seen.isEmpty)
    }

    func testFlushDrainsWhateverIsStillPending() async {
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(
            spotter: stage1, confirmer: RecordingConfirmer(verdict: true))

        var woke = 0
        spotter.onWoke = { _ in woke += 1 }

        await spotter.acceptWaveform(audio(speechFromMs: 0))
        stage1.script = [detection("circle", startMs: 0, endMs: 400)]
        await spotter.flush()

        XCTAssertEqual(stage1.flushed, 1)
        XCTAssertEqual(woke, 1)
    }

    func testResetClearsTheRingAndTheStageOne() async {
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(
            spotter: stage1, confirmer: RecordingConfirmer(verdict: true))
        spotter.onWoke = { _ in }

        await spotter.acceptWaveform(audio(speechFromMs: 0))
        spotter.reset()
        XCTAssertEqual(stage1.resets, 1)

        // After a reset the ring is empty, so a detection has nothing behind it
        // and is let through rather than judged against stale audio.
        var woke = 0
        spotter.onWoke = { _ in woke += 1 }
        stage1.script = [detection("circle", startMs: 0, endMs: 400)]
        await spotter.flush()
        XCTAssertEqual(woke, 1)
    }

    func testEveryPendingDetectionIsJudgedNotJustTheFirst() async {
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(
            spotter: stage1, confirmer: RecordingConfirmer(verdict: true))

        var woke = 0
        spotter.onWoke = { _ in woke += 1 }

        stage1.script = [
            detection("circle", startMs: 40, endMs: 200),
            detection("circle", startMs: 400, endMs: 600),
        ]
        await spotter.acceptWaveform(audio(speechFromMs: 0))
        XCTAssertEqual(woke, 2)
    }

    func testKeywordsAndShadowingComeStraightFromStageOne() {
        // Shadowed phrases are reported rather than silently dropped: somebody
        // typed that phrase in and deserves to be told it will never work.
        let stage1 = ScriptedSpotter()
        stage1.keywords = ["circle", "circle back"]
        stage1.shadowedKeywords = [(phrase: "circle back", shadowedBy: "circle")]

        let spotter = ConfirmedKeywordSpotter(spotter: stage1)
        XCTAssertEqual(spotter.keywords, ["circle", "circle back"])
        XCTAssertEqual(spotter.shadowedKeywords.count, 1)
        XCTAssertEqual(spotter.shadowedKeywords[0].shadowedBy, "circle")
    }

    func testAudioIsForwardedToStageOneUntouched() async {
        let stage1 = ScriptedSpotter()
        let spotter = ConfirmedKeywordSpotter(spotter: stage1)
        spotter.onWoke = { _ in }

        await spotter.acceptWaveform([Float](repeating: 0.1, count: 320))
        await spotter.acceptWaveform([Float](repeating: 0.1, count: 320))
        XCTAssertEqual(stage1.accepted, 640)
    }
}
