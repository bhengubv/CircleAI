// DtmfToneGenerator.cs
//
// (3.3.0) Generate the dual-tone audio for DTMF digits, and a helper
// that sends them through any ICallSession via SendAudioAsync — works
// regardless of whether the carrier supports out-of-band DTMF.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Stateless DTMF audio generator.</summary>
public static class DtmfToneGenerator
{
    /// <summary>(3.3.0) Standard DTMF frequencies (low row × high column).</summary>
    private static readonly Dictionary<char, (int Low, int High)> Frequencies = new()
    {
        ['1'] = (697, 1209),
        ['2'] = (697, 1336),
        ['3'] = (697, 1477),
        ['A'] = (697, 1633),
        ['4'] = (770, 1209),
        ['5'] = (770, 1336),
        ['6'] = (770, 1477),
        ['B'] = (770, 1633),
        ['7'] = (852, 1209),
        ['8'] = (852, 1336),
        ['9'] = (852, 1477),
        ['C'] = (852, 1633),
        ['*'] = (941, 1209),
        ['0'] = (941, 1336),
        ['#'] = (941, 1477),
        ['D'] = (941, 1633),
    };

    /// <summary>(3.3.0) Generate one PCM-16 mono buffer for the digit at the given sample rate.</summary>
    /// <param name="digit">DTMF digit: 0-9, *, #, A, B, C, D.</param>
    /// <param name="sampleRateHz">Output sample rate.</param>
    /// <param name="durationMs">Tone duration. Default 150 ms.</param>
    /// <param name="amplitude">Peak amplitude 0..1. Default 0.5.</param>
    public static byte[] Generate(char digit, int sampleRateHz, int durationMs = 150, float amplitude = 0.5f)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (durationMs   <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        var key = char.ToUpperInvariant(digit);
        if (!Frequencies.TryGetValue(key, out var pair))
        {
            throw new ArgumentException($"Unsupported DTMF digit '{digit}'.", nameof(digit));
        }

        int samples = sampleRateHz * durationMs / 1000;
        var buf = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            var t = (double)i / sampleRateHz;
            var s = 0.5 * amplitude * (Math.Sin(2 * Math.PI * pair.Low * t) + Math.Sin(2 * Math.PI * pair.High * t));
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2, 2), (short)(Math.Clamp(s, -1, 1) * short.MaxValue));
        }
        return buf;
    }

    /// <summary>(3.3.0) Generate a full string of digits with gap silence between them.</summary>
    public static byte[] GenerateSequence(
        string digits,
        int    sampleRateHz,
        int    toneDurationMs = 150,
        int    interDigitGapMs = 50,
        float  amplitude       = 0.5f)
    {
        if (string.IsNullOrEmpty(digits)) return Array.Empty<byte>();
        int gapSamples = sampleRateHz * interDigitGapMs / 1000;
        var gap = new byte[gapSamples * 2];

        using var ms = new System.IO.MemoryStream();
        for (int i = 0; i < digits.Length; i++)
        {
            var tone = Generate(digits[i], sampleRateHz, toneDurationMs, amplitude);
            ms.Write(tone, 0, tone.Length);
            if (i < digits.Length - 1)
            {
                ms.Write(gap, 0, gap.Length);
            }
        }
        return ms.ToArray();
    }

    /// <summary>(3.3.0) Send <paramref name="digits"/> over the call via in-band tones.</summary>
    public static async ValueTask SendThroughSessionAsync(
        ICallSession      session,
        string            digits,
        int               sampleRateHz   = 8000,
        int               toneDurationMs = 150,
        int               interDigitGapMs = 50,
        CancellationToken ct             = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(digits)) return;

        var pcm = GenerateSequence(digits, sampleRateHz, toneDurationMs, interDigitGapMs);
        var format = sampleRateHz switch
        {
            8000  => CallMediaFormat.Mulaw8000,
            16000 => CallMediaFormat.Pcm16000,
            24000 => CallMediaFormat.Pcm24000,
            _     => CallMediaFormat.Pcm16000,
        };
        await session.SendAudioAsync(new AudioFrame(pcm, format, TimeSpan.Zero), ct).ConfigureAwait(false);
    }
}
