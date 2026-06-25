// TurboQuantParityTests.cs
//
// Proves the C++ TurboQuant port in mnnbridge produces results that
// match CircleAI.Core.Compression.TurboQuantCodec to within FP rounding
// tolerance. Required so the parity-test exports earn their keep — and
// so the managed and native codecs stay in sync as they evolve.
//
// SKIPPED ON NON-WINDOWS in this session — native lib is only built for
// win-x64 right now. Other RIDs will pick this up once their rebuilds
// land in the same PR.

using System;
using System.Runtime.InteropServices;
using CircleAI.Core.Compression;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class TurboQuantParityTests
{
    // Tolerance vs the managed codec. Box-Muller + Lanczos + Adaptive
    // Simpson run identically in IEEE 754, but std::log / std::sin /
    // std::cos can differ by 1 ULP between MSVC's CRT and .NET's
    // Math.Log/Sin/Cos. Empirically the round-trip differs by < 1e-3 on
    // dim=128 vectors at 4 bits; we assert 1e-2 to leave headroom.
    private const float Tolerance = 1e-2f;

    [Theory]
    [InlineData(64,  4, 12345)]
    [InlineData(128, 4, 67890)]
    [InlineData(256, 3, 11111)]
    [InlineData(128, 2, 22222)]
    public unsafe void RoundTrip_MatchesManagedCodec(int dim, int bits, int seed)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Native turboquant only built for win-x64 in this session.

        // Deterministic input vector — fixed seed so the test is repeatable.
        var input = GenerateVector(dim, seed);

        // Managed round-trip.
        var managed = TurboQuantCodec.RoundTrip(input, bits);

        // Native round-trip via P/Invoke.
        var native = new float[dim];
        int rc;
        fixed (float* p = input)
        fixed (float* q = native)
        {
            rc = MnnInterop.mnn_turboquant_round_trip(p, dim, bits, q);
        }
        Assert.Equal(0, rc);

        // Per-element compare — same magnitude, same direction.
        Assert.Equal(managed.Length, native.Length);
        for (int i = 0; i < managed.Length; i++)
        {
            float diff = Math.Abs(managed[i] - native[i]);
            Assert.True(diff < Tolerance,
                $"i={i} managed={managed[i]:F6} native={native[i]:F6} diff={diff:E3}");
        }
    }

    [Fact]
    public unsafe void RoundTrip_ZeroVector_ReturnsZero()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        const int dim = 128;
        var input = new float[dim];
        var output = new float[dim];
        int rc;
        fixed (float* p = input)
        fixed (float* q = output)
        {
            rc = MnnInterop.mnn_turboquant_round_trip(p, dim, 4, q);
        }
        Assert.Equal(0, rc);
        foreach (var x in output) Assert.Equal(0f, x);
    }

    [Fact]
    public unsafe void RoundTrip_InvalidArgs_ReturnsNegative()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var v = new float[4];
        var o = new float[4];
        int rc;
        fixed (float* p = v)
        fixed (float* q = o)
        {
            rc = MnnInterop.mnn_turboquant_round_trip(p, 1, 4, q); // dim<=1
        }
        Assert.True(rc < 0);

        fixed (float* p = v)
        fixed (float* q = o)
        {
            rc = MnnInterop.mnn_turboquant_round_trip(p, 4, 9, q); // bits>8
        }
        Assert.True(rc < 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static float[] GenerateVector(int dim, int seed)
    {
        // Box-Muller over a SplitMix64 — same recipe as the codec's own RNG.
        // Used to fabricate a deterministic but realistic-looking input.
        var v = new float[dim];
        ulong state = (uint)seed | ((ulong)(uint)seed << 32);
        bool haveSpare = false;
        double spare = 0;
        for (int i = 0; i < dim; i++)
        {
            double s;
            if (haveSpare) { s = spare; haveSpare = false; }
            else
            {
                double u, w;
                do { u = NextUniform(ref state); } while (u <= 1e-300);
                w = NextUniform(ref state);
                double mag = Math.Sqrt(-2.0 * Math.Log(u));
                double ang = 2.0 * Math.PI * w;
                spare = mag * Math.Sin(ang);
                haveSpare = true;
                s = mag * Math.Cos(ang);
            }
            v[i] = (float)s;
        }
        return v;
    }

    private static double NextUniform(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
