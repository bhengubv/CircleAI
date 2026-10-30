// Ptq1.cs
//
// PTQ1_0, Bonsai 2's dense ternary format (ggml type 143), and its dot product with
// Q8_0 activations. One block holds 128 weights in 28 bytes:
//
//   qs[0..15]   five trits per byte  -> weights n*16 + m        (n = 0..4, m = 0..15)
//   qs[16..23]  five trits per byte  -> weights 80 + n*8 + m    (n = 0..4, m = 0..7)
//   qh[0..1]    four trits per byte  -> weights 120 + n*2 + h   (n = 0..3, h = 0..1)
//   d           fp16 scale at byte 26
//
// Trit n of a byte is ((byte * 3^n mod 256) * 3) >> 8, giving 0, 1 or 2; the weight is
// (trit - 1) * d. Each group of trits lands on CONSECUTIVE weights, which is what lets
// one vector multiply decode 16 weights at a time.
//
// The dot product sums in the same order as the fork's generic kernel
// (ggml-cpu/quants.c, ggml_vec_dot_ptq1_0_q8_0_generic): an exact int32 sum per
// 32-weight sub-block, scaled by that sub-block's activation scale, summed, then
// scaled by the block's weight scale. Every vector path below keeps that order, so
// all of them agree with the scalar path to the last bit.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Which instruction path a kernel ran on.</summary>
public enum KernelPath
{
    /// <summary>Plain C#, one value at a time: the reference every other path must equal.</summary>
    Scalar = 0,

    /// <summary>128-bit vectors through the portable <see cref="Vector128"/> API.</summary>
    Vector128 = 1,

    /// <summary>ARM NEON widening multiplies (ARMv8.0 - the Kirin 710's level).</summary>
    AdvSimd = 2,

    /// <summary>ARM dot-product instructions (ARMv8.2 and later).</summary>
    Dp = 3,

    /// <summary>x86 AVX2.</summary>
    Avx2 = 4,

    /// <summary>
    /// Plain pointer code with a 256-entry trit table and the block's four sub-block
    /// sums accumulated directly - no vectors. Built for Mono on Android, which exposes
    /// no ARM intrinsics and accelerated the vector path only 1.7x on the P30 (S0.2).
    /// </summary>
    Table = 5,

    /// <summary>
    /// Per-input lookup tables (<see cref="Ptq1Lut"/>): one load and one add per weight byte.
    /// Equal to the scalar path up to float rounding order, not to the last bit.
    /// </summary>
    Lut = 6,
}

/// <summary>PTQ1_0 decoding and its dot product with <see cref="Q8Activations"/>.</summary>
public static class Ptq1
{
    /// <summary>Weights per block.</summary>
    public const int BlockWeights = 128;

    /// <summary>Bytes per block.</summary>
    public const int BlockBytes = 28;

    private static ReadOnlySpan<byte> Pow3 => [1, 3, 9, 27, 81];

    /// <summary>The fastest path this CPU and runtime can take.</summary>
    public static KernelPath Best { get; } =
        Dp.IsSupported ? KernelPath.Dp :
        AdvSimd.Arm64.IsSupported ? KernelPath.AdvSimd :
        System.Runtime.Intrinsics.X86.Avx2.IsSupported ? KernelPath.Avx2 :
        System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated ? KernelPath.Vector128 :
        KernelPath.Scalar;

    /// <summary>Whether <paramref name="path"/> can run here.</summary>
    public static bool CanRun(KernelPath path) => path switch
    {
        KernelPath.Scalar => true,
        KernelPath.Table => true,
        KernelPath.Lut => true,
        KernelPath.Vector128 => System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated,
        KernelPath.AdvSimd => AdvSimd.Arm64.IsSupported,
        KernelPath.Dp => Dp.IsSupported,
        KernelPath.Avx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported,
        _ => false,
    };

    /// <summary>The weight scale of one block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scale(ReadOnlySpan<byte> block) => (float)BinaryPrimitives.ReadHalfLittleEndian(block.Slice(26, 2));

    /// <summary>Decodes one block's 128 weights to -1, 0 or +1, in weight order.</summary>
    public static void DecodeScalar(ReadOnlySpan<byte> block, Span<sbyte> trits)
    {
        var o = 0;
        for (var n = 0; n < 5; n++)
            for (var m = 0; m < 16; m++) trits[o++] = Trit(block[m], n);
        for (var n = 0; n < 5; n++)
            for (var m = 0; m < 8; m++) trits[o++] = Trit(block[16 + m], n);
        for (var n = 0; n < 4; n++)
            for (var h = 0; h < 2; h++) trits[o++] = Trit(block[24 + h], n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static sbyte Trit(byte b, int n) => (sbyte)((((byte)(b * Pow3[n]) * 3) >> 8) - 1);

    /// <summary>
    /// Decodes one block with 128-bit vectors: each multiply by 3^n yields 16 (or 8)
    /// consecutive weights. Equal to <see cref="DecodeScalar"/> byte for byte.
    /// </summary>
    public static void DecodeVector(ReadOnlySpan<byte> block, Span<sbyte> trits)
    {
        ref var src = ref MemoryMarshal.GetReference(block);
        ref var dst = ref MemoryMarshal.GetReference(trits);

        var (lo, hi) = Vector128.Widen(Vector128.LoadUnsafe(ref src));
        var small = Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 16))).AsByte());
        var mask = Vector128.Create((ushort)0xFF);
        var one = Vector128.Create((sbyte)1);

        for (var n = 0; n < 5; n++)
        {
            var p = Pow3[n];
            var tl = Vector128.ShiftRightLogical(((lo * p) & mask) * 3, 8);
            var th = Vector128.ShiftRightLogical(((hi * p) & mask) * 3, 8);
            (Vector128.Narrow(tl, th).AsSByte() - one).StoreUnsafe(ref dst, (nuint)(n * 16));

            var ts = Vector128.ShiftRightLogical(((small * p) & mask) * 3, 8);
            var narrow = (Vector128.Narrow(ts, ts).AsSByte() - one).AsUInt64().ToScalar();
            Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref dst, 80 + n * 8)), narrow);
        }

        // The last 8 weights are two bytes of four trits; vectors buy nothing here.
        var o = 120;
        for (var n = 0; n < 4; n++)
            for (var h = 0; h < 2; h++) trits[o++] = Trit(block[24 + h], n);
    }

    /// <summary>
    /// Dot product of one row of PTQ1_0 blocks with quantised activations, on the
    /// requested <paramref name="path"/>. The row holds <c>x.Length / 128</c> blocks.
    /// </summary>
    public static float Dot(ReadOnlySpan<byte> row, Q8Activations x, KernelPath path)
    {
        if (path == KernelPath.Table) return DotTable(row, BlockScales(row), x);
        if (path == KernelPath.Lut)
        {
            var lut = new Ptq1Lut(x.Length);
            lut.Build(x);
            return lut.Dot(row, BlockScales(row));
        }
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes)
            throw new ArgumentException($"Row holds {row.Length} bytes; {blocks} blocks need {blocks * BlockBytes}.", nameof(row));

        Span<sbyte> trits = stackalloc sbyte[BlockWeights];
        var q = x.Quants;
        var sumf = 0f;
        for (var i = 0; i < blocks; i++)
        {
            var block = row.Slice(i * BlockBytes, BlockBytes);
            if (path == KernelPath.Scalar) DecodeScalar(block, trits);
            else DecodeVector(block, trits);

            var sumi = 0f;
            for (var k = 0; k < 4; k++)
            {
                var a = trits.Slice(k * 32, 32);
                var b = q.AsSpan(i * BlockWeights + k * 32, 32);
                sumi += x.Scales[i * 4 + k] * Dot32(a, b, path);
            }
            sumf += Scale(block) * sumi;
        }
        return sumf;
    }

    // Every byte's five trits, -1/0/+1, decoded once: byte b's trit n is at b * 5 + n.
    // The two qh bytes of a block hold four trits and use the first four of these.
    private static readonly sbyte[] TritTable = BuildTritTable();

    private static sbyte[] BuildTritTable()
    {
        var table = new sbyte[256 * 5];
        for (var b = 0; b < 256; b++)
            for (var n = 0; n < 5; n++) table[b * 5 + n] = Trit((byte)b, n);
        return table;
    }

    /// <summary>Every block's weight scale as a float, decoded once instead of on every product.</summary>
    public static float[] BlockScales(ReadOnlySpan<byte> matrix)
    {
        var scales = new float[matrix.Length / BlockBytes];
        for (var i = 0; i < scales.Length; i++) scales[i] = Scale(matrix.Slice(i * BlockBytes, BlockBytes));
        return scales;
    }

    /// <summary>
    /// The <see cref="KernelPath.Table"/> dot product of one row: each byte's trits come from
    /// <see cref="TritTable"/> and go straight into the block's four sub-block sums, in the
    /// order the weights sit in. <paramref name="blockScales"/> holds this row's block scales
    /// (from <see cref="BlockScales"/>). Equal to <see cref="Dot"/> on the scalar path to the
    /// last bit: the integer sums are the same and the float steps run in the same order.
    /// </summary>
    public static unsafe float DotTable(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales, Q8Activations x)
    {
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes || blockScales.Length < blocks)
            throw new ArgumentException("Row or scales too short for the activations.");

        var sumf = 0f;
        fixed (byte* rowStart = row)
        fixed (float* dw = blockScales)
        fixed (sbyte* table = TritTable)
        fixed (sbyte* qStart = x.Quants)
        fixed (float* dy = x.Scales)
        {
            for (var i = 0; i < blocks; i++)
            {
                var blk = rowStart + i * BlockBytes;
                var y = qStart + i * BlockWeights;
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;

                // qs[0..15]: weights m, m+16 (sub-block 0), m+32, m+48 (1), m+64 (2).
                for (var m = 0; m < 16; m++)
                {
                    var t = table + blk[m] * 5;
                    s0 += t[0] * y[m] + t[1] * y[16 + m];
                    s1 += t[2] * y[32 + m] + t[3] * y[48 + m];
                    s2 += t[4] * y[64 + m];
                }

                // qs[16..23]: weights 80+m, 88+m (sub-block 2), 96+m, 104+m, 112+m (3).
                for (var m = 0; m < 8; m++)
                {
                    var t = table + blk[16 + m] * 5;
                    s2 += t[0] * y[80 + m] + t[1] * y[88 + m];
                    s3 += t[2] * y[96 + m] + t[3] * y[104 + m] + t[4] * y[112 + m];
                }

                // qh[0..1]: weights 120+h, 122+h, 124+h, 126+h (sub-block 3).
                for (var h = 0; h < 2; h++)
                {
                    var t = table + blk[24 + h] * 5;
                    s3 += t[0] * y[120 + h] + t[1] * y[122 + h] + t[2] * y[124 + h] + t[3] * y[126 + h];
                }

                var d = dy + i * 4;
                var sumi = 0f;
                sumi += d[0] * s0;
                sumi += d[1] * s1;
                sumi += d[2] * s2;
                sumi += d[3] * s3;
                sumf += dw[i] * sumi;
            }
        }
        return sumf;
    }

    /// <summary>An exact int32 dot product of 32 int8 pairs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Dot32(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b, KernelPath path)
    {
        ref var pa = ref MemoryMarshal.GetReference(a);
        ref var pb = ref MemoryMarshal.GetReference(b);
        switch (path)
        {
            case KernelPath.Dp:
            {
                var acc = Dp.DotProduct(Vector128<int>.Zero, Vector128.LoadUnsafe(ref pa), Vector128.LoadUnsafe(ref pb));
                acc = Dp.DotProduct(acc, Vector128.LoadUnsafe(ref pa, 16), Vector128.LoadUnsafe(ref pb, 16));
                return Vector128.Sum(acc);
            }
            case KernelPath.AdvSimd:
            {
                var acc = Vector128<int>.Zero;
                for (nuint off = 0; off < 32; off += 16)
                {
                    var va = Vector128.LoadUnsafe(ref pa, off);
                    var vb = Vector128.LoadUnsafe(ref pb, off);
                    acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningLower(va.GetLower(), vb.GetLower()));
                    acc = AdvSimd.AddPairwiseWideningAndAdd(acc, AdvSimd.MultiplyWideningUpper(va, vb));
                }
                return AdvSimd.Arm64.AddAcross(acc).ToScalar();
            }
            case KernelPath.Avx2:
            {
                var lo = Avx2.MultiplyAddAdjacent(
                    Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pa)),
                    Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pb)));
                var hi = Avx2.MultiplyAddAdjacent(
                    Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pa, 16)),
                    Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref pb, 16)));
                return Vector256.Sum(lo + hi);
            }
            case KernelPath.Vector128:
            {
                var acc = Vector128<int>.Zero;
                for (nuint off = 0; off < 32; off += 16)
                {
                    var (al, ah) = Vector128.Widen(Vector128.LoadUnsafe(ref pa, off));
                    var (bl, bh) = Vector128.Widen(Vector128.LoadUnsafe(ref pb, off));
                    var (pl0, pl1) = Vector128.Widen(al * bl);
                    var (ph0, ph1) = Vector128.Widen(ah * bh);
                    acc += pl0 + pl1 + ph0 + ph1;
                }
                return Vector128.Sum(acc);
            }
            default:
            {
                var s = 0;
                for (var j = 0; j < 32; j++) s += a[j] * b[j];
                return s;
            }
        }
    }
}
