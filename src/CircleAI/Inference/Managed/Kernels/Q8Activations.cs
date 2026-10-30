// Q8Activations.cs
//
// The int8 form an activation vector takes before it meets ternary weights. Bonsai's
// PTQ1_0 pairs with Q8_0: blocks of 32 values, one scale each. The rounding follows
// the pinned fork's x86 path (ggml-cpu/arch/x86/quants.c, quantize_row_q8_0) exactly,
// because that build made the S0 fixtures this engine is held to:
//
//   d  = amax / 127, stored as fp16
//   id = 127 / amax            (NOT 1/d - the reference quantiser and the x86 path
//                               differ here, and the x86 path is the ruler)
//   q  = round-half-to-even(x * id)
//
// Two values at a rounding midpoint go to different integers under half-away-from-
// zero, which is what C's roundf does; MathF.Round's default is half-to-even, which
// is what _mm256_round_ps(_MM_ROUND_NEAREST) does.

using System.Runtime.CompilerServices;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>An activation vector quantised to Q8_0 blocks of 32.</summary>
public sealed class Q8Activations
{
    /// <summary>Values per block.</summary>
    public const int BlockSize = 32;

    /// <summary>The scale of each block, already rounded through fp16 as the reference stores it.</summary>
    public float[] Scales { get; }

    /// <summary>The quantised values, <see cref="Length"/> of them.</summary>
    public sbyte[] Quants { get; }

    /// <summary>Number of values; a multiple of <see cref="BlockSize"/>.</summary>
    public int Length { get; }

    /// <summary>Allocates room for <paramref name="length"/> values.</summary>
    public Q8Activations(int length)
    {
        if (length <= 0 || length % BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Q8_0 needs a positive multiple of 32 values.");
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
            var amax = 0f;
            for (var j = 0; j < BlockSize; j++) amax = MathF.Max(amax, MathF.Abs(block[j]));

            Scales[b] = (float)(Half)(amax / 127f);
            var id = amax != 0f ? 127f / amax : 0f;
            var q = Quants.AsSpan(b * BlockSize, BlockSize);
            for (var j = 0; j < BlockSize; j++) q[j] = RoundToInt8(block[j] * id);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte RoundToInt8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v), -128, 127);
}
