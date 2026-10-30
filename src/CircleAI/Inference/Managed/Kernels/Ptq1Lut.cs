// Ptq1Lut.cs
//
// The lookup-table form of the PTQ1_0 product - the idea bitnet.cpp and T-MAC use for
// ternary weights on CPUs without fast vectors, which is what Mono on Android gives us
// (S0.2: no ARM intrinsics on the P30).
//
// Every row of a matrix is multiplied by the SAME input vector, and every PTQ1_0 byte
// holds five (or four) weights at fixed positions in its block. So for each byte
// position the sum (w0*x0 + ... + w4*x4) over all 256 possible byte values can be
// computed ONCE per input vector, and each row then pays one table load and one add per
// byte instead of five multiply-adds:
//
//   positions 0..15   bytes qs[m]     weights n*16 + m        (n = 0..4)
//   positions 16..23  bytes qs[16+m]  weights 80 + n*8 + m    (n = 0..4)
//   positions 24..25  bytes qh[h]     weights 120 + n*2 + h   (n = 0..3)
//
// One block's 26 tables take 26 KB, so a 2048-wide input needs 416 KB of them. MatVec
// therefore walks the matrix block by block across every row, keeping one block's tables
// in the L1 cache at a time; walking row by row sweeps all 416 KB for every row.
//
// The table is built from the activations as the reference quantised them (Q8_0
// values times their block scale), so its sums are the reference's sums up to float
// rounding order - not bit-exact, but well inside the drift the reference itself shows
// between thread counts (S0.1).

using System.Runtime.CompilerServices;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Per-input-vector lookup tables for PTQ1_0 products.</summary>
public sealed class Ptq1Lut
{
    /// <summary>Byte positions per block: 24 qs bytes and 2 qh bytes.</summary>
    public const int PositionsPerBlock = 26;

    private const int BlockTableFloats = PositionsPerBlock * 256;

    // Rows per chunk handed to a thread, at least: below this, warming each block's tables
    // into the cache costs a large share of the chunk's own work.
    private const int MinChunkRows = 128;

    private readonly float[] _table;
    private readonly float[] _y;
    private readonly float[] _sums;
    private readonly RowsWork _rows;

    /// <summary>Tables for an input of <paramref name="cols"/> values (a multiple of 128).</summary>
    public Ptq1Lut(int cols)
    {
        if (cols <= 0 || cols % Ptq1.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PTQ1_0 rows are whole 128-weight blocks.");
        Blocks = cols / Ptq1.BlockWeights;
        _table = new float[Blocks * BlockTableFloats];
        _y = new float[Ptq1.BlockWeights];
        _sums = new float[243];
        _rows = new RowsWork(this);
    }

    /// <summary>Blocks per row.</summary>
    public int Blocks { get; }

    /// <summary>Builds every position's table for one input vector.</summary>
    public unsafe void Build(Q8Activations x)
    {
        if (x.Length != Blocks * Ptq1.BlockWeights)
            throw new ArgumentException($"Expected {Blocks * Ptq1.BlockWeights} activations, got {x.Length}.", nameof(x));

        fixed (float* table = _table)
        fixed (float* y = _y)
        fixed (float* sums = _sums)
        fixed (sbyte* q = x.Quants)
        fixed (float* d = x.Scales)
        fixed (byte* q5 = QOf.Five)
        fixed (byte* q4 = QOf.Four)
        {
            for (var b = 0; b < Blocks; b++)
            {
                // The block's activations as the reference sees them: value times its scale.
                for (var k = 0; k < Ptq1.BlockWeights; k++)
                    y[k] = q[b * Ptq1.BlockWeights + k] * d[b * 4 + k / 32];

                var dst = table + b * BlockTableFloats;
                for (var m = 0; m < 16; m++, dst += 256)
                    Fill5(dst, sums, q5, y[m], y[16 + m], y[32 + m], y[48 + m], y[64 + m]);
                for (var m = 0; m < 8; m++, dst += 256)
                    Fill5(dst, sums, q5, y[80 + m], y[88 + m], y[96 + m], y[104 + m], y[112 + m]);
                for (var h = 0; h < 2; h++, dst += 256)
                    Fill4(dst, sums, q4, y[120 + h], y[122 + h], y[124 + h], y[126 + h]);
            }
        }
    }

    /// <summary>One row's product with the input the tables were built for.</summary>
    public unsafe float Dot(ReadOnlySpan<byte> row, ReadOnlySpan<float> blockScales)
    {
        if (row.Length < Blocks * Ptq1.BlockBytes || blockScales.Length < Blocks)
            throw new ArgumentException("Row or scales too short for the tables.");

        var sumf = 0f;
        fixed (byte* r = row)
        fixed (float* dw = blockScales)
        fixed (float* table = _table)
        {
            for (var b = 0; b < Blocks; b++)
                sumf += dw[b] * BlockSum(r + b * Ptq1.BlockBytes, table + b * BlockTableFloats);
        }
        return sumf;
    }

    /// <summary>
    /// Every row's product with the input the tables were built for, one value per row of
    /// <paramref name="output"/>; <paramref name="blockScales"/> holds the matrix's block
    /// scales (<see cref="Ptq1.BlockScales"/>). Equal to <see cref="Dot"/> on each row to
    /// the last bit: the same block sums, added in the same order.
    /// </summary>
    public unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, Span<float> output)
    {
        CheckMatrix(matrix, blockScales, output.Length);
        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        fixed (float* o = output)
            MatVecRows(m, dw, o, 0, output.Length);
    }

    /// <summary>
    /// The same product shared across <paramref name="pool"/>'s threads. Each row is still
    /// summed by one thread in the same order, so the result is the single-thread result to
    /// the last bit, whatever the number of threads. One product at a time per instance.
    /// </summary>
    public unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, Span<float> output, WorkerPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        CheckMatrix(matrix, blockScales, output.Length);
        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        fixed (float* o = output)
        {
            _rows.Set(m, dw, o, output.Length, Math.Max(MinChunkRows, output.Length / (pool.Threads * 4)));
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
        if (matrix.Length < (long)rows * Blocks * Ptq1.BlockBytes || blockScales.Length < (long)rows * Blocks)
            throw new ArgumentException("Matrix or scales too short for the output.");
    }

    // Rows [first, end) of the product, block by block across those rows so one block's
    // tables stay in the L1 cache while every row uses them.
    private unsafe void MatVecRows(byte* matrix, float* blockScales, float* output, int first, int end)
    {
        var blocks = Blocks;
        var rowBytes = blocks * Ptq1.BlockBytes;
        for (var r = first; r < end; r++) output[r] = 0f;
        fixed (float* table = _table)
        {
            for (var b = 0; b < blocks; b++)
            {
                var tab = table + b * BlockTableFloats;
                var blk = matrix + (long)first * rowBytes + b * Ptq1.BlockBytes;
                var scale = blockScales + (long)first * blocks + b;
                for (var r = first; r < end; r++, blk += rowBytes, scale += blocks)
                    output[r] += *scale * BlockSum(blk, tab);
            }
        }
    }

    // One pooled product: the rows go out in chunks taken in turn, so a slower core (a
    // phone's little cores) simply takes fewer of them.
    private sealed unsafe class RowsWork(Ptq1Lut lut) : WorkerPool.IWork
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

    // One block's sum: one lookup per byte position, in four running sums so no add waits
    // on the one before it. Written out in full because Mono does not unroll loops.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float BlockSum(byte* blk, float* tab)
    {
        var s0 = tab[blk[0]] + tab[1024 + blk[4]] + tab[2048 + blk[8]] + tab[3072 + blk[12]]
               + tab[4096 + blk[16]] + tab[5120 + blk[20]] + tab[6144 + blk[24]];
        var s1 = tab[256 + blk[1]] + tab[1280 + blk[5]] + tab[2304 + blk[9]] + tab[3328 + blk[13]]
               + tab[4352 + blk[17]] + tab[5376 + blk[21]] + tab[6400 + blk[25]];
        var s2 = tab[512 + blk[2]] + tab[1536 + blk[6]] + tab[2560 + blk[10]] + tab[3584 + blk[14]]
               + tab[4608 + blk[18]] + tab[5632 + blk[22]];
        var s3 = tab[768 + blk[3]] + tab[1792 + blk[7]] + tab[2816 + blk[11]] + tab[3840 + blk[15]]
               + tab[4864 + blk[19]] + tab[5888 + blk[23]];
        return (s0 + s1) + (s2 + s3);
    }

    // Every signed sum of five activations, numbered as the packer numbers a byte's trits
    // (digit = trit + 1, first trit most significant: q = d0*81 + d1*27 + d2*9 + d3*3 + d4),
    // built by running additions rather than 256 separate five-term sums, then spread over
    // the 256 byte values through each byte's q.
    private static unsafe void Fill5(float* dst, float* sums, byte* qOf, float y0, float y1, float y2, float y3, float y4)
    {
        var s = sums;
        for (var t0 = -1; t0 <= 1; t0++)
        {
            var a0 = t0 * y0;
            for (var t1 = -1; t1 <= 1; t1++)
            {
                var a1 = a0 + t1 * y1;
                for (var t2 = -1; t2 <= 1; t2++)
                {
                    var a2 = a1 + t2 * y2;
                    for (var t3 = -1; t3 <= 1; t3++)
                    {
                        var a3 = a2 + t3 * y3;
                        *s++ = a3 - y4;
                        *s++ = a3;
                        *s++ = a3 + y4;
                    }
                }
            }
        }
        for (var v = 0; v < 256; v++) dst[v] = sums[qOf[v]];
    }

    // The same for a qh byte's four weights; its fifth trit is padding.
    private static unsafe void Fill4(float* dst, float* sums, byte* qOf, float y0, float y1, float y2, float y3)
    {
        var s = sums;
        for (var t0 = -1; t0 <= 1; t0++)
        {
            var a0 = t0 * y0;
            for (var t1 = -1; t1 <= 1; t1++)
            {
                var a1 = a0 + t1 * y1;
                for (var t2 = -1; t2 <= 1; t2++)
                {
                    var a2 = a1 + t2 * y2;
                    *s++ = a2 - y3;
                    *s++ = a2;
                    *s++ = a2 + y3;
                }
            }
        }
        for (var v = 0; v < 256; v++) dst[v] = sums[qOf[v]];
    }

    // Each byte value's q over its first five trits and over its first four, read off the
    // scalar decoder so the tables follow the rule that decodes the model.
    private static readonly (byte[] Five, byte[] Four) QOf = BuildQ();

    private static (byte[] Five, byte[] Four) BuildQ()
    {
        var five = new byte[256];
        var four = new byte[256];
        var probe = new byte[Ptq1.BlockBytes];
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var v = 0; v < 256; v++)
        {
            // Decode a block whose first qs byte is v; weights n*16 hold its five trits.
            probe[0] = (byte)v;
            Ptq1.DecodeScalar(probe, trits);
            var q = 0;
            for (var n = 0; n < 4; n++) q = q * 3 + trits[n * 16] + 1;
            four[v] = (byte)q;
            five[v] = (byte)(q * 3 + trits[64] + 1);
        }
        return (five, four);
    }
}
