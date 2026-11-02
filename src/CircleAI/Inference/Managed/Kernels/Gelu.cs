// Gelu.cs
//
// GELU as the pinned fork's CPU backend computes it (ggml-cpu/vec.h, with GGML_GELU_FP16 on, as it
// is by default): the tanh form 0.5 x (1 + tanh(sqrt(2/pi) x (1 + 0.044715 x^2))) looked up in a
// table holding one entry per fp16 value - the input rounded to fp16, each entry computed in fp32
// and rounded to fp16 - except that at or below -10 the result is 0 and at or above 10 it is x.
// The rounding is the fork's, so it is kept: an exact GELU would differ from it by up to an
// fp16 step, which is the size of the disagreements this engine is held below.

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>GELU (tanh form) as the fork's fp16 table gives it.</summary>
public static class Gelu
{
    private const float CoefA = 0.044715f;
    private const float SqrtTwoOverPi = 0.79788456080286535587989211986876f;

    private static readonly Lazy<float[]> s_table = new(Build);

    /// <summary>The tanh-form GELU in fp32, as the table's entries are made.</summary>
    public static float Exact(float x) => 0.5f * x * (1.0f + MathF.Tanh(SqrtTwoOverPi * x * (1.0f + CoefA * x * x)));

    /// <summary>Replaces each value with its GELU, through the fp16 table.</summary>
    public static void Apply(Span<float> values)
    {
        var table = s_table.Value;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            values[i] = v <= -10f ? 0f : v >= 10f ? v : table[BitConverter.HalfToUInt16Bits((Half)v)];
        }
    }

    private static float[] Build()
    {
        var table = new float[1 << 16];
        for (var i = 0; i < table.Length; i++)
        {
            var x = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            table[i] = (float)(Half)Exact(x);
        }
        return table;
    }
}
