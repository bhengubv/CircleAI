// Pq2BatchLut.cs
//
// PQ2_0 products for SEVERAL inputs at once - the verification pass of speculative decoding, where
// the trunk runs the last token and the drafts behind it together. Pq2Lut's tables, one per input,
// interleaved: the entry for (block, byte position, byte value) holds every input's integer sum side
// by side, so the code byte a row reads is looked up once and its few neighbouring shorts serve
// every input - the weights are read once for all of them, and so are the table's cache lines.
//
// THE SAME SUMS AS Pq2Lut, IN THE SAME ORDER. Each input's table entries are Pq2Lut's integers; a
// row's block sum for one input is the same exact integer; the float steps are the reference's,
// block after block. So each input's product equals its own Pq2Lut product to the last bit, which
// is what keeps speculative decoding lossless: the verified logits are the plain logits.

using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>Interleaved PQ2_0 lookup tables for up to <see cref="Inputs"/> input vectors at once.</summary>
public sealed class Pq2BatchLut
{
    private const int Positions = Pq2Lut.PositionsPerBlock;
    private const int MinChunkRows = 64;
    private const int MaxChunkBytes = 256 * 1024;

    private readonly short[] _table;   // ((block * 32 + position) * 256 + byte) * Inputs + input
    private readonly float[] _scales;  // per input: Q8_K's per block, or Q8_0's per sub-block
    private readonly Q8KActivations[]? _q8k;
    private readonly Q8Activations[]? _q8;
    private readonly BuildWork _build;
    private readonly RowsWork _rows;
    private int _built;

    /// <summary>Tables for <paramref name="inputs"/> inputs of <paramref name="cols"/> values (a multiple of 128).</summary>
    public Pq2BatchLut(int cols, int inputs)
    {
        if (cols <= 0 || cols % Pq2.BlockWeights != 0)
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "PQ2_0 rows are whole 128-weight blocks.");
        ArgumentOutOfRangeException.ThrowIfLessThan(inputs, 1);
        Blocks = cols / Pq2.BlockWeights;
        Inputs = inputs;
        PerSubBlock = cols % Q8KActivations.BlockSize != 0; // the fork's Q8_0 fallback (ggml_cpu_pq2_needs_q8_0)
        _table = new short[(long)Blocks * Positions * 256 * inputs is var n && n <= int.MaxValue ? (int)n : throw new ArgumentOutOfRangeException(nameof(inputs))];
        _scales = new float[inputs * Blocks * 4];
        if (PerSubBlock)
        {
            _q8 = new Q8Activations[inputs];
            for (var i = 0; i < inputs; i++) _q8[i] = new Q8Activations(cols);
        }
        else
        {
            _q8k = new Q8KActivations[inputs];
            for (var i = 0; i < inputs; i++) _q8k[i] = new Q8KActivations(cols);
        }
        _build = new BuildWork(this);
        _rows = new RowsWork(this);
    }

    /// <summary>Blocks per row.</summary>
    public int Blocks { get; }

    /// <summary>The most inputs these tables hold.</summary>
    public int Inputs { get; }

    /// <summary>Whether the activations are Q8_0 (the width is not whole Q8_K blocks) rather than Q8_K.</summary>
    public bool PerSubBlock { get; }

    /// <summary>Quantises input <paramref name="input"/> as the single product would (Q8_K, or Q8_0 at this width).</summary>
    public void Quantize(int input, ReadOnlySpan<float> x)
    {
        if ((uint)input >= (uint)Inputs) throw new ArgumentOutOfRangeException(nameof(input));
        if (_q8 is not null)
        {
            _q8[input].Quantize(x);
            _q8[input].Scales.AsSpan(0, Blocks * 4).CopyTo(_scales.AsSpan(input * Blocks * 4));
        }
        else
        {
            _q8k![input].Quantize(x);
            for (var b = 0; b < Blocks; b++) _scales[input * Blocks * 4 + b] = _q8k[input].Scales[b >> 1];
        }
    }

    /// <summary>Builds the tables of the first <paramref name="n"/> inputs, on the pool's threads when one is given.</summary>
    public void Build(int n, WorkerPool? pool = null)
    {
        if (n < 1 || n > Inputs) throw new ArgumentOutOfRangeException(nameof(n));
        _built = n;
        if (pool is null)
        {
            BuildBlocks(0, Blocks, n);
            return;
        }
        _build.Set(n);
        pool.Run(_build);
    }

    private sbyte[] QuantsOf(int input) => _q8 is not null ? _q8[input].Quants : _q8k![input].Quants;

    private unsafe void BuildBlocks(int first, int end, int n)
    {
        fixed (short* table = _table)
        {
            for (var i = 0; i < n; i++)
            {
                fixed (sbyte* q = QuantsOf(i))
                {
                    for (var b = first; b < end; b++)
                    {
                        var y = q + b * Pq2.BlockWeights;
                        var dst = table + (long)b * Positions * 256 * Inputs + i;
                        for (var p = 0; p < Positions; p++, y += 4, dst += 256 * Inputs)
                            Fill(dst, Inputs, y[0], y[1], y[2], y[3]);
                    }
                }
            }
        }
    }

    // Pq2Lut.Fill, writing every Inputs-th short: each byte value's integer sum over its four weights.
    private static unsafe void Fill(short* dst, int stride, int y0, int y1, int y2, int y3)
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
                    *dst = (short)(a1 - y0); dst += stride;
                    *dst = (short)a1; dst += stride;
                    *dst = (short)(a1 + y0); dst += stride;
                    *dst = (short)(a1 + 2 * y0); dst += stride;
                }
            }
        }
    }

    /// <summary>
    /// Every row's product with each of the first <paramref name="n"/> inputs the tables were built
    /// for: <paramref name="outputs"/>[i][row]. Each equals that input's <see cref="Pq2Lut"/> product
    /// to the last bit, whatever the number of threads.
    /// </summary>
    public unsafe void MatVec(ReadOnlySpan<byte> matrix, ReadOnlySpan<float> blockScales, int n, float[][] outputs, WorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (n < 1 || n > _built) throw new ArgumentOutOfRangeException(nameof(n), n, $"Tables are built for {_built} inputs.");
        var rows = outputs[0].Length;
        for (var i = 1; i < n; i++)
            if (outputs[i].Length != rows) throw new ArgumentException("Every output has a value per row.", nameof(outputs));
        if (matrix.Length < (long)rows * Blocks * Pq2.BlockBytes || blockScales.Length < (long)rows * Blocks)
            throw new ArgumentException("Matrix or scales too short for the outputs.");

        fixed (byte* m = matrix)
        fixed (float* dw = blockScales)
        {
            var cached = Math.Max(MinChunkRows, MaxChunkBytes / (Blocks * Pq2.BlockBytes));
            _rows.Set(m, dw, outputs, n, rows, pool is null ? cached : Math.Max(MinChunkRows, Math.Min(rows / (pool.Threads * 4), cached)));
            try
            {
                if (pool is null) _rows.Execute(0, 1);
                else pool.Run(_rows);
            }
            finally
            {
                _rows.Clear();
            }
        }
    }

    // Rows [first, end) for n inputs, block by block across the rows, as Pq2Lut walks them.
    private unsafe void MatVecRows(byte* matrix, float* blockScales, float[][] outputs, int n, int first, int end)
    {
        var blocks = Blocks;
        var rowBytes = blocks * Pq2.BlockBytes;
        var stride = Inputs;
        for (var i = 0; i < n; i++) Array.Clear(outputs[i], first, end - first);
        var sums = stackalloc int[n * 4];
        fixed (short* table = _table)
        fixed (float* dyAll = _scales)
        {
            for (var b = 0; b < blocks; b++)
            {
                var tab = table + (long)b * Positions * 256 * stride;
                var codes = matrix + (long)first * rowBytes + b * Pq2.BlockBytes + Pq2.CodesOffset;
                var scale = blockScales + (long)first * blocks + b;
                for (var r = first; r < end; r++, codes += rowBytes, scale += blocks)
                {
                    // Integer sums per input, per 32-weight sub-block (Q8_0 needs them apart; Q8_K adds them).
                    for (var s = 0; s < n * 4; s++) sums[s] = 0;
                    for (var p = 0; p < Positions; p++)
                    {
                        var entry = tab + (p * 256 + codes[p]) * stride;
                        var sub = p >> 3;
                        for (var i = 0; i < n; i++) sums[i * 4 + sub] += entry[i];
                    }
                    var dw = *scale;
                    for (var i = 0; i < n; i++)
                    {
                        var dy = dyAll + i * blocks * 4;
                        var s4 = sums + i * 4;
                        float contribution;
                        if (PerSubBlock)
                        {
                            var d = dy + b * 4;
                            var sumi = 0f;
                            sumi += d[0] * s4[0];
                            sumi += d[1] * s4[1];
                            sumi += d[2] * s4[2];
                            sumi += d[3] * s4[3];
                            contribution = dw * sumi;
                        }
                        else
                        {
                            contribution = (dw * dy[b]) * (float)(s4[0] + s4[1] + s4[2] + s4[3]);
                        }
                        outputs[i][r] += contribution;
                    }
                }
            }
        }
    }

    private sealed unsafe class RowsWork(Pq2BatchLut lut) : WorkerPool.IWork
    {
        private byte* _matrix;
        private float* _scales;
        private float[][]? _outputs;
        private int _n;
        private int _count;
        private int _chunk;
        private int _next;

        public void Set(byte* matrix, float* scales, float[][] outputs, int n, int count, int chunk)
        {
            _matrix = matrix;
            _scales = scales;
            _outputs = outputs;
            _n = n;
            _count = count;
            _chunk = chunk;
            _next = 0;
        }

        public void Clear()
        {
            _matrix = null;
            _scales = null;
            _outputs = null;
        }

        public void Execute(int worker, int workers)
        {
            long first;
            while ((first = (long)(Interlocked.Increment(ref _next) - 1) * _chunk) < _count)
                lut.MatVecRows(_matrix, _scales, _outputs!, _n, (int)first, (int)Math.Min(_count, first + _chunk));
        }
    }

    private sealed class BuildWork(Pq2BatchLut lut) : WorkerPool.IWork
    {
        private int _n;
        private int _next;

        public void Set(int n)
        {
            _n = n;
            _next = 0;
        }

        public void Execute(int worker, int workers)
        {
            int b;
            while ((b = Interlocked.Increment(ref _next) - 1) < lut.Blocks)
                lut.BuildBlocks(b, b + 1, _n);
        }
    }
}
