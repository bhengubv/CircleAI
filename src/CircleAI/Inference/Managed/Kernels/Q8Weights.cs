// Q8Weights.cs
//
// Q8_0 WEIGHTS (ggml type 8) and their product with Q8_0 activations: the MTP head's block is
// stored this way in the Bonsai 2 PQ2_0-MTP files (blk.{n_layer}.*, Q8_0, not Hadamard-folded).
// One block holds 32 weights in 34 bytes: an fp16 scale, then 32 int8 values.
//
// The product sums as the fork's x86 build does (ggml-cpu/arch/x86/quants.c,
// ggml_vec_dot_q8_0_q8_0 - the shipped win-x64 build is SSE4.2, so neither its AVX2 nor its AVX
// loop is compiled and the scalar tail is the whole of it): an exact int32 sum per block, then
//
//   sumf += sumi * (d_w * d_y)
//
// block after block. The vector path keeps every integer sum exact and the float steps in that
// order, so it equals the scalar path to the last bit.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Q8_0 weight rows and their product with <see cref="Q8Activations"/>.</summary>
public static class Q8Weights
{
    /// <summary>Weights per block.</summary>
    public const int BlockWeights = 32;

    /// <summary>Bytes per block: an fp16 scale and 32 int8 values.</summary>
    public const int BlockBytes = 34;

    /// <summary>The weight scale of one block.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Scale(ReadOnlySpan<byte> block) => (float)BinaryPrimitives.ReadHalfLittleEndian(block[..2]);

    /// <summary>Every block's weight scale as a float, decoded once.</summary>
    public static float[] BlockScales(ReadOnlySpan<byte> matrix)
    {
        var scales = new float[matrix.Length / BlockBytes];
        for (var i = 0; i < scales.Length; i++) scales[i] = Scale(matrix.Slice(i * BlockBytes, BlockBytes));
        return scales;
    }

    /// <summary>One row's product with <paramref name="x"/>, one value at a time: the reference.</summary>
    public static float DotScalar(ReadOnlySpan<byte> row, Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes) throw new ArgumentException("The row is too short for the activations.", nameof(row));
        var sumf = 0f;
        for (var i = 0; i < blocks; i++)
        {
            var block = row.Slice(i * BlockBytes, BlockBytes);
            var q = MemoryMarshal.Cast<byte, sbyte>(block[2..]);
            var y = x.Quants.AsSpan(i * BlockWeights, BlockWeights);
            var sumi = 0;
            for (var j = 0; j < BlockWeights; j++) sumi += q[j] * y[j];
            sumf += sumi * (Scale(block) * x.Scales[i]);
        }
        return sumf;
    }

    /// <summary>
    /// One row's product with <paramref name="x"/>, its integer sums by vectors, given the row's block
    /// scales decoded once (<see cref="BlockScales"/>). Equal to <see cref="DotScalar"/> to the last bit.
    /// </summary>
    public static unsafe float Dot(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales, Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        var blocks = x.Length / BlockWeights;
        if (row.Length < blocks * BlockBytes || blockScales.Length < blocks)
            throw new ArgumentException("Row or scales too short for the activations.");
        fixed (byte* r = row)
        fixed (float* dw = blockScales)
        fixed (sbyte* q = x.Quants)
        fixed (float* dy = x.Scales)
            return RowDot(r, dw, q, dy, blocks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float RowDot(byte* row, float* dw, sbyte* q, float* dy, int blocks)
    {
        var sumf = 0f;
        for (var i = 0; i < blocks; i++)
        {
            var w = (sbyte*)(row + i * BlockBytes + 2);
            var y = q + i * BlockWeights;
            sumf += Dot32(w, y) * (dw[i] * dy[i]);
        }
        return sumf;
    }

    // An exact int32 dot product of 32 int8 pairs: widened to 16 bits, multiplied, widened to 32.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int Dot32(sbyte* a, sbyte* b)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            var s = 0;
            for (var j = 0; j < BlockWeights; j++) s += a[j] * b[j];
            return s;
        }
        var acc = Vector128<int>.Zero;
        for (var off = 0; off < BlockWeights; off += 16)
        {
            var (al, ah) = Vector128.Widen(Vector128.Load(a + off));
            var (bl, bh) = Vector128.Widen(Vector128.Load(b + off));
            var (pl0, pl1) = Vector128.Widen(al * bl);
            var (ph0, ph1) = Vector128.Widen(ah * bh);
            acc += pl0 + pl1 + ph0 + ph1;
        }
        return Vector128.Sum(acc);
    }

    /// <summary>
    /// Every row's product with <paramref name="x"/>, one value per row of <paramref name="output"/>,
    /// shared across <paramref name="pool"/>'s threads when one is given. Each row is summed by one
    /// thread in the reference's order, so the result does not depend on the number of threads.
    /// </summary>
    public static unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, Q8Activations x, Span<float> output, WorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(x);
        var blocks = x.Length / BlockWeights;
        if (matrix.Length < (long)output.Length * blocks * BlockBytes || blockScales.Length < (long)output.Length * blocks)
            throw new ArgumentException("Matrix or scales too short for the output.");
        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        fixed (sbyte* q = x.Quants)
        fixed (float* dy = x.Scales)
        fixed (float* o = output)
        {
            var work = new RowsWork(m, dw, q, dy, o, output.Length, blocks);
            if (pool is null) work.Execute(0, 1);
            else pool.Run(work);
        }
    }

    // Rows handed out in chunks to whichever thread is free.
    private sealed unsafe class RowsWork(byte* matrix, float* scales, sbyte* q, float* dy, float* output, int rows, int blocks) : WorkerPool.IWork
    {
        private const int Chunk = 64;
        private int _next;

        public void Execute(int worker, int workers)
        {
            var rowBytes = (long)blocks * BlockBytes;
            int first;
            while ((first = (Interlocked.Increment(ref _next) - 1) * Chunk) < rows)
            {
                var end = Math.Min(rows, first + Chunk);
                for (var r = first; r < end; r++)
                    output[r] = RowDot(matrix + r * rowBytes, scales + (long)r * blocks, q, dy, blocks);
            }
        }
    }
}
