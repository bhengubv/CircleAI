// Q8KActivations.cs
//
// The int8 form an activation vector takes before it meets PQ2_0 weights: Q8_K, blocks of 256
// values with one fp32 scale each, as the pinned fork pairs them (ggml-cpu.c: PQ2_0's vec_dot_type
// is GGML_TYPE_Q8_K). With one scale per 256 values, a 128-weight PQ2_0 block's whole integer dot
// is scaled once instead of four times.
//
// The rounding is the fork's reference quantiser exactly (ggml-quants.c, quantize_row_q8_K_ref):
// every x86 build calls it (arch/x86/quants.c, quantize_row_q8_K), so the build that made the S0
// fixtures did too:
//
//   max    = the value of largest magnitude, the first of them on a tie (|x| > amax, strictly)
//   iscale = -127 / max            (negative: the largest value lands on -127)
//   q      = min(127, nearest_int(iscale * x))   nearest_int rounds half to even
//   d      = 1 / iscale            (so d is negative when max is positive)
//
// A block of zeros has d = 0 and every q = 0.

using System.Runtime.CompilerServices;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>An activation vector quantised to Q8_K blocks of 256.</summary>
public sealed class Q8KActivations
{
    /// <summary>Values per block.</summary>
    public const int BlockSize = 256;

    /// <summary>The scale of each block, as the reference stores it (fp32, negative when the block's largest value is positive).</summary>
    public float[] Scales { get; }

    /// <summary>The quantised values, <see cref="Length"/> of them, in their original order.</summary>
    public sbyte[] Quants { get; }

    /// <summary>Number of values; a multiple of <see cref="BlockSize"/>.</summary>
    public int Length { get; }

    /// <summary>Allocates room for <paramref name="length"/> values.</summary>
    public Q8KActivations(int length)
    {
        if (length <= 0 || length % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Q8_K needs a positive multiple of 256 values.");
        Length = length;
        Scales = new float[length / BlockSize];
        Quants = new sbyte[length];
    }

    /// <summary>Quantises <paramref name="x"/> in place of whatever was held before.</summary>
    public void Quantize(ReadOnlySpan<float> x)
    {
        if (x.Length != Length)
            throw new ArgumentException($"Expected {Length} values, got {x.Length}.", nameof(x));

        for (var b = 0; b < Scales.Length; b++)
        {
            var block = x.Slice(b * BlockSize, BlockSize);
            var q = Quants.AsSpan(b * BlockSize, BlockSize);
            var max = 0f;
            var amax = 0f;
            for (var j = 0; j < BlockSize; j++)
            {
                var ax = MathF.Abs(block[j]);
                if (ax > amax)
                {
                    amax = ax;
                    max = block[j];
                }
            }
            if (amax == 0f)
            {
                Scales[b] = 0f;
                q.Clear();
                continue;
            }

            var iscale = -127f / max;
            for (var j = 0; j < BlockSize; j++) q[j] = (sbyte)Math.Min(127, NearestInt(iscale * block[j]));
            Scales[b] = 1f / iscale;
        }
    }

    // The reference's nearest_int: the float's own rounding, half to even, which is MathF.Round's default.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NearestInt(float v) => (int)MathF.Round(v);
}
