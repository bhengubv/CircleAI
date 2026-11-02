// Q8Activations.cs
//
// The int8 form an activation vector takes before it meets ternary weights. Bonsai's
// PTQ1_0 pairs with Q8_0: blocks of 32 values, one scale each. The rounding follows
// the quantiser the build that made the fixtures actually runs, exactly:
//
//   d  = amax / 127, stored as fp16
//   id = 1 / d                 (d before its fp16 rounding)
//   q  = roundf(x * id)        (a midpoint goes away from zero)
//
// THAT IS THE REFERENCE QUANTISER, quantize_row_q8_0_ref, AND THIS FILE SAID OTHERWISE until
// S5. It followed the AVX branch of ggml-cpu/arch/x86/quants.c (id = 127 / amax, midpoints to
// even), on the belief that the shipped win-x64 llama.dll takes it. It does not: that build is
// MSVC for x64 without /arch:AVX - its system info reads "BMI2 | LLAMAFILE | OPENMP | REPACK",
// no AVX - so quantize_row_q8_0 falls through its `#if defined(__AVX2__) || defined(__AVX__)`
// to the reference. 127 / amax and 1 / (amax / 127) differ in the last bit for 27% of blocks
// (200,000 random scales), and where x * id sits that close to a midpoint the two put a value on different
// integers: amax 1.4285147, x 1.1304388 is 101 here and was 100 (Ptq1KernelTests pins it).
// The fork on the phone (the NEON branch) also takes id = 1 / d; only its midpoints go to even.

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

            var d = amax / 127f;
            scales[b] = (float)(Half)d;
            var id = d != 0f ? 1f / d : 0f;
            var q = quants.Slice(b * BlockSize, BlockSize);
            for (var j = 0; j < BlockSize; j++) q[j] = RoundToInt8(block[j] * id);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte RoundToInt8(float v) =>
        (sbyte)Math.Clamp((int)MathF.Round(v, MidpointRounding.AwayFromZero), -128, 127);
}
