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
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
            var amax = AbsoluteMax(block);

            scales[b] = (float)(Half)(amax / 127f);
            var id = amax != 0f ? 127f / amax : 0f;
            var q = quants.Slice(b * BlockSize, BlockSize);
            if (Vectored) RoundBlock(block, id, q);
            else for (var j = 0; j < BlockSize; j++) q[j] = RoundToInt8(block[j] * id);
        }
    }

    // Whether this runtime has real vectors: the Mono that ships does not, and would pay to pretend.
    private static bool Vectored => AdvSimd.Arm64.IsSupported || Sse41.IsSupported;

    private static float AbsoluteMax(ReadOnlySpan<float> block)
    {
        var amax = 0f;
        if (!Vectored)
        {
            for (var j = 0; j < BlockSize; j++) amax = MathF.Max(amax, MathF.Abs(block[j]));
            return amax;
        }

        ref var x = ref MemoryMarshal.GetReference(block);
        var most = Vector128<float>.Zero;
        for (nuint j = 0; j < BlockSize; j += 4) most = Vector128.Max(most, Vector128.Abs(Vector128.LoadUnsafe(ref x, j)));
        for (var j = 0; j < 4; j++) amax = MathF.Max(amax, most[j]);
        return amax;
    }

    // RoundToInt8, sixteen at a time: rounded half to even, and nothing a block holds can leave the range.
    private static void RoundBlock(ReadOnlySpan<float> block, float id, Span<sbyte> q)
    {
        ref var x = ref MemoryMarshal.GetReference(block);
        ref var y = ref MemoryMarshal.GetReference(q);
        var scale = Vector128.Create(id);
        for (nuint j = 0; j < BlockSize; j += 16)
        {
            var a = Vector128.ConvertToInt32(Vector128.Round(Vector128.LoadUnsafe(ref x, j) * scale));
            var b = Vector128.ConvertToInt32(Vector128.Round(Vector128.LoadUnsafe(ref x, j + 4) * scale));
            var c = Vector128.ConvertToInt32(Vector128.Round(Vector128.LoadUnsafe(ref x, j + 8) * scale));
            var d = Vector128.ConvertToInt32(Vector128.Round(Vector128.LoadUnsafe(ref x, j + 12) * scale));
            Vector128.Narrow(Vector128.Narrow(a, b), Vector128.Narrow(c, d)).StoreUnsafe(ref y, j);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte RoundToInt8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v), -128, 127);
}
