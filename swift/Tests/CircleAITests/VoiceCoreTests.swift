// VoiceCoreTests.swift
//
// Verifies the CircleAI.Voice core contracts + deterministic implementations
// (VoiceCore.swift): null defaults, EnergyVadDetector segmentation,
// EnergyWakeWordDetector firing on transcript match, and the VoicePipeline
// wake -> capture -> transcribe -> onTranscribed flow.

import XCTest
@testable import CircleAI

final class VoiceCoreTests: XCTestCase {

    // ── helpers ──────────────────────────────────────────────────────────
    private func pcm(_ samples: [Int16]) -> Data {
        var b = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count { writeInt16LE(&b, i * 2, samples[i]) }
        return Data(b)
    }
    private func sine(_ n: Int, amp: Double, period: Int) -> [Int16] {
        (0..<n).map { Int16(amp * sin(2 * Double.pi * Double($0) / Double(period))) }
    }

    /// Capture that yields a fixed list of chunks, then finishes.
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

    /// Transcriber returning a canned single-shot result; streaming yields one final.
    final class CannedTranscriber: IVoiceTranscriber, @unchecked Sendable {
        let text: String
        let confidence: Float
        init(text: String, confidence: Float = 0.9) { self.text = text; self.confidence = confidence }
        func transcribe(pcmAudio: Data) async throws -> TranscriptionResult {
            TranscriptionResult(text: text, confidence: confidence, languageCode: "en")
        }
        func streamTranscribe(audioChunks: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<PartialTranscription, Error> {
            AsyncThrowingStream { cont in
                let t = Task {
                    do {
                        for try await _ in audioChunks {}
                        cont.yield(PartialTranscription(text: text, isFinal: true, confidence: confidence))
                        cont.finish()
                    } catch { cont.finish(throwing: error) }
                }
                cont.onTermination = { _ in t.cancel() }
            }
        }
        func dispose() async {}
    }

    private func collect(_ stream: AsyncThrowingStream<VadSegment, Error>) async throws -> [VadSegment] {
        var out: [VadSegment] = []
        for try await s in stream { out.append(s) }
        return out
    }

    // ── Null defaults ────────────────────────────────────────────────────

    func testNullAudioCaptureYieldsNothing() async throws {
        let cap = NullAudioCapture()
        XCTAssertEqual(cap.format, .pcm16Mono16k)
        var count = 0
        for try await _ in cap.capture() { count += 1 }
        XCTAssertEqual(count, 0)
        await cap.dispose()
    }

    func testNullTranscriberDrainsAndReturnsEmpty() async throws {
        let t = NullVoiceTranscriber()
        let r = try await t.transcribe(pcmAudio: pcm([1, 2, 3]))
        XCTAssertEqual(r.text, "")
        XCTAssertEqual(r.languageCode, "und")

        let input = AsyncThrowingStream<Data, Error> { cont in
            cont.yield(self.pcm([1]))
            cont.yield(self.pcm([2]))
            cont.finish()
        }
        var partials = 0
        for try await _ in t.streamTranscribe(audioChunks: input) { partials += 1 }
        XCTAssertEqual(partials, 0)  // drains input, emits nothing
        await t.dispose()
    }

    func testNullTranscriberAfterDisposeThrows() async throws {
        let t = NullVoiceTranscriber()
        await t.dispose()
        do {
            _ = try await t.transcribe(pcmAudio: Data())
            XCTFail("expected disposed")
        } catch { XCTAssertEqual(error as? VoiceError, .disposed) }
    }

    func testNullTtsEngineEmpty() async throws {
        let e = NullTtsEngine()
        let r = try await e.synthesise(text: "hi")
        XCTAssertTrue(r.audioData.isEmpty)
        XCTAssertEqual(r.sampleRate, 24_000)
        XCTAssertEqual(r.channels, 1)
        XCTAssertEqual(r.bitsPerSample, 16)
        var chunks = 0
        for try await _ in e.streamSynthesise(text: "hi") { chunks += 1 }
        XCTAssertEqual(chunks, 0)
    }

    func testNullVoiceActivityDetectorPassesThrough() async throws {
        let vad = NullVoiceActivityDetector()
        let input = AsyncThrowingStream<Data, Error> { cont in
            cont.yield(self.pcm([1, 2]))
            cont.yield(self.pcm([3, 4]))
            cont.finish()
        }
        let segs = try await collect(vad.detect(audioStream: input))
        XCTAssertEqual(segs.count, 2)
        XCTAssertTrue(segs.allSatisfy { $0.isSpeech })
    }

    func testNullWakeWordDetectorStateAndNoFire() async throws {
        let d = NullWakeWordDetector()
        XCTAssertEqual(d.wakeWord, "Hey B")
        XCTAssertFalse(d.isListening)
        try await d.start()
        XCTAssertTrue(d.isListening)
        try await d.stop()
        XCTAssertFalse(d.isListening)
        var fired = 0
        for await _ in d.detections() { fired += 1 }  // completes immediately
        XCTAssertEqual(fired, 0)
        await d.dispose()
    }

    // ── EnergyVadDetector ────────────────────────────────────────────────

    func testEnergyVadEmitsSpeechSegment() async throws {
        // One frame = 640 bytes = 320 samples. Loud speech frames, then enough
        // silence frames (>= silenceFrames) to close the segment.
        let vad = EnergyVadDetector(energyThreshold: 0.02, silenceFrames: 3, frameSizeBytes: 640)
        let loud = pcm(sine(320, amp: 15000, period: 20))
        let quiet = pcm([Int16](repeating: 0, count: 320))
        let input = AsyncThrowingStream<Data, Error> { cont in
            cont.yield(loud)   // speech onset
            cont.yield(loud)
            cont.yield(quiet)  // silence 1
            cont.yield(quiet)  // silence 2
            cont.yield(quiet)  // silence 3 -> closes segment
            cont.finish()
        }
        let segs = try await collect(vad.detect(audioStream: input))
        XCTAssertEqual(segs.count, 1)
        XCTAssertTrue(segs[0].isSpeech)
        // 2 loud + 3 buffered silence frames = 5 * 640 bytes.
        XCTAssertEqual(segs[0].audio.count, 5 * 640)
    }

    func testEnergyVadEmitsTrailingPartialOnStreamEnd() async throws {
        let vad = EnergyVadDetector(energyThreshold: 0.02, silenceFrames: 10, frameSizeBytes: 640)
        let loud = pcm(sine(320, amp: 15000, period: 20))
        let input = AsyncThrowingStream<Data, Error> { cont in
            cont.yield(loud)
            cont.yield(loud)
            cont.finish()  // ends mid-speech
        }
        let segs = try await collect(vad.detect(audioStream: input))
        XCTAssertEqual(segs.count, 1)
        XCTAssertEqual(segs[0].audio.count, 2 * 640)
    }

    func testEnergyVadReassemblesAcrossChunkBoundaries() async throws {
        // Feed audio in odd-sized chunks that don't align to frame size; the
        // residual buffering must still yield the right total.
        let vad = EnergyVadDetector(energyThreshold: 0.02, silenceFrames: 2, frameSizeBytes: 640)
        let loud = pcm(sine(640, amp: 15000, period: 20))   // 2 frames worth
        let quiet = pcm([Int16](repeating: 0, count: 640))  // 2 frames of silence
        // Split into 500-byte chunks.
        var all = Data(); all.append(loud); all.append(quiet)
        var chunks: [Data] = []
        var i = 0
        while i < all.count { chunks.append(all.subdata(in: i..<min(i + 500, all.count))); i += 500 }
        let input = AsyncThrowingStream<Data, Error> { cont in
            for c in chunks { cont.yield(c) }
            cont.finish()
        }
        let segs = try await collect(vad.detect(audioStream: input))
        XCTAssertEqual(segs.count, 1)
        // 2 loud frames + 2 silence frames buffered = 4 * 640.
        XCTAssertEqual(segs[0].audio.count, 4 * 640)
    }

    // ── EnergyWakeWordDetector ───────────────────────────────────────────

    func testEnergyWakeWordFiresOnMatch() async throws {
        let loud = pcm(sine(320, amp: 15000, period: 20))
        let quiet = pcm([Int16](repeating: 0, count: 320))
        // Enough frames to produce a speech segment (silenceFrames default 10 here).
        var chunks: [Data] = [loud, loud, loud]
        chunks.append(contentsOf: Array(repeating: quiet, count: 12))
        let capture = FixedCapture(chunks: chunks)
        let transcriber = CannedTranscriber(text: "hey b what's the weather")
        let detector = EnergyWakeWordDetector(capture: capture, transcriber: transcriber, wakeWord: "hey b")

        let box = EventBox()
        let consumer = Task {
            for await ev in detector.detections() { box.add(ev) }
        }
        try await detector.start()

        for _ in 0..<100 where box.count == 0 {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        try await detector.stop()
        await detector.dispose()
        consumer.cancel()

        XCTAssertGreaterThanOrEqual(box.count, 1)
        XCTAssertEqual(box.first?.wakeWord, "hey b")
    }

    func testEnergyWakeWordDoesNotFireWithoutMatch() async throws {
        let loud = pcm(sine(320, amp: 15000, period: 20))
        let quiet = pcm([Int16](repeating: 0, count: 320))
        var chunks: [Data] = [loud, loud, loud]
        chunks.append(contentsOf: Array(repeating: quiet, count: 12))
        let capture = FixedCapture(chunks: chunks)
        let transcriber = CannedTranscriber(text: "just talking about lunch")
        let detector = EnergyWakeWordDetector(capture: capture, transcriber: transcriber, wakeWord: "hey b")

        let box = EventBox()
        let consumer = Task { for await ev in detector.detections() { box.add(ev) } }
        try await detector.start()
        // Give the loop time to process the whole (finite) capture.
        try await Task.sleep(nanoseconds: 300_000_000)
        try await detector.stop()
        await detector.dispose()
        consumer.cancel()
        XCTAssertEqual(box.count, 0)
    }

    // ── VoicePipeline ────────────────────────────────────────────────────

    /// Wake detector we can fire manually via its detections() stream.
    final class ManualWake: IWakeWordDetector, @unchecked Sendable {
        let wakeWord = "hey b"
        private let lock = NSLock()
        private var listening = false
        private var cont: AsyncStream<WakeWordDetectedEventArgs>.Continuation?
        var isListening: Bool { lock.lock(); defer { lock.unlock() }; return listening }
        func detections() -> AsyncStream<WakeWordDetectedEventArgs> {
            AsyncStream(bufferingPolicy: .unbounded) { c in
                self.lock.lock(); self.cont = c; self.lock.unlock()
            }
        }
        func fire() {
            lock.lock(); let c = cont; lock.unlock()
            c?.yield(WakeWordDetectedEventArgs(wakeWord: wakeWord, confidence: 1))
        }
        func start() async throws { lock.lock(); listening = true; lock.unlock() }
        func stop() async throws { lock.lock(); listening = false; lock.unlock() }
        func dispose() async { lock.lock(); let c = cont; cont = nil; lock.unlock(); c?.finish() }
    }

    func testVoicePipelineWakeToTranscription() async throws {
        let wake = ManualWake()
        let capture = FixedCapture(chunks: [pcm(sine(320, amp: 10000, period: 20))])
        let transcriber = CannedTranscriber(text: "play some jazz")
        let pipeline = VoicePipeline(wake: wake, transcriber: transcriber, capture: capture)

        let resultBox = ResultBox()
        pipeline.onTranscribed = { ev in resultBox.set(ev.result.text) }

        try await pipeline.start()
        XCTAssertTrue(wake.isListening)
        wake.fire()

        for _ in 0..<100 where resultBox.value == nil {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTAssertEqual(resultBox.value, "play some jazz")
        await pipeline.dispose()
    }

    func testVoicePipelineStartStopDelegate() async throws {
        let wake = ManualWake()
        let pipeline = VoicePipeline(wake: wake, transcriber: NullVoiceTranscriber())
        try await pipeline.start()
        XCTAssertTrue(wake.isListening)
        try await pipeline.stop()
        XCTAssertFalse(wake.isListening)
        await pipeline.dispose()
    }

    func testVoicePipelineStartAfterDisposeThrows() async throws {
        let wake = ManualWake()
        let pipeline = VoicePipeline(wake: wake, transcriber: NullVoiceTranscriber())
        await pipeline.dispose()
        do {
            try await pipeline.start()
            XCTFail("expected disposed")
        } catch { XCTAssertEqual(error as? VoiceError, .disposed) }
    }

    // ── thread-safe capture boxes ────────────────────────────────────────
    final class EventBox: @unchecked Sendable {
        private let lock = NSLock()
        private var events: [WakeWordDetectedEventArgs] = []
        func add(_ e: WakeWordDetectedEventArgs) { lock.lock(); events.append(e); lock.unlock() }
        var count: Int { lock.lock(); defer { lock.unlock() }; return events.count }
        var first: WakeWordDetectedEventArgs? { lock.lock(); defer { lock.unlock() }; return events.first }
    }
    final class ResultBox: @unchecked Sendable {
        private let lock = NSLock()
        private var _value: String?
        func set(_ v: String) { lock.lock(); _value = v; lock.unlock() }
        var value: String? { lock.lock(); defer { lock.unlock() }; return _value }
    }
}
