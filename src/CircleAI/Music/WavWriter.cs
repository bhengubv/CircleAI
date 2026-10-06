using System.Buffers.Binary;

namespace CircleAI.Music;

/// <summary>
/// Writes a canonical 44-byte-header RIFF/WAVE file wrapping linear PCM. Pure
/// managed, endian-correct, zero dependencies — this is what makes "an artifact
/// always exists offline" literally true.
/// </summary>
public static class WavWriter
{
    private const int HeaderLength = 44;
    private const short PcmFormatTag = 1;

    /// <summary>
    /// Wrap raw interleaved PCM bytes in a complete WAV container.
    /// </summary>
    /// <param name="pcm">Interleaved little-endian PCM sample bytes.</param>
    /// <param name="format">Format describing <paramref name="pcm"/>.</param>
    /// <returns>A ready-to-save <c>.wav</c> byte array.</returns>
    public static byte[] ToWav(ReadOnlySpan<byte> pcm, AudioPcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        int dataLength = pcm.Length;
        var buffer = new byte[HeaderLength + dataLength];
        Span<byte> span = buffer;

        // ---- RIFF chunk descriptor ----
        WriteTag(span, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        WriteTag(span, 8, "WAVE");

        // ---- "fmt " sub-chunk ----
        WriteTag(span, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // PCM fmt size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], PcmFormatTag);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], format.ByteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)format.BlockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)format.BitsPerSample);

        // ---- "data" sub-chunk ----
        WriteTag(span, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        pcm.CopyTo(span[HeaderLength..]);

        return buffer;
    }

    /// <summary>
    /// Write a WAV container for <paramref name="pcm"/> to a stream.
    /// </summary>
    public static async Task WriteAsync(
        Stream destination,
        ReadOnlyMemory<byte> pcm,
        AudioPcmFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(format);

        byte[] wav = ToWav(pcm.Span, format);
        await destination.WriteAsync(wav, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteTag(Span<byte> destination, int offset, string tag)
    {
        // WAV chunk tags are 4 ASCII characters.
        for (int i = 0; i < tag.Length; i++)
        {
            destination[offset + i] = (byte)tag[i];
        }
    }
}
