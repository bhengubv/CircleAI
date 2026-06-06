// TurboQuantCodecTests.cs
//
// Exercises the TurboQuant pure-C# codec end-to-end.
//
// Three classes of assertion:
//   1. Structural — bit-pack round-trip, payload byte count, compression
//      ratio matches the spec.
//   2. Algorithmic — rotation matrix is orthogonal, codebook is monotonic
//      and symmetric, decode preserves vector geometry.
//   3. Statistical — cosine similarity between original and reconstructed
//      vectors meets the bit-rate's expected accuracy bound.

using CircleAI.Core.Compression;
using Xunit;

namespace CircleAI.Tests;

public sealed class TurboQuantCodecTests
{
    // ── helpers ──────────────────────────────────────────────────────────

    private static float[] RandomUnit(int dim, int seed = 42)
    {
        // Deterministic Gaussian → unit-normalise. Reuses the same sampler
        // the rotation matrix uses, so the tests are reproducible across runs.
        var sampler = new SeededGaussianForTests((ulong)seed);
        var v = new float[dim];
        double sumSq = 0;
        for (int i = 0; i < dim; i++) { v[i] = (float)sampler.Sample(); sumSq += v[i] * v[i]; }
        var inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (int i = 0; i < dim; i++) v[i] *= inv;
        return v;
    }

    private sealed class SeededGaussianForTests
    {
        private ulong _state;
        private bool _hasSpare;
        private double _spare;
        public SeededGaussianForTests(ulong seed) =>
            _state = seed == 0 ? 0xDEADBEEFCAFEBABEUL : seed;
        public double Sample()
        {
            if (_hasSpare) { _hasSpare = false; return _spare; }
            double u, v;
            do { u = NextUniform(); } while (u <= 1e-300);
            v = NextUniform();
            double mag = Math.Sqrt(-2.0 * Math.Log(u));
            double ang = 2.0 * Math.PI * v;
            _spare = mag * Math.Sin(ang);
            _hasSpare = true;
            return mag * Math.Cos(ang);
        }
        private double NextUniform()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z = z ^ (z >> 31);
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }

    private static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom < 1e-30 ? 0f : (float)(dot / denom);
    }

    // ══════════════════════════════════════════════════════════════════════
    // BitPacker
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void BitPacker_RoundTrip_PreservesIndices(int bits)
    {
        ushort max = (ushort)((1 << bits) - 1);
        var rng = new Random(123);
        var indices = new ushort[256];
        for (int i = 0; i < indices.Length; i++) indices[i] = (ushort)rng.Next(0, max + 1);

        var packed = BitPacker.Pack(indices, bits);
        var unpacked = BitPacker.Unpack(packed, indices.Length, bits);

        Assert.Equal(indices.Length, unpacked.Length);
        for (int i = 0; i < indices.Length; i++) Assert.Equal(indices[i], unpacked[i]);
    }

    [Fact]
    public void BitPacker_ByteCount_MatchesSpec()
    {
        // 1536 indices at 2 bits = 384 bytes (the headline shrink figure).
        var indices = new ushort[1536];
        var packed = BitPacker.Pack(indices, 2);
        Assert.Equal(384, packed.Length);
    }

    [Fact]
    public void BitPacker_RejectsOverflowingIndex()
    {
        // At 2 bits the max legal value is 3.
        ushort[] bad = { 4 };
        Assert.Throws<ArgumentException>(() => BitPacker.Pack(bad, 2));
    }

    // ══════════════════════════════════════════════════════════════════════
    // OrthogonalRotation
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Rotation_PreservesL2Norm()
    {
        const int dim = 64;
        var v = RandomUnit(dim);
        var r = new float[dim];
        OrthogonalRotation.Rotate(dim, v, r);

        double sqA = 0, sqR = 0;
        for (int i = 0; i < dim; i++) { sqA += v[i] * v[i]; sqR += r[i] * r[i]; }
        Assert.InRange(Math.Sqrt(sqR), Math.Sqrt(sqA) - 1e-3, Math.Sqrt(sqA) + 1e-3);
    }

    [Fact]
    public void Rotation_Followed_ByUnrotation_RecoversInput()
    {
        const int dim = 64;
        var v = RandomUnit(dim);
        var r = new float[dim];
        var v2 = new float[dim];
        OrthogonalRotation.Rotate(dim, v, r);
        OrthogonalRotation.Unrotate(dim, r, v2);

        for (int i = 0; i < dim; i++)
            Assert.InRange(v2[i] - v[i], -1e-3f, 1e-3f);
    }

    [Fact]
    public void Rotation_IsDeterministic_AcrossCalls()
    {
        var a = OrthogonalRotation.GetMatrix(32);
        var b = OrthogonalRotation.GetMatrix(32);
        Assert.Same(a, b); // cached
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    // ══════════════════════════════════════════════════════════════════════
    // BetaLloydMaxCodebook
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 64)]
    [InlineData(3, 128)]
    [InlineData(4, 256)]
    public void Codebook_HasCorrectSizes(int bits, int dim)
    {
        var cb = BetaLloydMaxCodebook.Get(bits, dim);
        int n = 1 << bits;
        Assert.Equal(n, cb.Centroids.Length);
        Assert.Equal(n - 1, cb.Boundaries.Length);
    }

    [Fact]
    public void Codebook_CentroidsAreMonotonic()
    {
        var cb = BetaLloydMaxCodebook.Get(4, 128);
        for (int i = 1; i < cb.Centroids.Length; i++)
            Assert.True(cb.Centroids[i] > cb.Centroids[i - 1],
                $"centroid[{i}]={cb.Centroids[i]} not > centroid[{i - 1}]={cb.Centroids[i - 1]}");
    }

    [Fact]
    public void Codebook_BinFor_RoundTripsThroughBoundaries()
    {
        var cb = BetaLloydMaxCodebook.Get(2, 64);
        // Values just past each boundary land in the next bin.
        for (int i = 0; i < cb.Boundaries.Length; i++)
        {
            var justBefore = cb.Boundaries[i] - 1e-6f;
            var justAfter = cb.Boundaries[i] + 1e-6f;
            Assert.Equal((ushort)i, BetaLloydMaxCodebook.BinFor(justBefore, cb.Boundaries));
            Assert.Equal((ushort)(i + 1), BetaLloydMaxCodebook.BinFor(justAfter, cb.Boundaries));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // TurboQuantCodec end-to-end
    // ══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(64, 4)]
    [InlineData(128, 4)]
    [InlineData(256, 3)]
    [InlineData(512, 2)]
    public void Codec_RoundTrip_PreservesGeometry(int dim, int bits)
    {
        var v = RandomUnit(dim);
        var reconstructed = TurboQuantCodec.RoundTrip(v, bits);

        Assert.Equal(dim, reconstructed.Length);

        var cos = Cosine(v, reconstructed);
        // Quality bound: 4-bit recovers cos > 0.99, 3-bit > 0.96, 2-bit > 0.85.
        var floor = bits switch { 4 => 0.99f, 3 => 0.96f, 2 => 0.85f, _ => 0.5f };
        Assert.True(cos >= floor,
            $"dim={dim} bits={bits}: cos similarity {cos} below floor {floor}");
    }

    [Fact]
    public void Codec_ZeroVector_RoundTripsToZeros()
    {
        var z = new float[64];
        var r = TurboQuantCodec.RoundTrip(z, 2);
        Assert.All(r, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Codec_PayloadSize_MatchesSpec()
    {
        // 1536-dim at 2 bits/dim: 384 bytes payload + 4-byte norm header.
        Assert.Equal(384, TurboQuantCodec.PayloadByteCount(1536, 2));
    }

    [Fact]
    public void Codec_CompressionRatio_AtTwoBits_ExceedsTwelveX()
    {
        // Strict math: 1536*4 / (1536*2/8 + 4) = 6144 / 388 ≈ 15.83×
        var ratio = TurboQuantCodec.CompressionRatio(1536, 2);
        Assert.True(ratio > 15.0,
            $"Expected > 15× compression at 1536-dim/2-bit; got {ratio:F2}×");
    }

    [Fact]
    public void Codec_Encode_InvalidBits_Throws()
    {
        var v = new float[32];
        v[0] = 1f;
        Assert.Throws<ArgumentOutOfRangeException>(() => TurboQuantCodec.Encode(v, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TurboQuantCodec.Encode(v, 9));
    }

    [Fact]
    public void Codec_Encode_TinyVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => TurboQuantCodec.Encode(new float[] { 1f }, 2));
    }

    [Fact]
    public void Codec_DeterministicEncode_AcrossRuns()
    {
        var v = RandomUnit(128, seed: 7);
        var a = TurboQuantCodec.Encode(v, 3);
        var b = TurboQuantCodec.Encode(v, 3);
        Assert.Equal(a.Norm, b.Norm);
        Assert.Equal(a.PackedIndices, b.PackedIndices);
    }

    [Fact]
    public void Codec_InnerProductPreserved_BetweenCompressedVectors()
    {
        // Two correlated unit vectors. After compress-decompress, the cosine
        // between the reconstructions should track the cosine between the
        // originals — this is the property retrieval workloads depend on.
        const int dim = 128;
        var a = RandomUnit(dim, seed: 1);
        var b = RandomUnit(dim, seed: 2);

        // Blend so they have a known correlation (~0.7 cosine).
        var blended = new float[dim];
        for (int i = 0; i < dim; i++) blended[i] = 0.7f * a[i] + 0.3f * b[i];
        float blendNorm = 0;
        for (int i = 0; i < dim; i++) blendNorm += blended[i] * blended[i];
        var invN = (float)(1.0 / Math.Sqrt(blendNorm));
        for (int i = 0; i < dim; i++) blended[i] *= invN;

        var trueCos = Cosine(a, blended);
        var aHat = TurboQuantCodec.RoundTrip(a, 4);
        var blendHat = TurboQuantCodec.RoundTrip(blended, 4);
        var reconCos = Cosine(aHat, blendHat);

        Assert.InRange(reconCos, trueCos - 0.05f, trueCos + 0.05f);
    }
}
