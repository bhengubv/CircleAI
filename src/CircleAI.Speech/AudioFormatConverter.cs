// AudioFormatConverter.cs
//
// (3.3.0) Audio format conversion. Phone carriers feed mu-law / a-law
// at 8 kHz; cloud STT/TTS speak linear PCM at 16/24/44.1 kHz. The
// converter handles every common path:
//   - mu-law 8 kHz   ↔ PCM-16 16 kHz / 24 kHz
//   - a-law  8 kHz   ↔ PCM-16 16 kHz / 24 kHz
//   - PCM-16 N kHz   → PCM-16 M kHz  (linear interpolation)

using System;
using System.Buffers.Binary;

namespace CircleAI.Speech;

/// <summary>(3.3.0) Carrier-native audio formats we know how to convert.</summary>
public enum AudioCodec
{
    /// <summary>16-bit signed linear PCM, little-endian, mono.</summary>
    Pcm16,
    /// <summary>G.711 μ-law (telephony, North America / Japan).</summary>
    MuLaw,
    /// <summary>G.711 A-law (telephony, Europe).</summary>
    ALaw,
}

/// <summary>(3.3.0) Stateless audio-format converter.</summary>
public static class AudioFormatConverter
{
    /// <summary>
    /// (3.3.0) Convert audio from one (codec, sample rate) to another. Returns
    /// the freshly allocated output buffer; caller does NOT need to size it.
    /// </summary>
    public static byte[] Convert(
        ReadOnlySpan<byte> input,
        AudioCodec         inputCodec,
        int                inputSampleRateHz,
        AudioCodec         outputCodec,
        int                outputSampleRateHz)
    {
        if (inputSampleRateHz <= 0)  throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        if (outputSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRateHz));

        // 1) Decode source to PCM-16.
        var pcmIn = inputCodec switch
        {
            AudioCodec.Pcm16  => input.ToArray(),
            AudioCodec.MuLaw  => DecodeMuLawToPcm16(input),
            AudioCodec.ALaw   => DecodeALawToPcm16(input),
            _                 => throw new NotSupportedException($"Unknown input codec {inputCodec}"),
        };

        // 2) Resample if needed.
        var pcmResampled = inputSampleRateHz == outputSampleRateHz
            ? pcmIn
            : ResamplePcm16Linear(pcmIn, inputSampleRateHz, outputSampleRateHz);

        // 3) Encode to target codec.
        return outputCodec switch
        {
            AudioCodec.Pcm16 => pcmResampled,
            AudioCodec.MuLaw => EncodePcm16ToMuLaw(pcmResampled),
            AudioCodec.ALaw  => EncodePcm16ToALaw(pcmResampled),
            _                => throw new NotSupportedException($"Unknown output codec {outputCodec}"),
        };
    }

    // ===== μ-law =====

    public static byte[] DecodeMuLawToPcm16(ReadOnlySpan<byte> mulaw)
    {
        var pcm = new byte[mulaw.Length * 2];
        for (int i = 0; i < mulaw.Length; i++)
        {
            short s = MuLawToLinear(mulaw[i]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), s);
        }
        return pcm;
    }

    public static byte[] EncodePcm16ToMuLaw(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        var mulaw = new byte[samples];
        for (int i = 0; i < samples; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
            mulaw[i] = LinearToMuLaw(s);
        }
        return mulaw;
    }

    private static short MuLawToLinear(byte mu)
    {
        // G.711 μ-law decode (ITU-T G.711).
        mu = (byte)~mu;
        int sign = mu & 0x80;
        int exponent = (mu >> 4) & 0x07;
        int mantissa = mu & 0x0F;
        int magnitude = ((mantissa << 3) + 0x84) << exponent;
        int sample = magnitude - 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }

    private static byte LinearToMuLaw(short pcm)
    {
        const int Bias = 0x84;
        const int Clip = 32635;
        int sign = (pcm >> 8) & 0x80;
        int v = pcm;
        if (sign != 0) v = -v;
        if (v > Clip) v = Clip;
        v += Bias;

        int exponent;
        if      (v >= 0x4000) exponent = 7;
        else if (v >= 0x2000) exponent = 6;
        else if (v >= 0x1000) exponent = 5;
        else if (v >= 0x0800) exponent = 4;
        else if (v >= 0x0400) exponent = 3;
        else if (v >= 0x0200) exponent = 2;
        else if (v >= 0x0100) exponent = 1;
        else                  exponent = 0;

        int mantissa = (v >> (exponent + 3)) & 0x0F;
        return (byte)(~(sign | (exponent << 4) | mantissa));
    }

    // ===== a-law =====

    public static byte[] DecodeALawToPcm16(ReadOnlySpan<byte> alaw)
    {
        var pcm = new byte[alaw.Length * 2];
        for (int i = 0; i < alaw.Length; i++)
        {
            short s = ALawToLinear(alaw[i]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), s);
        }
        return pcm;
    }

    public static byte[] EncodePcm16ToALaw(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        var alaw = new byte[samples];
        for (int i = 0; i < samples; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
            alaw[i] = LinearToALaw(s);
        }
        return alaw;
    }

    private static short ALawToLinear(byte a)
    {
        a ^= 0x55;
        int sign = a & 0x80;
        int exponent = (a >> 4) & 0x07;
        int mantissa = a & 0x0F;
        int magnitude;
        if (exponent != 0)
        {
            magnitude = ((mantissa << 4) + 0x108) << (exponent - 1);
        }
        else
        {
            magnitude = (mantissa << 4) + 0x08;
        }
        return (short)(sign != 0 ? -magnitude : magnitude);
    }

    private static byte LinearToALaw(short pcm)
    {
        int sign = (pcm >> 8) & 0x80;
        int v = pcm;
        if (sign != 0) v = -v;
        if (v > 0x7FFF) v = 0x7FFF;

        int exponent;
        int mantissa;
        if (v < 256)
        {
            exponent = 0;
            mantissa = v >> 4;
        }
        else
        {
            if      (v >= 0x4000) exponent = 7;
            else if (v >= 0x2000) exponent = 6;
            else if (v >= 0x1000) exponent = 5;
            else if (v >= 0x0800) exponent = 4;
            else if (v >= 0x0400) exponent = 3;
            else if (v >= 0x0200) exponent = 2;
            else                  exponent = 1;
            mantissa = (v >> (exponent + 3)) & 0x0F;
        }
        return (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
    }

    // ===== resample (linear interpolation) =====

    public static byte[] ResamplePcm16Linear(byte[] pcm, int fromHz, int toHz)
    {
        if (fromHz == toHz) return pcm;
        int srcSamples = pcm.Length / 2;
        int dstSamples = (int)((long)srcSamples * toHz / fromHz);
        var dst = new byte[dstSamples * 2];
        for (int i = 0; i < dstSamples; i++)
        {
            double srcIdx = (double)i * fromHz / toHz;
            int    idx0   = (int)Math.Floor(srcIdx);
            int    idx1   = Math.Min(idx0 + 1, srcSamples - 1);
            double frac   = srcIdx - idx0;
            short  s0     = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(idx0 * 2, 2));
            short  s1     = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(idx1 * 2, 2));
            short  s      = (short)(s0 + (s1 - s0) * frac);
            BinaryPrimitives.WriteInt16LittleEndian(dst.AsSpan(i * 2, 2), s);
        }
        return dst;
    }
}
