// Pq2Lut.cs
//
// The lookup-table form of the PQ2_0 product, built as Ptq1Lut is (the bitnet.cpp / T-MAC idea for
// CPUs without fast vectors - Mono on Android exposes no ARM intrinsics, S0.2): every row of a
// matrix meets the SAME input vector, and every PQ2_0 byte holds four weights at fixed positions,
// so for each byte position the sum over its four weights for all 256 byte values is computed once
// per input, and each row then pays one table load and one add per byte instead of four
// multiply-adds:
//
//   position p (0..31) of a block   byte qs[p]   weights 4p .. 4p+3
//
// UNLIKE PTQ1_0's TABLES THESE ARE EXACT. PQ2_0 meets Q8_K activations, one scale per 256 values,
// so a block's whole sum is an integer before it is scaled: the tables hold integer sums of the
// int8 activations (at most 4 * 2 * 128 = 1024 in size, an int16) and a row's block sum is their
// exact total. The float steps then run as the reference runs them - (d_w * d_y) * sum, block after
// block - so a product through the tables equals the scalar product to the last bit. The same holds
// for Q8_0 activations (a row that is not whole 256s), summed and scaled per 32-weight sub-block.
//
// One block's tables are 16 KB, so MatVec walks the matrix block by block across a chunk of rows,
// keeping one block's tables in the L1 cache, as Ptq1Lut does.

using System.Runtime.CompilerServices;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Per-input-vector lookup tables for PQ2_0 products.</summary>
public sealed class Pq2Lut
{
    /// <summary>Byte positions per block: 32 code bytes.</summary>
    public const int PositionsPerBlock = 32;

    private const int BlockTableEntries = PositionsPerBlock * 256;

    // Rows per chunk handed to a thread, at least, and a chunk's weights at most: Ptq1Lut's reasons.
    private const int MinChunkRows = 128;
    private const int MaxChunkBytes = 256 * 1024;

    private readonly short[] _table;
    private readonly float[] _scales;
    private readonly RowsWork _rows;
    private readonly BuildWork _build;

    /// <summary>Tables for an input of <paramref name="cols"/> values (a multiple of 128).</summary>
    public Pq2Lut(int cols)
    {
        if (cols <= 0 || cols % Pq2.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PQ2_0 rows are whole 128-weight blocks.");
        Blocks = cols / Pq2.BlockWeights;
        _table = new short[Blocks * BlockTableEntries];
        _scales = new float[Blocks * 4];
        _rows = new RowsWork(this);
        _build = new BuildWork(this);
    }

    /// <summary>Blocks per row.</summary>
    public int Blocks { get; }

    /// <summary>
    /// Whether the tables were last built from Q8_0 activations (a scale per 32-weight sub-block)
    /// rather than Q8_K (a scale per 256).
    /// </summary>
    public bool PerSubBlock { get; private set; }

    /// <summary>Builds every position's table for one Q8_K input vector.</summary>
    public void Build(Q8KActivations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        Check(x.Length);
        SetScales(x);
        BuildBlocks(x.Quants, 0, Blocks);
    }

    /// <summary>Builds every position's table for one Q8_0 input vector.</summary>
    public void Build(Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        Check(x.Length);
        SetScales(x);
        BuildBlocks(x.Quants, 0, Blocks);
    }

    /// <summary>
    /// The same tables built across <paramref name="pool"/>'s threads, a block at a time as each
    /// thread is free; every block is built exactly as <see cref="Build(Q8KActivations)"/> builds it.
    /// One build at a time per instance.
    /// </summary>
    public void Build(Q8KActivations x, WorkerPool pool)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(pool);
        Check(x.Length);
        SetScales(x);
        RunBuild(x.Quants, pool);
    }

    /// <summary>The same for a Q8_0 input vector.</summary>
    public void Build(Q8Activations x, WorkerPool pool)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(pool);
        Check(x.Length);
        SetScales(x);
        RunBuild(x.Quants, pool);
    }

    private void Check(int length)
    {
        if (length != Blocks * Pq2.BlockWeights)
            throw new ArgumentException($"Expected {Blocks * Pq2.BlockWeights} activations, got {length}.");
    }

    // A block's activation scale - the Q8_K block it is half of.
    private void SetScales(Q8KActivations x)
    {
        PerSubBlock = false;
        for (var b = 0; b < Blocks; b++) _scales[b] = x.Scales[b >> 1];
    }

    // Each 32-weight sub-block's activation scale.
    private void SetScales(Q8Activations x)
    {
        PerSubBlock = true;
        x.Scales.AsSpan(0, Blocks * 4).CopyTo(_scales);
    }

    private void RunBuild(sbyte[] quants, WorkerPool pool)
    {
        _build.Set(quants);
        try
        {
            pool.Run(_build);
        }
        finally
        {
            _build.Clear();
        }
    }

    // Blocks [first, end) of the tables, from the int8 activations.
    private unsafe void BuildBlocks(sbyte[] quants, int first, int end)
    {
        fixed (short* table = _table)
        fixed (sbyte* q = quants)
        {
            for (var b = first; b < end; b++)
            {
                var y = q + b * Pq2.BlockWeights;
                var dst = table + b * BlockTableEntries;
                for (var p = 0; p < PositionsPerBlock; p++, y += 4, dst += 256)
                    Fill(dst, y[0], y[1], y[2], y[3]);
            }
        }
    }

    // Every byte value's sum (code0 - 1) y0 + ... + (code3 - 1) y3, by running additions in byte
    // order: byte = c0 | c1 << 2 | c2 << 4 | c3 << 6, so c0 moves fastest.
    private static unsafe void Fill(short* dst, int y0, int y1, int y2, int y3)
    {
        for (var c3 = -1; c3 <= 2; c3++)
        {
            var a3 = c3 * y3;
            for (var c2 = -1; c2 <= 2; c2++)
            {
                var a2 = a3 + c2 * y2;
                for (var c1 = -1; c1 <= 2; c1++)
                {
                    var a1 = a2 + c1 * y1;
                    *dst++ = (short)(a1 - y0);
                    *dst++ = (short)a1;
                    *dst++ = (short)(a1 + y0);
                    *dst++ = (short)(a1 + 2 * y0);
                }
            }
        }
    }

    /// <summary>One row's product with the input the tables were built for.</summary>
    public unsafe float Dot(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales)
    {
        if (row.Length < Blocks * Pq2.BlockBytes || blockScales.Length < Blocks)
            throw new ArgumentException("Row or scales too short for the tables.");

        var sumf = 0f;
        fixed (byte* r = row)
        fixed (float* dw = blockScales)
        fixed (short* table = _table)
        fixed (float* dy = _scales)
        {
            for (var b = 0; b < Blocks; b++)
                sumf += BlockProduct(r + b * Pq2.BlockBytes + Pq2.CodesOffset, table + b * BlockTableEntries, dw[b], dy, b);
        }
        return sumf;
    }

    /// <summary>
    /// Every row's product with the input the tables were built for, one value per row of
    /// <paramref name="output"/>; <paramref name="blockScales"/> holds the matrix's block scales
    /// (<see cref="Pq2.BlockScales"/>). Equal to <see cref="Dot"/> on each row to the last bit.
    /// </summary>
    public unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, Span<float> output)
    {
        CheckMatrix(matrix, blockScales, output.Length);
        var chunk = Math.Max(MinChunkRows, MaxChunkBytes / (Blocks * Pq2.BlockBytes));
        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        fixed (float* o = output)
        {
            for (var first = 0; first < output.Length; first += chunk)
                MatVecRows(m, dw, o, first, Math.Min(output.Length, first + chunk));
        }
    }

    /// <summary>
    /// The same product shared across <paramref name="pool"/>'s threads. Each row is still summed by
    /// one thread in the same order, so the result is the single-thread result to the last bit,
    /// whatever the number of threads. One product at a time per instance.
    /// </summary>
    public unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, Span<float> output, WorkerPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        CheckMatrix(matrix, blockScales, output.Length);
        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        fixed (float* o = output)
        {
            var cached = MaxChunkBytes / (Blocks * Pq2.BlockBytes);
            _rows.Set(m, dw, o, output.Length, Math.Max(MinChunkRows, Math.Min(output.Length / (pool.Threads * 4), cached)));
            try
            {
                pool.Run(_rows);
            }
            finally
            {
                _rows.Clear();
            }
        }
    }

    private void CheckMatrix(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, int rows)
    {
        if (matrix.Length < (long)rows * Blocks * Pq2.BlockBytes || blockScales.Length < (long)rows * Blocks)
            throw new ArgumentException("Matrix or scales too short for the output.");
    }

    // Rows [first, end) of the product, block by block across those rows so one block's tables stay
    // in the L1 cache while every row uses them.
    private unsafe void MatVecRows(byte* matrix, float* blockScales, float* output, int first, int end)
    {
        var blocks = Blocks;
        var rowBytes = blocks * Pq2.BlockBytes;
        for (var r = first; r < end; r++) output[r] = 0f;
        fixed (short* table = _table)
        fixed (float* dy = _scales)
        {
            for (var b = 0; b < blocks; b++)
            {
                var tab = table + b * BlockTableEntries;
                var codes = matrix + (long)first * rowBytes + b * Pq2.BlockBytes + Pq2.CodesOffset;
                var scale = blockScales + (long)first * blocks + b;
                for (var r = first; r < end; r++, codes += rowBytes, scale += blocks)
                    output[r] += BlockProduct(codes, tab, *scale, dy, b);
            }
        }
    }

    // One block's contribution, in the reference's float order for the activations' kind.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe float BlockProduct(byte* codes, short* tab, float dw, float* dy, int block)
    {
        if (!PerSubBlock) return (dw * dy[block]) * (float)BlockSum(codes, tab);
        var d = dy + block * 4;
        var sumi = 0f;
        sumi += d[0] * SubBlockSum(codes, tab);
        sumi += d[1] * SubBlockSum(codes + 8, tab + 8 * 256);
        sumi += d[2] * SubBlockSum(codes + 16, tab + 16 * 256);
        sumi += d[3] * SubBlockSum(codes + 24, tab + 24 * 256);
        return dw * sumi;
    }

    // One block's integer sum: one lookup per byte, in four running sums so no add waits on the one
    // before it. Written out in full because Mono does not unroll loops.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int BlockSum(byte* c, short* tab)
    {
        var s0 = tab[c[0]] + tab[1024 + c[4]] + tab[2048 + c[8]] + tab[3072 + c[12]]
               + tab[4096 + c[16]] + tab[5120 + c[20]] + tab[6144 + c[24]] + tab[7168 + c[28]];
        var s1 = tab[256 + c[1]] + tab[1280 + c[5]] + tab[2304 + c[9]] + tab[3328 + c[13]]
               + tab[4352 + c[17]] + tab[5376 + c[21]] + tab[6400 + c[25]] + tab[7424 + c[29]];
        var s2 = tab[512 + c[2]] + tab[1536 + c[6]] + tab[2560 + c[10]] + tab[3584 + c[14]]
               + tab[4608 + c[18]] + tab[5632 + c[22]] + tab[6656 + c[26]] + tab[7680 + c[30]];
        var s3 = tab[768 + c[3]] + tab[1792 + c[7]] + tab[2816 + c[11]] + tab[3840 + c[15]]
               + tab[4864 + c[19]] + tab[5888 + c[23]] + tab[6912 + c[27]] + tab[7936 + c[31]];
        return (s0 + s1) + (s2 + s3);
    }

    // One 32-weight sub-block's integer sum: its eight bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int SubBlockSum(byte* c, short* tab) =>
        (tab[c[0]] + tab[256 + c[1]] + tab[512 + c[2]] + tab[768 + c[3]])
        + (tab[1024 + c[4]] + tab[1280 + c[5]] + tab[1536 + c[6]] + tab[1792 + c[7]]);

    // One pooled product: the rows go out in chunks taken in turn, so a slower core simply takes fewer.
    private sealed unsafe class RowsWork(Pq2Lut lut) : WorkerPool.IWork
    {
        private byte* _matrix;
        private float* _scales;
        private float* _output;
        private int _count;
        private int _chunk;
        private int _next;

        public void Set(byte* matrix, float* scales, float* output, int count, int chunk)
        {
            _matrix = matrix;
            _scales = scales;
            _output = output;
            _count = count;
            _chunk = chunk;
            _next = 0;
        }

        public void Clear()
        {
            _matrix = null;
            _scales = null;
            _output = null;
        }

        public void Execute(int worker, int workers)
        {
            long first;
            while ((first = (long)(Interlocked.Increment(ref _next) - 1) * _chunk) < _count)
                lut.MatVecRows(_matrix, _scales, _output, (int)first, (int)Math.Min(_count, first + _chunk));
        }
    }

    // One pooled table build: the blocks go out one at a time to whichever thread is free.
    private sealed class BuildWork(Pq2Lut lut) : WorkerPool.IWork
    {
        private sbyte[]? _quants;
        private int _next;

        public void Set(sbyte[] quants)
        {
            _quants = quants;
            _next = 0;
        }

        public void Clear() => _quants = null;

        public void Execute(int worker, int workers)
        {
            int b;
            while ((b = Interlocked.Increment(ref _next) - 1) < lut.Blocks)
                lut.BuildBlocks(_quants!, b, b + 1);
        }
    }
}
