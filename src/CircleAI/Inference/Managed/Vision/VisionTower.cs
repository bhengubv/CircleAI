// VisionTower.cs
//
// A Bonsai 2 vision tower (projector qwen3vl_merger) run in C#, step for step as the pinned fork's
// clip graph runs it (tools/mtmd/models/qwen3vl.cpp, clip.cpp, ggml-cpu):
//
//   patches    each 16x16 patch's values (channel, row, column) through both temporal slices of the
//              patch kernel - a still picture is both frames - added, then the patch bias
//   order      the patches taken window by window, 2x2 (windows row by row; in each, top-left,
//              top-right, bottom-left, bottom-right): the order the merge reads them in
//   positions  the learned 48x48 table resized to the patch grid (bilinear, corners aligned) and
//              added in that same order
//   layers     x += out(attention(LN1(x))); x += down(GELU(up(LN2(x)))). Attention: fused q, k, v
//              (+bias); 2-D RoPE on q and k - each head's first quarter of rotary pairs turned by the
//              patch's row, the second by its column, base 10000; softmax(q k / sqrt(d)) v over every
//              patch, with k and v held in fp16 as the fork's flash-attention path holds them; then
//              the output projection (+bias)
//   merge      the post-norm, then each window's four patches side by side through mm.0 (+bias),
//              GELU, mm.2 (+bias): one token per window, as wide as the language model's embedding
//
// The tokens come out row by row over the grid of windows, which is how the language model places
// them (Qwen35Model.ForwardImageToken). Products are WeightMatrix's, rounded as the fork rounds
// them; norms sum in double as ggml does; GELU is the fork's fp16 table.
//
// Not thread-safe: one Encode at a time. The file must stay open while the tower is used.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Vision;

namespace CircleAI.Inference.Managed.Vision;

/// <summary>An image's tokens from a <see cref="VisionTower"/>, row by row over its grid.</summary>
/// <param name="Values">Every token's values, one after another.</param>
/// <param name="Columns">Tokens across.</param>
/// <param name="Rows">Tokens down.</param>
/// <param name="Width">Values per token: the language model's embedding width.</param>
public sealed record ImageEmbeddings(float[] Values, int Columns, int Rows, int Width)
{
    /// <summary>Tokens in all.</summary>
    public int Count => Columns * Rows;

    /// <summary>Token <paramref name="index"/>'s values.</summary>
    public ReadOnlySpan<float> Token(int index) => Values.AsSpan(index * Width, Width);
}

/// <summary>A Bonsai 2 vision tower, loaded from its mapped GGUF and run in C#.</summary>
public sealed class VisionTower
{
    private const float RopeBase = 10000f;
    private const int QueryTile = 16;
    private const int ForkQueryTile = 64; // GGML_FA_TILE_Q: the fewest queries the fork's tiled attention takes

    private readonly GgufFile _file;
    private readonly WorkerPool? _pool;
    private readonly GgufTensor _positions;
    private readonly WeightMatrix _patch0, _patch1;
    private readonly float[] _patchBias;
    private readonly Layer[] _layers;
    private readonly float[] _postWeight, _postBias;
    private readonly WeightMatrix _merge0, _merge2;
    private readonly float[] _merge0Bias, _merge2Bias;
    private readonly RowWork _rows = new();

    private VisionTower(GgufFile file, WorkerPool? pool)
    {
        _file = file;
        _pool = pool;
        Config = VisionConfig.FromGguf(file);
        var c = Config;
        var patchValues = 3 * c.PatchSize * c.PatchSize;

        _patch0 = WeightMatrix.Bind(file, "v.patch_embd.weight", patchValues, c.Embedding);
        _patch1 = WeightMatrix.Bind(file, "v.patch_embd.weight.1", patchValues, c.Embedding);
        _patchBias = Floats(file, "v.patch_embd.bias", c.Embedding);
        _positions = file.Tensor("v.position_embd.weight")!;
        if (_positions.Type != GgmlType.F32) throw new NotSupportedException($"The position table is {_positions.Type}; the fork resizes it as F32.");

        _layers = new Layer[c.Layers];
        for (var il = 0; il < c.Layers; il++) _layers[il] = new Layer(file, c, il);
        _postWeight = Floats(file, "v.post_ln.weight", c.Embedding);
        _postBias = Floats(file, "v.post_ln.bias", c.Embedding);

        var merged = c.Embedding * c.MergeSize * c.MergeSize;
        _merge0 = WeightMatrix.Bind(file, "mm.0.weight", merged, merged);
        _merge0Bias = Floats(file, "mm.0.bias", merged);
        _merge2 = WeightMatrix.Bind(file, "mm.2.weight", merged, c.Projection);
        _merge2Bias = Floats(file, "mm.2.bias", c.Projection);
    }

    /// <summary>The tower's shape.</summary>
    public VisionConfig Config { get; }

    /// <summary>
    /// Binds every weight of <paramref name="file"/> (which must stay open while the tower is used)
    /// and checks every shape. Products share <paramref name="pool"/>'s threads when one is given.
    /// </summary>
    public static VisionTower Load(GgufFile file, WorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new VisionTower(file, pool);
    }

    /// <summary>
    /// <paramref name="image"/>'s tokens: sized by <see cref="VisionConfig.Fit"/> within
    /// <paramref name="maxPixels"/>, prepared as the fork prepares it, then encoded.
    /// </summary>
    public ImageEmbeddings Encode(RasterImage image, int maxPixels)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (width, height) = Config.Fit(image.Width, image.Height, maxPixels);
        var ready = VisionImage.Fit(image, width, height);
        return Encode(VisionImage.Normalize(ready, Config.ImageMean, Config.ImageStd), width, height);
    }

    /// <summary>
    /// The tokens of an image already prepared - <paramref name="width"/> x <paramref name="height"/>
    /// pixels of normalised RGB, interleaved, row by row - whose sides are whole tokens.
    /// </summary>
    public ImageEmbeddings Encode(float[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var c = Config;
        if (width <= 0 || height <= 0 || width % c.TokenSide != 0 || height % c.TokenSide != 0)
            throw new ArgumentException($"{width}x{height} is not whole {c.TokenSide}-pixel tokens.");
        if (pixels.Length != width * height * 3) throw new ArgumentException($"{width}x{height} RGB is {width * height * 3} values, not {pixels.Length}.", nameof(pixels));

        var started = Stopwatch.GetTimestamp();
        var e = c.Embedding;
        var ps = c.PatchSize;
        var gw = width / ps;
        var gh = height / ps;
        var n = gw * gh;
        var scratch = new ProductScratch();

        // The patches, window by window, as the merge will read them.
        var patchValues = 3 * ps * ps;
        var patches = new float[n * patchValues];
        var rowOf = new int[n];
        var colOf = new int[n];
        var k = 0;
        for (var wy = 0; wy < gh / 2; wy++)
        for (var wx = 0; wx < gw / 2; wx++)
        for (var dy = 0; dy < 2; dy++)
        for (var dx = 0; dx < 2; dx++, k++)
        {
            var py = 2 * wy + dy;
            var px = 2 * wx + dx;
            rowOf[k] = py;
            colOf[k] = px;
            var dst = patches.AsSpan(k * patchValues, patchValues);
            for (var ch = 0; ch < 3; ch++)
            for (var ky = 0; ky < ps; ky++)
            {
                var src = ((py * ps + ky) * width + px * ps) * 3 + ch;
                for (var kx = 0; kx < ps; kx++) dst[ch * ps * ps + ky * ps + kx] = pixels[src + kx * 3];
            }
        }

        // Both frames' kernels, the bias, the positions: ((frame0 + frame1) + bias) + position.
        var x = new float[n * e];
        var other = new float[n * e];
        _patch0.Multiply(patches, n, x, null, scratch, _pool);
        _patch1.Multiply(patches, n, other, null, scratch, _pool);
        var positions = Positions(gw, gh);
        for (var t = 0; t < n; t++)
        {
            var at = t * e;
            var pos = (rowOf[t] * gw + colOf[t]) * e;
            for (var i = 0; i < e; i++) x[at + i] = x[at + i] + other[at + i] + _patchBias[i] + positions[pos + i];
        }

        var (cos, sin) = RopeAngles(rowOf, colOf);
        var h = new float[n * e];
        var qkv = new float[n * 3 * e];
        var q = new float[n * e];
        var kc = new float[n * e];
        var vc = new float[n * e];
        var attended = new float[n * e];
        var up = new float[n * c.FeedForward];
        foreach (var layer in _layers)
        {
            LayerNorms(x, h, n, layer.Norm1Weight, layer.Norm1Bias);
            layer.Qkv.Multiply(h, n, qkv, layer.QkvBias, scratch, _pool);
            SplitAndTurn(qkv, n, cos, sin, q, kc, vc);
            Attend(q, kc, vc, n, attended);
            layer.Output.Multiply(attended, n, other, layer.OutputBias, scratch, _pool);
            for (var i = 0; i < x.Length; i++) x[i] = other[i] + x[i];

            LayerNorms(x, h, n, layer.Norm2Weight, layer.Norm2Bias);
            layer.Up.Multiply(h, n, up, layer.UpBias, scratch, _pool);
            ApplyGelu(up);
            layer.Down.Multiply(up, n, other, layer.DownBias, scratch, _pool);
            for (var i = 0; i < x.Length; i++) x[i] = x[i] + other[i];
        }

        // Post-norm; each window's four patches side by side are one row of the merge.
        LayerNorms(x, h, n, _postWeight, _postBias);
        var windows = n / 4;
        var merged = new float[windows * _merge0.Rows];
        _merge0.Multiply(h, windows, merged, _merge0Bias, scratch, _pool);
        ApplyGelu(merged);
        var tokens = new float[windows * c.Projection];
        _merge2.Multiply(merged, windows, tokens, _merge2Bias, scratch, _pool);

        LastEncode = Stopwatch.GetElapsedTime(started);
        return new ImageEmbeddings(tokens, gw / 2, gh / 2, c.Projection);
    }

    /// <summary>How long the last <see cref="Encode(float[], int, int)"/> took.</summary>
    public TimeSpan LastEncode { get; private set; }

    // ---- the parts ----

    // The learned table resized to the patch grid, bilinear with corners aligned (ggml_interpolate,
    // GGML_SCALE_MODE_BILINEAR | GGML_SCALE_FLAG_ALIGN_CORNERS), row by row: [py][px][e].
    private float[] Positions(int gw, int gh)
    {
        var table = _file.Floats(_positions);
        var grid = Config.PositionGrid;
        var e = Config.Embedding;
        if (gw == grid && gh == grid) return table.ToArray();

        var sx = gw > 1 && grid > 1 ? (float)(gw - 1) / (grid - 1) : (float)gw / grid;
        var sy = gh > 1 && grid > 1 ? (float)(gh - 1) / (grid - 1) : (float)gh / grid;
        var result = new float[gw * gh * e];
        for (var py = 0; py < gh; py++)
        {
            var y = py / sy;
            var y0 = Math.Clamp((int)MathF.Floor(y), 0, grid - 1);
            var y1 = Math.Clamp((int)MathF.Floor(y) + 1, 0, grid - 1);
            var dy = Math.Clamp(y - y0, 0f, 1f);
            for (var px = 0; px < gw; px++)
            {
                var xf = px / sx;
                var x0 = Math.Clamp((int)MathF.Floor(xf), 0, grid - 1);
                var x1 = Math.Clamp((int)MathF.Floor(xf) + 1, 0, grid - 1);
                var dx = Math.Clamp(xf - x0, 0f, 1f);
                var a = table.Slice((y0 * grid + x0) * e, e);
                var b = table.Slice((y0 * grid + x1) * e, e);
                var cc = table.Slice((y1 * grid + x0) * e, e);
                var d = table.Slice((y1 * grid + x1) * e, e);
                var o = result.AsSpan((py * gw + px) * e, e);
                for (var i = 0; i < e; i++)
                    o[i] = a[i] * (1 - dx) * (1 - dy) + b[i] * dx * (1 - dy) + cc[i] * (1 - dx) * dy + d[i] * dx * dy;
            }
        }
        return result;
    }

    // Each patch's rotary angles (ggml_mrope_cache_init in vision mode, sections of a quarter
    // head each): pairs [0, d/4) turned by the row, [d/4, d/2) by the column, each section's
    // angle restarting at its own position and multiplied by base^(-2/(d/2)) pair by pair.
    private (float[] Cos, float[] Sin) RopeAngles(int[] rowOf, int[] colOf)
    {
        var d = Config.HeadDim;
        var half = d / 2;
        var quarter = d / 4;
        var scale = MathF.Pow(RopeBase, -2.0f / half);
        var cos = new float[rowOf.Length * half];
        var sin = new float[rowOf.Length * half];
        for (var t = 0; t < rowOf.Length; t++)
        {
            float byRow = rowOf[t], byColumn = colOf[t];
            for (var pair = 0; pair < half; pair++)
            {
                if (pair == quarter) byColumn = colOf[t];
                var theta = pair < quarter ? byRow : byColumn;
                cos[t * half + pair] = MathF.Cos(theta);
                sin[t * half + pair] = MathF.Sin(theta);
                byRow *= scale;
                byColumn *= scale;
            }
        }
        return (cos, sin);
    }

    // q, k and v of every head out of the fused product; q and k turned (rotate-half over the whole
    // head); k and v rounded through fp16, as the fork casts them for flash attention. Laid out
    // head by head: [head][patch][d].
    private void SplitAndTurn(float[] qkv, int n, float[] cos, float[] sin, float[] q, float[] k, float[] v)
    {
        var c = Config;
        var e = c.Embedding;
        var d = c.HeadDim;
        var half = d / 2;
        _rows.Run(_pool, n, 32, (_, from, to) =>
        {
            for (var t = from; t < to; t++)
            {
                var row = t * 3 * e;
                var angles = t * half;
                for (var head = 0; head < c.Heads; head++)
                {
                    var at = (head * n + t) * d;
                    var qs = row + head * d;
                    var ks = row + e + head * d;
                    var vs = row + 2 * e + head * d;
                    for (var i = 0; i < half; i++)
                    {
                        var co = cos[angles + i];
                        var si = sin[angles + i];
                        float q0 = qkv[qs + i], q1 = qkv[qs + i + half];
                        q[at + i] = q0 * co - q1 * si;
                        q[at + i + half] = q0 * si + q1 * co;
                        float k0 = qkv[ks + i], k1 = qkv[ks + i + half];
                        k[at + i] = (float)(Half)(k0 * co - k1 * si);
                        k[at + i + half] = (float)(Half)(k0 * si + k1 * co);
                    }
                    for (var i = 0; i < d; i++) v[at + i] = (float)(Half)qkv[vs + i];
                }
            }
        });
    }

    // Every patch attends to every patch, head by head: softmax(q k * 1/sqrt(d)) over the keys, then
    // the weighted sum of v, divided by the weights' sum. Shared out by head and tiles of queries.
    //
    // TWO ARITHMETICS, AS THE FORK HAS TWO. Its CPU flash attention takes its tiled path only for
    // at least a 64-row tile of queries (GGML_FA_TILE_Q): q in fp32, scores and the weighted sum in
    // fp32. Under that - a picture of fewer than 64 patches - it takes the one-chunk path
    // (ggml_compute_forward_flash_attn_ext_f16_one_chunk): q rounded to fp16, each score an fp16
    // dot summed in double, and the weighted values accumulated in fp16 key by key, rescaled in fp16
    // whenever a larger score arrives. Both are followed, each where the fork takes it.
    private unsafe void Attend(float[] q, float[] k, float[] v, int n, float[] output)
    {
        var c = Config;
        var d = c.HeadDim;
        var e = c.Embedding;
        var scale = 1.0f / MathF.Sqrt(d);
        var tiled = n >= ForkQueryTile;
        var tiles = (n + QueryTile - 1) / QueryTile;
        var workers = Math.Max(1, _pool?.Threads ?? 1);
        var scores = new float[workers][];
        var sums = new float[workers][];
        var queries = new float[workers][];
        for (var i = 0; i < workers; i++)
        {
            scores[i] = new float[n];
            sums[i] = new float[d];
            queries[i] = new float[d];
        }

        _rows.Run(_pool, c.Heads * tiles, 1, (worker, from, to) =>
        {
            var s = scores[worker];
            var acc = sums[worker];
            fixed (float* qp = q)
            fixed (float* kp = k)
            fixed (float* vp = v)
            fixed (float* sp = s)
            fixed (float* ap = acc)
            fixed (float* q16 = queries[worker])
            {
                for (var item = from; item < to; item++)
                {
                    var head = item / tiles;
                    var first = item % tiles * QueryTile;
                    var last = Math.Min(n, first + QueryTile);
                    var keys = kp + (long)head * n * d;
                    var values = vp + (long)head * n * d;
                    for (var t = first; t < last; t++)
                    {
                        var query = qp + ((long)head * n + t) * d;
                        var o = output.AsSpan(t * e + head * d, d);
                        if (tiled) AttendTiled(query, keys, values, n, d, scale, sp, ap, o);
                        else AttendOneChunk(query, keys, values, n, d, scale, q16, ap, o);
                    }
                }
            }
        });
    }

    // The fork's tiled path for one query: fp32 throughout, the sum divided out at the end.
    private static unsafe void AttendTiled(float* query, float* keys, float* values, int n, int d, float scale, float* scores, float* acc, Span<float> output)
    {
        var max = float.NegativeInfinity;
        for (var j = 0; j < n; j++)
        {
            var score = Dot(query, keys + (long)j * d, d) * scale;
            scores[j] = score;
            if (score > max) max = score;
        }
        var total = 0f;
        new Span<float>(acc, d).Clear();
        for (var j = 0; j < n; j++)
        {
            var w = MathF.Exp(scores[j] - max);
            total += w;
            AddScaled(acc, values + (long)j * d, w, d);
        }
        var inverse = total == 0f ? 0f : 1.0f / total;
        for (var i = 0; i < d; i++) output[i] = acc[i] * inverse;
    }

    // The fork's one-chunk path for one query, step for step: q in fp16; per key, the score as an
    // fp16 dot summed in double, scaled; a new maximum rescales the fp16 sum by exp(old - new),
    // otherwise the key weighs exp(score - max); the sum takes value * weight, rounded to fp16;
    // S = S * rescale + weight. Then the sum, widened, times 1/S.
    private static unsafe void AttendOneChunk(float* query, float* keys, float* values, int n, int d, float scale, float* q16, float* acc, Span<float> output)
    {
        for (var i = 0; i < d; i++) q16[i] = (float)(Half)query[i];
        new Span<float>(acc, d).Clear();
        var max = float.NegativeInfinity;
        var sum = 0f;
        for (var j = 0; j < n; j++)
        {
            var key = keys + (long)j * d;
            double dot = 0;
            for (var i = 0; i < d; i++) dot += key[i] * q16[i];
            var score = (float)dot * scale;

            var rescale = 1f;
            var weight = 1f;
            if (score > max)
            {
                rescale = MathF.Exp(max - score);
                max = score;
                for (var i = 0; i < d; i++) acc[i] = (float)(Half)(acc[i] * rescale);
            }
            else
            {
                weight = MathF.Exp(score - max);
            }
            var value = values + (long)j * d;
            for (var i = 0; i < d; i++) acc[i] = (float)(Half)(acc[i] + value[i] * weight);
            sum = sum * rescale + weight;
        }
        var inverse = sum == 0f ? 0f : 1.0f / sum;
        for (var i = 0; i < d; i++) output[i] = acc[i] * inverse;
    }

    // ggml_norm, then the weight and the bias, token by token: the mean and the variance summed in
    // double, each value centred, scaled, weighted and biased in fp32.
    private void LayerNorms(float[] x, float[] y, int n, float[] weight, float[] bias)
    {
        var e = Config.Embedding;
        var eps = Config.LayerNormEps;
        _rows.Run(_pool, n, 16, (_, from, to) =>
        {
            for (var t = from; t < to; t++)
            {
                var src = x.AsSpan(t * e, e);
                var dst = y.AsSpan(t * e, e);
                double sum = 0;
                for (var i = 0; i < e; i++) sum += src[i];
                var mean = (float)sum / e;
                double squares = 0;
                for (var i = 0; i < e; i++)
                {
                    var centred = src[i] - mean;
                    dst[i] = centred;
                    squares += centred * centred;
                }
                var variance = (float)(squares / e);
                var scale = 1.0f / MathF.Sqrt(variance + eps);
                for (var i = 0; i < e; i++) dst[i] = dst[i] * scale * weight[i] + bias[i];
            }
        });
    }

    private void ApplyGelu(float[] values)
    {
        const int chunk = 1 << 14;
        _rows.Run(_pool, (values.Length + chunk - 1) / chunk, 1, (_, from, to) =>
        {
            var start = from * chunk;
            var end = Math.Min(values.Length, to * chunk);
            Gelu.Apply(values.AsSpan(start, end - start));
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Dot(float* a, float* b, int n)
    {
        var i = 0;
        var sum = 0f;
        if (Vector256.IsHardwareAccelerated && n >= 8)
        {
            var acc = Vector256<float>.Zero;
            for (; i + 8 <= n; i += 8) acc += Vector256.Load(a + i) * Vector256.Load(b + i);
            sum = Vector256.Sum(acc);
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void AddScaled(float* acc, float* x, float w, int n)
    {
        var i = 0;
        if (Vector256.IsHardwareAccelerated && n >= 8)
        {
            var weight = Vector256.Create(w);
            for (; i + 8 <= n; i += 8) (Vector256.Load(acc + i) + Vector256.Load(x + i) * weight).Store(acc + i);
        }
        for (; i < n; i++) acc[i] += x[i] * w;
    }

    private static float[] Floats(GgufFile file, string name, int length)
    {
        var tensor = file.Tensor(name) ?? throw new InvalidDataException($"The tower has no {name}.");
        if (tensor.Type != GgmlType.F32) throw new NotSupportedException($"{name} is {tensor.Type}; it is read as F32.");
        if (tensor.Elements != length) throw new InvalidDataException($"{name} holds {tensor.Elements} values, not {length}.");
        return file.Floats(tensor).ToArray();
    }

    private sealed class Layer
    {
        public Layer(GgufFile file, VisionConfig c, int il)
        {
            string Name(string part) => $"v.blk.{il}.{part}";
            var e = c.Embedding;
            Norm1Weight = Floats(file, Name("ln1.weight"), e);
            Norm1Bias = Floats(file, Name("ln1.bias"), e);
            Qkv = WeightMatrix.Bind(file, Name("attn_qkv.weight"), e, 3 * e);
            QkvBias = Floats(file, Name("attn_qkv.bias"), 3 * e);
            Output = WeightMatrix.Bind(file, Name("attn_out.weight"), e, e);
            OutputBias = Floats(file, Name("attn_out.bias"), e);
            Norm2Weight = Floats(file, Name("ln2.weight"), e);
            Norm2Bias = Floats(file, Name("ln2.bias"), e);
            Up = WeightMatrix.Bind(file, Name("ffn_up.weight"), e, c.FeedForward);
            UpBias = Floats(file, Name("ffn_up.bias"), c.FeedForward);
            Down = WeightMatrix.Bind(file, Name("ffn_down.weight"), c.FeedForward, e);
            DownBias = Floats(file, Name("ffn_down.bias"), e);
            if (file.Tensor(Name("ffn_gate.weight")) is not null)
                throw new NotSupportedException($"Layer {il} has a gated feed-forward; the Bonsai towers' is up, GELU, down.");
        }

        public float[] Norm1Weight { get; }
        public float[] Norm1Bias { get; }
        public WeightMatrix Qkv { get; }
        public float[] QkvBias { get; }
        public WeightMatrix Output { get; }
        public float[] OutputBias { get; }
        public float[] Norm2Weight { get; }
        public float[] Norm2Bias { get; }
        public WeightMatrix Up { get; }
        public float[] UpBias { get; }
        public WeightMatrix Down { get; }
        public float[] DownBias { get; }
    }

    // Rows of work in tiles, claimed by whichever of the pool's threads is free.
    private sealed class RowWork : WorkerPool.IWork
    {
        private Action<int, int, int>? _body;
        private int _rows;
        private int _tile;
        private int _next;

        public void Run(WorkerPool? pool, int rows, int tile, Action<int, int, int> body)
        {
            _body = body;
            _rows = rows;
            _tile = Math.Max(1, tile);
            _next = 0;
            try
            {
                if (pool is null) Execute(0, 1);
                else pool.Run(this);
            }
            finally
            {
                _body = null;
            }
        }

        public void Execute(int worker, int workers)
        {
            while (true)
            {
                var from = (Interlocked.Increment(ref _next) - 1) * _tile;
                if (from >= _rows) return;
                _body!(worker, from, Math.Min(_rows, from + _tile));
            }
        }
    }
}
