// Q8Activations.cs
//
// The int8 form an activation vector takes before it meets ternary weights. Bonsai's
// PTQ1_0 pairs with Q8_0: blocks of 32 values, one scale each. The rounding follows
// the AVX branch of the pinned fork's x86 quantize_row_q8_0 (ggml-cpu/arch/x86/quants.c):
//
//   d  = amax / 127, stored as fp16
//   id = 127 / amax
//   q  = round-half-to-even(x * id)
//
// Two values at a rounding midpoint go to different integers under half-away-from-
// zero, which is what C's roundf does; MathF.Round's default is half-to-even, which
// is what _mm256_round_ps(_MM_ROUND_NEAREST) does.
//
// THE RULER DOES NOT RUN THIS BRANCH - MEASURED, AND KEPT ON PURPOSE (S5). The shipped win-x64 fork
// is built without AVX (its CMakeCache: GGML_AVX/AVX2 OFF, GGML_SSE42 ON), so its quantize_row_q8_0
// is the reference one: id = 1 / d, C's roundf. Asked directly - ggml_get_type_traits_cpu(Q8_0)'s
// from_float in the shipped ggml-cpu.dll - it puts amax 1.4285147, x 1.1304388 on 101 where this
// puts it on 100, and 2.5 at amax 127 on 3 where this puts it on 2. (127 / amax and 1 / d differ in
// the last bit for 27% of blocks.) S5 switched this file to the reference rounding and measured, over
// all 12 x 32 parity steps: the text no closer (mean top-20 differences 0.036 for the 2B and 0.035 for
// the 4B with it, 0.036 and 0.036 with this; worst 0.276 and 0.318 with it, 0.249 and 0.309 with this)
// and the vision tower's worst value further from the fork's (0.148 against 0.029, beyond the fork's
// own 0.11). So the switch was taken back, and which rounding the
// engine keeps is a decision for the owner with that evidence. The fork on the phone (the NEON
// branch) takes id = 1 / d with midpoints to even - neither this nor the reference.

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
        QuantizeInto(x, Scales, Quants);
    }

    /// <summary>
    /// Quantises <paramref name="x"/> - a multiple of 32 values - into <paramref name="scales"/>
    /// (one per block) and <paramref name="quants"/>, rounding exactly as <see cref="Quantize"/>
    /// does: every Q8_0 product in the engine reads activations rounded this one way.
    /// </summary>
    public static void QuantizeInto(ReadOnlySpan<float> x, Span<float> scales, Span<sbyte> quants)
    {
        if (x.Length % BlockSize != 0 || scales.Length < x.Length / BlockSize || quants.Length < x.Length)
            throw new ArgumentException("Q8_0 takes whole blocks of 32, with room for each block's scale and values.", nameof(x));

        for (var b = 0; b < x.Length / BlockSize; b++)
        {
            var block = x.Slice(b * BlockSize, BlockSize);
            var amax = 0f;
            for (var j = 0; j < BlockSize; j++) amax = MathF.Max(amax, MathF.Abs(block[j]));

            scales[b] = (float)(Half)(amax / 127f);
            var id = amax != 0f ? 127f / amax : 0f;
            var q = quants.Slice(b * BlockSize, BlockSize);
            for (var j = 0; j < BlockSize; j++) q[j] = RoundToInt8(block[j] * id);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte RoundToInt8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v), -128, 127);
}
