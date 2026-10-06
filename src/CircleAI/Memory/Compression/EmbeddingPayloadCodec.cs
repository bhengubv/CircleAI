// EmbeddingPayloadCodec.cs
//
// Glue between TurboQuantPayload (norm + packed indices) and the byte[] /
// string formats the memory stores can persist.
//
// Wire format (binary):
//   bytes [0..3]   = bit-width as uint32 little-endian
//   bytes [4..7]   = dimension as uint32 little-endian
//   bytes [8..11]  = norm as float32 little-endian
//   bytes [12..]   = packed indices
//
// Base64-encoded for tag storage. The bit-width + dimension are part of
// the payload so callers can decode without out-of-band metadata.

using System;
using System.Buffers.Binary;
using CircleAI.Core.Compression;

namespace CircleAI.Memory.Compression;

/// <summary>
/// Encodes and decodes TurboQuant-compressed embeddings as binary blobs
/// suitable for persistence (e.g. in a tag value).
/// </summary>
public static class EmbeddingPayloadCodec
{
    /// <summary>Magic header bytes that identify a TurboQuant-encoded blob.</summary>
    public static readonly ReadOnlyMemory<byte> Magic = new byte[] { 0x54, 0x51, 0x33, 0x01 }; // "TQ3\1"

    /// <summary>
    /// Encodes <paramref name="vector"/> at <paramref name="bitsPerDim"/>
    /// bits per coordinate into a self-describing byte payload.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<float> vector, int bitsPerDim)
    {
        if (vector.Length <= 1)
            throw new ArgumentException("Vector must have length > 1.", nameof(vector));

        var payload = TurboQuantCodec.Encode(vector, bitsPerDim);
        var buf = new byte[Magic.Length + 4 + 4 + 4 + payload.PackedIndices.Length];
        var span = buf.AsSpan();
        int o = 0;
        Magic.Span.CopyTo(span);
        o += Magic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(o), (uint)bitsPerDim); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(o), (uint)vector.Length); o += 4;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o), payload.Norm); o += 4;
        payload.PackedIndices.AsSpan().CopyTo(span.Slice(o));
        return buf;
    }

    /// <summary>
    /// Decodes a byte payload produced by <see cref="Encode"/> back into a
    /// float vector.
    /// </summary>
    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length + 12)
            throw new ArgumentException("Payload too short.", nameof(bytes));
        if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic.Span))
            throw new ArgumentException("Magic header missing — not a TurboQuant payload.", nameof(bytes));

        int o = Magic.Length;
        int bitsPerDim = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(o)); o += 4;
        int dim = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(o)); o += 4;
        float norm = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(o)); o += 4;
        var packed = bytes.Slice(o).ToArray();
        var payload = new TurboQuantPayload(norm, packed);
        return TurboQuantCodec.Decode(payload, dim, bitsPerDim);
    }

    /// <summary>
    /// Returns true when the byte span begins with the TurboQuant magic
    /// header — useful for stores that mix raw FP32 + compressed entries.
    /// </summary>
    public static bool IsEncoded(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Magic.Length &&
        bytes.Slice(0, Magic.Length).SequenceEqual(Magic.Span);

    /// <summary>Convenience: encode + base64-stringify for tag-style storage.</summary>
    public static string EncodeBase64(ReadOnlySpan<float> vector, int bitsPerDim)
        => Convert.ToBase64String(Encode(vector, bitsPerDim));

    /// <summary>Convenience: base64-decode + decode.</summary>
    public static float[] DecodeBase64(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        var bytes = Convert.FromBase64String(base64);
        return Decode(bytes);
    }
}
