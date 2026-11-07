// WalshHadamard.cs
//
// The normalised Sylvester-Walsh-Hadamard transform, in place - the one kernel behind both
// rotations in this library: the one Bonsai 2's weights are folded with (PrismHadamard, which
// has to match the pinned fork's ggml_compute_forward_fwht number for number) and the one
// TurboQuant's version-2 payloads are written with (HadamardRotation). Scaled by 1/sqrt(n)
// first, then the butterflies (u + v, u - v) at widths 1, 2, 4 ... n/2, in the fork's order.
// The normalised transform is orthogonal and its own inverse: applying it twice gives the
// input back, to rounding.

using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
            // Where the runtime has vectors, the same sums in the same order, four at a time.
            if ((AdvSimd.Arm64.IsSupported || Sse2.IsSupported) && n >= 8)
            {
                Vectors(p, n, scale);
                return;
            }

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

    // Widths 1 and 2 stay inside a vector, so each four go through them one by one; from 4 on a
    // butterfly's two halves are whole vectors.
    private static unsafe void Vectors(float* p, int n, float scale)
    {
        for (var i = 0; i < n; i += 4)
        {
            var a = p[i] * scale;
            var b = p[i + 1] * scale;
            var c = p[i + 2] * scale;
            var d = p[i + 3] * scale;
            var sum = a + b;
            var difference = a - b;
            var sum2 = c + d;
            var difference2 = c - d;
            p[i] = sum + sum2;
            p[i + 1] = difference + difference2;
            p[i + 2] = sum - sum2;
            p[i + 3] = difference - difference2;
        }

        for (var len = 4; len < n; len <<= 1)
        {
            for (var i = 0; i < n; i += 2 * len)
            {
                var a = p + i;
                var c = a + len;
                for (var j = 0; j < len; j += 4)
                {
                    var u = Vector128.Load(a + j);
                    var v = Vector128.Load(c + j);
                    Vector128.Store(u + v, a + j);
                    Vector128.Store(u - v, c + j);
                }
            }
        }
    }
}
