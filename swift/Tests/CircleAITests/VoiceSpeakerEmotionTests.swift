// VoiceSpeakerEmotionTests.swift
//
// Verifies SpeakerIdentityService, SpeechEmotionService, and KwsWakeWordDetector
// (VoiceSpeakerEmotion.swift). The injected-runner seams are driven directly for
// full determinism; the no-runner DSP fallback is checked for structural
// behaviour (enroll/identify round-trip, sample-rate + min-duration guards,
// circumplex mapping, softmax argmax).

import XCTest
@testable import CircleAI

final class VoiceSpeakerEmotionTests: XCTestCase {

    private func pcm(_ samples: [Int16]) -> Data {
        var b = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count { writeInt16LE(&b, i * 2, samples[i]) }
        return Data(b)
    }
    private func tone(_ n: Int, amp: Double, period: Int) -> Data {
        pcm((0..<n).map { Int16(amp * sin(2 * Double.pi * Double($0) / Double(period))) })
    }

    // ── SpeakerIdentityService ───────────────────────────────────────────

    func testEnrollThenIdentifySingleSpeakerFallback() async throws {
        // Single enrolled speaker + identical audio => cosine sim 1.0 >= 0.55.
        let svc = SpeakerIdentityService()
        let audio = tone(20_000, amp: 8000, period: 40) // 1.25s @ 16k (> min 1s)
        try await svc.enroll(userId: "alice", audioPcm16: audio, sampleRateHz: 16_000)
        let id = try await svc.identify(audioPcm16: audio, sampleRateHz: 16_000)
        XCTAssertEqual(id, "alice")
        await svc.dispose()
    }

    func testEnrollAveragingIncrementsSampleCount() async throws {
        let svc = SpeakerIdentityService()
        let a = tone(20_000, amp: 8000, period: 40)
        let b = tone(20_000, amp: 6000, period: 50)
        try await svc.enroll(userId: "bob", audioPcm16: a, sampleRateHz: 16_000)
        try await svc.enroll(userId: "bob", audioPcm16: b, sampleRateHz: 16_000)
        let recs = svc.enrolledSpeakers
        XCTAssertEqual(recs.count, 1)
        XCTAssertEqual(recs.first?.userId, "bob")
        XCTAssertEqual(recs.first?.sampleCount, 2)
        // Centroid stays L2-normalised (norm ~ 1).
        let norm = (recs.first?.centroid.reduce(0.0) { $0 + Double($1) * Double($1) } ?? 0).squareRoot()
        XCTAssertEqual(norm, 1.0, accuracy: 1e-4)
        await svc.dispose()
    }

    func testIdentifyEmptyEnrollmentReturnsNil() async throws {
        let svc = SpeakerIdentityService()
        let id = try await svc.identify(audioPcm16: tone(20_000, amp: 8000, period: 40), sampleRateHz: 16_000)
        XCTAssertNil(id)
        await svc.dispose()
    }

    func testIdentifyEmptyAudioReturnsNil() async throws {
        let svc = SpeakerIdentityService()
        try await svc.enroll(userId: "x", audioPcm16: tone(20_000, amp: 8000, period: 40), sampleRateHz: 16_000)
        let id = try await svc.identify(audioPcm16: Data(), sampleRateHz: 16_000)
        XCTAssertNil(id)
        await svc.dispose()
    }

    func testWrongSampleRateEnrollFails() async throws {
        let svc = SpeakerIdentityService() // model sample rate 16k
        do {
            try await svc.enroll(userId: "y", audioPcm16: tone(20_000, amp: 8000, period: 40), sampleRateHz: 8_000)
            XCTFail("expected embeddingFailed (sample-rate mismatch -> nil embedding)")
        } catch {
            XCTAssertEqual(error as? SpeakerIdentityError, .embeddingFailed)
        }
        await svc.dispose()
    }

    func testTooShortUtteranceEnrollFails() async throws {
        let svc = SpeakerIdentityService()
        // 100 samples << min 16000.
        do {
            try await svc.enroll(userId: "z", audioPcm16: tone(100, amp: 8000, period: 20), sampleRateHz: 16_000)
            XCTFail("expected embeddingFailed for too-short utterance")
        } catch {
            XCTAssertEqual(error as? SpeakerIdentityError, .embeddingFailed)
        }
        await svc.dispose()
    }

    func testEnrollBlankUserIdThrows() async throws {
        let svc = SpeakerIdentityService()
        do {
            try await svc.enroll(userId: "  ", audioPcm16: tone(20_000, amp: 8000, period: 40), sampleRateHz: 16_000)
            XCTFail("expected userIdRequired")
        } catch { XCTAssertEqual(error as? SpeakerIdentityError, .userIdRequired) }
        await svc.dispose()
    }

    func testIdentifyPicksBetterMatchWithInjectedRunner() async throws {
        // Deterministic embeddings: alice -> [1,0], bob -> [0,1].
        final class Runner: ISpeakerEmbeddingRunner {
            var next: [Float] = [1, 0]
            func embed(window: [Float], inputKind: SpeakerEmbedderInputKind) -> [Float] { next }
        }
        let runner = Runner()
        let svc = SpeakerIdentityService(config: SpeakerIdentityConfig(minUtteranceMs: 0, matchThreshold: 0.5), runner: runner)
        let audio = tone(2_000, amp: 8000, period: 40)
        runner.next = [1, 0]; try await svc.enroll(userId: "alice", audioPcm16: audio, sampleRateHz: 16_000)
        runner.next = [0, 1]; try await svc.enroll(userId: "bob", audioPcm16: audio, sampleRateHz: 16_000)

        runner.next = [0.9, 0.1]
        XCTAssertEqual(try await svc.identify(audioPcm16: audio, sampleRateHz: 16_000), "alice")
        runner.next = [0.1, 0.9]
        XCTAssertEqual(try await svc.identify(audioPcm16: audio, sampleRateHz: 16_000), "bob")
        await svc.dispose()
    }

    func testIdentifyBelowThresholdReturnsNilWithRunner() async throws {
        final class Runner: ISpeakerEmbeddingRunner {
            var next: [Float] = [1, 0]
            func embed(window: [Float], inputKind: SpeakerEmbedderInputKind) -> [Float] { next }
        }
        let runner = Runner()
        let svc = SpeakerIdentityService(config: SpeakerIdentityConfig(minUtteranceMs: 0, matchThreshold: 0.9), runner: runner)
        let audio = tone(2_000, amp: 8000, period: 40)
        runner.next = [1, 0]; try await svc.enroll(userId: "alice", audioPcm16: audio, sampleRateHz: 16_000)
        // Orthogonal probe -> cosine 0 < 0.9 threshold.
        runner.next = [0, 1]
        XCTAssertNil(try await svc.identify(audioPcm16: audio, sampleRateHz: 16_000))
        await svc.dispose()
    }

    func testEnrollmentPersistsToDiskAndReloads() async throws {
        let dir = NSTemporaryDirectory() + "circleai-spk-\(UUID().uuidString)"
        let path = dir + "/enroll.json"
        let cfg = SpeakerIdentityConfig(enrollmentStorePath: path)
        let svc = SpeakerIdentityService(config: cfg)
        let audio = tone(20_000, amp: 8000, period: 40)
        try await svc.enroll(userId: "carol", audioPcm16: audio, sampleRateHz: 16_000)
        await svc.dispose()

        // New service pointed at same store reloads the enrollment.
        let svc2 = SpeakerIdentityService(config: cfg)
        XCTAssertEqual(svc2.enrolledSpeakers.first?.userId, "carol")
        XCTAssertEqual(try await svc2.identify(audioPcm16: audio, sampleRateHz: 16_000), "carol")
        await svc2.dispose()
        try? FileManager.default.removeItem(atPath: dir)
    }

    // ── SpeechEmotionService ─────────────────────────────────────────────

    func testEmotionSenseFallbackReturnsKnownLabel() async throws {
        let svc = SpeechEmotionService()
        let frame = try await svc.sense(audioPcm16: tone(8_000, amp: 8000, period: 40), sampleRateHz: 16_000)
        XCTAssertNotNil(frame)
        // Label must be one of the default labels (all present in the circumplex).
        XCTAssertTrue(SpeechEmotionService.defaultLabels.contains(frame!.label))
        XCTAssertGreaterThan(frame!.probability, 0)
        await svc.dispose()
    }

    func testEmotionSenseEmptyAudioReturnsNil() async throws {
        let svc = SpeechEmotionService()
        let frame = try await svc.sense(audioPcm16: Data(), sampleRateHz: 16_000)
        XCTAssertNil(frame)
        await svc.dispose()
    }

    func testEmotionSenseWrongSampleRateReturnsNil() async throws {
        let svc = SpeechEmotionService()
        let frame = try await svc.sense(audioPcm16: tone(8_000, amp: 8000, period: 40), sampleRateHz: 8_000)
        XCTAssertNil(frame)
        await svc.dispose()
    }

    func testEmotionRunnerDrivesLabelAndCircumplex() async throws {
        // Force argmax onto class index 2 == "angry"; check circumplex coords.
        final class Runner: IEmotionLogitsRunner {
            func logits(window: [Float]) -> [Float] { [0.1, 0.2, 5.0, 0.0] } // argmax idx 2
        }
        let svc = SpeechEmotionService(runner: Runner())
        let frame = try await svc.sense(audioPcm16: tone(4_000, amp: 8000, period: 40), sampleRateHz: 16_000)
        XCTAssertEqual(frame?.label, "angry")
        XCTAssertEqual(frame?.arousal ?? 0, 0.74, accuracy: 1e-9)
        XCTAssertEqual(frame?.valence ?? 0, -0.62, accuracy: 1e-9)
        // Softmax prob of the dominant class is the largest and < 1.
        XCTAssertGreaterThan(frame?.probability ?? 0, 0.9)
        await svc.dispose()
    }

    func testEmotionUnknownIndexMapsToOriginCoords() async throws {
        // Custom single label not in circumplex -> coords (0,0).
        final class Runner: IEmotionLogitsRunner {
            func logits(window: [Float]) -> [Float] { [9.0] }
        }
        let svc = SpeechEmotionService(config: SpeechEmotionConfig(labels: ["mystery"]), runner: Runner())
        let frame = try await svc.sense(audioPcm16: tone(4_000, amp: 8000, period: 40), sampleRateHz: 16_000)
        XCTAssertEqual(frame?.label, "mystery")
        XCTAssertEqual(frame?.arousal, 0)
        XCTAssertEqual(frame?.valence, 0)
        await svc.dispose()
    }

    // ── KwsWakeWordDetector ──────────────────────────────────────────────

    final class FixedCapture: IAudioCapture, @unchecked Sendable {
        let format: AudioFormat = .pcm16Mono16k
        private let chunks: [Data]
        init(chunks: [Data]) { self.chunks = chunks }
        func capture() -> AsyncThrowingStream<Data, Error> {
            AsyncThrowingStream { cont in
                for c in chunks { cont.yield(c) }
                cont.finish()
            }
        }
        func dispose() async {}
    }

    final class EventBox: @unchecked Sendable {
        private let lock = NSLock()
        private var events: [WakeWordDetectedEventArgs] = []
        func add(_ e: WakeWordDetectedEventArgs) { lock.lock(); events.append(e); lock.unlock() }
        var count: Int { lock.lock(); defer { lock.unlock() }; return events.count }
    }

    func testKwsFiresWithRunnerAboveThreshold() async throws {
        // Runner forces the target class high -> softmax over target >= threshold.
        final class Runner: IKwsModelRunner {
            func classLogits(window: [Float], inputKind: KwsInputKind) -> [Float] { [0.0, 10.0] } // target idx 1
        }
        // Provide >= 1 window's worth of audio (windowMs default 1000 @16k = 16000 samples).
        let big = pcm([Int16](repeating: 4000, count: 20_000))
        let capture = FixedCapture(chunks: [big])
        let cfg = KwsConfig(threshold: 0.7, minIntervalBetweenFires: 0.0)
        let det = KwsWakeWordDetector(capture: capture, config: cfg, runner: Runner())
        XCTAssertEqual(det.wakeWord, "hey b")

        let box = EventBox()
        let consumer = Task { for await ev in det.detections() { box.add(ev) } }
        try await det.start()
        try await Task.sleep(nanoseconds: 300_000_000)
        try await det.stop()
        await det.dispose()
        consumer.cancel()
        XCTAssertGreaterThanOrEqual(box.count, 1)
    }

    func testKwsDoesNotFireBelowThreshold() async throws {
        final class Runner: IKwsModelRunner {
            func classLogits(window: [Float], inputKind: KwsInputKind) -> [Float] { [10.0, 0.0] } // target idx 1 low
        }
        let big = pcm([Int16](repeating: 4000, count: 20_000))
        let capture = FixedCapture(chunks: [big])
        let det = KwsWakeWordDetector(capture: capture, config: KwsConfig(threshold: 0.7, minIntervalBetweenFires: 0.0), runner: Runner())
        let box = EventBox()
        let consumer = Task { for await ev in det.detections() { box.add(ev) } }
        try await det.start()
        try await Task.sleep(nanoseconds: 300_000_000)
        try await det.stop()
        await det.dispose()
        consumer.cancel()
        XCTAssertEqual(box.count, 0)
    }

    func testKwsListeningState() async throws {
        let capture = FixedCapture(chunks: [])
        let det = KwsWakeWordDetector(capture: capture, config: KwsConfig())
        XCTAssertFalse(det.isListening)
        try await det.start()
        XCTAssertTrue(det.isListening)
        try await det.stop()
        XCTAssertFalse(det.isListening)
        await det.dispose()
    }
}
