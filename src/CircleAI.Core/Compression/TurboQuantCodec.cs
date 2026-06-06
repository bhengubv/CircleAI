// TurboQuantCodec.cs
//
// CircleAI's pure-C# implementation of TurboQuant — Google Research's
// data-oblivious vector quantizer (arxiv:2504.19874). Designed for two use
// cases in the personhood engine:
//
//   1. Embedding compression (TurboVec-style): 1536-dim FP32 (6144 bytes)
//      → 384 bytes at 2-bit (16× shrink). Drop-in for IEpisodicMemoryStore /
//      MultimodalMemoryEntry retrieval.
//
//   2. KV cache compression for transformer inference: same algorithm,
//      applied per-head per-token. 6× memory + 8× attention speedup with
//      the Phase 4.1 native port.
//
// Algorithm (per-vector):
//   norm = ||v||
//   v_unit = v / norm
//   v_rot = R · v_unit          where R is a fixed orthogonal matrix
//   For each coord j:
//     idx[j] = argmin_k |v_rot[j] - centroid_k|
//                                 (where centroids are Lloyd-Max-optimal
//                                  for Beta((d-1)/2, (d-1)/2))
//   packed = BitPack(idx)
//   payload = (norm, packed)
//
// Decode is the reverse: unpack indices → centroid lookup → inverse rotation
// → multiply by norm.
//
// Compression ratio at b bits/dim: 32/b × ratio (e.g. 2 bits → 16× shrink
// of the float payload, plus a 4-byte norm header that's amortised over
// the whole vector).

using System;

namespace CircleAI.Core.Compression;

/// <summary>
/// Output of <see cref="TurboQuantCodec.Encode"/>.
/// </summary>
/// <param name="Norm">L2 norm of the original vector — needed to reconstruct magnitude.</param>
/// <param name="PackedIndices">Bit-packed Lloyd-Max bin indices, one per dimension.</param>
public sealed record TurboQuantPayload(float Norm, byte[] PackedIndices);

/// <summary>
/// TurboQuant encoder / decoder.
/// </summary>
public static class TurboQuantCodec
{
    /// <summary>
    /// Encodes a float vector at <paramref name="bitsPerDim"/> bits per
    /// dimension. Higher bits = better fidelity, larger payload.
    /// Typical: 2 bits (16× compression), 3 bits (~10× compression).
    /// </summary>
    public static TurboQuantPayload Encode(ReadOnlySpan<float> vector, int bitsPerDim)
    {
        if (vector.Length <= 1)
            throw new ArgumentException("Vector must have length > 1.", nameof(vector));
        if (bitsPerDim is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim), "bitsPerDim must be 1..8.");

        var dim = vector.Length;

        // 1. Norm.
        double sumSq = 0.0;
        for (int i = 0; i < dim; i++) sumSq += (double)vector[i] * vector[i];
        float norm = (float)Math.Sqrt(sumSq);

        // Edge case — zero vector. Round-trip preserves the all-zero shape.
        if (norm < 1e-20f)
        {
            var allZeros = new byte[(dim * bitsPerDim + 7) / 8];
            return new TurboQuantPayload(0f, allZeros);
        }

        // 2. Unit-normalise.
        var unit = new float[dim];
        float invNorm = 1f / norm;
        for (int i = 0; i < dim; i++) unit[i] = vector[i] * invNorm;

        // 3. Rotate.
        var rotated = new float[dim];
        OrthogonalRotation.Rotate(dim, unit, rotated);

        // 4. Quantize per-coordinate.
        var codebook = BetaLloydMaxCodebook.Get(bitsPerDim, dim);
        var indices = new ushort[dim];
        var boundariesSpan = codebook.Boundaries.AsSpan();
        for (int i = 0; i < dim; i++)
        {
            indices[i] = BetaLloydMaxCodebook.BinFor(rotated[i], boundariesSpan);
        }

        // 5. Pack.
        var packed = BitPacker.Pack(indices, bitsPerDim);
        return new TurboQuantPayload(norm, packed);
    }

    /// <summary>
    /// Decodes a TurboQuant payload back into the original-magnitude vector
    /// (modulo quantization error).
    /// </summary>
    public static float[] Decode(TurboQuantPayload payload, int dim, int bitsPerDim)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (dim <= 1) throw new ArgumentOutOfRangeException(nameof(dim));
        if (bitsPerDim is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim));

        var result = new float[dim];
        if (payload.Norm == 0f) return result; // all zeros

        // 1. Unpack indices.
        var indices = BitPacker.Unpack(payload.PackedIndices, dim, bitsPerDim);

        // 2. Map indices → centroids (rotated-space reconstruction).
        var rotated = new float[dim];
        var centroids = BetaLloydMaxCodebook.Get(bitsPerDim, dim).Centroids;
        for (int i = 0; i < dim; i++) rotated[i] = centroids[indices[i]];

        // 3. Inverse rotation.
        var unit = new float[dim];
        OrthogonalRotation.Unrotate(dim, rotated, unit);

        // 4. Scale by stored norm.
        var scale = payload.Norm;
        for (int i = 0; i < dim; i++) result[i] = unit[i] * scale;
        return result;
    }

    /// <summary>
    /// Convenience: encode and immediately decode, returning the reconstruction.
    /// Useful for benchmarking quantization error.
    /// </summary>
    public static float[] RoundTrip(ReadOnlySpan<float> vector, int bitsPerDim)
    {
        var encoded = Encode(vector, bitsPerDim);
        return Decode(encoded, vector.Length, bitsPerDim);
    }

    /// <summary>
    /// Returns the bytes-per-vector required at the given <paramref name="dim"/>
    /// and <paramref name="bitsPerDim"/> (excluding the 4-byte norm header).
    /// </summary>
    public static int PayloadByteCount(int dim, int bitsPerDim) =>
        (dim * bitsPerDim + 7) / 8;

    /// <summary>
    /// Returns the compression ratio vs raw FP32 (vector bytes / encoded
    /// bytes including norm).
    /// </summary>
    public static double CompressionRatio(int dim, int bitsPerDim)
    {
        var raw = dim * 4;
        var encoded = PayloadByteCount(dim, bitsPerDim) + 4 /* norm */;
        return (double)raw / encoded;
    }
}
