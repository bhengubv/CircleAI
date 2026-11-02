// Qwen35Model.cs
//
// The qwen35 architecture - the whole Bonsai 2 fleet - run one token at a time in C#, the way
// the pinned fork's graph runs it (src/models/qwen35.cpp, delta-net-base.cpp, ggml-cpu ops):
//
//   x = the token's embedding row, turned back from the table's rotation
//   each layer:  h = rms(x) * attn_norm
//                three of every four - Gated DeltaNet: qkv and z from h, beta and alpha from h
//                  unturned; a causal conv over 4 steps then silu; q and k L2-normalised per
//                  head; the delta rule on a 128x128 fp32 state per value head (the fork's fused
//                  gated_delta_net op, step for step); rms(o) * ssm_norm * silu(z); ssm_out
//                every fourth - full attention: attn_q gives each head [q | gate]; q and k
//                  RMS-normalised per head; RoPE on the first rope dims; grouped heads over an
//                  fp16 cache; the result times sigmoid(gate); attn_output
//                x += that;  h2 = rms(x) * post_attention_norm;  x += down(silu(gate(h2)) * up(h2))
//   logits = head(rms(x) * output_norm), the head being the embedding table read forward (tied)
//
// Every ternary product's input is turned first (PrismHadamard) and quantised as the fork pairs it
// with the file's packing - Q8_0 for PTQ1_0, Q8_K for PQ2_0 (Q8_0 where a width is not whole 256s) -
// and the products that read one activation share one lookup table. A file holds one packing
// throughout. Norms, gates and the recurrence repeat the fork's float operations; the attention and
// the PTQ1_0 products sum in their own order, so the logits agree with the fork's to within
// rounding, not to the bit.
//
// One Forward at a time per model: it works in buffers it owns. Each Forward's time is added up
// by part (TakeTimes), so a phone can say where a token's second goes.

using System.Diagnostics;
using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Rotation;
using CircleAI.Inference.Managed.Runtime;

namespace CircleAI.Inference.Managed.Model;

/// <summary>A qwen35 model (Bonsai 2) loaded from a mapped GGUF file, run one token at a time.</summary>
public sealed class Qwen35Model
{
    private readonly PrismHadamard? _hadamard;
    private readonly WorkerPool? _pool;
    private readonly Layer[] _layers;
    private readonly float[] _outputNorm;
    private readonly TernaryMatrix _embedding; // the lookup table and, tied, the head

    // One turned buffer, one quantisation and one lookup table per product width.
    private readonly Dictionary<int, InputTables> _inputs = [];

    // Working buffers, sized once.
    private readonly float[] _x, _h, _qkv, _z, _conv, _o, _beta, _alpha;
    private readonly float[] _qg, _q, _k, _v, _gate, _attn, _cos, _sin;
    private readonly float[] _mixOut, _ffnGate, _ffnUp, _ffnOut;
    private readonly sbyte[] _weights = new sbyte[Ptq1.BlockWeights]; // one decoded embedding block (both packings hold 128)

    // The value heads of a DeltaNet layer, shared out to the pool's threads; each thread has its
    // own delta buffer.
    private readonly DeltaWork _deltaWork;
    private readonly float[][] _deltas;

    // Where the time went since the last TakeTimes, in Stopwatch ticks.
    private long _tTotal, _tTables, _tProducts, _tRecurrence, _tAttention, _tHead;
    private int _tokens;

    private Qwen35Model(GgufFile file, WorkerPool? pool)
    {
        Config = Qwen35Config.FromGguf(file);
        var c = Config;
        _hadamard = PrismHadamard.FromGguf(file);
        _pool = pool;

        if (file.Tensor("output.weight") is not null) throw new NotSupportedException("An untied output head is not read in this pass.");
        Packing = PackingOf(file);
        _embedding = new TernaryMatrix(file, "token_embd.weight", _hadamard, c.Embedding, c.Vocabulary, Packing);
        _outputNorm = Floats(file, "output_norm.weight", c.Embedding);

        _layers = new Layer[c.Layers];
        for (var il = 0; il < c.Layers; il++) _layers[il] = new Layer(file, _hadamard, c, il, Packing);

        foreach (var width in new[] { c.Embedding, c.SsmInner, c.Heads * c.HeadDim, c.FeedForward }.Distinct())
            _inputs[width] = new InputTables(width, Packing);

        _x = new float[c.Embedding];
        _h = new float[c.Embedding];
        _qkv = new float[c.ConvChannels];
        _z = new float[c.SsmInner];
        _conv = new float[c.ConvChannels];
        _o = new float[c.SsmInner];
        _beta = new float[c.SsmHeads];
        _alpha = new float[c.SsmHeads];
        _qg = new float[2 * c.Heads * c.HeadDim];
        _q = new float[c.Heads * c.HeadDim];
        _k = new float[c.KvHeads * c.HeadDim];
        _v = new float[c.KvHeads * c.HeadDim];
        _gate = new float[c.Heads * c.HeadDim];
        _attn = new float[c.Heads * c.HeadDim];
        _cos = new float[c.RopeDims / 2];
        _sin = new float[c.RopeDims / 2];
        _mixOut = new float[c.Embedding];
        _ffnGate = new float[c.FeedForward];
        _ffnUp = new float[c.FeedForward];
        _ffnOut = new float[c.Embedding];

        _deltaWork = new DeltaWork(this);
        _deltas = new float[Math.Max(1, pool?.Threads ?? 1)][];
        for (var i = 0; i < _deltas.Length; i++) _deltas[i] = new float[c.SsmHeadDim];
    }

    /// <summary>The model's shape.</summary>
    public Qwen35Config Config { get; }

    /// <summary>How the file packs its ternary weights: PTQ1_0 or PQ2_0, one packing throughout.</summary>
    public GgmlType Packing { get; }

    /// <summary>
    /// The ternary packing <paramref name="file"/>'s weights are in, read off its embedding table; null
    /// when it is not one this engine reads (PTQ1_0 or PQ2_0).
    /// </summary>
    public static GgmlType? TernaryPackingOf(GgufFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Tensor("token_embd.weight")?.Type is { } type && type is GgmlType.PTQ1_0 or GgmlType.PQ2_0 ? type : null;
    }

    private static GgmlType PackingOf(GgufFile file) => TernaryPackingOf(file)
        ?? throw new NotSupportedException($"token_embd.weight is {file.Tensor("token_embd.weight")?.Type.ToString() ?? "missing"}; this engine reads PTQ1_0 and PQ2_0.");

    /// <summary>
    /// Binds every weight of <paramref name="file"/> (which must stay open while the model is used)
    /// and checks every shape. Products share <paramref name="pool"/>'s threads when one is given.
    /// </summary>
    public static Qwen35Model Load(GgufFile file, WorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new Qwen35Model(file, pool);
    }

    /// <summary>A new, empty conversation state holding up to <paramref name="capacity"/> tokens.</summary>
    public Qwen35State NewState(int capacity) => NewState(capacity, KvCompressionMode.Off, TurboQuantForm.Mse);

    /// <summary>
    /// A new, empty conversation state holding up to <paramref name="capacity"/> tokens, its attention
    /// keys and values held in <paramref name="kvMode"/> - the keys in <paramref name="keyForm"/>
    /// when compressed.
    /// </summary>
    public Qwen35State NewState(int capacity, KvCompressionMode kvMode, TurboQuantForm keyForm) =>
        new(Config, Math.Min(capacity, Config.ContextLength), kvMode, keyForm);

    /// <summary>The time the Forward calls since the last call took, by part; the count starts again.</summary>
    public ForwardTimes TakeTimes()
    {
        var times = new ForwardTimes(_tokens, Elapsed(_tTotal), Elapsed(_tTables), Elapsed(_tProducts),
            Elapsed(_tRecurrence), Elapsed(_tAttention), Elapsed(_tHead));
        _tTotal = _tTables = _tProducts = _tRecurrence = _tAttention = _tHead = 0;
        _tokens = 0;
        return times;

        static TimeSpan Elapsed(long ticks) => TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    /// <summary>
    /// Runs <paramref name="token"/> at the state's next position. When <paramref name="logits"/> is
    /// given (one value per vocabulary token) it receives the next-token logits; leave it empty for
    /// a prompt token whose logits nobody reads, and the head is not run.
    /// </summary>
    public void Forward(int token, Qwen35State state, Span<float> logits = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var c = Config;
        if ((uint)token >= (uint)c.Vocabulary) throw new ArgumentOutOfRangeException(nameof(token), token, $"The vocabulary holds {c.Vocabulary}.");
        if (state.Position >= state.Capacity) throw new InvalidOperationException($"The state holds {state.Capacity} tokens and is full.");
        if (logits.Length != 0 && logits.Length != c.Vocabulary) throw new ArgumentException($"Logits take {c.Vocabulary} values.", nameof(logits));

        var started = Stopwatch.GetTimestamp();
        Embed(token, _x);
        for (var il = 0; il < c.Layers; il++)
        {
            var layer = _layers[il];
            RmsNorm(_x, layer.AttnNorm, _h, c.RmsEps);
            if (c.IsRecurrent(il)) DeltaNet(il, layer, state);
            else Attention(il, layer, state);
            Add(_x, _mixOut);

            RmsNorm(_x, layer.PostNorm, _h, c.RmsEps);
            FeedForward(layer);
            Add(_x, _ffnOut);
        }
        state.Position++;

        if (logits.Length != 0)
        {
            var head = Stopwatch.GetTimestamp();
            RmsNorm(_x, _outputNorm, _h, c.RmsEps);
            _embedding.MatVec(Prepare(_h, _embedding.Input), logits, _pool);
            _tHead += Stopwatch.GetTimestamp() - head;
        }
        _tTotal += Stopwatch.GetTimestamp() - started;
        _tokens++;
    }

    // ---- the parts of a token ----

    // The embedding row, dequantised (weight * scale, as the fork's dequantize_row does) and turned
    // back: h = s * (H z).
    private void Embed(int token, Span<float> x)
    {
        var pq2 = Packing == GgmlType.PQ2_0;
        var blockBytes = pq2 ? Pq2.BlockBytes : Ptq1.BlockBytes;
        var blocks = Config.Embedding / Ptq1.BlockWeights;
        var rowBytes = blocks * blockBytes;
        var row = _embedding.Bytes.Slice(token * rowBytes, rowBytes);
        for (var b = 0; b < blocks; b++)
        {
            var block = row.Slice(b * blockBytes, blockBytes);
            float scale;
            if (pq2)
            {
                Pq2.DecodeScalar(block, _weights);
                scale = Pq2.Scale(block);
            }
            else
            {
                Ptq1.DecodeScalar(block, _weights);
                scale = Ptq1.Scale(block);
            }
            for (var i = 0; i < Ptq1.BlockWeights; i++) x[b * Ptq1.BlockWeights + i] = _weights[i] * scale;
        }
        if (_hadamard is { InvertsEmbedding: true }) _hadamard.InvertLookup(x);
    }

    private void DeltaNet(int il, Layer layer, Qwen35State state)
    {
        var c = Config;
        var lut = Tables(_h, layer.Qkv!.Input);
        Product(layer.Qkv, lut, _qkv);
        Product(layer.Z!, lut, _z);

        var started = Stopwatch.GetTimestamp();
        F32MatVec(layer.Beta!, _h, _beta); // beta and alpha read h unturned: they are not folded
        F32MatVec(layer.Alpha!, _h, _alpha);

        // The causal conv (ggml_ssm_conv): each channel's last kernel-1 inputs and this one, then silu.
        var window = c.ConvKernel - 1;
        var conv = state.Conv[il];
        var kernel = layer.ConvKernel!;
        for (var ch = 0; ch < c.ConvChannels; ch++)
        {
            var past = conv.AsSpan(ch * window, window);
            var taps = kernel.AsSpan(ch * c.ConvKernel, c.ConvKernel);
            var sum = 0f;
            for (var t = 0; t < window; t++) sum += past[t] * taps[t];
            sum += _qkv[ch] * taps[window];
            for (var t = 0; t + 1 < window; t++) past[t] = past[t + 1];
            if (window > 0) past[window - 1] = _qkv[ch];
            _conv[ch] = Silu(sum);
        }

        // q heads then k heads, adjacent and of one width: each L2-normalised.
        var hk = c.SsmState;
        for (var head = 0; head < 2 * c.SsmGroups; head++) L2Norm(_conv.AsSpan(head * hk, hk), c.RmsEps);

        // The delta rule and the gated norm, value head by value head, on the pool's threads.
        _deltaWork.Set(layer, state.Ssm[il]);
        try
        {
            if (_pool is null) _deltaWork.Execute(0, 1);
            else _pool.Run(_deltaWork);
        }
        finally
        {
            _deltaWork.Clear();
        }
        _tRecurrence += Stopwatch.GetTimestamp() - started;

        Product(layer.SsmOut!, Tables(_o, layer.SsmOut!.Input), _mixOut);
    }

    // One value head of a DeltaNet layer: the delta rule as the fork's fused op runs it with raw
    // gates, then the gated RMS norm - rms(o) * ssm_norm, times silu(z). Heads touch disjoint
    // parts of the state and of the output, so any thread may take any head; each head's sums run
    // in the same order whichever thread takes it.
    private unsafe void DeltaHead(Layer layer, float[] ssm, int h, float[] deltaBuffer)
    {
        var c = Config;
        var hk = c.SsmState;
        var group = h % c.SsmGroups;
        var beta = 1.0f / (1.0f + MathF.Exp(-_beta[h]));
        var x = _alpha[h] + layer.DtBias![h];
        var g = layer.A![h] * (x > 20.0f ? x : MathF.Log(1.0f + MathF.Exp(x)));
        var decay = MathF.Exp(g);
        var scale = 1.0f / MathF.Sqrt(hk);

        fixed (float* conv = _conv)
        fixed (float* states = ssm)
        fixed (float* delta = deltaBuffer)
        fixed (float* output = _o)
        {
            var q = conv + group * hk;
            var k = conv + (c.SsmGroups + group) * hk;
            var v = conv + 2 * c.SsmGroups * hk + h * hk;
            var m = states + (long)h * hk * hk; // row j of m is column j of the state
            var cells = hk * hk;
            for (var i = 0; i < cells; i++) m[i] *= decay;
            for (var j = 0; j < hk; j++) delta[j] = (v[j] - Dot(m + j * hk, k, hk)) * beta;
            for (var j = 0; j < hk; j++)
            {
                var row = m + j * hk;
                var d = delta[j];
                for (var i = 0; i < hk; i++) row[i] += k[i] * d;
            }
            var o = output + h * hk;
            for (var j = 0; j < hk; j++) o[j] = Dot(m + j * hk, q, hk) * scale;
        }

        var head = _o.AsSpan(h * hk, hk);
        RmsNorm(head, layer.SsmNorm!, head, c.RmsEps);
        var z = _z.AsSpan(h * hk, hk);
        for (var j = 0; j < hk; j++) head[j] = Silu(z[j]) * head[j];
    }

    private void Attention(int il, Layer layer, Qwen35State state)
    {
        var c = Config;
        var hd = c.HeadDim;
        var lut = Tables(_h, layer.Q!.Input);
        Product(layer.Q, lut, _qg);
        Product(layer.K!, lut, _k);
        Product(layer.V!, lut, _v);

        var started = Stopwatch.GetTimestamp();
        // attn_q gives each head [q | gate]; q and k are RMS-normalised per head.
        for (var h = 0; h < c.Heads; h++)
        {
            var q = _q.AsSpan(h * hd, hd);
            _qg.AsSpan(h * 2 * hd, hd).CopyTo(q);
            RmsNorm(q, layer.QNorm!, q, c.RmsEps);
            _qg.AsSpan(h * 2 * hd + hd, hd).CopyTo(_gate.AsSpan(h * hd, hd));
        }
        for (var g = 0; g < c.KvHeads; g++)
        {
            var k = _k.AsSpan(g * hd, hd);
            RmsNorm(k, layer.KNorm!, k, c.RmsEps);
        }

        var position = state.Position;
        RopeAngles(position);
        for (var h = 0; h < c.Heads; h++) Rope(_q.AsSpan(h * hd, hd));
        for (var g = 0; g < c.KvHeads; g++) Rope(_k.AsSpan(g * hd, hd));

        var count = position + 1;
        var scores = Scores(count);
        var scale = 1.0f / MathF.Sqrt(hd);
        var groupSize = c.Heads / c.KvHeads;
        if (state.Turbo[il] is { } turbo)
        {
            // A compressed mode: this token's keys and values join the cache as TurboQuant codes,
            // and each query head attends on the codes of its group's key/value head.
            turbo.Write(position, _k, _v);
            for (var h = 0; h < c.Heads; h++)
            {
                var g = h / groupSize;
                turbo.Scores(g, _q.AsSpan(h * hd, hd), count, scores);
                Weights(scores.AsSpan(0, count), scale);
                turbo.Mix(g, scores, count, _attn.AsSpan(h * hd, hd));
            }
            for (var i = 0; i < _attn.Length; i++) _attn[i] *= 1.0f / (1.0f + MathF.Exp(-_gate[i]));
            _tAttention += Stopwatch.GetTimestamp() - started;
            Product(layer.Output!, Tables(_attn, layer.Output!.Input), _mixOut);
            return;
        }

        // This token's keys and values join the cache in fp16, as the fork's default cache keeps them.
        var kvWidth = c.KvHeads * hd;
        var keys = state.Keys[il];
        var values = state.Values[il];
        for (var i = 0; i < kvWidth; i++)
        {
            keys[position * kvWidth + i] = (Half)_k[i];
            values[position * kvWidth + i] = (Half)_v[i];
        }

        // Each query head attends over every position so far through its group's key/value head.
        for (var h = 0; h < c.Heads; h++)
        {
            var g = h / groupSize;
            var q = _q.AsSpan(h * hd, hd);
            var max = float.NegativeInfinity;
            for (var t = 0; t < count; t++)
            {
                var key = keys.AsSpan(t * kvWidth + g * hd, hd);
                var s = 0f;
                for (var i = 0; i < hd; i++) s += q[i] * (float)key[i];
                s *= scale;
                scores[t] = s;
                if (s > max) max = s;
            }
            var sum = 0f;
            for (var t = 0; t < count; t++)
            {
                scores[t] = MathF.Exp(scores[t] - max);
                sum += scores[t];
            }
            var output = _attn.AsSpan(h * hd, hd);
            output.Clear();
            for (var t = 0; t < count; t++)
            {
                var weight = scores[t] / sum;
                var value = values.AsSpan(t * kvWidth + g * hd, hd);
                for (var i = 0; i < hd; i++) output[i] += weight * (float)value[i];
            }
        }

        for (var i = 0; i < _attn.Length; i++) _attn[i] *= 1.0f / (1.0f + MathF.Exp(-_gate[i]));
        _tAttention += Stopwatch.GetTimestamp() - started;
        Product(layer.Output!, Tables(_attn, layer.Output!.Input), _mixOut);
    }

    private void FeedForward(Layer layer)
    {
        var lut = Tables(_h, layer.FfnGate.Input);
        Product(layer.FfnGate, lut, _ffnGate);
        Product(layer.FfnUp, lut, _ffnUp);
        for (var i = 0; i < _ffnGate.Length; i++) _ffnGate[i] = Silu(_ffnGate[i]) * _ffnUp[i];
        Product(layer.FfnDown, Tables(_ffnGate, layer.FfnDown.Input), _ffnOut);
    }

    // An activation ready for the ternary products that read it: turned, quantised, tabled - the
    // tables built on the pool's threads.
    private InputTables Prepare(ReadOnlySpan<float> x, InputTransform? transform)
    {
        var tables = _inputs[x.Length];
        if (transform is null) x.CopyTo(tables.Turned);
        else transform.Apply(x, tables.Turned);
        tables.Build(_pool);
        return tables;
    }

    // Prepare, its time counted as the tables'.
    private InputTables Tables(ReadOnlySpan<float> x, InputTransform? transform)
    {
        var started = Stopwatch.GetTimestamp();
        var tables = Prepare(x, transform);
        _tTables += Stopwatch.GetTimestamp() - started;
        return tables;
    }

    // A ternary product, its time counted as the products'.
    private void Product(TernaryMatrix matrix, InputTables tables, Span<float> output)
    {
        var started = Stopwatch.GetTimestamp();
        matrix.MatVec(tables, output, _pool);
        _tProducts += Stopwatch.GetTimestamp() - started;
    }

    private float[] Scores(int count)
    {
        if (_scoresBuffer.Length < count) _scoresBuffer = new float[Math.Max(count, _scoresBuffer.Length * 2)];
        return _scoresBuffer;
    }

    private float[] _scoresBuffer = new float[256];

    // Scaled scores turned into attention weights, in place: softmax over them.
    private static void Weights(Span<float> scores, float scale)
    {
        var max = float.NegativeInfinity;
        for (var t = 0; t < scores.Length; t++)
        {
            scores[t] *= scale;
            if (scores[t] > max) max = scores[t];
        }
        var sum = 0f;
        for (var t = 0; t < scores.Length; t++)
        {
            scores[t] = MathF.Exp(scores[t] - max);
            sum += scores[t];
        }
        for (var t = 0; t < scores.Length; t++) scores[t] /= sum;
    }

    // ggml's rope cache: theta starts at the position and is multiplied by base^(-2/dims) per pair.
    private void RopeAngles(int position)
    {
        var theta = (float)position;
        var thetaScale = MathF.Pow(Config.RopeBase, -2.0f / Config.RopeDims);
        for (var i = 0; i < _cos.Length; i++)
        {
            _cos[i] = MathF.Cos(theta);
            _sin[i] = MathF.Sin(theta);
            theta *= thetaScale;
        }
    }

    // Rotate-half RoPE over the first rope dims: pair (i, i + dims/2).
    private void Rope(Span<float> x)
    {
        var half = Config.RopeDims / 2;
        for (var i = 0; i < half; i++)
        {
            var x0 = x[i];
            var x1 = x[i + half];
            x[i] = x0 * _cos[i] - x1 * _sin[i];
            x[i + half] = x0 * _sin[i] + x1 * _cos[i];
        }
    }

    // ---- the fork's elementwise ops ----

    // ggml rms_norm then the weight: the squares summed in double, the scale in float.
    private static void RmsNorm(ReadOnlySpan<float> x, float[] weight, Span<float> y, float eps)
    {
        double sum = 0;
        for (var i = 0; i < x.Length; i++) sum += x[i] * x[i];
        var mean = (float)(sum / x.Length);
        var scale = 1.0f / MathF.Sqrt(mean + eps);
        for (var i = 0; i < x.Length; i++) y[i] = x[i] * scale * weight[i];
    }

    // ggml l2_norm: the squares summed in double, the root taken in float, never below eps.
    private static void L2Norm(Span<float> x, float eps)
    {
        double sum = 0;
        for (var i = 0; i < x.Length; i++) sum += x[i] * x[i];
        var scale = 1.0f / MathF.Max(MathF.Sqrt((float)sum), eps);
        for (var i = 0; i < x.Length; i++) x[i] *= scale;
    }

    private static float Silu(float x) => x / (1.0f + MathF.Exp(-x));

    private static void Add(Span<float> x, ReadOnlySpan<float> y)
    {
        for (var i = 0; i < x.Length; i++) x[i] += y[i];
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    // The same sum through pointers: Mono checks every span index, and the delta rule takes
    // four of these passes over a 128x128 state per head.
    private static unsafe float Dot(float* a, float* b, int n)
    {
        var s = 0f;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    // A small F32 weight (rows of the input width) times x.
    private static void F32MatVec(float[] weight, ReadOnlySpan<float> x, Span<float> y)
    {
        for (var r = 0; r < y.Length; r++) y[r] = Dot(weight.AsSpan(r * x.Length, x.Length), x);
    }

    private static float[] Floats(GgufFile file, string name, int length)
    {
        var tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
        if (tensor.Type != GgmlType.F32) throw new NotSupportedException($"{name} is {tensor.Type}; it is read as F32.");
        if (tensor.Elements != length) throw new InvalidDataException($"{name} holds {tensor.Elements} values, not {length}.");
        return file.Floats(tensor).ToArray();
    }

    // One DeltaNet layer's value heads, handed out one at a time to whichever thread is free.
    private sealed class DeltaWork(Qwen35Model model) : WorkerPool.IWork
    {
        private Layer? _layer;
        private float[]? _ssm;
        private int _next;

        public void Set(Layer layer, float[] ssm)
        {
            _layer = layer;
            _ssm = ssm;
            _next = 0;
        }

        public void Clear()
        {
            _layer = null;
            _ssm = null;
        }

        public void Execute(int worker, int workers)
        {
            int h;
            while ((h = Interlocked.Increment(ref _next) - 1) < model.Config.SsmHeads)
                model.DeltaHead(_layer!, _ssm!, h, model._deltas[worker]);
        }
    }

    // ---- weights ----

    // One activation width's quantised form and lookup table for the file's packing: Q8_0 and a
    // Ptq1Lut for PTQ1_0; Q8_K and a Pq2Lut for PQ2_0 - or Q8_0 and a Pq2Lut at a width that is not
    // whole Q8_K blocks, as the fork falls back (ggml_cpu_pq2_needs_q8_0).
    private sealed class InputTables
    {
        private readonly Q8Activations? _q8;
        private readonly Q8KActivations? _q8k;
        private readonly Ptq1Lut? _ptq1;
        private readonly Pq2Lut? _pq2;

        public InputTables(int width, GgmlType packing)
        {
            Turned = new float[width];
            if (packing == GgmlType.PTQ1_0)
            {
                _q8 = new Q8Activations(width);
                _ptq1 = new Ptq1Lut(width);
                return;
            }
            if (width % Q8KActivations.BlockSize == 0) _q8k = new Q8KActivations(width);
            else _q8 = new Q8Activations(width);
            _pq2 = new Pq2Lut(width);
        }

        public float[] Turned { get; }

        public Ptq1Lut Ptq1 => _ptq1 ?? throw new InvalidOperationException("These tables are PQ2_0's.");

        public Pq2Lut Pq2 => _pq2 ?? throw new InvalidOperationException("These tables are PTQ1_0's.");

        // Quantises the turned activation and builds the tables, on the pool's threads when there is one.
        public void Build(WorkerPool? pool)
        {
            if (_q8k is not null)
            {
                _q8k.Quantize(Turned);
                if (pool is null) _pq2!.Build(_q8k);
                else _pq2!.Build(_q8k, pool);
                return;
            }
            _q8!.Quantize(Turned);
            if (_ptq1 is not null)
            {
                if (pool is null) _ptq1.Build(_q8);
                else _ptq1.Build(_q8, pool);
            }
            else if (pool is null) _pq2!.Build(_q8);
            else _pq2!.Build(_q8, pool);
        }
    }

    // A ternary weight, PTQ1_0 or PQ2_0: its bytes stay in the mapped file; its block scales are
    // decoded once.
    private sealed class TernaryMatrix
    {
        private readonly GgufFile _file;
        private readonly GgufTensor _tensor;

        public TernaryMatrix(GgufFile file, string name, PrismHadamard? hadamard, int columns, int rows, GgmlType packing)
        {
            _file = file;
            _tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
            if (_tensor.Type != packing)
                throw new NotSupportedException($"{name} is {_tensor.Type} in a {packing} model; one packing throughout is read.");
            if (_tensor.RowLength != columns || _tensor.Rows != rows)
                throw new InvalidDataException($"{name} is {_tensor.RowLength}x{_tensor.Rows}, not {columns}x{rows}.");
            Packing = packing;
            Scales = packing == GgmlType.PQ2_0 ? Pq2.BlockScales(file.Bytes(_tensor)) : Ptq1.BlockScales(file.Bytes(_tensor));
            Input = hadamard?.InputOf(name);
        }

        public GgmlType Packing { get; }

        public float[] Scales { get; }

        public InputTransform? Input { get; }

        public ReadOnlySpan<byte> Bytes => _file.Bytes(_tensor);

        public void MatVec(InputTables tables, Span<float> output, WorkerPool? pool)
        {
            if (Packing == GgmlType.PQ2_0)
            {
                if (pool is null) tables.Pq2.MatVec(Bytes, Scales, output);
                else tables.Pq2.MatVec(Bytes, Scales, output, pool);
                return;
            }
            if (pool is null) tables.Ptq1.MatVec(Bytes, Scales, output);
            else tables.Ptq1.MatVec(Bytes, Scales, output, pool);
        }
    }

    private sealed class Layer
    {
        public Layer(GgufFile file, PrismHadamard? hadamard, Qwen35Config c, int il, GgmlType packing)
        {
            string Name(string part) => $"blk.{il}.{part}";
            TernaryMatrix Ternary(string part, int columns, int rows) => new(file, Name(part), hadamard, columns, rows, packing);
            AttnNorm = Floats(file, Name("attn_norm.weight"), c.Embedding);
            PostNorm = Floats(file, Name("post_attention_norm.weight"), c.Embedding);
            FfnGate = Ternary("ffn_gate.weight", c.Embedding, c.FeedForward);
            FfnUp = Ternary("ffn_up.weight", c.Embedding, c.FeedForward);
            FfnDown = Ternary("ffn_down.weight", c.FeedForward, c.Embedding);

            if (c.IsRecurrent(il))
            {
                Qkv = Ternary("attn_qkv.weight", c.Embedding, c.ConvChannels);
                Z = Ternary("attn_gate.weight", c.Embedding, c.SsmInner);
                SsmOut = Ternary("ssm_out.weight", c.SsmInner, c.Embedding);
                Beta = Floats(file, Name("ssm_beta.weight"), c.Embedding * c.SsmHeads);
                Alpha = Floats(file, Name("ssm_alpha.weight"), c.Embedding * c.SsmHeads);
                ConvKernel = Floats(file, Name("ssm_conv1d.weight"), c.ConvKernel * c.ConvChannels);
                A = Floats(file, Name("ssm_a"), c.SsmHeads);
                DtBias = Floats(file, Name("ssm_dt.bias"), c.SsmHeads);
                SsmNorm = Floats(file, Name("ssm_norm.weight"), c.SsmState);
                if (!ReferenceEquals(Qkv.Input, Z.Input)) throw new NotSupportedException($"Layer {il}'s qkv and z read differently turned inputs.");
            }
            else
            {
                Q = Ternary("attn_q.weight", c.Embedding, 2 * c.Heads * c.HeadDim);
                K = Ternary("attn_k.weight", c.Embedding, c.KvHeads * c.HeadDim);
                V = Ternary("attn_v.weight", c.Embedding, c.KvHeads * c.HeadDim);
                Output = Ternary("attn_output.weight", c.Heads * c.HeadDim, c.Embedding);
                QNorm = Floats(file, Name("attn_q_norm.weight"), c.HeadDim);
                KNorm = Floats(file, Name("attn_k_norm.weight"), c.HeadDim);
                if (!ReferenceEquals(Q.Input, K.Input) || !ReferenceEquals(Q.Input, V.Input))
                    throw new NotSupportedException($"Layer {il}'s q, k and v read differently turned inputs.");
            }
            if (!ReferenceEquals(FfnGate.Input, FfnUp.Input)) throw new NotSupportedException($"Layer {il}'s ffn gate and up read differently turned inputs.");
        }

        public float[] AttnNorm { get; }
        public float[] PostNorm { get; }
        public TernaryMatrix FfnGate { get; }
        public TernaryMatrix FfnUp { get; }
        public TernaryMatrix FfnDown { get; }

        public TernaryMatrix? Qkv { get; }
        public TernaryMatrix? Z { get; }
        public TernaryMatrix? SsmOut { get; }
        public float[]? Beta { get; }
        public float[]? Alpha { get; }
        public float[]? ConvKernel { get; }
        public float[]? A { get; }
        public float[]? DtBias { get; }
        public float[]? SsmNorm { get; }

        public TernaryMatrix? Q { get; }
        public TernaryMatrix? K { get; }
        public TernaryMatrix? V { get; }
        public TernaryMatrix? Output { get; }
        public float[]? QNorm { get; }
        public float[]? KNorm { get; }
    }
}

/// <summary>Where the time of some <see cref="Qwen35Model.Forward"/> calls went.</summary>
/// <param name="Tokens">Forward calls counted.</param>
/// <param name="Total">All of it.</param>
/// <param name="Tables">Turning, quantising and tabling the activations the ternary products read.</param>
/// <param name="Products">The ternary products, but for the output head's.</param>
/// <param name="Recurrence">The DeltaNet layers' gates, conv, delta rule and gated norm.</param>
/// <param name="Attention">The full-attention layers' norms, RoPE, cache and attention.</param>
/// <param name="Head">The output head, its table included.</param>
public readonly record struct ForwardTimes(int Tokens, TimeSpan Total, TimeSpan Tables, TimeSpan Products,
    TimeSpan Recurrence, TimeSpan Attention, TimeSpan Head)
{
    /// <summary>Everything else: the embedding row, the layer norms, the residual adds, the feed-forward gate.</summary>
    public TimeSpan Rest => Total - Tables - Products - Recurrence - Attention - Head;

    /// <inheritdoc/>
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Tokens} tokens in {Total.TotalMilliseconds:F0} ms: products {Products.TotalMilliseconds:F0}, tables {Tables.TotalMilliseconds:F0}, recurrence {Recurrence.TotalMilliseconds:F0}, attention {Attention.TotalMilliseconds:F0}, head {Head.TotalMilliseconds:F0}, rest {Rest.TotalMilliseconds:F0}");
}
