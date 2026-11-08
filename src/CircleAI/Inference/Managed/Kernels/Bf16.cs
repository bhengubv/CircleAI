// Bf16.cs
//
// BF16 WEIGHTS, MULTIPLIED AS THE FORK MULTIPLIES THEM. A bfloat16 is the top half of a float, so widening one is exact.
// The fork's CPU product with a BF16 weight (ggml's vec_dot_type for BF16 is BF16) rounds its input to BF16 first, as
// ggml_compute_fp32_to_bf16 rounds - to nearest, ties to even, a NaN kept quiet - then multiplies each pair in single
// precision, which is exact (eight bits of mantissa times eight), and sums the products in double, in order: the scalar
// loop of ggml_vec_dot_bf16, the one an ARMv8.0 phone without BF16 instructions runs. The 27B keeps its DeltaNet gates
// (ssm_alpha, ssm_beta) in BF16; the 2B keeps them in F32.

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>bfloat16 values: widened exactly, rounded as the fork rounds, and multiplied as its CPU product does.</summary>
public static class Bf16
{
    /// <summary>The float a bfloat16's bits stand for: exact.</summary>
    public static float Widen(ushort bits) => BitConverter.UInt32BitsToSingle((uint)bits << 16);

    /// <summary>
    /// <paramref name="value"/> rounded to a bfloat16 as ggml_compute_fp32_to_bf16 rounds it: to nearest, ties to even;
    /// a NaN stays a NaN, made quiet.
    /// </summary>
    public static ushort Round(float value)
    {
        var u = BitConverter.SingleToUInt32Bits(value);
        if ((u & 0x7fff_ffffu) > 0x7f80_0000u) return (ushort)((u >> 16) | 64);
        return (ushort)((u + (0x7fffu + ((u >> 16) & 1u))) >> 16);
    }

    /// <summary>Each of <paramref name="x"/> rounded to a bfloat16 and widened back, into <paramref name="rounded"/>.</summary>
    public static void Round(ReadOnlySpan<float> x, Span<float> rounded)
    {
        if (rounded.Length < x.Length) throw new ArgumentException("The rounded values need as much room as the values.", nameof(rounded));
        for (var i = 0; i < x.Length; i++) rounded[i] = Widen(Round(x[i]));
    }

    /// <summary>
    /// y = W x for a row-major BF16 <paramref name="weights"/> of <paramref name="y"/>'s length rows, each as long as
    /// <paramref name="rounded"/> - an input already rounded to bfloat16 (<see cref="Round(ReadOnlySpan{float}, Span{float})"/>).
    /// Each product in single precision, each row's sum in double, in order.
    /// </summary>
    public static void MatVec(ReadOnlySpan<ushort> weights, ReadOnlySpan<float> rounded, Span<float> y)
    {
        var n = rounded.Length;
        if (weights.Length < (long)y.Length * n) throw new ArgumentException($"{y.Length} rows of {n} need {(long)y.Length * n} weights.", nameof(weights));
        for (var r = 0; r < y.Length; r++)
        {
            var row = weights.Slice(r * n, n);
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var product = Widen(row[i]) * rounded[i];
                sum += product;
            }
            y[r] = (float)sum;
        }
    }
}
