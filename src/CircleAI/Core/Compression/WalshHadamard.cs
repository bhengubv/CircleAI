// WalshHadamard.cs
//
// The normalised Sylvester-Walsh-Hadamard transform, in place - the one kernel behind both
// rotations in this library: the one Bonsai 2's weights are folded with (PrismHadamard, which
// has to match the pinned fork's ggml_compute_forward_fwht number for number) and the one
// TurboQuant's version-2 payloads are written with (HadamardRotation). Scaled by 1/sqrt(n)
// first, then the butterflies (u + v, u - v) at widths 1, 2, 4 ... n/2, in the fork's order.
// The normalised transform is orthogonal and its own inverse: applying it twice gives the
// input back, to rounding.

namespace CircleAI.Core.Compression;

/// <summary>The normalised Walsh-Hadamard transform over a power-of-two block.</summary>
public static class WalshHadamard
{
    /// <summary>Transforms <paramref name="block"/> in place; its length must be a power of two.</summary>
    public static unsafe void Transform(Span<float> block)
    {
        var n = block.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException($"A Hadamard block is a power of two, not {n}.", nameof(block));
        var scale = 1.0f / MathF.Sqrt(n);
        fixed (float* p = block)
        {
            for (var j = 0; j < n; j++) p[j] *= scale;
            for (var len = 1; len < n; len <<= 1)
            {
                for (var i = 0; i < n; i += 2 * len)
                {
                    var a = p + i;
                    var c = a + len;
                    for (var j = 0; j < len; j++)
                    {
                        var u = a[j];
                        var v = c[j];
                        a[j] = u + v;
                        c[j] = u - v;
                    }
                }
            }
        }
    }
}
