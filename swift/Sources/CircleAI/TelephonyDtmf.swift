// TelephonyDtmf.swift
//
// Port of CircleAI.Telephony.DtmfToneGenerator — generate the dual-tone audio
// for DTMF digits, and a helper that sends them through any ICallSession via
// sendAudio — works regardless of whether the carrier supports out-of-band
// DTMF.

import Foundation

/// Stateless DTMF audio generator. Port of
/// `CircleAI.Telephony.DtmfToneGenerator`.
public enum DtmfToneGenerator {

    /// Standard DTMF frequencies (low row × high column). Port of the C#
    /// `Frequencies` dictionary (keyed by the uppercase digit).
    private static let frequencies: [Character: (low: Int, high: Int)] = [
        "1": (697, 1209),
        "2": (697, 1336),
        "3": (697, 1477),
        "A": (697, 1633),
        "4": (770, 1209),
        "5": (770, 1336),
        "6": (770, 1477),
        "B": (770, 1633),
        "7": (852, 1209),
        "8": (852, 1336),
        "9": (852, 1477),
        "C": (852, 1633),
        "*": (941, 1209),
        "0": (941, 1336),
        "#": (941, 1477),
        "D": (941, 1633),
    ]

    /// Generate one PCM-16 mono buffer for the digit at the given sample rate.
    /// Port of `Generate(char digit, int sampleRateHz, int durationMs, float amplitude)`.
    ///
    /// Samples are little-endian Int16; the sample count is
    /// `sampleRateHz * durationMs / 1000` (integer arithmetic, matching C#).
    public static func generate(
        digit: Character,
        sampleRateHz: Int,
        durationMs: Int = 150,
        amplitude: Float = 0.5
    ) throws -> Data {
        if sampleRateHz <= 0 { throw TelephonyError.argument("sampleRateHz") }
        if durationMs <= 0 { throw TelephonyError.argument("durationMs") }
        let key = Character(String(digit).uppercased())
        guard let pair = frequencies[key] else {
            throw TelephonyError.argument("Unsupported DTMF digit '\(digit)'.")
        }

        let samples = sampleRateHz * durationMs / 1000
        if samples <= 0 { return Data() }
        var buf = Data(count: samples * 2)
        buf.withUnsafeMutableBytes { (raw: UnsafeMutableRawBufferPointer) in
            for i in 0..<samples {
                let t = Double(i) / Double(sampleRateHz)
                let s = 0.5 * Double(amplitude) *
                    (sin(2 * Double.pi * Double(pair.low) * t) + sin(2 * Double.pi * Double(pair.high) * t))
                let clamped = min(max(s, -1), 1)
                // C# casts `(short)(clamped * short.MaxValue)` (truncation toward
                // zero). The scaled value is already within Int16 range; the
                // extra Int() round-trip + explicit clamp removes any FP-boundary
                // trap risk while preserving truncation-toward-zero semantics.
                let scaled = clamped * Double(Int16.max)
                let intVal = Int(scaled) // truncates toward zero
                let value = Int16(min(max(intVal, Int(Int16.min)), Int(Int16.max)))
                // Little-endian write via subscript (no force-unwrap).
                raw[i * 2] = UInt8(truncatingIfNeeded: value)
                raw[i * 2 + 1] = UInt8(truncatingIfNeeded: value >> 8)
            }
        }
        return buf
    }

    /// Generate a full string of digits with gap silence between them. Port of
    /// `GenerateSequence(...)`. Empty input → empty buffer.
    public static func generateSequence(
        digits: String,
        sampleRateHz: Int,
        toneDurationMs: Int = 150,
        interDigitGapMs: Int = 50,
        amplitude: Float = 0.5
    ) throws -> Data {
        if digits.isEmpty { return Data() }
        let gapSamples = sampleRateHz * interDigitGapMs / 1000
        let gap = Data(count: gapSamples * 2)

        var out = Data()
        let chars = Array(digits)
        for i in 0..<chars.count {
            let tone = try generate(
                digit: chars[i],
                sampleRateHz: sampleRateHz,
                durationMs: toneDurationMs,
                amplitude: amplitude)
            out.append(tone)
            if i < chars.count - 1 {
                out.append(gap)
            }
        }
        return out
    }

    /// Send `digits` over the call via in-band tones. Port of
    /// `SendThroughSessionAsync(...)`. No-op for empty input.
    ///
    /// Format selection mirrors the C# switch: 8000→Mulaw8000, 16000→Pcm16000,
    /// 24000→Pcm24000, else Pcm16000.
    public static func sendThroughSession(
        _ session: ICallSession,
        digits: String,
        sampleRateHz: Int = 8000,
        toneDurationMs: Int = 150,
        interDigitGapMs: Int = 50
    ) async throws {
        if digits.isEmpty { return }
        let pcm = try generateSequence(
            digits: digits,
            sampleRateHz: sampleRateHz,
            toneDurationMs: toneDurationMs,
            interDigitGapMs: interDigitGapMs)
        let format: CallMediaFormat
        switch sampleRateHz {
        case 8000: format = .mulaw8000
        case 16000: format = .pcm16000
        case 24000: format = .pcm24000
        default: format = .pcm16000
        }
        try await session.sendAudio(AudioFrame(pcm: pcm, format: format, offset: 0))
    }
}
