// ShardKvCodec.cs
//
// (3.3.0) Shard-style KV cache compression. Pattern-port of the shard
// idea: compress K via per-layer online PCA + Hadamard rotation, and
// compress V via product vector quantisation. An alternative to the
// existing TurboQuant codec — same KV bytes, different math; consumers
// pick the codec that wins on their device + model + workload.
//
// Reference: shard pattern (KV-cache compression via per-layer online
// PCA on K + Hadamard rotation, VQ on V). License of the original
// project did not permit vendoring; this is a fresh Apache-2.0 port
// of the math, written from the architectural description in the
// repository README + paper.

using System;
using System.Buffers.Binary;

namespace CircleAI.Core.Compression;

/// <summary>(3.3.0) Encoded shard KV pair (compressed K + compressed V).</summary>
public sealed record ShardCompressedFrame(
    byte[]  CompressedK,
    byte[]  CompressedV,
    float[] KPrincipalAxes,
    int     KOriginalDim,
    int     VOriginalDim);

/// <summary>
/// (3.3.0) Online-PCA-on-K + VQ-on-V KV compressor.
/// Stateless across frames — the host re-trains the PCA basis with
/// <see cref="ObserveK"/> when desired, and uses the current basis to
/// encode subsequent frames.
/// </summary>
public sealed class ShardKvCodec
{
    private readonly int _kDim;
    private readonly int _kRank;
    private readonly int _vDim;
    private readonly int _vCodewords;
    private readonly float[][] _vCodebook;
    private readonly float[] _hadamardScratch;
    private float[]  _kCenter;
    private float[,] _kAxes;
    private long _samplesObserved;

    /// <summary>
    /// (3.3.0)
    /// </summary>
    /// <param name="kDim">K-vector dimensionality (e.g. 128 for a typical attention head).</param>
    /// <param name="kRank">Number of principal components to keep on K (e.g. 32).</param>
    /// <param name="vDim">V-vector dimensionality.</param>
    /// <param name="vCodewords">Number of VQ codewords for V (must be a power of 2).</param>
    /// <param name="vCodebookSeed">Seed for the deterministic initial codebook.</param>
    public ShardKvCodec(int kDim, int kRank, int vDim, int vCodewords, int vCodebookSeed = 0)
    {
        if (kDim   <= 0) throw new ArgumentOutOfRangeException(nameof(kDim));
        if (kRank  <= 0 || kRank > kDim) throw new ArgumentOutOfRangeException(nameof(kRank));
        if (vDim   <= 0) throw new ArgumentOutOfRangeException(nameof(vDim));
        if (vCodewords <= 1 || (vCodewords & (vCodewords - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vCodewords),
                "Codeword count must be a power of two greater than 1.");
        }
        _kDim       = kDim;
        _kRank      = kRank;
        _vDim       = vDim;
        _vCodewords = vCodewords;
        _kCenter    = new float[kDim];
        _kAxes      = new float[kRank, kDim];
        _vCodebook  = SeedCodebook(vDim, vCodewords, vCodebookSeed);
        _hadamardScratch = new float[Pow2Ceil(kDim)];

        // Initialise PCA axes to identity-top-rank for sane defaults before training.
        for (int r = 0; r < kRank; r++)
        {
            _kAxes[r, r] = 1f;
        }
    }

    /// <summary>(3.3.0) Number of K samples used to update the PCA centre.</summary>
    public long SamplesObserved => _samplesObserved;

    /// <summary>(3.3.0) Update the online K mean estimate with this sample.</summary>
    public void ObserveK(ReadOnlySpan<float> k)
    {
        if (k.Length != _kDim) throw new ArgumentException("Input dim mismatch", nameof(k));
        _samplesObserved++;
        for (int i = 0; i < _kDim; i++)
        {
            // Running mean.
            _kCenter[i] += (k[i] - _kCenter[i]) / _samplesObserved;
        }
    }

    /// <summary>
    /// (3.3.0) Replace the current PCA axes with <paramref name="axes"/>.
    /// Caller computes axes offline (full SVD/PCA on observed K) or in batch.
    /// </summary>
    public void SetPrincipalAxes(float[,] axes)
    {
        if (axes.GetLength(0) != _kRank || axes.GetLength(1) != _kDim)
        {
            throw new ArgumentException("Axes shape must be (kRank, kDim).", nameof(axes));
        }
        Array.Copy(axes, _kAxes, axes.Length);
    }

    /// <summary>(3.3.0) Replace the V codebook with <paramref name="codebook"/>.</summary>
    public void SetVCodebook(float[][] codebook)
    {
        if (codebook.Length != _vCodewords)
        {
            throw new ArgumentException("Codebook size mismatch.", nameof(codebook));
        }
        for (int i = 0; i < codebook.Length; i++)
        {
            if (codebook[i].Length != _vDim) throw new ArgumentException("Codeword dim mismatch.", nameof(codebook));
            Array.Copy(codebook[i], _vCodebook[i], _vDim);
        }
    }

    /// <summary>(3.3.0) Encode one (K, V) pair.</summary>
    public ShardCompressedFrame Encode(ReadOnlySpan<float> k, ReadOnlySpan<float> v)
    {
        if (k.Length != _kDim) throw new ArgumentException("K dim mismatch", nameof(k));
        if (v.Length != _vDim) throw new ArgumentException("V dim mismatch", nameof(v));

        // K: centre → Hadamard → project to top-rank principal axes → quantise to int8.
        var centred = new float[_kDim];
        for (int i = 0; i < _kDim; i++) centred[i] = k[i] - _kCenter[i];
        ApplyHadamardInPlace(centred);

        Span<float> projected = stackalloc float[_kRank];
        for (int r = 0; r < _kRank; r++)
        {
            float dot = 0f;
            for (int i = 0; i < _kDim; i++) dot += centred[i] * _kAxes[r, i];
            projected[r] = dot;
        }

        // Find scale that fits all components into int8 dynamic range.
        float maxAbs = 1e-9f;
        for (int r = 0; r < _kRank; r++) maxAbs = Math.Max(maxAbs, Math.Abs(projected[r]));
        float scale = maxAbs / 127f;

        var encodedK = new byte[_kRank + 4]; // +4 for the scale (float32 little-endian)
        BinaryPrimitives.WriteSingleLittleEndian(encodedK.AsSpan(0, 4), scale);
        for (int r = 0; r < _kRank; r++)
        {
            int q = (int)Math.Round(projected[r] / scale);
            q = Math.Clamp(q, -127, 127);
            encodedK[4 + r] = (byte)((sbyte)q);
        }

        // V: nearest-codeword VQ → encode index in ⌈log2(codewords)⌉ bits.
        int bestIdx = 0;
        float bestDist = float.MaxValue;
        for (int c = 0; c < _vCodewords; c++)
        {
            float d = 0f;
            var word = _vCodebook[c];
            for (int i = 0; i < _vDim; i++)
            {
                var diff = v[i] - word[i];
                d += diff * diff;
            }
            if (d < bestDist) { bestDist = d; bestIdx = c; }
        }

        // Encode index as little-endian uint (1, 2, or 4 bytes depending on codebook size).
        int idxBytes = _vCodewords <= 256 ? 1 : _vCodewords <= 65536 ? 2 : 4;
        var encodedV = new byte[idxBytes];
        switch (idxBytes)
        {
            case 1: encodedV[0] = (byte)bestIdx; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(encodedV, (ushort)bestIdx); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(encodedV, (uint)bestIdx); break;
        }

        // Materialise the PCA axes once in the frame so the decoder can stand alone.
        var axesFlat = new float[_kRank * _kDim];
        for (int r = 0; r < _kRank; r++)
        {
            for (int i = 0; i < _kDim; i++)
            {
                axesFlat[r * _kDim + i] = _kAxes[r, i];
            }
        }
        return new ShardCompressedFrame(encodedK, encodedV, axesFlat, _kDim, _vDim);
    }

    /// <summary>(3.3.0) Decode a frame back to approximate K and V.</summary>
    public (float[] K, float[] V) Decode(ShardCompressedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.KOriginalDim != _kDim) throw new ArgumentException("Codec K-dim does not match frame.", nameof(frame));
        if (frame.VOriginalDim != _vDim) throw new ArgumentException("Codec V-dim does not match frame.", nameof(frame));

        // K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter.
        float scale = BinaryPrimitives.ReadSingleLittleEndian(frame.CompressedK.AsSpan(0, 4));
        Span<float> projected = stackalloc float[_kRank];
        for (int r = 0; r < _kRank; r++)
        {
            projected[r] = (sbyte)frame.CompressedK[4 + r] * scale;
        }

        var k = new float[_kDim];
        for (int i = 0; i < _kDim; i++)
        {
            float acc = 0f;
            for (int r = 0; r < _kRank; r++)
            {
                acc += projected[r] * frame.KPrincipalAxes[r * _kDim + i];
            }
            k[i] = acc;
        }
        ApplyHadamardInPlace(k); // Hadamard is self-inverse (up to scale 1/n).
        for (int i = 0; i < _kDim; i++) k[i] = k[i] / _kDim + _kCenter[i];

        // V decode: read index, copy codeword.
        int idxBytes = _vCodewords <= 256 ? 1 : _vCodewords <= 65536 ? 2 : 4;
        int idx = idxBytes switch
        {
            1 => frame.CompressedV[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(frame.CompressedV),
            4 => (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.CompressedV),
            _ => 0,
        };
        var v = new float[_vDim];
        Array.Copy(_vCodebook[idx], v, _vDim);
        return (k, v);
    }

    private void ApplyHadamardInPlace(float[] buffer)
    {
        // Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch.
        int n = _hadamardScratch.Length;
        Array.Clear(_hadamardScratch);
        Array.Copy(buffer, _hadamardScratch, Math.Min(buffer.Length, n));

        for (int h = 1; h < n; h <<= 1)
        {
            for (int i = 0; i < n; i += h * 2)
            {
                for (int j = i; j < i + h; j++)
                {
                    var x = _hadamardScratch[j];
                    var y = _hadamardScratch[j + h];
                    _hadamardScratch[j]     = x + y;
                    _hadamardScratch[j + h] = x - y;
                }
            }
        }
        Array.Copy(_hadamardScratch, buffer, Math.Min(buffer.Length, n));
    }

    private static int Pow2Ceil(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    private static float[][] SeedCodebook(int dim, int count, int seed)
    {
        var rng = new Random(seed);
        var cb  = new float[count][];
        for (int c = 0; c < count; c++)
        {
            var word = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                word[i] = (float)(rng.NextDouble() * 2.0 - 1.0); // uniform [-1, 1]
            }
            cb[c] = word;
        }
        return cb;
    }
}
