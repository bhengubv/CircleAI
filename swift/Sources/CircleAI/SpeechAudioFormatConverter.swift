// SpeechAudioFormatConverter.swift
//
// Port of CircleAI.Speech.AudioFormatConverter (AudioFormatConverter.cs).
//
// Stateless audio-format conversion:
//   - mu-law 8 kHz   <-> PCM-16 16 kHz / 24 kHz
//   - a-law  8 kHz   <-> PCM-16 16 kHz / 24 kHz
//   - PCM-16 N kHz   -> PCM-16 M kHz  (linear interpolation)
//
// The G.711 mu-law / a-law bit arithmetic and the linear resampler are ported
// exactly, honouring C# integer-promotion + truncating-cast semantics so the
// byte output is identical.

import Foundation

/// Carrier-native audio formats. Port of `CircleAI.Speech.AudioCodec`.
public enum AudioCodec: Sendable, Equatable, Codable {
    /// 16-bit signed linear PCM, little-endian, mono.
    case pcm16
    /// G.711 μ-law (telephony, North America / Japan).
    case muLaw
    /// G.711 A-law (telephony, Europe).
    case aLaw
}

/// Error thrown for unsupported conversion arguments (mirrors C# throws).
public enum AudioFormatConverterError: Error, Equatable {
    case argumentOutOfRange(String)
    case notSupported(String)
}

/// Stateless audio-format converter. Port of `CircleAI.Speech.AudioFormatConverter`.
public enum AudioFormatConverter {

    /// Convert audio from one (codec, sample rate) to another. Returns a freshly
    /// allocated output buffer; caller does NOT need to size it.
    public static func convert(
        input: [UInt8],
        inputCodec: AudioCodec,
        inputSampleRateHz: Int,
        outputCodec: AudioCodec,
        outputSampleRateHz: Int
    ) throws -> [UInt8] {
        if inputSampleRateHz <= 0 { throw AudioFormatConverterError.argumentOutOfRange("inputSampleRateHz") }
        if outputSampleRateHz <= 0 { throw AudioFormatConverterError.argumentOutOfRange("outputSampleRateHz") }

        // 1) Decode source to PCM-16.
        let pcmIn: [UInt8]
        switch inputCodec {
        case .pcm16: pcmIn = input
        case .muLaw: pcmIn = decodeMuLawToPcm16(input)
        case .aLaw:  pcmIn = decodeALawToPcm16(input)
        }

        // 2) Resample if needed.
        let pcmResampled = inputSampleRateHz == outputSampleRateHz
            ? pcmIn
            : resamplePcm16Linear(pcmIn, fromHz: inputSampleRateHz, toHz: outputSampleRateHz)

        // 3) Encode to target codec.
        switch outputCodec {
        case .pcm16: return pcmResampled
        case .muLaw: return encodePcm16ToMuLaw(pcmResampled)
        case .aLaw:  return encodePcm16ToALaw(pcmResampled)
        }
    }

    // ===== μ-law =====

    public static func decodeMuLawToPcm16(_ mulaw: [UInt8]) -> [UInt8] {
        var pcm = [UInt8](repeating: 0, count: mulaw.count * 2)
        for i in 0..<mulaw.count {
            let s = muLawToLinear(mulaw[i])
            writeInt16LE(&pcm, i * 2, s)
        }
        return pcm
    }

    public static func encodePcm16ToMuLaw(_ pcm: [UInt8]) -> [UInt8] {
        let samples = pcm.count / 2
        var mulaw = [UInt8](repeating: 0, count: samples)
        for i in 0..<samples {
            let s = readInt16LE(pcm, i * 2)
            mulaw[i] = linearToMuLaw(s)
        }
        return mulaw
    }

    private static func muLawToLinear(_ muByte: UInt8) -> Int16 {
        // G.711 μ-law decode (ITU-T G.711).
        let mu = Int(~muByte)          // (byte)~mu, widened to int for masking
        let sign = mu & 0x80
        let exponent = (mu >> 4) & 0x07
        let mantissa = mu & 0x0F
        let magnitude = ((mantissa << 3) + 0x84) << exponent
        let sample = magnitude - 0x84
        let value = sign != 0 ? -sample : sample
        return Int16(truncatingIfNeeded: value)
    }

    private static func linearToMuLaw(_ pcm: Int16) -> UInt8 {
        let bias = 0x84
        let clip = 32635
        let sign = (Int(pcm) >> 8) & 0x80   // 0 or 0x80
        var v = Int(pcm)
        if sign != 0 { v = -v }
        if v > clip { v = clip }
        v += bias

        let exponent: Int
        if v >= 0x4000 { exponent = 7 }
        else if v >= 0x2000 { exponent = 6 }
        else if v >= 0x1000 { exponent = 5 }
        else if v >= 0x0800 { exponent = 4 }
        else if v >= 0x0400 { exponent = 3 }
        else if v >= 0x0200 { exponent = 2 }
        else if v >= 0x0100 { exponent = 1 }
        else { exponent = 0 }

        let mantissa = (v >> (exponent + 3)) & 0x0F
        let byteVal = ~(sign | (exponent << 4) | mantissa)
        return UInt8(truncatingIfNeeded: byteVal)
    }

    // ===== a-law =====

    public static func decodeALawToPcm16(_ alaw: [UInt8]) -> [UInt8] {
        var pcm = [UInt8](repeating: 0, count: alaw.count * 2)
        for i in 0..<alaw.count {
            let s = aLawToLinear(alaw[i])
            writeInt16LE(&pcm, i * 2, s)
        }
        return pcm
    }

    public static func encodePcm16ToALaw(_ pcm: [UInt8]) -> [UInt8] {
        let samples = pcm.count / 2
        var alaw = [UInt8](repeating: 0, count: samples)
        for i in 0..<samples {
            let s = readInt16LE(pcm, i * 2)
            alaw[i] = linearToALaw(s)
        }
        return alaw
    }

    private static func aLawToLinear(_ aByte: UInt8) -> Int16 {
        let a = Int(aByte ^ 0x55)
        let sign = a & 0x80
        let exponent = (a >> 4) & 0x07
        let mantissa = a & 0x0F
        let magnitude: Int
        if exponent != 0 {
            magnitude = ((mantissa << 4) + 0x108) << (exponent - 1)
        } else {
            magnitude = (mantissa << 4) + 0x08
        }
        let value = sign != 0 ? -magnitude : magnitude
        return Int16(truncatingIfNeeded: value)
    }

    private static func linearToALaw(_ pcm: Int16) -> UInt8 {
        let sign = (Int(pcm) >> 8) & 0x80
        var v = Int(pcm)
        if sign != 0 { v = -v }
        if v > 0x7FFF { v = 0x7FFF }

        let exponent: Int
        let mantissa: Int
        if v < 256 {
            exponent = 0
            mantissa = v >> 4
        } else {
            if v >= 0x4000 { exponent = 7 }
            else if v >= 0x2000 { exponent = 6 }
            else if v >= 0x1000 { exponent = 5 }
            else if v >= 0x0800 { exponent = 4 }
            else if v >= 0x0400 { exponent = 3 }
            else if v >= 0x0200 { exponent = 2 }
            else { exponent = 1 }
            mantissa = (v >> (exponent + 3)) & 0x0F
        }
        let byteVal = (sign | (exponent << 4) | mantissa) ^ 0x55
        return UInt8(truncatingIfNeeded: byteVal)
    }

    // ===== resample (linear interpolation) =====

    public static func resamplePcm16Linear(_ pcm: [UInt8], fromHz: Int, toHz: Int) -> [UInt8] {
        if fromHz == toHz { return pcm }
        let srcSamples = pcm.count / 2
        // (int)((long)srcSamples * toHz / fromHz)
        let dstSamples = Int((Int64(srcSamples) * Int64(toHz)) / Int64(fromHz))
        var dst = [UInt8](repeating: 0, count: dstSamples * 2)
        for i in 0..<dstSamples {
            let srcIdx = Double(i) * Double(fromHz) / Double(toHz)
            let idx0 = Int(srcIdx.rounded(.down))
            let idx1 = min(idx0 + 1, srcSamples - 1)
            let frac = srcIdx - Double(idx0)
            let s0 = readInt16LE(pcm, idx0 * 2)
            let s1 = readInt16LE(pcm, idx1 * 2)
            // (short)(s0 + (s1 - s0) * frac) — double math, truncating cast.
            let interp = Double(s0) + Double(Int(s1) - Int(s0)) * frac
            let s = Int16(truncatingIfNeeded: Int(interp))
            writeInt16LE(&dst, i * 2, s)
        }
        return dst
    }
}
