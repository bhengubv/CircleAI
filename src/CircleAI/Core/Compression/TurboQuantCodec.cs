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
//
// VERSION 2 (S3 of the Bonsai 2 plan) rotates with HadamardRotation - random
// signs and Walsh-Hadamard blocks, O(d log d) - instead of the dense matrix,
// and comes in the paper's two forms: Mse, the quantiser above at the full
// width, and Prod, the quantiser one bit narrower plus QJL's one bit a
// coordinate over the residual (QjlProjection), whose inner-product estimates
// carry no bias. A payload says which version and form wrote it; version 1 -
// every payload written before, and what the Rust, C++ and other ports agree
// on - decodes exactly as it always did.

using System;

namespace CircleAI.Core.Compression;

/// <summary>Which of TurboQuant's two forms wrote a version-2 payload.</summary>
public enum TurboQuantForm
{
    /// <summary>
    /// The MSE quantiser at the full bit width: the closest reconstruction, with every inner
    /// product shrunk toward zero by about the quantiser's distortion.
    /// </summary>
    Mse = 0,

    /// <summary>
    /// The MSE quantiser one bit narrower, then QJL's one bit a coordinate over what it left:
    /// inner products without bias, at the price of more variance.
    /// </summary>
    Prod = 1,
}

/// <summary>
/// Output of <see cref="TurboQuantCodec.Encode(ReadOnlySpan{float}, int)"/> and its version-2 form.
/// </summary>
/// <param name="Norm">L2 norm of the original vector — needed to reconstruct magnitude.</param>
/// <param name="PackedIndices">Bit-packed Lloyd-Max bin indices, one per dimension.</param>
public sealed record TurboQuantPayload(float Norm, byte[] PackedIndices)
{
    /// <summary>1: the dense rotation, the MSE form (every payload written before version 2). 2: the Hadamard rotation.</summary>
    public int Version { get; init; } = 1;

    /// <summary>The form a version-2 payload was written in; version 1 is always <see cref="TurboQuantForm.Mse"/>.</summary>
    public TurboQuantForm Form { get; init; } = TurboQuantForm.Mse;

    /// <summary>The norm of what the first stage left over, in the unit vector's rotated space (Prod only).</summary>
    public float ResidualNorm { get; init; }

    /// <summary>The signs of the residual's projection, one bit a coordinate (Prod only).</summary>
    public byte[]? Signs { get; init; }
}

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
    /// Encodes a float vector as a version-2 payload in <paramref name="form"/>, at
    /// <paramref name="bitsPerDim"/> bits a coordinate in all: Mse spends them all on the
    /// quantiser; Prod spends one fewer there and one on the residual's sign (so Prod needs two).
    /// </summary>
    public static TurboQuantPayload Encode(ReadOnlySpan<float> vector, int bitsPerDim, TurboQuantForm form)
    {
        if (vector.Length <= 1)
            throw new ArgumentException("Vector must have length > 1.", nameof(vector));
        var stageBits = StageBits(bitsPerDim, form);
        var dim = vector.Length;

        double sumSq = 0.0;
        for (int i = 0; i < dim; i++) sumSq += (double)vector[i] * vector[i];
        float norm = (float)Math.Sqrt(sumSq);
        if (norm < 1e-20f)
        {
            return new TurboQuantPayload(0f, new byte[(dim * stageBits + 7) / 8])
            {
                Version = 2,
                Form = form,
                Signs = form == TurboQuantForm.Prod ? new byte[(dim + 7) / 8] : null,
            };
        }

        var unit = new float[dim];
        float invNorm = 1f / norm;
        for (int i = 0; i < dim; i++) unit[i] = vector[i] * invNorm;
        var rotated = new float[dim];
        HadamardRotation.Rotate(unit, rotated);

        var codebook = BetaLloydMaxCodebook.Get(stageBits, dim);
        var indices = new ushort[dim];
        for (int i = 0; i < dim; i++) indices[i] = BetaLloydMaxCodebook.BinFor(rotated[i], codebook.Boundaries);
        var payload = new TurboQuantPayload(norm, BitPacker.Pack(indices, stageBits)) { Version = 2, Form = form };
        if (form == TurboQuantForm.Mse) return payload;

        // The residual in the rotated unit space, and one sign a coordinate of its projection.
        double residualSq = 0.0;
        for (int i = 0; i < dim; i++)
        {
            rotated[i] -= codebook.Centroids[indices[i]];
            residualSq += (double)rotated[i] * rotated[i];
        }
        var projection = QjlProjection.For(dim);
        var signs = new byte[projection.SignBytes];
        projection.Signs(rotated, signs);
        return payload with { ResidualNorm = (float)Math.Sqrt(residualSq), Signs = signs };
    }

    /// <summary>
    /// The inner product of <paramref name="query"/> with the vector <paramref name="payload"/>
    /// holds, estimated from the codes: the query is rotated once and meets the centroids where
    /// they are, and a Prod payload adds the second stage's unbiased term. A version-1 payload
    /// is decoded first.
    /// </summary>
    public static float InnerProduct(ReadOnlySpan<float> query, TurboQuantPayload payload, int bitsPerDim)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var dim = query.Length;
        if (payload.Version != 2)
        {
            var decoded = Decode(payload, dim, bitsPerDim);
            float dot = 0f;
            for (int i = 0; i < dim; i++) dot += query[i] * decoded[i];
            return dot;
        }
        if (payload.Norm == 0f) return 0f;

        var stageBits = StageBits(bitsPerDim, payload.Form);
        var rotated = new float[dim];
        HadamardRotation.Rotate(query, rotated);
        var indices = BitPacker.Unpack(payload.PackedIndices, dim, stageBits);
        var centroids = BetaLloydMaxCodebook.Get(stageBits, dim).Centroids;
        float sum = 0f;
        for (int i = 0; i < dim; i++) sum += rotated[i] * centroids[indices[i]];

        if (payload.Form == TurboQuantForm.Prod)
        {
            var projection = QjlProjection.For(dim);
            var projected = new float[dim];
            projection.Project(rotated, projected);
            sum += projection.Estimate(projected, payload.Signs ?? throw new System.IO.InvalidDataException("A Prod payload carries its signs."), payload.ResidualNorm);
        }
        return payload.Norm * sum;
    }

    // The bits the first stage gets: all of them for Mse, one fewer for Prod.
    private static int StageBits(int bitsPerDim, TurboQuantForm form)
    {
        var stage = form == TurboQuantForm.Prod ? bitsPerDim - 1 : bitsPerDim;
        if (stage is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bitsPerDim), bitsPerDim,
                form == TurboQuantForm.Prod ? "Prod needs 2..9 bits: one is the residual's sign." : "bitsPerDim must be 1..8.");
        return stage;
    }

    /// <summary>
    /// Decodes a TurboQuant payload back into the original-magnitude vector
    /// (modulo quantization error).
    /// </summary>
    public static float[] Decode(TurboQuantPayload payload, int dim, int bitsPerDim)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (dim <= 1) throw new ArgumentOutOfRangeException(nameof(dim));
        if (payload.Version == 2) return DecodeVersion2(payload, dim, bitsPerDim);
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

    // Centroids, plus the second stage's dequantised residual for Prod, turned back and scaled.
    private static float[] DecodeVersion2(TurboQuantPayload payload, int dim, int bitsPerDim)
    {
        var result = new float[dim];
        if (payload.Norm == 0f) return result;

        var stageBits = StageBits(bitsPerDim, payload.Form);
        var indices = BitPacker.Unpack(payload.PackedIndices, dim, stageBits);
        var centroids = BetaLloydMaxCodebook.Get(stageBits, dim).Centroids;
        var rotated = new float[dim];
        for (int i = 0; i < dim; i++) rotated[i] = centroids[indices[i]];
        if (payload.Form == TurboQuantForm.Prod)
            QjlProjection.For(dim).AddDequantized(payload.Signs ?? throw new System.IO.InvalidDataException("A Prod payload carries its signs."), payload.ResidualNorm, rotated);

        HadamardRotation.Unrotate(rotated, result);
        for (int i = 0; i < dim; i++) result[i] *= payload.Norm;
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
