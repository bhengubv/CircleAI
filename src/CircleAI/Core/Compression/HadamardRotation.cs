// HadamardRotation.cs
//
// The rotation TurboQuant's version-2 payloads are written with: seeded random signs, then the
// normalised Walsh-Hadamard transform (WalshHadamard, the kernel Bonsai 2's own rotation runs) on
// power-of-two blocks. O(d log d) to apply and nothing to build, where OrthogonalRotation's dense
// matrix costs O(d^2) a vector and O(d^3) to make - minutes at a model's widths. After it a unit
// vector's coordinates are spread about 1/sqrt(d) each, which is what the Lloyd-Max codebook is
// built for and all TurboQuant asks of a rotation.
//
// A width that is not a power of two (384, 768, 1536) splits into the powers of two that sum to
// it, largest first; a seeded permutation deals the coordinates across those blocks first, so a
// vector whose weight sits at one end is not left in one block. The signs and the permutation come
// from a fixed seed and the width, so what one device writes another reads.

using System.Collections.Concurrent;

namespace CircleAI.Core.Compression;

/// <summary>The fast rotation of TurboQuant's version-2 payloads: random signs, then Walsh-Hadamard blocks.</summary>
public static class HadamardRotation
{
    /// <summary>The seed every CircleAI process shares, so a rotation made on one device is the same on another.</summary>
    public const ulong Seed = 0xC1C1EA10_7E4DA5A2UL;

    private static readonly ConcurrentDictionary<int, Plan> Plans = new();

    // The signs (one per coordinate of the input), the permutation (coordinate taken into each
    // rotated position; null for a power of two) and the block lengths.
    private sealed record Plan(float[] Signs, int[]? Order, int[] Blocks);

    /// <summary>The power-of-two blocks a width rotates in, largest first.</summary>
    public static IReadOnlyList<int> Blocks(int dim) => PlanFor(dim).Blocks;

    /// <summary>Rotates <paramref name="x"/> into <paramref name="y"/> (the same length).</summary>
    public static void Rotate(ReadOnlySpan<float> x, Span<float> y)
    {
        if (y.Length != x.Length) throw new ArgumentException("The output must be as long as the input.", nameof(y));
        var plan = PlanFor(x.Length);
        if (plan.Order is null)
        {
            for (var i = 0; i < x.Length; i++) y[i] = x[i] * plan.Signs[i];
        }
        else
        {
            for (var i = 0; i < x.Length; i++)
            {
                var from = plan.Order[i];
                y[i] = x[from] * plan.Signs[from];
            }
        }
        Blockwise(y, plan.Blocks);
    }

    /// <summary>Turns <paramref name="y"/> back into <paramref name="x"/>: the inverse of <see cref="Rotate"/>.</summary>
    public static void Unrotate(ReadOnlySpan<float> y, Span<float> x)
    {
        if (x.Length != y.Length) throw new ArgumentException("The output must be as long as the input.", nameof(x));
        var plan = PlanFor(y.Length);
        if (plan.Order is null)
        {
            y.CopyTo(x);
            Blockwise(x, plan.Blocks); // each block is its own inverse
            for (var i = 0; i < x.Length; i++) x[i] *= plan.Signs[i];
            return;
        }

        Span<float> turned = y.Length <= 2048 ? stackalloc float[y.Length] : new float[y.Length];
        y.CopyTo(turned);
        Blockwise(turned, plan.Blocks);
        for (var i = 0; i < y.Length; i++)
        {
            var to = plan.Order[i];
            x[to] = turned[i] * plan.Signs[to];
        }
    }

    private static void Blockwise(Span<float> v, int[] blocks)
    {
        var at = 0;
        foreach (var block in blocks)
        {
            WalshHadamard.Transform(v.Slice(at, block));
            at += block;
        }
    }

    private static Plan PlanFor(int dim)
    {
        if (dim < 2) throw new ArgumentOutOfRangeException(nameof(dim), dim, "A rotation needs at least two coordinates.");
        return Plans.GetOrAdd(dim, Make);
    }

    private static Plan Make(int dim)
    {
        var random = new SplitMix(Seed ^ ((ulong)dim * 0x9E3779B97F4A7C15UL));
        var signs = new float[dim];
        for (var i = 0; i < dim; i++) signs[i] = (random.Next() >> 63) == 0 ? 1f : -1f;

        var blocks = new List<int>();
        for (var bit = 30; bit >= 0; bit--)
        {
            if ((dim & (1 << bit)) != 0) blocks.Add(1 << bit);
        }

        int[]? order = null;
        if (blocks.Count > 1)
        {
            order = new int[dim];
            for (var i = 0; i < dim; i++) order[i] = i;
            for (var i = dim - 1; i > 0; i--) // Fisher-Yates
            {
                var j = (int)(random.Next() % (ulong)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }
        }
        return new Plan(signs, order, blocks.ToArray());
    }

    // SplitMix64: the same small generator SeededGaussian draws from, so the stream is the same on
    // every runtime.
    private struct SplitMix(ulong state)
    {
        private ulong _state = state;

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
