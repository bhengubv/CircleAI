// WeightMatrix.cs
//
// A weight read in place from the mapped GGUF - one row of Columns values per output, held as
// Q8_0 blocks, fp16 or fp32 - times a batch of activation rows, the way the pinned fork's CPU
// ggml_mul_mat computes it: the activations first take the form the weight type's dot product
// reads (ggml's vec_dot_type), then each output is one dot product, plus the bias if there is one.
//
//   Q8_0  activations quantised to Q8_0 exactly as Q8Activations rounds them. Each block's 32
//         products are summed as an exact integer, then sum += isum * (dw * dx), block by block:
//         the order of the fork's generic ggml_vec_dot_q8_0_q8_0, which its shipped Windows build
//         (compiled without AVX) runs. The AVX2 path sums each block in vector lanes and then
//         exactly, so it equals the scalar path to the last bit.
//   F16   activations rounded to fp16, the vec_dot_type of F16. A product of two fp16 values is
//         exact in fp32; the products are summed in double, as the fork's scalar path sums them.
//   F32   fp32 products summed in double, as the fork's scalar ggml_vec_dot_f32.
//
// A batch is many rows - an image's patches - so the work is shared out by output rows: tiles of
// rows claimed from one counter by the pool's threads, so a slow core never holds up the rest.
// A tile's rows stay in cache while every activation row passes them, and each output is summed
// in the same order whichever thread takes its tile.
//
// One product at a time per ProductScratch: it holds the converted activations and the threads'
// share-out.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Kernels;

/// <summary>A Q8_0, F16 or F32 weight in the mapped file, multiplied by batches of activation rows.</summary>
public sealed unsafe class WeightMatrix
{
    /// <summary>Output rows one worker claims at a time.</summary>
    internal const int TileRows = 16;

    private const int Q8Block = 32;
    private const int Q8BlockBytes = 34;

    private readonly GgufFile _file;
    private readonly GgufTensor _tensor;
    private readonly long _rowBytes;

    private WeightMatrix(GgufFile file, GgufTensor tensor, int columns, int rows)
    {
        _file = file;
        _tensor = tensor;
        Columns = columns;
        Rows = rows;
        _rowBytes = tensor.Type switch
        {
            GgmlType.Q8_0 => (long)columns / Q8Block * Q8BlockBytes,
            GgmlType.F16 => columns * 2L,
            _ => columns * 4L,
        };
    }

    /// <summary>
    /// Binds tensor <paramref name="name"/> of <paramref name="file"/> (which must stay open) as
    /// <paramref name="rows"/> rows of <paramref name="columns"/>. A tensor of more than two
    /// dimensions is read flattened - a convolution kernel's [kw, kh, channels, outputs] is one row
    /// of kw * kh * channels per output - so its first dimension must divide a row.
    /// </summary>
    public static WeightMatrix Bind(GgufFile file, string name, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
        if (tensor.Type is not (GgmlType.Q8_0 or GgmlType.F16 or GgmlType.F32))
            throw new NotSupportedException($"{name} is {tensor.Type}; weights are read as Q8_0, F16 or F32.");
        if (columns <= 0 || rows <= 0 || tensor.Elements != (long)columns * rows || columns % tensor.RowLength != 0)
            throw new InvalidDataException($"{name} is [{string.Join(", ", tensor.Dimensions)}], not {rows} rows of {columns}.");
        if (tensor.Type == GgmlType.Q8_0 && columns % Q8Block != 0)
            throw new InvalidDataException($"{name}: a row of {columns} is not whole Q8_0 blocks.");
        return new WeightMatrix(file, tensor, columns, rows);
    }

    /// <summary>The tensor's name.</summary>
    public string Name => _tensor.Name;

    /// <summary>How the weights are held.</summary>
    public GgmlType Type => _tensor.Type;

    /// <summary>Values in a row: the width of an activation row.</summary>
    public int Columns { get; }

    /// <summary>Rows: the width of an output row.</summary>
    public int Rows { get; }

    /// <summary>The fastest path for a product on this CPU and runtime.</summary>
    public static KernelPath Best { get; } = Avx2.IsSupported ? KernelPath.Avx2 : KernelPath.Scalar;

    /// <summary>
    /// <paramref name="y"/>[t * Rows + r] = row r of the weight dotted with row t of
    /// <paramref name="x"/> (Columns values each), plus <paramref name="bias"/>[r] when given,
    /// for the first <paramref name="tokens"/> rows, on <paramref name="path"/> (the fastest here
    /// unless given).
    /// </summary>
    public void Multiply(float[] x, int tokens, float[] y, float[]? bias, ProductScratch scratch, WorkerPool? pool, KernelPath? path = null)
    {
        var kernel = path ?? Best;
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        if (x.Length < (long)tokens * Columns) throw new ArgumentException($"{tokens} rows of {Columns} do not fit {x.Length} values.", nameof(x));
        if (y.Length < (long)tokens * Rows) throw new ArgumentException($"{tokens} rows of {Rows} do not fit {y.Length} values.", nameof(y));
        if (bias is not null && bias.Length != Rows) throw new ArgumentException($"A bias takes {Rows} values.", nameof(bias));
        if (kernel is not (KernelPath.Scalar or KernelPath.Avx2)) throw new ArgumentOutOfRangeException(nameof(path), kernel, "Products run Scalar or Avx2.");
        if (kernel == KernelPath.Avx2 && !Avx2.IsSupported) throw new PlatformNotSupportedException("AVX2 is not available here.");
        if (tokens == 0) return;

        // The activations in the form this weight's dot product reads.
        var workers = Math.Max(1, pool?.Threads ?? 1);
        switch (Type)
        {
            case GgmlType.Q8_0:
                scratch.Reserve(quants: (long)tokens * Columns, scales: (long)tokens * (Columns / Q8Block), rounded: 0,
                    workers, tileFloats: TileRows * (Columns / Q8Block));
                Q8Activations.QuantizeInto(x.AsSpan(0, tokens * Columns), scratch.Scales, scratch.Quants);
                break;
            case GgmlType.F16:
                scratch.Reserve(quants: 0, scales: 0, rounded: (long)tokens * Columns, workers, tileFloats: TileRows * Columns);
                for (var i = 0; i < tokens * Columns; i++) scratch.Rounded[i] = (float)(Half)x[i];
                break;
            default:
                scratch.Reserve(quants: 0, scales: 0, rounded: 0, workers, tileFloats: 0);
                break;
        }

        var work = scratch.Job;
        work.Set(this, Type == GgmlType.F32 ? x : scratch.Rounded, tokens, y, bias, kernel);
        try
        {
            if (pool is null) work.Execute(0, 1);
            else pool.Run(work);
        }
        finally
        {
            work.Clear();
        }
    }

    // Output rows [r0, r1) for every activation row, on worker `worker`.
    internal void RunTile(ProductScratch.Work job, int worker, int r0, int r1)
    {
        var data = _file.Pointer(_tensor);
        var tile = job.Scratch.TileFloats[worker];
        var tokens = job.Tokens;
        var bias = job.Bias;
        fixed (float* y = job.Y)
        fixed (float* t = tile)
        {
            if (Type == GgmlType.Q8_0)
            {
                // The tile's weight scales once - every activation row reads them - laid out four
                // rows side by side per block: t[(group * blocks + b) * 4 + row in group].
                var blocks = Columns / Q8Block;
                for (var r = r0; r < r1; r++)
                {
                    var row = data + r * _rowBytes;
                    var at = t + (r - r0) / 4 * blocks * 4 + (r - r0) % 4;
                    for (var b = 0; b < blocks; b++) at[b * 4] = (float)Unsafe.ReadUnaligned<Half>(row + b * Q8BlockBytes);
                }
                fixed (sbyte* xq = job.Scratch.Quants)
                fixed (float* xs = job.Scratch.Scales)
                {
                    for (var tok = 0; tok < tokens; tok++)
                    {
                        var xrow = xq + (long)tok * Columns;
                        var xsc = xs + (long)tok * blocks;
                        var yrow = y + (long)tok * Rows;
                        var r = r0;
                        if (job.Path == KernelPath.Avx2)
                        {
                            for (; r + 4 <= r1; r += 4)
                            {
                                var sums = DotQ8Avx2x4(data + r * _rowBytes, _rowBytes, xrow, xsc, t + (r - r0) / 4 * blocks * 4, blocks);
                                for (var k = 0; k < 4; k++) yrow[r + k] = bias is null ? sums.GetElement(k) : sums.GetElement(k) + bias[r + k];
                            }
                        }
                        for (; r < r1; r++)
                        {
                            var ws = t + (r - r0) / 4 * blocks * 4 + (r - r0) % 4;
                            var sum = DotQ8Scalar(data + r * _rowBytes, xrow, xsc, ws, blocks);
                            yrow[r] = bias is null ? sum : sum + bias[r];
                        }
                    }
                }
                return;
            }

            // F16 rows are widened once per tile; F32 rows are read where they lie.
            float* rows;
            if (Type == GgmlType.F16)
            {
                for (var r = r0; r < r1; r++)
                {
                    var src = (Half*)(data + r * _rowBytes);
                    var dst = t + (long)(r - r0) * Columns;
                    for (var i = 0; i < Columns; i++) dst[i] = (float)src[i];
                }
                rows = t;
            }
            else
            {
                rows = (float*)(data + r0 * _rowBytes);
            }

            fixed (float* x = job.X)
            {
                for (var tok = 0; tok < tokens; tok++)
                {
                    var xrow = x + (long)tok * Columns;
                    var yrow = y + (long)tok * Rows;
                    for (var r = r0; r < r1; r++)
                    {
                        var w = rows + (long)(r - r0) * Columns;
                        var sum = job.Path == KernelPath.Avx2 ? DotFloatAvx(w, xrow, Columns) : DotFloatScalar(w, xrow, Columns);
                        yrow[r] = bias is null ? sum : sum + bias[r];
                    }
                }
            }
        }
    }

    // The fork's generic Q8_0 x Q8_0 order: an exact integer per block, scaled, summed in order.
    // The row's weight scales are every fourth float from ws (the tile's interleaved layout).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotQ8Scalar(byte* w, sbyte* x, float* xs, float* ws, int blocks)
    {
        var sum = 0f;
        for (var b = 0; b < blocks; b++)
        {
            var q = (sbyte*)(w + b * Q8BlockBytes + 2);
            var v = x + b * Q8Block;
            var isum = 0;
            for (var i = 0; i < Q8Block; i++) isum += q[i] * v[i];
            sum += isum * (ws[b * 4] * xs[b]);
        }
        return sum;
    }

    // Four rows at once, the same sums: each block's 32 products per row multiplied and paired in
    // vector lanes (|q| and v * sign(q) through maddubs - neither operand is -128, so no pair
    // saturates), the four rows' block totals added exactly across lanes together, then each row's
    // total scaled and added to that row's own running sum - one lane a row, block by block, in
    // the scalar order - so every row equals DotQ8Scalar to the last bit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> DotQ8Avx2x4(byte* w, long rowBytes, sbyte* x, float* xs, float* ws, int blocks)
    {
        var ones = Vector256.Create((short)1);
        var sum = Vector128<float>.Zero;
        for (var b = 0; b < blocks; b++)
        {
            var v = Avx.LoadVector256(x + b * Q8Block);
            var at = w + b * Q8BlockBytes + 2;
            var q0 = Avx.LoadVector256((sbyte*)at);
            var q1 = Avx.LoadVector256((sbyte*)(at + rowBytes));
            var q2 = Avx.LoadVector256((sbyte*)(at + 2 * rowBytes));
            var q3 = Avx.LoadVector256((sbyte*)(at + 3 * rowBytes));
            var p0 = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(Avx2.Abs(q0), Avx2.Sign(v, q0)), ones);
            var p1 = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(Avx2.Abs(q1), Avx2.Sign(v, q1)), ones);
            var p2 = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(Avx2.Abs(q2), Avx2.Sign(v, q2)), ones);
            var p3 = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(Avx2.Abs(q3), Avx2.Sign(v, q3)), ones);
            var h = Avx2.HorizontalAdd(Avx2.HorizontalAdd(p0, p1), Avx2.HorizontalAdd(p2, p3));
            var totals = Sse2.Add(h.GetLower(), h.GetUpper()); // row 0, 1, 2, 3
            var scales = Sse.Multiply(Sse.LoadVector128(ws + b * 4), Vector128.Create(xs[b]));
            sum = Sse.Add(sum, Sse.Multiply(Sse2.ConvertToVector128Single(totals), scales));
        }
        return sum;
    }

    // fp32 products summed in double, as the fork's scalar ggml_vec_dot_f32 and _f16.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotFloatScalar(float* w, float* x, int n)
    {
        double sum = 0;
        for (var i = 0; i < n; i++) sum += w[i] * x[i];
        return (float)sum;
    }

    // The same products, eight at a time, summed in double lanes: equal to the scalar path but
    // for the order of the double additions.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotFloatAvx(float* w, float* x, int n)
    {
        var lo = Vector256<double>.Zero;
        var hi = Vector256<double>.Zero;
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            var p = Avx.Multiply(Avx.LoadVector256(w + i), Avx.LoadVector256(x + i));
            lo = Avx.Add(lo, Avx.ConvertToVector256Double(p.GetLower()));
            hi = Avx.Add(hi, Avx.ConvertToVector256Double(p.GetUpper()));
        }
        var s = Avx.Add(lo, hi);
        double sum = s.GetElement(0) + s.GetElement(1) + s.GetElement(2) + s.GetElement(3);
        for (; i < n; i++) sum += w[i] * x[i];
        return (float)sum;
    }
}

/// <summary>
/// What a <see cref="WeightMatrix"/> product works in - the activations in the weight's dot-product
/// form and each worker's tile buffer - grown as needed and reused. One product at a time.
/// </summary>
public sealed class ProductScratch
{
    internal sbyte[] Quants = [];
    internal float[] Scales = [];
    internal float[] Rounded = [];
    internal float[][] TileFloats = [];

    internal Work Job { get; }

    /// <summary>An empty scratch; it grows to the products it is used for.</summary>
    public ProductScratch() => Job = new Work(this);

    internal void Reserve(long quants, long scales, long rounded, int workers, int tileFloats)
    {
        if (Quants.Length < quants) Quants = new sbyte[quants];
        if (Scales.Length < scales) Scales = new float[scales];
        if (Rounded.Length < rounded) Rounded = new float[rounded];
        if (TileFloats.Length < workers) Array.Resize(ref TileFloats, workers);
        for (var i = 0; i < workers; i++)
        {
            if (TileFloats[i] is null || TileFloats[i].Length < tileFloats) TileFloats[i] = new float[Math.Max(1, tileFloats)];
        }
    }

    // One product, its output-row tiles handed out to whichever worker is free.
    internal sealed class Work(ProductScratch scratch) : WorkerPool.IWork
    {
        private int _next;
        private int _tiles;

        public ProductScratch Scratch { get; } = scratch;
        public WeightMatrix? Matrix { get; private set; }
        public float[] X { get; private set; } = [];
        public int Tokens { get; private set; }
        public float[] Y { get; private set; } = [];
        public float[]? Bias { get; private set; }
        public KernelPath Path { get; private set; }

        public void Set(WeightMatrix matrix, float[] x, int tokens, float[] y, float[]? bias, KernelPath path)
        {
            Matrix = matrix;
            X = x;
            Tokens = tokens;
            Y = y;
            Bias = bias;
            Path = path;
            _tiles = (matrix.Rows + WeightMatrix.TileRows - 1) / WeightMatrix.TileRows;
            _next = 0;
        }

        public void Clear()
        {
            Matrix = null;
            X = [];
            Y = [];
            Bias = null;
        }

        public void Execute(int worker, int workers)
        {
            var matrix = Matrix!;
            int tile;
            while ((tile = Interlocked.Increment(ref _next) - 1) < _tiles)
            {
                var r0 = tile * WeightMatrix.TileRows;
                matrix.RunTile(this, worker, r0, Math.Min(matrix.Rows, r0 + WeightMatrix.TileRows));
            }
        }
    }
}
