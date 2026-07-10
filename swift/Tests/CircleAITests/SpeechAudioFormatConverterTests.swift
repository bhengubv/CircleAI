// SpeechAudioFormatConverterTests.swift
//
// Verifies AudioFormatConverter (SpeechAudioFormatConverter.swift). The G.711
// mu-law / a-law encodings and the linear resampler are checked against known
// ITU-T G.711 anchor values and against structural invariants (buffer sizing,
// pass-through, round-trip decode-of-encode).

import XCTest
@testable import CircleAI

final class SpeechAudioFormatConverterTests: XCTestCase {

    private func pcm(_ samples: [Int16]) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count { writeInt16LE(&b, i * 2, samples[i]) }
        return b
    }
    private func shorts(_ bytes: [UInt8]) -> [Int16] {
        (0..<(bytes.count / 2)).map { readInt16LE(bytes, $0 * 2) }
    }

    // ── G.711 anchor values ──────────────────────────────────────────────

    func testMuLawEncodesZeroTo0xFF() {
        // ITU-T G.711 μ-law encodes linear 0 as 0xFF.
        let mu = AudioFormatConverter.encodePcm16ToMuLaw(pcm([0]))
        XCTAssertEqual(mu, [0xFF])
    }

    func testMuLawDecodeOf0xFFIsZero() {
        let pcmBack = AudioFormatConverter.decodeMuLawToPcm16([0xFF])
        XCTAssertEqual(shorts(pcmBack), [0])
    }

    func testALawEncodesZeroTo0x55() {
        // ITU-T G.711 A-law encodes linear 0 as 0x55.
        let a = AudioFormatConverter.encodePcm16ToALaw(pcm([0]))
        XCTAssertEqual(a, [0x55])
    }

    func testMuLawRoundTripApproximatesPositiveAndNegative() {
        // G.711 is lossy but monotonic; decode(encode(x)) should stay near x and
        // preserve sign for mid-range samples.
        for s: Int16 in [-8000, -1000, 1000, 8000, 20000, -20000] {
            let mu = AudioFormatConverter.encodePcm16ToMuLaw(pcm([s]))
            let back = shorts(AudioFormatConverter.decodeMuLawToPcm16(mu))[0]
            XCTAssertEqual(back.signum(), s.signum(), "sign preserved for \(s)")
            XCTAssertLessThan(abs(Int(back) - Int(s)), 2000, "μ-law quantisation error bounded for \(s)")
        }
    }

    func testALawRoundTripApproximates() {
        for s: Int16 in [-8000, -1000, 1000, 8000, 20000, -20000] {
            let a = AudioFormatConverter.encodePcm16ToALaw(pcm([s]))
            let back = shorts(AudioFormatConverter.decodeALawToPcm16(a))[0]
            XCTAssertEqual(back.signum(), s.signum(), "sign preserved for \(s)")
            XCTAssertLessThan(abs(Int(back) - Int(s)), 2000, "A-law quantisation error bounded for \(s)")
        }
    }

    func testMuLawDecodeProducesTwoBytesPerSample() {
        let pcm = AudioFormatConverter.decodeMuLawToPcm16([0x00, 0x7F, 0xFF])
        XCTAssertEqual(pcm.count, 6)
    }

    // ── Resampling ───────────────────────────────────────────────────────

    func testResampleUpDoublesSampleCount8kTo16k() {
        let src = pcm([100, 200, 300, 400]) // 4 samples
        let dst = AudioFormatConverter.resamplePcm16Linear(src, fromHz: 8_000, toHz: 16_000)
        XCTAssertEqual(dst.count / 2, 8) // 4 * 16000/8000
        // First sample equals source[0] (srcIdx 0, frac 0).
        XCTAssertEqual(shorts(dst)[0], 100)
    }

    func testResampleDownHalvesSampleCount16kTo8k() {
        let src = pcm([Int16](repeating: 1234, count: 8))
        let dst = AudioFormatConverter.resamplePcm16Linear(src, fromHz: 16_000, toHz: 8_000)
        XCTAssertEqual(dst.count / 2, 4)
        XCTAssertEqual(shorts(dst), [Int16](repeating: 1234, count: 4)) // constant signal preserved
    }

    func testResampleSameRateIsIdentity() {
        let src = pcm([1, 2, 3])
        XCTAssertEqual(AudioFormatConverter.resamplePcm16Linear(src, fromHz: 16_000, toHz: 16_000), src)
    }

    func testResampleLinearInterpolationMidpoint() {
        // Upsampling [0, 1000] 8k->16k: dst has 4 samples; index 1 maps to srcIdx 0.5
        // => interpolate 0 + (1000-0)*0.5 = 500.
        let src = pcm([0, 1000])
        let dst = shorts(AudioFormatConverter.resamplePcm16Linear(src, fromHz: 8_000, toHz: 16_000))
        XCTAssertEqual(dst[0], 0)
        XCTAssertEqual(dst[1], 500)
    }

    // ── convert() end-to-end ─────────────────────────────────────────────

    func testConvertPcmPassThroughSameRate() throws {
        let src = pcm([5, 6, 7, 8])
        let out = try AudioFormatConverter.convert(
            input: src, inputCodec: .pcm16, inputSampleRateHz: 16_000,
            outputCodec: .pcm16, outputSampleRateHz: 16_000)
        XCTAssertEqual(out, src)
    }

    func testConvertMuLaw8kToPcm16k() throws {
        // 3 mu-law bytes -> decode to 3 PCM samples -> resample 8k->16k -> 6 samples.
        let out = try AudioFormatConverter.convert(
            input: [0xFF, 0xFF, 0xFF], inputCodec: .muLaw, inputSampleRateHz: 8_000,
            outputCodec: .pcm16, outputSampleRateHz: 16_000)
        XCTAssertEqual(out.count / 2, 6)
        XCTAssertEqual(shorts(out), [Int16](repeating: 0, count: 6)) // 0xFF decodes to 0
    }

    func testConvertPcm16kToMuLaw8k() throws {
        let out = try AudioFormatConverter.convert(
            input: pcm([Int16](repeating: 0, count: 4)), inputCodec: .pcm16, inputSampleRateHz: 16_000,
            outputCodec: .muLaw, outputSampleRateHz: 8_000)
        XCTAssertEqual(out.count, 2)        // 4 samples down to 2, one byte each
        XCTAssertEqual(out, [0xFF, 0xFF])   // zeros -> 0xFF
    }

    func testConvertRejectsNonPositiveSampleRates() {
        XCTAssertThrowsError(try AudioFormatConverter.convert(
            input: [0, 0], inputCodec: .pcm16, inputSampleRateHz: 0,
            outputCodec: .pcm16, outputSampleRateHz: 16_000))
        XCTAssertThrowsError(try AudioFormatConverter.convert(
            input: [0, 0], inputCodec: .pcm16, inputSampleRateHz: 16_000,
            outputCodec: .pcm16, outputSampleRateHz: -1))
    }
}
