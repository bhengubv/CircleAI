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
// Every ternary product's input is turned first (PrismHadamard) and quantised to Q8_0, as the
// fork does; the products that read one activation share one lookup table. Norms, gates and the
// recurrence repeat the fork's float operations; the attention and the products sum in their
// own order, so the logits agree with the fork's to within rounding, not to the bit.
//
// One Forward at a time per model: it works in buffers it owns.

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

    // One turned buffer, one Q8 quantisation and one lookup table per product width.
    private readonly Dictionary<int, (float[] Turned, Q8Activations Q8, Ptq1Lut Lut)> _inputs = [];

    // Working buffers, sized once.
    private readonly float[] _x, _h, _qkv, _z, _conv, _delta, _o, _beta, _alpha;
    private readonly float[] _qg, _q, _k, _v, _gate, _attn, _cos, _sin;
    private readonly float[] _mixOut, _ffnGate, _ffnUp, _ffnOut;
    private readonly sbyte[] _trits = new sbyte[Ptq1.BlockWeights];

    private Qwen35Model(GgufFile file, WorkerPool? pool)
    {
        Config = Qwen35Config.FromGguf(file);
        var c = Config;
        _hadamard = PrismHadamard.FromGguf(file);
        _pool = pool;

        if (file.Tensor("output.weight") is not null) throw new NotSupportedException("An untied output head is not read in this pass.");
        _embedding = new TernaryMatrix(file, "token_embd.weight", _hadamard, c.Embedding, c.Vocabulary);
        _outputNorm = Floats(file, "output_norm.weight", c.Embedding);

        _layers = new Layer[c.Layers];
        for (var il = 0; il < c.Layers; il++) _layers[il] = new Layer(file, _hadamard, c, il);

        foreach (var width in new[] { c.Embedding, c.SsmInner, c.Heads * c.HeadDim, c.FeedForward }.Distinct())
            _inputs[width] = (new float[width], new Q8Activations(width), new Ptq1Lut(width));

        _x = new float[c.Embedding];
        _h = new float[c.Embedding];
        _qkv = new float[c.ConvChannels];
        _z = new float[c.SsmInner];
        _conv = new float[c.ConvChannels];
        _delta = new float[c.SsmHeadDim];
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
    }

    /// <summary>The model's shape.</summary>
    public Qwen35Config Config { get; }

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
    public Qwen35State NewState(int capacity) => new(Config, Math.Min(capacity, Config.ContextLength));

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

        if (logits.Length == 0) return;
        RmsNorm(_x, _outputNorm, _h, c.RmsEps);
        _embedding.MatVec(Prepare(_h, _embedding.Input), logits, _pool);
    }

    // ---- the parts of a token ----

    // The embedding row, dequantised (trit * scale) and turned back: h = s * (H z).
    private void Embed(int token, Span<float> x)
    {
        var rowBytes = Config.Embedding / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var row = _embedding.Bytes.Slice(token * rowBytes, rowBytes);
        for (var b = 0; b < Config.Embedding / Ptq1.BlockWeights; b++)
        {
            var block = row.Slice(b * Ptq1.BlockBytes, Ptq1.BlockBytes);
            Ptq1.DecodeScalar(block, _trits);
            var scale = Ptq1.Scale(block);
            for (var i = 0; i < Ptq1.BlockWeights; i++) x[b * Ptq1.BlockWeights + i] = _trits[i] * scale;
        }
        if (_hadamard is { InvertsEmbedding: true }) _hadamard.InvertLookup(x);
    }

    private void DeltaNet(int il, Layer layer, Qwen35State state)
    {
        var c = Config;
        var lut = Prepare(_h, layer.Qkv!.Input);
        layer.Qkv.MatVec(lut, _qkv, _pool);
        layer.Z!.MatVec(lut, _z, _pool);
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

        // The delta rule per value head, as the fork's fused op runs it with raw gates.
        var ssm = state.Ssm[il];
        var scale = 1.0f / MathF.Sqrt(hk);
        for (var h = 0; h < c.SsmHeads; h++)
        {
            var group = h % c.SsmGroups;
            var q = _conv.AsSpan(group * hk, hk);
            var k = _conv.AsSpan((c.SsmGroups + group) * hk, hk);
            var v = _conv.AsSpan(2 * c.SsmGroups * hk + h * hk, hk);
            var beta = 1.0f / (1.0f + MathF.Exp(-_beta[h]));
            var x = _alpha[h] + layer.DtBias![h];
            var g = layer.A![h] * (x > 20.0f ? x : MathF.Log(1.0f + MathF.Exp(x)));

            var m = ssm.AsSpan(h * hk * hk, hk * hk); // row j of m is column j of the state
            var decay = MathF.Exp(g);
            for (var i = 0; i < m.Length; i++) m[i] *= decay;
            for (var j = 0; j < hk; j++) _delta[j] = (v[j] - Dot(m.Slice(j * hk, hk), k)) * beta;
            for (var j = 0; j < hk; j++)
            {
                var row = m.Slice(j * hk, hk);
                var d = _delta[j];
                for (var i = 0; i < hk; i++) row[i] += k[i] * d;
            }
            for (var j = 0; j < hk; j++) _o[h * hk + j] = Dot(m.Slice(j * hk, hk), q) * scale;
        }

        // Gated RMS norm per value head: rms(o) * ssm_norm, times silu(z).
        for (var h = 0; h < c.SsmHeads; h++)
        {
            var o = _o.AsSpan(h * hk, hk);
            RmsNorm(o, layer.SsmNorm!, o, c.RmsEps);
            var z = _z.AsSpan(h * hk, hk);
            for (var j = 0; j < hk; j++) o[j] = Silu(z[j]) * o[j];
        }

        layer.SsmOut!.MatVec(Prepare(_o, layer.SsmOut.Input), _mixOut, _pool);
    }

    private void Attention(int il, Layer layer, Qwen35State state)
    {
        var c = Config;
        var hd = c.HeadDim;
        var lut = Prepare(_h, layer.Q!.Input);
        layer.Q.MatVec(lut, _qg, _pool);
        layer.K!.MatVec(lut, _k, _pool);
        layer.V!.MatVec(lut, _v, _pool);

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
        var count = position + 1;
        var scores = Scores(count);
        var scale = 1.0f / MathF.Sqrt(hd);
        var groupSize = c.Heads / c.KvHeads;
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
        layer.Output!.MatVec(Prepare(_attn, layer.Output.Input), _mixOut, _pool);
    }

    private void FeedForward(Layer layer)
    {
        var lut = Prepare(_h, layer.FfnGate.Input);
        layer.FfnGate.MatVec(lut, _ffnGate, _pool);
        layer.FfnUp.MatVec(lut, _ffnUp, _pool);
        for (var i = 0; i < _ffnGate.Length; i++) _ffnGate[i] = Silu(_ffnGate[i]) * _ffnUp[i];
        layer.FfnDown.MatVec(Prepare(_ffnGate, layer.FfnDown.Input), _ffnOut, _pool);
    }

    // An activation ready for the ternary products that read it: turned, quantised, tabled.
    private Ptq1Lut Prepare(ReadOnlySpan<float> x, InputTransform? transform)
    {
        var (turned, q8, lut) = _inputs[x.Length];
        if (transform is null) x.CopyTo(turned);
        else transform.Apply(x, turned);
        q8.Quantize(turned);
        lut.Build(q8);
        return lut;
    }

    private float[] Scores(int count)
    {
        if (_scoresBuffer.Length < count) _scoresBuffer = new float[Math.Max(count, _scoresBuffer.Length * 2)];
        return _scoresBuffer;
    }

    private float[] _scoresBuffer = new float[256];

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

    // ---- weights ----

    // A PTQ1_0 weight: its bytes stay in the mapped file; its block scales are decoded once.
    private sealed class TernaryMatrix
    {
        private readonly GgufFile _file;
        private readonly GgufTensor _tensor;

        public TernaryMatrix(GgufFile file, string name, PrismHadamard? hadamard, int columns, int rows)
        {
            _file = file;
            _tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
            if (_tensor.Type != GgmlType.PTQ1_0) throw new NotSupportedException($"{name} is {_tensor.Type}; only PTQ1_0 is read in this pass.");
            if (_tensor.RowLength != columns || _tensor.Rows != rows)
                throw new InvalidDataException($"{name} is {_tensor.RowLength}x{_tensor.Rows}, not {columns}x{rows}.");
            Scales = Ptq1.BlockScales(file.Bytes(_tensor));
            Input = hadamard?.InputOf(name);
        }

        public float[] Scales { get; }

        public InputTransform? Input { get; }

        public ReadOnlySpan<byte> Bytes => _file.Bytes(_tensor);

        public void MatVec(Ptq1Lut lut, Span<float> output, WorkerPool? pool)
        {
            if (pool is null) lut.MatVec(Bytes, Scales, output);
            else lut.MatVec(Bytes, Scales, output, pool);
        }
    }

    private sealed class Layer
    {
        public Layer(GgufFile file, PrismHadamard? hadamard, Qwen35Config c, int il)
        {
            string Name(string part) => $"blk.{il}.{part}";
            AttnNorm = Floats(file, Name("attn_norm.weight"), c.Embedding);
            PostNorm = Floats(file, Name("post_attention_norm.weight"), c.Embedding);
            FfnGate = new TernaryMatrix(file, Name("ffn_gate.weight"), hadamard, c.Embedding, c.FeedForward);
            FfnUp = new TernaryMatrix(file, Name("ffn_up.weight"), hadamard, c.Embedding, c.FeedForward);
            FfnDown = new TernaryMatrix(file, Name("ffn_down.weight"), hadamard, c.FeedForward, c.Embedding);

            if (c.IsRecurrent(il))
            {
                Qkv = new TernaryMatrix(file, Name("attn_qkv.weight"), hadamard, c.Embedding, c.ConvChannels);
                Z = new TernaryMatrix(file, Name("attn_gate.weight"), hadamard, c.Embedding, c.SsmInner);
                SsmOut = new TernaryMatrix(file, Name("ssm_out.weight"), hadamard, c.SsmInner, c.Embedding);
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
                Q = new TernaryMatrix(file, Name("attn_q.weight"), hadamard, c.Embedding, 2 * c.Heads * c.HeadDim);
                K = new TernaryMatrix(file, Name("attn_k.weight"), hadamard, c.Embedding, c.KvHeads * c.HeadDim);
                V = new TernaryMatrix(file, Name("attn_v.weight"), hadamard, c.Embedding, c.KvHeads * c.HeadDim);
                Output = new TernaryMatrix(file, Name("attn_output.weight"), hadamard, c.Heads * c.HeadDim, c.Embedding);
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
