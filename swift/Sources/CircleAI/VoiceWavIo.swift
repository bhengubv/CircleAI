// VoiceWavIo.swift
//
// Port of src/CircleAI.Voice/WavIo.cs — minimal RIFF/WAVE reading and PCM-16
// packing, so a reference recording can become the float samples a voice needs.
//
// Parity is asserted against fixtures/voice_wav_io.json.

import Foundation

/// Minimal RIFF/WAVE reading and PCM-16 packing.
public enum VoiceWavIo {

    /// Mimi's sample rate — what `readMono24k` resamples to.
    public static let targetRate = 24000

    public enum WavError: Error, CustomStringConvertible {
        case notRiff(String)
        case noUsableChunk(String)
        case unsupported(format: Int, bits: Int)

        public var description: String {
            switch self {
            case .notRiff(let p):        return "'\(p)' is not a RIFF/WAVE file."
            case .noUsableChunk(let p):  return "'\(p)' has no usable fmt/data chunk."
            case .unsupported(let f, let b):
                return "WAV format \(f) at \(b) bits is not decoded by this reader."
            }
        }
    }

    /// Read a WAV file as mono float samples at 24 kHz, resampling if needed.
    public static func readMono24k(path: String, maxSeconds: Int = 30) throws -> [Float] {
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        var (samples, rate, channels) = try read(data, path: path)

        if channels > 1 {
            var mono = [Float](repeating: 0, count: samples.count / channels)
            for i in 0..<mono.count {
                var sum: Float = 0
                for c in 0..<channels { sum += samples[i * channels + c] }
                mono[i] = sum / Float(channels)
            }
            samples = mono
        }

        if rate != targetRate { samples = resample(samples, from: rate, to: targetRate) }

        let cap = maxSeconds * targetRate
        if samples.count > cap { samples = Array(samples[0..<cap]) }
        return samples
    }

    /// Pack float samples in [-1,1] as little-endian signed 16-bit PCM.
    public static func toPcm16(_ samples: [Float]) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count {
            let v = Int16(max(-1, min(1, samples[i])) * Float(Int16.max))
            let u = UInt16(bitPattern: v)
            bytes[i * 2] = UInt8(u & 0xFF)
            bytes[i * 2 + 1] = UInt8(u >> 8)
        }
        return bytes
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// Parse a RIFF/WAVE buffer into (samples, rate, channels).
    public static func read(_ raw: Data, path: String = "<memory>") throws
        -> (samples: [Float], rate: Int, channels: Int)
    {
        let bytes = [UInt8](raw)
        guard bytes.count >= 12,
              be32(bytes, 0) == 0x52494646,          // "RIFF"
              be32(bytes, 8) == 0x57415645           // "WAVE"
        else { throw WavError.notRiff(path) }

        var format = 0, channels = 0, rate = 0, bits = 0
        var offset = 12
        var dataRange: Range<Int>? = nil

        // WALK THE CHUNKS. A WAV written by anything other than the simplest
        // encoder carries LIST/fact/cue chunks before the data, and assuming
        // data starts at byte 44 reads metadata as audio — which sounds like a
        // short burst of noise before the real recording.
        while offset + 8 <= bytes.count {
            let id = be32(bytes, offset)
            var size = Int(le32(bytes, offset + 4))
            let body = offset + 8
            if size < 0 || body + size > bytes.count { size = bytes.count - body }

            if id == 0x666D7420 {                     // "fmt "
                format = Int(le16(bytes, body))
                channels = Int(le16(bytes, body + 2))
                rate = Int(le32(bytes, body + 4))
                bits = Int(le16(bytes, body + 14))
            } else if id == 0x64617461 {              // "data"
                dataRange = body..<(body + size)
            }

            offset = body + size + (size & 1)         // chunks are word-aligned
        }

        guard channels > 0, rate > 0, let range = dataRange, !range.isEmpty else {
            throw WavError.noUsableChunk(path)
        }
        let data = Array(bytes[range])

        // 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format
        // lives in a sub-chunk — treated as PCM here, which is what it is in
        // every file the voice stack has met.
        // BY BYTE OFFSET, NOT BY SLICE. An ArraySlice keeps its PARENT's indices,
        // so `data[3..<5][0]` traps rather than returning the first element —
        // and it traps at runtime, in a decoder, on real audio. Closing over
        // `data` and indexing from a plain Int sidesteps the whole hazard.
        let samples: [Float]
        switch (format, bits) {
        case (1, 8), (0xFFFE, 8):
            samples = map(data, 1) { Float(Int(data[$0]) - 128) / 128 }
        case (1, 16), (0xFFFE, 16):
            samples = map(data, 2) {
                Float(Int16(bitPattern: UInt16(data[$0]) | (UInt16(data[$0 + 1]) << 8))) / 32768
            }
        case (1, 24), (0xFFFE, 24):
            samples = map(data, 3) {
                let v = Int32(data[$0]) | (Int32(data[$0 + 1]) << 8) | (Int32(data[$0 + 2]) << 16)
                // Sign-extend the 24-bit value. Swift's shift operators are
                // non-associative, so the parentheses are required, not style.
                return Float((v << 8) >> 8) / 8388608
            }
        case (1, 32), (0xFFFE, 32):
            samples = map(data, 4) { Float(Int32(bitPattern: le32(data, $0))) / 2147483648 }
        case (3, 32):
            samples = map(data, 4) { Float(bitPattern: le32(data, $0)) }
        default:
            throw WavError.unsupported(format: format, bits: bits)
        }

        return (samples, rate, channels)
    }

    /// Convert `count` fixed-width frames, handing the closure a BYTE OFFSET.
    private static func map(_ data: [UInt8], _ stride: Int,
                            _ convert: (Int) -> Float) -> [Float] {
        let count = data.count / stride
        var out = [Float](repeating: 0, count: count)
        for i in 0..<count { out[i] = convert(i * stride) }
        return out
    }

    /// Linear resample. Adequate here: the target is a speaker embedding, not playback.
    private static func resample(_ input: [Float], from: Int, to: Int) -> [Float] {
        if input.isEmpty { return input }
        let count = Int((Double(input.count) * Double(to) / Double(from)).rounded())
        var output = [Float](repeating: 0, count: max(count, 1))
        let step = Double(input.count - 1) / Double(max(output.count - 1, 1))
        for i in 0..<output.count {
            let x = Double(i) * step
            let lo = Int(x)
            let hi = min(lo + 1, input.count - 1)
            output[i] = Float(Double(input[lo]) + (Double(input[hi]) - Double(input[lo])) * (x - Double(lo)))
        }
        return output
    }

    private static func be32(_ b: [UInt8], _ i: Int) -> UInt32 {
        UInt32(b[i]) << 24 | UInt32(b[i + 1]) << 16 | UInt32(b[i + 2]) << 8 | UInt32(b[i + 3])
    }
    private static func le32(_ b: [UInt8], _ i: Int) -> UInt32 {
        UInt32(b[i]) | UInt32(b[i + 1]) << 8 | UInt32(b[i + 2]) << 16 | UInt32(b[i + 3]) << 24
    }
    private static func le16(_ b: [UInt8], _ i: Int) -> UInt16 {
        UInt16(b[i]) | UInt16(b[i + 1]) << 8
    }
}
