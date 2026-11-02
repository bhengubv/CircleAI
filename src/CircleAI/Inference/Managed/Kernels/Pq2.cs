// Pq2.cs
//
// PQ2_0, Bonsai 2's 2-bit packing (ggml type 142), and its dot product with Q8_K activations (and
// with Q8_0 where a row is not whole Q8_K blocks). One block holds 128 weights in 34 bytes:
//
//   d          fp16 scale at byte 0
//   qs[0..31]  four 2-bit codes per byte, lowest pair first: weight j is bits 2*(j%4) of qs[j/4]
//
// The weight is (code - 1) * d, so the ternary {-1, 0, +1} sit on codes 0, 1 and 2 (code 3, +2, is
// a legal value nothing in the fleet writes). Each byte holds four CONSECUTIVE weights, which is
// what makes the lookup-table form (Pq2Lut) one table per byte position.
//
// The dot product sums as the fork's x86 build does (ggml-cpu/arch/x86/quants.c,
// ggml_vec_dot_pq2_0_q8_K, the SSE path the shipped win-x64 build takes - it is compiled for SSE4.2,
// without AVX): an exact int32 sum over the whole 128-weight block, then
//
//   sumf += (d_w * d_y) * (float)sumi        d_y being the scale of the Q8_K block it falls in
//
// block after block. With Q8_0 activations (a row that is not whole 256s, ggml_cpu_pq2_needs_q8_0)
// it is ggml_vec_dot_pq2_0_q8_0's order instead: an int32 sum per 32-weight sub-block, scaled by
// that sub-block's activation scale and summed, then scaled by the block's weight scale. Every path
// below keeps the order of the activations it is given, so all of them agree with the scalar path
// to the last bit.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>PQ2_0 decoding and its dot products with <see cref="Q8KActivations"/> and <see cref="Q8Activations"/>.</summary>
public static class Pq2
{
    /// <summary>Weights per block.</summary>
    public const int BlockWeights = 128;

    /// <summary>Bytes per block.</summary>
    public const int BlockBytes = 34;

    /// <summary>Where a block's codes start: after its fp16 scale.</summary>
    public const int CodesOffset = 2;

    /// <summary>The fastest path this CPU and runtime can take: the instruction paths are PTQ1_0's.</summary>
    public static KernelPath Best => Ptq1.Best;

    /// <summary>Whether <paramref name="path"/> can run here.</summary>
    public static bool CanRun(KernelPath path) => Ptq1.CanRun(path);

    /// <summary>The weight scale of one block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scale(ReadOnlySpan<byte> block) => (float)BinaryPrimitives.ReadHalfLittleEndian(block[..2]);

    /// <summary>Decodes one block's 128 weights to code - 1 (-1, 0, +1 or +2), in weight order.</summary>
    public static void DecodeScalar(ReadOnlySpan<byte> block, Span<sbyte> values)
    {
        for (var j = 0; j < BlockWeights; j++)
            values[j] = (sbyte)(((block[CodesOffset + (j >> 2)] >> (2 * (j & 3))) & 3) - 1);
    }

    // For 16 weights at a time: each of four code bytes repeated four times, widened to 16 bits and
    // multiplied so that the wanted pair lands at bits 6-7; a shift and a mask then leave the code.
    private static readonly Vector128<byte>[] Repeat =
    [
        Vector128.Create((byte)0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3),
        Vector128.Create((byte)4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7),
        Vector128.Create((byte)8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11),
        Vector128.Create((byte)12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15),
    ];

    private static readonly Vector128<ushort> PairToTop = Vector128.Create((ushort)64, 16, 4, 1, 64, 16, 4, 1);

    /// <summary>
    /// Decodes one block with 128-bit vectors, 16 weights a step. Equal to <see cref="DecodeScalar"/>
    /// byte for byte.
    /// </summary>
    public static void DecodeVector(ReadOnlySpan<byte> block, Span<sbyte> values)
    {
        if (block.Length < BlockBytes) throw new ArgumentException($"A block is {BlockBytes} bytes.", nameof(block));
        if (values.Length < BlockWeights) throw new ArgumentException($"A block decodes to {BlockWeights} values.", nameof(values));
        ref var src = ref MemoryMarshal.GetReference(block);
        ref var dst = ref MemoryMarshal.GetReference(values);
        var three = Vector128.Create((ushort)3);
        var one = Vector128.Create((sbyte)1);
        for (var half = 0; half < 2; half++)
        {
            var codes = Vector128.LoadUnsafe(ref src, (nuint)(CodesOffset + half * 16));
            for (var group = 0; group < 4; group++)
            {
                var repeated = Vector128.Shuffle(codes, Repeat[group]);
                var (lo, hi) = Vector128.Widen(repeated);
                lo = Vector128.ShiftRightLogical(lo * PairToTop, 6) & three;
                hi = Vector128.ShiftRightLogical(hi * PairToTop, 6) & three;
                (Vector128.Narrow(lo, hi).AsSByte() - one).StoreUnsafe(ref dst, (nuint)(half * 64 + group * 16));
            }
        }
    }

    /// <summary>
    /// Dot product of one row of PQ2_0 blocks with Q8_K activations, on the requested
    /// <paramref name="path"/>. The row holds <c>x.Length / 128</c> blocks.
    /// </summary>
    public static float Dot(ReadOnlySpan<byte> row, Q8KActivations x, KernelPath path)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (path == KernelPath.Table) return DotTable(row, BlockScales(row), x);
        if (path == KernelPath.Lut)
        {
            var lut = new Pq2Lut(x.Length);
            lut.Build(x);
            return lut.Dot(row, BlockScales(row));
        }
        var blocks = x.Length / BlockWeights;
        CheckRow(row, blocks);

        Span<sbyte> values = stackalloc sbyte[BlockWeights];
        var q = x.Quants;
        var sumf = 0f;
        for (var i = 0; i < blocks; i++)
        {
            var block = row.Slice(i * BlockBytes, BlockBytes);
            if (path == KernelPath.Scalar) DecodeScalar(block, values);
            else DecodeVector(block, values);

            var sumi = 0;
            for (var k = 0; k < 4; k++)
                sumi += Ptq1.Dot32(values.Slice(k * 32, 32), q.AsSpan(i * BlockWeights + k * 32, 32), path);
            sumf += (Scale(block) * x.Scales[i >> 1]) * (float)sumi;
        }
        return sumf;
    }

    /// <summary>
    /// Dot product of one row of PQ2_0 blocks with Q8_0 activations - the fork's form for a row that
    /// is not whole Q8_K blocks - on the requested <paramref name="path"/>.
    /// </summary>
    public static float Dot(ReadOnlySpan<byte> row, Q8Activations x, KernelPath path)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (path == KernelPath.Table) return DotTable(row, BlockScales(row), x);
        if (path == KernelPath.Lut)
        {
            var lut = new Pq2Lut(x.Length);
            lut.Build(x);
            return lut.Dot(row, BlockScales(row));
        }
        var blocks = x.Length / BlockWeights;
        CheckRow(row, blocks);

        Span<sbyte> values = stackalloc sbyte[BlockWeights];
        var q = x.Quants;
        var sumf = 0f;
        for (var i = 0; i < blocks; i++)
        {
            var block = row.Slice(i * BlockBytes, BlockBytes);
            if (path == KernelPath.Scalar) DecodeScalar(block, values);
            else DecodeVector(block, values);

            var sumi = 0f;
            for (var k = 0; k < 4; k++)
                sumi += x.Scales[i * 4 + k] * Ptq1.Dot32(values.Slice(k * 32, 32), q.AsSpan(i * BlockWeights + k * 32, 32), path);
            sumf += Scale(block) * sumi;
        }
        return sumf;
    }

    private static void CheckRow(ReadOnlySpan<byte> row, int blocks)
    {
        if (row.Length < blocks * BlockBytes)
            throw new ArgumentException($"Row holds {row.Length} bytes; {blocks} blocks need {blocks * BlockBytes}.", nameof(row));
    }

    // Every byte's four weights, code - 1, decoded once: byte b's weight k is at b * 4 + k.
    private static readonly sbyte[] CodeTable = BuildCodeTable();

    private static sbyte[] BuildCodeTable()
    {
        var table = new sbyte[256 * 4];
        for (var b = 0; b < 256; b++)
            for (var k = 0; k < 4; k++) table[b * 4 + k] = (sbyte)(((b >> (2 * k)) & 3) - 1);
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
    /// The <see cref="KernelPath.Table"/> dot product of one row with Q8_K activations: each byte's
    /// four weights come from a 256-entry table and go straight into the block's one integer sum.
    /// <paramref name="blockScales"/> holds this row's block scales (<see cref="BlockScales"/>).
    /// Equal to <see cref="Dot(ReadOnlySpan{byte}, Q8KActivations, KernelPath)"/> on the scalar path
    /// to the last bit.
    /// </summary>
    public static unsafe float DotTable(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales, Q8KActivations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes || blockScales.Length < blocks)
            throw new ArgumentException("Row or scales too short for the activations.");

        var sumf = 0f;
        fixed (byte* rowStart = row)
        fixed (float* dw = blockScales)
        fixed (sbyte* table = CodeTable)
        fixed (sbyte* qStart = x.Quants)
        fixed (float* dy = x.Scales)
        {
            for (var i = 0; i < blocks; i++)
            {
                var codes = rowStart + i * BlockBytes + CodesOffset;
                var y = qStart + i * BlockWeights;
                var s = 0;
                for (var p = 0; p < 32; p++, y += 4)
                {
                    var t = table + codes[p] * 4;
                    s += t[0] * y[0] + t[1] * y[1] + t[2] * y[2] + t[3] * y[3];
                }
                sumf += (dw[i] * dy[i >> 1]) * (float)s;
            }
        }
        return sumf;
    }

    /// <summary>
    /// The <see cref="KernelPath.Table"/> dot product of one row with Q8_0 activations; equal to
    /// <see cref="Dot(ReadOnlySpan{byte}, Q8Activations, KernelPath)"/> on the scalar path to the last bit.
    /// </summary>
    public static unsafe float DotTable(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales, Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes || blockScales.Length < blocks)
            throw new ArgumentException("Row or scales too short for the activations.");

        var sumf = 0f;
        fixed (byte* rowStart = row)
        fixed (float* dw = blockScales)
        fixed (sbyte* table = CodeTable)
        fixed (sbyte* qStart = x.Quants)
        fixed (float* dy = x.Scales)
        {
            for (var i = 0; i < blocks; i++)
            {
                var codes = rowStart + i * BlockBytes + CodesOffset;
                var y = qStart + i * BlockWeights;
                var sumi = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var s = 0;
                    for (var p = 8 * k; p < 8 * k + 8; p++, y += 4)
                    {
                        var t = table + codes[p] * 4;
                        s += t[0] * y[0] + t[1] * y[1] + t[2] * y[2] + t[3] * y[3];
                    }
                    sumi += dy[i * 4 + k] * s;
                }
                sumf += dw[i] * sumi;
            }
        }
        return sumf;
    }
}
