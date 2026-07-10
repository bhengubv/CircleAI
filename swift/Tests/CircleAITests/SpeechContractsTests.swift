// SpeechContractsTests.swift
//
// Verifies the CircleAI.Speech contract DTOs + fail-closed null defaults
// (SpeechContracts.swift): backend ids, empty deterministic answers, and the
// wake-word subscription handle.

import XCTest
@testable import CircleAI

final class SpeechContractsTests: XCTestCase {

    func testNullSpeechRecognizerReturnsEmptyAndEchoesLanguageHint() async throws {
        let r = NullSpeechRecognizer.instance
        XCTAssertEqual(r.backendId, "null")
        let result = try await r.transcribe(audioPcm16Mono: Data([1, 2, 3, 4]), sampleRateHz: 16_000, languageHint: "en")
        XCTAssertEqual(result.text, "")
        XCTAssertEqual(result.language, "en")
        XCTAssertTrue(result.segments.isEmpty)
        XCTAssertEqual(result.totalDuration, 0)
    }

    func testNullSpeechRecognizerLanguageNilWhenNoHint() async throws {
        let result = try await NullSpeechRecognizer.instance.transcribe(audioPcm16Mono: Data(), sampleRateHz: 16_000)
        XCTAssertNil(result.language)
    }

    func testNullSpeechSynthesizerReturnsEmpty16kAudio() async throws {
        let s = NullSpeechSynthesizer.instance
        XCTAssertEqual(s.backendId, "null")
        let result = try await s.synthesize(text: "hello")
        XCTAssertTrue(result.audioPcm16Mono.isEmpty)
        XCTAssertEqual(result.sampleRateHz, 16_000)
        XCTAssertEqual(result.duration, 0)
    }

    /// Thread-safe flag for the (never-invoked) subscription callback.
    final class Flag: @unchecked Sendable {
        private let lock = NSLock()
        private var _fired = false
        func fire() { lock.lock(); _fired = true; lock.unlock() }
        var fired: Bool { lock.lock(); defer { lock.unlock() }; return _fired }
    }

    func testNullWakeWordDetectorNeverFires() async throws {
        let d = NullSpeechWakeWordDetector()
        XCTAssertEqual(d.backendId, "null")
        let flag = Flag()
        let sub = d.subscribe { _ in flag.fire() }
        try await d.start()
        try await d.stop()
        await d.dispose()
        sub.dispose()
        XCTAssertFalse(flag.fired)
    }

    func testNullOcrReturnsEmpty() async throws {
        let o = NullOpticalCharacterRecognizer.instance
        XCTAssertEqual(o.backendId, "null")
        let result = try await o.recognize(imageBytes: Data([9, 9, 9]))
        XCTAssertEqual(result.text, "")
        XCTAssertTrue(result.blocks.isEmpty)
    }

    func testDtoEquatableAndCodableRoundTrip() throws {
        let seg = SpeechTranscribedSegment(text: "hi", offset: 0.5, duration: 1.0, language: "en", confidence: 0.9)
        let res = SpeechTranscriptionResult(text: "hi there", language: "en", segments: [seg], totalDuration: 1.5)
        let data = try JSONEncoder().encode(res)
        let back = try JSONDecoder().decode(SpeechTranscriptionResult.self, from: data)
        XCTAssertEqual(res, back)

        let block = OcrTextBlock(text: "T", x: 1, y: 2, width: 3, height: 4, confidence: 0.5, language: "eng")
        let ocr = OcrResult(text: "T", blocks: [block])
        let ocrData = try JSONEncoder().encode(ocr)
        XCTAssertEqual(try JSONDecoder().decode(OcrResult.self, from: ocrData), ocr)
    }
}
