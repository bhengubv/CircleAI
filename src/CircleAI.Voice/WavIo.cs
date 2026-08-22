using System.Buffers.Binary;

namespace CircleAI.Voice;

/// <summary>
/// Minimal RIFF/WAVE reading and PCM-16 packing, for the voice stack's own use.
/// </summary>
/// <remarks>
/// Deliberately small: it exists so a reference recording can become the float
/// samples <see cref="PocketTtsEngine"/> needs, on every platform CircleAI
/// ships to, without dragging in an audio library. It reads what the voice
/// stack actually encounters — PCM 8/16/24/32-bit and IEEE float — and refuses
/// anything else loudly rather than producing noise.
/// </remarks>
public static class WavIo
{
    private const int TargetRate = PocketTtsEngine.SampleRate;

    /// <summary>
    /// Read a WAV file as mono float samples at 24 kHz, resampling if needed.
    /// </summary>
    public static float[] ReadMono24k(string path, int maxSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var (samples, rate, channels) = Read(path);

        if (channels > 1)
        {
            var mono = new float[samples.Length / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
                mono[i] = sum / channels;
            }
            samples = mono;
        }

        if (rate != TargetRate) samples = Resample(samples, rate, TargetRate);

        var cap = maxSeconds * TargetRate;
        if (samples.Length > cap) samples = samples[..cap];
        return samples;
    }

    /// <summary>Pack float samples in [-1,1] as little-endian signed 16-bit PCM.</summary>
    public static byte[] ToPcm16(IReadOnlyList<float> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
        {
            var v = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), v);
        }
        return bytes;
    }

    private static (float[] Samples, int Rate, int Channels) Read(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 12 ||
            BinaryPrimitives.ReadUInt32BigEndian(raw) != 0x52494646 ||        // "RIFF"
            BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8)) != 0x57415645) // "WAVE"
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        var offset = 12;
        ReadOnlySpan<byte> data = default;

        // WALK THE CHUNKS. A WAV written by anything other than the simplest
        // encoder carries LIST/fact/cue chunks before the data, and assuming
        // data starts at byte 44 reads metadata as audio — which sounds like a
        // short burst of noise before the real recording.
        while (offset + 8 <= raw.Length)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset));
            var size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset + 4));
            var body = offset + 8;
            if (size < 0 || body + size > raw.Length) size = raw.Length - body;

            if (id == 0x666D7420)                    // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(body + 14));
            }
            else if (id == 0x64617461)               // "data"
            {
                data = raw.AsSpan(body, size);
            }

            offset = body + size + (size & 1);       // chunks are word-aligned
        }

        if (channels == 0 || rate == 0 || data.IsEmpty)
            throw new InvalidDataException($"'{path}' has no usable fmt/data chunk.");

        // 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format
        // lives in a sub-chunk — treated as PCM here, which is what it is in
        // every file the voice stack has met.
        var samples = (format, bits) switch
        {
            (1 or 0xFFFE, 8)  => Map(data, 1, b => (b[0] - 128) / 128f),
            (1 or 0xFFFE, 16) => Map(data, 2, b => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f),
            (1 or 0xFFFE, 24) => Map(data, 3, b => ((b[2] << 16 | b[1] << 8 | b[0]) << 8 >> 8) / 8388608f),
            (1 or 0xFFFE, 32) => Map(data, 4, b => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f),
            (3, 32)           => Map(data, 4, b => BitConverter.ToSingle(b)),
            _ => throw new NotSupportedException(
                     $"'{path}' is WAV format {format} at {bits} bits, which this reader does not decode."),
        };

        return (samples, rate, channels);
    }

    private static float[] Map(ReadOnlySpan<byte> data, int stride, Convert convert)
    {
        var count = data.Length / stride;
        var result = new float[count];
        for (var i = 0; i < count; i++) result[i] = convert(data.Slice(i * stride, stride));
        return result;
    }

    private delegate float Convert(ReadOnlySpan<byte> bytes);

    /// <summary>Linear resample. Adequate here: the target is a speaker embedding, not playback.</summary>
    private static float[] Resample(float[] input, int from, int to)
    {
        if (input.Length == 0) return input;
        var count = (int)Math.Round((double)input.Length * to / from);
        var output = new float[Math.Max(count, 1)];
        var step = (double)(input.Length - 1) / Math.Max(output.Length - 1, 1);
        for (var i = 0; i < output.Length; i++)
        {
            var x = i * step;
            var lo = (int)x;
            var hi = Math.Min(lo + 1, input.Length - 1);
            output[i] = (float)(input[lo] + (input[hi] - input[lo]) * (x - lo));
        }
        return output;
    }
}
