// SpeechAudioProcessingTests.swift
//
// Verifies echo cancellers, noise reducers, frame VAD, and end-of-turn
// detectors (SpeechAudioProcessing.swift). Algorithms are checked against the
// C# behaviour: pass-through nulls, NLMS echo suppression, spectral-subtraction
// gating, RMS+ZCR+hangover VAD, and punctuation/hanging/max-silence rules.

import XCTest
@testable import CircleAI

final class SpeechAudioProcessingTests: XCTestCase {

    // ── PCM helpers ──────────────────────────────────────────────────────
    private func pcm(_ samples: [Int16]) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count { writeInt16LE(&b, i * 2, samples[i]) }
        return b
    }
    private func samples(_ bytes: [UInt8]) -> [Int16] {
        (0..<(bytes.count / 2)).map { readInt16LE(bytes, $0 * 2) }
    }
    private func sine(_ n: Int, amp: Double, period: Int) -> [Int16] {
        (0..<n).map { Int16(amp * sin(2 * Double.pi * Double($0) / Double(period))) }
    }

    // ── Echo cancellers ──────────────────────────────────────────────────

    func testNullEchoCancellerCopies() {
        let ec = NullEchoCanceller.instance
        XCTAssertEqual(ec.backendId, "null")
        let mic = pcm([100, -200, 300])
        let far = pcm([1, 2, 3])
        XCTAssertEqual(ec.cancel(nearEndMicrophone: mic, farEndReference: far, sampleRateHz: 16_000), mic)
    }

    func testNlmsReducesPureEcho() {
        // Near-end is entirely echo of far-end (no local speech). NLMS should
        // adapt and drive the residual below the input magnitude over time.
        let ec = NlmsEchoCanceller()
        XCTAssertEqual(ec.backendId, "nlms")
        let tone = sine(2000, amp: 8000, period: 32)
        let mic = pcm(tone)
        let far = pcm(tone)
        let out = samples(ec.cancel(nearEndMicrophone: mic, farEndReference: far, sampleRateHz: 16_000))

        func energy(_ arr: ArraySlice<Int16>) -> Double {
            arr.reduce(0.0) { $0 + Double($1) * Double($1) } / Double(arr.count)
        }
        let tailIn = energy(tone[1500...])
        let tailOut = energy(out[1500...])
        XCTAssertLessThan(tailOut, tailIn * 0.5, "adaptive filter should suppress steady echo tail")
    }

    func testNlmsResetClearsState() {
        let ec = NlmsEchoCanceller()
        let tone = pcm(sine(512, amp: 6000, period: 16))
        _ = ec.cancel(nearEndMicrophone: tone, farEndReference: tone, sampleRateHz: 16_000)
        ec.reset()
        // After reset the first output sample equals mic - 0 (weights zeroed) clamped.
        let out = samples(ec.cancel(nearEndMicrophone: tone, farEndReference: tone, sampleRateHz: 16_000))
        XCTAssertEqual(out.first, samples(tone).first, "first post-reset sample: weights are zero so residual == mic")
    }

    func testWebRtcEchoCancellerFallsBackWithoutRunner() {
        let ec = WebRtcEchoCanceller()
        XCTAssertEqual(ec.backendId, "webrtc-aec3 (fallback)")
        let mic = pcm([10, 20, 30, 40])
        _ = ec.cancel(nearEndMicrophone: mic, farEndReference: mic, sampleRateHz: 16_000)  // does not crash
    }

    func testWebRtcEchoCancellerUsesRunnerWhenPresent() {
        final class Runner: IEchoCancellerModelRunner {
            func process(nearEnd: [UInt8], farEnd: [UInt8], sampleRateHz: Int) -> [UInt8] {
                [UInt8](repeating: 0, count: nearEnd.count)  // silence
            }
            func reset() {}
        }
        let ec = WebRtcEchoCanceller(runner: Runner())
        XCTAssertEqual(ec.backendId, "webrtc-aec3")
        let mic = pcm([500, -500, 500, -500])
        XCTAssertEqual(ec.cancel(nearEndMicrophone: mic, farEndReference: mic, sampleRateHz: 16_000),
                       [UInt8](repeating: 0, count: mic.count))
    }

    // ── Noise reducers ───────────────────────────────────────────────────

    func testNullNoiseReducerCopies() {
        let nr = NullNoiseReducer.instance
        XCTAssertEqual(nr.backendId, "null")
        XCTAssertTrue(nr.isAvailable)
        let audio = pcm([1000, -1000, 500])
        XCTAssertEqual(nr.reduce(audioPcm16Mono: audio, sampleRateHz: 16_000), audio)
    }

    func testSpectralSubtractionAttenuatesBelowFloorPassesAbove() {
        // floor = 0.008 * 32767 = 262 (truncated). Samples with |s| <= 262 are
        // scaled by 0.25; louder samples pass unchanged.
        let nr = SpectralSubtractionNoiseReducer()
        XCTAssertEqual(nr.backendId, "passthrough")
        let input: [Int16] = [100, 5000, -200, -9000, 262, 263]
        let out = samples(nr.reduce(audioPcm16Mono: pcm(input), sampleRateHz: 16_000))
        XCTAssertEqual(out[0], Int16(Float(100) * 0.25))   // 25
        XCTAssertEqual(out[1], 5000)                        // above floor
        XCTAssertEqual(out[2], Int16(Float(-200) * 0.25))  // -50
        XCTAssertEqual(out[3], -9000)                       // above floor
        XCTAssertEqual(out[4], Int16(Float(262) * 0.25))   // <= floor -> attenuated
        XCTAssertEqual(out[5], 263)                         // > floor -> unchanged
    }

    func testKrispAndDeepFilterNetFallbackBackendIds() {
        XCTAssertEqual(KrispNoiseReducer().backendId, "krisp (fallback)")
        XCTAssertEqual(DeepFilterNetNoiseReducer().backendId, "deepfilternet (fallback)")
    }

    func testKrispUsesRunnerWhenPresent() {
        final class Runner: INoiseReducerModelRunner {
            func process(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8] {
                [UInt8](repeating: 0xAB, count: audioPcm16Mono.count)
            }
        }
        let nr = KrispNoiseReducer(runner: Runner())
        XCTAssertEqual(nr.backendId, "krisp")
        XCTAssertEqual(nr.reduce(audioPcm16Mono: pcm([1, 2, 3]), sampleRateHz: 16_000),
                       [UInt8](repeating: 0xAB, count: 6))
    }

    // ── Frame VAD ────────────────────────────────────────────────────────

    func testNullFrameVadAlwaysSpeech() {
        let vad = NullFrameVoiceActivityDetector.instance
        XCTAssertEqual(vad.backendId, "null")
        XCTAssertEqual(vad.speechThreshold, 0.5)
        let r = vad.classify(audioPcm16Mono: pcm([0, 0, 0]), sampleRateHz: 16_000, offset: 2.0)
        XCTAssertTrue(r.isSpeech)
        XCTAssertEqual(r.speechProbability, 1)
        XCTAssertEqual(r.offset, 2.0)
    }

    func testEnergyVadSpeechVsSilence() {
        let vad = EnergyVoiceActivityDetector()
        XCTAssertEqual(vad.backendId, "energy")
        // Silence: all zeros -> low RMS -> not speech.
        let silence = vad.classify(audioPcm16Mono: pcm([Int16](repeating: 0, count: 320)), sampleRateHz: 16_000, offset: 0)
        XCTAssertFalse(silence.isSpeech)

        // Voiced speech: loud tone with moderate ZCR.
        let tone = sine(320, amp: 12000, period: 20) // ZCR ~ 2/period range -> voiced
        let speech = vad.classify(audioPcm16Mono: pcm(tone), sampleRateHz: 16_000, offset: 0.1)
        XCTAssertTrue(speech.isSpeech)
        XCTAssertGreaterThanOrEqual(speech.speechProbability, vad.speechThreshold)
        XCTAssertEqual(speech.offset, 0.1)
    }

    func testEnergyVadHangoverKeepsSpeechThenReset() {
        let vad = EnergyVoiceActivityDetector(speechThreshold: 0.55, energyThreshold: 0.012, hangoverFrames: 3)
        let tone = pcm(sine(320, amp: 12000, period: 20))
        let silence = pcm([Int16](repeating: 0, count: 320))
        _ = vad.classify(audioPcm16Mono: tone, sampleRateHz: 16_000, offset: 0)  // sets hangover=3
        // Next silence frames stay "speech" for hangover frames.
        XCTAssertTrue(vad.classify(audioPcm16Mono: silence, sampleRateHz: 16_000, offset: 0).isSpeech)
        XCTAssertTrue(vad.classify(audioPcm16Mono: silence, sampleRateHz: 16_000, offset: 0).isSpeech)
        XCTAssertTrue(vad.classify(audioPcm16Mono: silence, sampleRateHz: 16_000, offset: 0).isSpeech)
        // Hangover exhausted -> silence.
        XCTAssertFalse(vad.classify(audioPcm16Mono: silence, sampleRateHz: 16_000, offset: 0).isSpeech)

        vad.reset()
        // After reset, immediate silence is not speech (no lingering hangover).
        XCTAssertFalse(vad.classify(audioPcm16Mono: silence, sampleRateHz: 16_000, offset: 0).isSpeech)
    }

    func testSileroVadFallsBackWithoutRunner() {
        let vad = SileroVoiceActivityDetector()
        XCTAssertEqual(vad.backendId, "silero (fallback)")
        let silence = vad.classify(audioPcm16Mono: pcm([Int16](repeating: 0, count: 320)), sampleRateHz: 16_000, offset: 0)
        XCTAssertFalse(silence.isSpeech)
    }

    func testSileroVadUsesRunnerAndHangover() {
        final class Runner: IVadModelRunner {
            var next: Float = 0
            func scoreFrame(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> Float { next }
        }
        let runner = Runner()
        let vad = SileroVoiceActivityDetector(runner: runner, speechThreshold: 0.5, hangoverFrames: 2)
        XCTAssertEqual(vad.backendId, "silero")
        runner.next = 0.9
        XCTAssertTrue(vad.classify(audioPcm16Mono: pcm([0]), sampleRateHz: 16_000, offset: 0).isSpeech)
        runner.next = 0.1
        XCTAssertTrue(vad.classify(audioPcm16Mono: pcm([0]), sampleRateHz: 16_000, offset: 0).isSpeech)  // hangover 2
        XCTAssertTrue(vad.classify(audioPcm16Mono: pcm([0]), sampleRateHz: 16_000, offset: 0).isSpeech)  // hangover 1
        XCTAssertFalse(vad.classify(audioPcm16Mono: pcm([0]), sampleRateHz: 16_000, offset: 0).isSpeech) // exhausted
    }

    // ── End-of-turn ──────────────────────────────────────────────────────

    func testNullEndOfTurnAlwaysComplete() {
        let d = NullEndOfTurnDetector.instance
        XCTAssertEqual(d.backendId, "null")
        let r = d.predict(partialTranscript: "anything", trailingSilence: 0)
        XCTAssertTrue(r.isComplete)
        XCTAssertEqual(r.confidence, 1)
        XCTAssertEqual(r.waitMoreMs, 0)
    }

    func testRuleBasedTerminalPunctuationCompletes() {
        let d = RuleBasedEndOfTurnDetector()
        XCTAssertEqual(d.backendId, "rules")
        let r = d.predict(partialTranscript: "What time is it?", trailingSilence: 0.5) // >= minSilence 0.4
        XCTAssertTrue(r.isComplete)
        XCTAssertEqual(r.confidence, 0.9)
    }

    func testRuleBasedHangingWordExtendsWait() {
        let d = RuleBasedEndOfTurnDetector()
        // ends with "and" -> hanging; silence 0.2s < hangingSilence 0.9s -> not complete.
        let r = d.predict(partialTranscript: "I want pizza and", trailingSilence: 0.2)
        XCTAssertFalse(r.isComplete)
        XCTAssertEqual(r.confidence, 0.4)
        XCTAssertEqual(r.waitMoreMs, Int(ceil((0.9 - 0.2) * 1000)))  // 700
    }

    func testRuleBasedMaxSilenceForcesComplete() {
        let d = RuleBasedEndOfTurnDetector()
        let r = d.predict(partialTranscript: "um", trailingSilence: 3.0) // >= maxSilence 2.5
        XCTAssertTrue(r.isComplete)
        XCTAssertEqual(r.confidence, 0.7)
    }

    func testRuleBasedEmptyTranscriptWaits() {
        let d = RuleBasedEndOfTurnDetector()
        let r = d.predict(partialTranscript: "   ", trailingSilence: 0.1)
        XCTAssertFalse(r.isComplete)
        XCTAssertEqual(r.confidence, 0.2)
        XCTAssertEqual(r.waitMoreMs, Int(max(150.0, (0.4 - 0.1) * 1000)))  // 300
    }

    func testSmartTurnFallbackAndRunner() {
        XCTAssertEqual(SmartTurnDetector().backendId, "smart-turn (fallback)")
        final class Runner: ITurnModelRunner {
            var score: Float = 0
            func scoreCompletion(partialTranscript: String, trailingSilence: TimeInterval) -> Float { score }
        }
        let runner = Runner()
        let d = SmartTurnDetector(runner: runner, threshold: 0.5)
        XCTAssertEqual(d.backendId, "smart-turn-v2")
        runner.score = 0.8
        let complete = d.predict(partialTranscript: "done", trailingSilence: 0)
        XCTAssertTrue(complete.isComplete)
        XCTAssertEqual(complete.confidence, 0.8)
        runner.score = 0.25
        let incomplete = d.predict(partialTranscript: "wait", trailingSilence: 0)
        XCTAssertFalse(incomplete.isComplete)
        XCTAssertEqual(incomplete.waitMoreMs, Int((Double(1 - 0.25) * 1000).rounded(.toNearestOrEven)))  // 750
    }
}
