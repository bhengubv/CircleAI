// Ptq1Pair.cs
//
// THE TERNARY PRODUCT ON ARM VECTORS, WHERE THE RUNTIME GIVES THEM (CoreCLR; Mono on Android does not).
//
// The lookup-table kernel (Ptq1Lut) runs one load and one add per weight byte, on one lane. Measured on
// the P30's A73 on 2026-11-06 it takes the same 36 ns a block with its data in L1 as in main memory: it
// is never waiting on memory, it simply has too many instructions to run. This one spends about a third
// of that, and got there from three numbers measured on that core:
//
//   a vector instruction   about 0.75 cycles, whichever it is
//   a vector load          about 2.5 cycles, so the loads cost more than the arithmetic if left alone
//   ld1r, a byte broadcast about 3 cycles: eight a block were half of an earlier kernel's whole time
//
// So nothing is broadcast. An activation is spread once per input (Ptq1PairInput), eight copies of a
// column then eight of the next: one 16-byte load holds two columns, smlal reads the low half and
// smlal2 the high. The weights are repacked to match, once, when the model loads, at two bits each:
// eight rows of a column in a vector's low lanes, the same rows of the next column in its high lanes,
// a row to a lane, so a sum never has to be added across lanes.
//
//   a tile is 16 rows; a tile's block is 512 bytes: 16 groups of 8 columns, each two 16-byte vectors,
//   rows 0-7 then 8-15; in a vector a byte's four fields are that group's four column pairs
//
// A MASK ALONE UNPACKS THE TWO MIDDLE FIELDS: they stay at four and sixteen times their value in sums
// of their own and come down once per 32-wide part. Eight columns of at most 32 * 127 fit 16 bits.
// EVERY LOCAL IS ASSIGNED ONCE: the JIT copies a register at GetLower on one assigned twice, and those
// copies were a quarter of an earlier version's instructions.
//
// SEVERAL TOKENS AT ONCE TAKE ANOTHER ROAD. A multiply is the wall here: a vector instruction a cycle,
// and sixteen multiplies for every 128 weights whatever else is done. So a prompt's tokens go in side by
// side, a lane each, and a weight byte does not multiply anything: its four codes are looked up in a table
// built for those four columns, one load and one add for four weights of eight tokens. On the P30 on
// 2026-11-06 a token's products took 46 ms that way against 59 ms alone, and only a full pass pays, since
// a pass costs the same however few lanes it fills. Decoding the weights once for four tokens and
// multiplying was tried first: its sums spilled out of the registers and it gained nothing.
//
// THE SUMS ARE THE REFERENCE'S: each 32-wide part is an exact integer, scaled by its activation scale
// and then the block's, as Ptq1.Dot sums them, so the result differs from it only in the order of the
// float additions. A code is the weight plus one, so a part's sum of quants comes off at the end.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>One activation vector in the form <see cref="Ptq1PairMatrix"/> reads.</summary>
public sealed unsafe class Ptq1PairInput
{
    private readonly ulong[] _spread;
    private readonly float[] _scales;
    private readonly float[] _offsets;

    /// <summary>Room for an input of <paramref name="cols"/> values (a multiple of 128).</summary>
    public Ptq1PairInput(int cols)
    {
        if (cols <= 0 || cols % Ptq1.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PTQ1_0 rows are whole 128-weight blocks.");
        Length = cols;
        _spread = GC.AllocateUninitializedArray<ulong>(cols, pinned: true);
        _scales = GC.AllocateUninitializedArray<float>(cols / Q8Activations.BlockSize, pinned: true);
        _offsets = GC.AllocateUninitializedArray<float>(cols / Ptq1.BlockWeights, pinned: true);
        Spread = (sbyte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_spread));
        Scales = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_scales));
        Offsets = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_offsets));
    }

    /// <summary>Values in the input.</summary>
    public int Length { get; }

    internal sbyte* Spread { get; }

    internal float* Scales { get; }

    internal float* Offsets { get; }

    /// <summary>Takes in one quantised activation.</summary>
    public void Set(Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != Length) throw new ArgumentException($"Expected {Length} activations, got {x.Length}.", nameof(x));

        fixed (sbyte* q = x.Quants)
        fixed (float* d = x.Scales)
        {
            var spread = (ulong*)Spread;
            for (var i = 0; i < Length; i++) spread[i] = (byte)q[i] * 0x0101_0101_0101_0101UL;

            for (var b = 0; b < Length / Ptq1.BlockWeights; b++)
            {
                var offset = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var part = q + b * Ptq1.BlockWeights + k * Q8Activations.BlockSize;
                    var sum = 0;
                    for (var j = 0; j < Q8Activations.BlockSize; j++) sum += part[j];
                    Scales[b * 4 + k] = d[b * 4 + k];
                    offset += d[b * 4 + k] * sum;
                }
                Offsets[b] = offset;
            }
        }
    }
}

/// <summary>
/// Up to <see cref="Lanes"/> activation vectors at once, an input to a lane, for the batched product
/// (<see cref="Ptq1PairMatrix.MatVec(Ptq1PairBatch, int, float[][], WorkerPool?)"/>).
/// </summary>
public sealed unsafe class Ptq1PairBatch
{
    /// <summary>Inputs one pass over the weights takes.</summary>
    public const int Lanes = 8;

    /// <summary>Entries from one group's table to the next: 256 and a few more, so the groups do not fall on the same lines of the cache.</summary>
    internal const int GroupEntries = 256 + 20;

    /// <summary>A 32-wide part's tables: eight groups of four columns, an entry for every byte, in shorts.</summary>
    internal const int TableShorts = 8 * GroupEntries * Lanes;

    /// <summary>Tiles one worker takes at a time: every worker's sums are this long.</summary>
    internal const int MostTiles = 16;

    private const int SumFloats = MostTiles * 16 * Lanes;

    private readonly short[] _columns;
    private readonly float[] _scales;
    private readonly float[] _offsets;
    private readonly short[] _tables;
    private readonly float[] _sums;

    /// <summary>Room for inputs of <paramref name="cols"/> values (a multiple of 128), and for <paramref name="workers"/> threads.</summary>
    public Ptq1PairBatch(int cols, int workers = 1)
    {
        if (cols <= 0 || cols % Ptq1.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PTQ1_0 rows are whole 128-weight blocks.");
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        Length = cols;
        Workers = workers;
        _columns = GC.AllocateUninitializedArray<short>(cols * Lanes, pinned: true);
        _scales = GC.AllocateUninitializedArray<float>(cols / Q8Activations.BlockSize * Lanes, pinned: true);
        _offsets = GC.AllocateUninitializedArray<float>(cols / Ptq1.BlockWeights * Lanes, pinned: true);
        _tables = GC.AllocateUninitializedArray<short>(workers * TableShorts, pinned: true);
        _sums = GC.AllocateUninitializedArray<float>(workers * 2 * SumFloats, pinned: true);
        Columns = (short*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_columns));
        Scales = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_scales));
        Offsets = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_offsets));
    }

    /// <summary>Values in an input.</summary>
    public int Length { get; }

    /// <summary>Threads it has room for.</summary>
    public int Workers { get; }

    internal short* Columns { get; }

    internal float* Scales { get; }

    internal float* Offsets { get; }

    internal Vector128<short>* Tables(int worker) =>
        (Vector128<short>*)Unsafe.AsPointer(ref _tables[worker * TableShorts]);

    internal float* Sums(int worker) => (float*)Unsafe.AsPointer(ref _sums[worker * 2 * SumFloats]);

    internal float* Totals(int worker) => (float*)Unsafe.AsPointer(ref _sums[(worker * 2 + 1) * SumFloats]);

    /// <summary>Takes in one quantised activation as input <paramref name="lane"/>.</summary>
    public void Set(int lane, Q8Activations x)
    {
        ArgumentNullException.ThrowIfNull(x);
        if ((uint)lane >= Lanes) throw new ArgumentOutOfRangeException(nameof(lane));
        if (x.Length != Length) throw new ArgumentException($"Expected {Length} activations, got {x.Length}.", nameof(x));

        fixed (sbyte* q = x.Quants)
        fixed (float* d = x.Scales)
        {
            for (var i = 0; i < Length; i++) Columns[i * Lanes + lane] = q[i];
            for (var b = 0; b < Length / Ptq1.BlockWeights; b++)
            {
                var offset = 0f;
                for (var k = 0; k < 4; k++)
                {
                    var part = q + b * Ptq1.BlockWeights + k * Q8Activations.BlockSize;
                    var sum = 0;
                    for (var j = 0; j < Q8Activations.BlockSize; j++) sum += part[j];
                    Scales[(b * 4 + k) * Lanes + lane] = d[b * 4 + k];
                    offset += d[b * 4 + k] * sum;
                }
                Offsets[b * Lanes + lane] = offset;
            }
        }
    }
}

/// <summary>A PTQ1_0 matrix repacked for ARM vectors; see the file's header for the layout.</summary>
public sealed unsafe class Ptq1PairMatrix
{
    /// <summary>Rows a tile holds: a matrix is a whole number of tiles.</summary>
    public const int TileRows = 16;

    private const int TileBlockBytes = 512;

    // A byte's five codes (a weight plus one), as Ptq1 decodes them, and where a block's column goes
    // in a tile's block: the byte and the shift of its field. A row's lane comes on top of the byte.
    private static readonly byte[] Codes = BuildCodes();
    private static readonly short[] ColumnByte = BuildColumns(col => (short)(col / 8 * 32 + col % 2 * 8));
    private static readonly short[] ColumnShift = BuildColumns(col => (short)(col % 8 / 2 * 2));

    private readonly byte[] _bytes;
    private readonly float[] _blockScales;
    private readonly byte* _weights;
    private readonly float* _scales;
    private readonly int _blocks;
    private readonly int _tiles;
    private readonly RowsWork _rows;
    private readonly BatchWork _batch;

    /// <summary>
    /// Repacks <paramref name="matrix"/>, <paramref name="rows"/> rows of <paramref name="cols"/>
    /// PTQ1_0 values, on <paramref name="pool"/>'s threads when there is one.
    /// </summary>
    public Ptq1PairMatrix(ReadOnlySpan<byte> matrix, int rows, int cols, WorkerPool? pool = null)
    {
        if (cols <= 0 || cols % Ptq1.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PTQ1_0 rows are whole 128-weight blocks.");
        if (rows <= 0 || rows % TileRows != 0)
            throw new ArgumentOutOfRangeException(nameof(rows), rows, $"A matrix is a whole number of {TileRows}-row tiles.");
        _blocks = cols / Ptq1.BlockWeights;
        _tiles = rows / TileRows;
        if (matrix.Length < (long)rows * _blocks * Ptq1.BlockBytes) throw new ArgumentException("The matrix is too short.", nameof(matrix));

        _bytes = GC.AllocateUninitializedArray<byte>(checked(_tiles * _blocks * TileBlockBytes), pinned: true);
        _blockScales = GC.AllocateUninitializedArray<float>(checked(_tiles * _blocks * TileRows), pinned: true);
        _weights = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_bytes));
        _scales = (float*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_blockScales));
        _rows = new RowsWork(this);
        _batch = new BatchWork(this);

        fixed (byte* m = matrix)
        {
            var pack = new PackWork(this, m);
            if (pool is null) pack.Execute(0, 1);
            else pool.Run(pack);
        }
    }

    /// <summary>Whether this process can run the vector kernel, and is allowed to (<see cref="KernelChoice"/>).</summary>
    public static bool Usable => AdvSimd.Arm64.IsSupported && KernelChoice.Allows(KernelPath.AdvSimd);

    /// <summary>Bytes this copy of the matrix holds, its scales included.</summary>
    public long Size => _bytes.LongLength + _blockScales.LongLength * sizeof(float);

    /// <summary>Every row's product with <paramref name="x"/>, one value per row.</summary>
    public void MatVec(Ptq1PairInput x, Span<float> output, WorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(x);
        if (x.Length != _blocks * Ptq1.BlockWeights) throw new ArgumentException($"Expected {_blocks * Ptq1.BlockWeights} activations, got {x.Length}.", nameof(x));
        if (output.Length != _tiles * TileRows) throw new ArgumentException($"Expected {_tiles * TileRows} outputs, got {output.Length}.", nameof(output));

        fixed (float* o = output)
        {
            if (pool is null)
            {
                Tiles(x, o, 0, _tiles);
                return;
            }
            _rows.Set(x, o, Math.Max(1, _tiles / (pool.Threads * 4)));
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

    /// <summary>
    /// Every row's product with the first <paramref name="n"/> inputs of <paramref name="x"/>, in one
    /// pass over the weights: <paramref name="outputs"/>[<paramref name="first"/> + i][row]. Each is its
    /// own <see cref="MatVec(Ptq1PairInput, Span{float}, WorkerPool?)"/> to the last bit: the same integers
    /// and the same fused steps in the same order.
    /// </summary>
    public void MatVec(Ptq1PairBatch x, int n, float[][] outputs, WorkerPool? pool = null, int first = 0)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(outputs);
        if (x.Length != _blocks * Ptq1.BlockWeights) throw new ArgumentException($"Expected {_blocks * Ptq1.BlockWeights} activations, got {x.Length}.", nameof(x));
        if (n < 1 || n > Ptq1PairBatch.Lanes || first + n > outputs.Length) throw new ArgumentOutOfRangeException(nameof(n));
        var threads = pool?.Threads ?? 1;
        if (threads > x.Workers) throw new ArgumentException($"The batch has room for {x.Workers} threads, not {threads}.", nameof(x));
        for (var i = first; i < first + n; i++)
            if (outputs[i].Length != _tiles * TileRows) throw new ArgumentException($"Expected {_tiles * TileRows} outputs, got {outputs[i].Length}.", nameof(outputs));

        _batch.Set(x, n, outputs, first, Math.Clamp(_tiles / threads, 1, Ptq1PairBatch.MostTiles));
        try
        {
            if (pool is null) _batch.Execute(0, 1);
            else pool.Run(_batch);
        }
        finally
        {
            _batch.Clear();
        }
    }

    private void Tiles(Ptq1PairInput x, float* output, int first, int end)
    {
        if (AdvSimd.Arm64.IsSupported) Vectors(x.Spread, x.Scales, x.Offsets, output, first, end);
        else Scalar(x, output, first, end);
    }

    // One piece of a pooled product: tiles go out in chunks taken in turn.
    private sealed class RowsWork(Ptq1PairMatrix matrix) : WorkerPool.IWork
    {
        private Ptq1PairInput? _x;
        private float* _output;
        private int _chunk;
        private int _next;

        public void Set(Ptq1PairInput x, float* output, int chunk)
        {
            _x = x;
            _output = output;
            _chunk = chunk;
            _next = 0;
        }

        public void Clear()
        {
            _x = null;
            _output = null;
        }

        public void Execute(int worker, int workers)
        {
            long first;
            while ((first = (long)(Interlocked.Increment(ref _next) - 1) * _chunk) < matrix._tiles)
                matrix.Tiles(_x!, _output, (int)first, (int)Math.Min(matrix._tiles, first + _chunk));
        }
    }

    private sealed class BatchWork(Ptq1PairMatrix matrix) : WorkerPool.IWork
    {
        private Ptq1PairBatch? _x;
        private float[][]? _outputs;
        private int _n;
        private int _firstOutput;
        private int _chunk;
        private int _next;

        public void Set(Ptq1PairBatch x, int n, float[][] outputs, int firstOutput, int chunk)
        {
            _x = x;
            _n = n;
            _outputs = outputs;
            _firstOutput = firstOutput;
            _chunk = chunk;
            _next = 0;
        }

        public void Clear()
        {
            _x = null;
            _outputs = null;
        }

        public void Execute(int worker, int workers)
        {
            long first;
            while ((first = (long)(Interlocked.Increment(ref _next) - 1) * _chunk) < matrix._tiles)
                matrix.Lanes(_x!, worker, _n, _outputs!, _firstOutput, (int)first, (int)Math.Min(matrix._tiles, first + _chunk));
        }
    }

    private sealed class PackWork(Ptq1PairMatrix matrix, byte* source) : WorkerPool.IWork
    {
        private int _next;

        public void Execute(int worker, int workers)
        {
            int tile;
            while ((tile = Interlocked.Increment(ref _next) - 1) < matrix._tiles) matrix.Pack(source, tile);
        }
    }

    // ---- the repack ----

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Pack(byte* source, int tile)
    {
        var codes = stackalloc byte[Ptq1.BlockWeights];
        var rowBytes = (long)_blocks * Ptq1.BlockBytes;
        fixed (byte* table = Codes)
        fixed (short* column = ColumnByte)
        fixed (short* shift = ColumnShift)
        {
            for (var b = 0; b < _blocks; b++)
            {
                var dst = _weights + ((long)tile * _blocks + b) * TileBlockBytes;
                new Span<byte>(dst, TileBlockBytes).Clear();
                for (var lane = 0; lane < TileRows; lane++)
                {
                    var block = source + (tile * TileRows + lane) * rowBytes + b * Ptq1.BlockBytes;
                    Unpack(block, table, codes);
                    var row = dst + lane / 8 * 16 + lane % 8;
                    for (var col = 0; col < Ptq1.BlockWeights; col++) row[column[col]] |= (byte)(codes[col] << shift[col]);
                    _scales[((long)tile * _blocks + b) * TileRows + lane] = Ptq1.Scale(new ReadOnlySpan<byte>(block, Ptq1.BlockBytes));
                }
            }
        }
    }

    // A block's 128 codes in weight order.
    private static void Unpack(byte* block, byte* table, byte* codes)
    {
        for (var m = 0; m < 16; m++)
        {
            var c = table + block[m] * 5;
            for (var n = 0; n < 5; n++) codes[n * 16 + m] = c[n];
        }
        for (var m = 0; m < 8; m++)
        {
            var c = table + block[16 + m] * 5;
            for (var n = 0; n < 5; n++) codes[80 + n * 8 + m] = c[n];
        }
        for (var h = 0; h < 2; h++)
        {
            var c = table + block[24 + h] * 5;
            for (var n = 0; n < 4; n++) codes[120 + n * 2 + h] = c[n];
        }
    }

    private static byte[] BuildCodes()
    {
        var codes = new byte[256 * 5];
        var probe = new byte[Ptq1.BlockBytes];
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var v = 0; v < 256; v++)
        {
            // Read off the scalar decoder, so the codes follow the rule that decodes the model.
            probe[0] = (byte)v;
            Ptq1.DecodeScalar(probe, trits);
            for (var n = 0; n < 5; n++) codes[v * 5 + n] = (byte)(trits[n * 16] + 1);
        }
        return codes;
    }

    private static short[] BuildColumns(Func<int, short> place)
    {
        var columns = new short[Ptq1.BlockWeights];
        for (var col = 0; col < columns.Length; col++) columns[col] = place(col);
        return columns;
    }

    // ---- the products ----

    // The layout read back one weight at a time: what the vectors do, on any processor.
    private void Scalar(Ptq1PairInput x, float* output, int first, int end)
    {
        var spread = (ulong*)x.Spread;
        for (var tile = first; tile < end; tile++)
        {
            for (var lane = 0; lane < TileRows; lane++)
            {
                var sum = 0f;
                for (var b = 0; b < _blocks; b++)
                {
                    var src = _weights + ((long)tile * _blocks + b) * TileBlockBytes + lane / 8 * 16 + lane % 8;
                    var parts = 0f;
                    for (var k = 0; k < 4; k++)
                    {
                        var part = 0;
                        for (var col = k * 32; col < k * 32 + 32; col++)
                        {
                            var code = (src[ColumnByte[col]] >> ColumnShift[col]) & 3;
                            part += code * (sbyte)spread[b * Ptq1.BlockWeights + col];
                        }
                        parts = MathF.FusedMultiplyAdd(part, x.Scales[b * 4 + k], parts);
                    }
                    sum = MathF.FusedMultiplyAdd(parts - x.Offsets[b], _scales[((long)tile * _blocks + b) * TileRows + lane], sum);
                }
                output[tile * TileRows + lane] = sum;
            }
        }
    }

    // ---- several inputs at once ----

    // Tiles [first, end) for up to eight inputs, a lane each: part by part, each part's tables built here
    // and read by every row of these tiles.
    private void Lanes(Ptq1PairBatch x, int worker, int n, float[][] outputs, int firstOutput, int first, int end)
    {
        const int lanes = Ptq1PairBatch.Lanes;
        var tables = x.Tables(worker);
        var sums = x.Sums(worker);
        var totals = x.Totals(worker);
        var tiles = end - first;
        var rows = tiles * TileRows;
        new Span<float>(totals, rows * lanes).Clear();
        var stride = (long)_blocks * TileBlockBytes;
        for (var b = 0; b < _blocks; b++)
        {
            var at = _weights + (long)first * stride + b * TileBlockBytes;
            var dw = _scales + ((long)first * _blocks + b) * TileRows;
            for (var s = 0; s < 4; s++, at += TileBlockBytes / 4)
            {
                var part = b * 4 + s;
                BuildTables(x.Columns + (long)part * Q8Activations.BlockSize * lanes, tables);
                PartRows(at, stride, tiles, tables, x.Scales + part * lanes, sums, s, totals, dw, (long)_blocks * TileRows, x.Offsets + b * lanes);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var output = outputs[firstOutput + i];
            var row = first * TileRows;
            for (var r = 0; r < rows; r++) output[row + r] = totals[r * lanes + i];
        }
    }

    // A part's eight groups of four columns: a group's byte holds the codes of columns two apart, and
    // its table holds, for every byte a weight can be, what those four codes come to for each input.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BuildTables(short* columns, Vector128<short>* tables)
    {
        const int lanes = Ptq1PairBatch.Lanes;
        for (var group = 0; group < 8; group++, tables += Ptq1PairBatch.GroupEntries)
        {
            var first = columns + ((group >> 1) * 8 + (group & 1)) * lanes;
            var x0 = Vector128.Load(first);
            var x1 = Vector128.Load(first + 2 * lanes);
            var x2 = Vector128.Load(first + 4 * lanes);
            var x3 = Vector128.Load(first + 6 * lanes);
            var twice = x0 + x0;
            var c3 = Vector128<short>.Zero;
            for (var i3 = 0; i3 < 3; i3++, c3 += x3)
            {
                var c2 = c3;
                for (var i2 = 0; i2 < 3; i2++, c2 += x2)
                {
                    var c1 = c2;
                    var entry = tables + i3 * 64 + i2 * 16;
                    for (var i1 = 0; i1 < 3; i1++, c1 += x1, entry += 4)
                    {
                        entry[0] = c1;
                        entry[1] = c1 + x0;
                        entry[2] = c1 + twice;
                    }
                }
            }
        }
    }

    // One part of every row of some tiles, eight rows at a time: a row's eight bytes each pick their
    // entry, a short to an input, and the sum is exact. Then it is scaled as the reference scales it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void PartRows(byte* weights, long stride, int tiles, Vector128<short>* tables, float* scales, float* sums,
        int part, float* totals, float* dw, long dwStride, float* offsets)
    {
        var low = Vector128.Load(scales);
        var high = Vector128.Load(scales + 4);
        var offsetLow = Vector128.Load(offsets);
        var offsetHigh = Vector128.Load(offsets + 4);
        for (var tile = 0; tile < tiles; tile++, weights += stride, dw += dwStride)
        {
            for (var half = 0; half < 2; half++, sums += 64, totals += 64)
            {
                var p = weights + half * 16;
                var t = (byte*)tables;
                Vector128<short> r0 = default, r1 = default, r2 = default, r3 = default, r4 = default, r5 = default, r6 = default, r7 = default;
                for (var g = 0; g < 4; g++, p += 32, t += 2 * Ptq1PairBatch.GroupEntries * 16)
                {
                    var w = *(ulong*)p;
                    r0 += *(Vector128<short>*)(t + ((w << 4) & 0xFF0));
                    r1 += *(Vector128<short>*)(t + ((w >> 4) & 0xFF0));
                    r2 += *(Vector128<short>*)(t + ((w >> 12) & 0xFF0));
                    r3 += *(Vector128<short>*)(t + ((w >> 20) & 0xFF0));
                    r4 += *(Vector128<short>*)(t + ((w >> 28) & 0xFF0));
                    r5 += *(Vector128<short>*)(t + ((w >> 36) & 0xFF0));
                    r6 += *(Vector128<short>*)(t + ((w >> 44) & 0xFF0));
                    r7 += *(Vector128<short>*)(t + ((w >> 52) & 0xFF0));
                    var u = t + Ptq1PairBatch.GroupEntries * 16;
                    var v = *(ulong*)(p + 8);
                    r0 += *(Vector128<short>*)(u + ((v << 4) & 0xFF0));
                    r1 += *(Vector128<short>*)(u + ((v >> 4) & 0xFF0));
                    r2 += *(Vector128<short>*)(u + ((v >> 12) & 0xFF0));
                    r3 += *(Vector128<short>*)(u + ((v >> 20) & 0xFF0));
                    r4 += *(Vector128<short>*)(u + ((v >> 28) & 0xFF0));
                    r5 += *(Vector128<short>*)(u + ((v >> 36) & 0xFF0));
                    r6 += *(Vector128<short>*)(u + ((v >> 44) & 0xFF0));
                    r7 += *(Vector128<short>*)(u + ((v >> 52) & 0xFF0));
                }

                var d = dw + half * 8;
                if (part == 0)
                {
                    Begun(sums, r0, low, high);
                    Begun(sums + 8, r1, low, high);
                    Begun(sums + 16, r2, low, high);
                    Begun(sums + 24, r3, low, high);
                    Begun(sums + 32, r4, low, high);
                    Begun(sums + 40, r5, low, high);
                    Begun(sums + 48, r6, low, high);
                    Begun(sums + 56, r7, low, high);
                }
                else if (part < 3)
                {
                    Added(sums, r0, low, high);
                    Added(sums + 8, r1, low, high);
                    Added(sums + 16, r2, low, high);
                    Added(sums + 24, r3, low, high);
                    Added(sums + 32, r4, low, high);
                    Added(sums + 40, r5, low, high);
                    Added(sums + 48, r6, low, high);
                    Added(sums + 56, r7, low, high);
                }
                else
                {
                    Ended(sums, totals, r0, low, high, offsetLow, offsetHigh, d[0]);
                    Ended(sums + 8, totals + 8, r1, low, high, offsetLow, offsetHigh, d[1]);
                    Ended(sums + 16, totals + 16, r2, low, high, offsetLow, offsetHigh, d[2]);
                    Ended(sums + 24, totals + 24, r3, low, high, offsetLow, offsetHigh, d[3]);
                    Ended(sums + 32, totals + 32, r4, low, high, offsetLow, offsetHigh, d[4]);
                    Ended(sums + 40, totals + 40, r5, low, high, offsetLow, offsetHigh, d[5]);
                    Ended(sums + 48, totals + 48, r6, low, high, offsetLow, offsetHigh, d[6]);
                    Ended(sums + 56, totals + 56, r7, low, high, offsetLow, offsetHigh, d[7]);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Begun(float* sum, Vector128<short> part, Vector128<float> low, Vector128<float> high)
    {
        Vector128.Store(Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenLower(part)), low, Vector128<float>.Zero), sum);
        Vector128.Store(Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenUpper(part)), high, Vector128<float>.Zero), sum + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Added(float* sum, Vector128<short> part, Vector128<float> low, Vector128<float> high)
    {
        Vector128.Store(Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenLower(part)), low, Vector128.Load(sum)), sum);
        Vector128.Store(Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenUpper(part)), high, Vector128.Load(sum + 4)), sum + 4);
    }

    // The block's last part: its sum goes into the row's total under the block's weight scale.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ended(float* sum, float* total, Vector128<short> part, Vector128<float> low, Vector128<float> high,
        Vector128<float> offsetLow, Vector128<float> offsetHigh, float scale)
    {
        var w = Vector128.Create(scale);
        var a = Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenLower(part)), low, Vector128.Load(sum));
        var b = Vector128.FusedMultiplyAdd(Vector128.ConvertToSingle(Vector128.WidenUpper(part)), high, Vector128.Load(sum + 4));
        Vector128.Store(Vector128.FusedMultiplyAdd(a - offsetLow, w, Vector128.Load(total)), total);
        Vector128.Store(Vector128.FusedMultiplyAdd(b - offsetHigh, w, Vector128.Load(total + 4)), total + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Vectors(sbyte* table, float* dx, float* offsets, float* output, int first, int end)
    {
        var mask = Vector128.Create((byte)3);
        var second = Vector128.Create((byte)0x0C);
        var third = Vector128.Create((byte)0x30);
        var blocks = _blocks;
        var p = _weights + (long)first * blocks * TileBlockBytes;
        var dw = _scales + (long)first * blocks * TileRows;
        output += first * TileRows;
        for (var t = first; t < end; t++, output += TileRows)
        {
            Vector128<float> f0 = default, f1 = default, f2 = default, f3 = default;
            var e = table;
            var db = dx;
            for (var b = 0; b < blocks; b++, dw += TileRows)
            {
                Vector128<float> g0 = default, g1 = default, g2 = default, g3 = default;
                for (var s = 0; s < 4; s++, db++)
                {
                    Vector128<short> a0 = default, a1 = default, a2 = default, a3 = default, b0 = default, b1 = default, b2 = default, b3 = default;
                    for (var g = 0; g < 4; g++, p += 32, e += 64)
                    {
                        var va = AdvSimd.LoadVector128(p);
                        var vb = AdvSimd.LoadVector128(p + 16);
                        var x0 = AdvSimd.LoadVector128(e);
                        var x1 = AdvSimd.LoadVector128(e + 16);
                        var x2 = AdvSimd.LoadVector128(e + 32);
                        var x3 = AdvSimd.LoadVector128(e + 48);
                        var c0 = AdvSimd.And(va, mask).AsSByte();
                        var d0 = AdvSimd.And(vb, mask).AsSByte();
                        var c1 = AdvSimd.And(va, second).AsSByte();
                        var d1 = AdvSimd.And(vb, second).AsSByte();
                        var c2 = AdvSimd.And(va, third).AsSByte();
                        var d2 = AdvSimd.And(vb, third).AsSByte();
                        var c3 = AdvSimd.ShiftRightLogical(va, 6).AsSByte();
                        var d3 = AdvSimd.ShiftRightLogical(vb, 6).AsSByte();

                        a0 = AdvSimd.MultiplyWideningUpperAndAdd(a0, c0, x0);
                        b0 = AdvSimd.MultiplyWideningUpperAndAdd(b0, d0, x0);
                        a1 = AdvSimd.MultiplyWideningUpperAndAdd(a1, c1, x1);
                        b1 = AdvSimd.MultiplyWideningUpperAndAdd(b1, d1, x1);
                        a2 = AdvSimd.MultiplyWideningUpperAndAdd(a2, c2, x2);
                        b2 = AdvSimd.MultiplyWideningUpperAndAdd(b2, d2, x2);
                        a3 = AdvSimd.MultiplyWideningUpperAndAdd(a3, c3, x3);
                        b3 = AdvSimd.MultiplyWideningUpperAndAdd(b3, d3, x3);

                        a0 = AdvSimd.MultiplyWideningLowerAndAdd(a0, c0.GetLower(), AdvSimd.LoadVector64(e));
                        b0 = AdvSimd.MultiplyWideningLowerAndAdd(b0, d0.GetLower(), AdvSimd.LoadVector64(e));
                        a1 = AdvSimd.MultiplyWideningLowerAndAdd(a1, c1.GetLower(), AdvSimd.LoadVector64(e + 16));
                        b1 = AdvSimd.MultiplyWideningLowerAndAdd(b1, d1.GetLower(), AdvSimd.LoadVector64(e + 16));
                        a2 = AdvSimd.MultiplyWideningLowerAndAdd(a2, c2.GetLower(), AdvSimd.LoadVector64(e + 32));
                        b2 = AdvSimd.MultiplyWideningLowerAndAdd(b2, d2.GetLower(), AdvSimd.LoadVector64(e + 32));
                        a3 = AdvSimd.MultiplyWideningLowerAndAdd(a3, c3.GetLower(), AdvSimd.LoadVector64(e + 48));
                        b3 = AdvSimd.MultiplyWideningLowerAndAdd(b3, d3.GetLower(), AdvSimd.LoadVector64(e + 48));
                    }
                    var lo = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(a0, a3), a1, 2), a2, 4);
                    var hi = AdvSimd.ShiftRightArithmeticAdd(AdvSimd.ShiftRightArithmeticAdd(AdvSimd.Add(b0, b3), b1, 2), b2, 4);
                    var d = AdvSimd.LoadAndReplicateToVector128(db);
                    g0 = AdvSimd.FusedMultiplyAdd(g0, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(lo)), d);
                    g1 = AdvSimd.FusedMultiplyAdd(g1, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(lo.GetLower())), d);
                    g2 = AdvSimd.FusedMultiplyAdd(g2, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningUpper(hi)), d);
                    g3 = AdvSimd.FusedMultiplyAdd(g3, AdvSimd.ConvertToSingle(AdvSimd.SignExtendWideningLower(hi.GetLower())), d);
                }
                var offset = AdvSimd.LoadAndReplicateToVector128(offsets + b);
                f0 = AdvSimd.FusedMultiplyAdd(f0, AdvSimd.Subtract(g0, offset), AdvSimd.LoadVector128(dw + 4));
                f1 = AdvSimd.FusedMultiplyAdd(f1, AdvSimd.Subtract(g1, offset), AdvSimd.LoadVector128(dw));
                f2 = AdvSimd.FusedMultiplyAdd(f2, AdvSimd.Subtract(g2, offset), AdvSimd.LoadVector128(dw + 12));
                f3 = AdvSimd.FusedMultiplyAdd(f3, AdvSimd.Subtract(g3, offset), AdvSimd.LoadVector128(dw + 8));
            }
            AdvSimd.Store(output, f1);
            AdvSimd.Store(output + 4, f0);
            AdvSimd.Store(output + 8, f3);
            AdvSimd.Store(output + 12, f2);
        }
    }
}
