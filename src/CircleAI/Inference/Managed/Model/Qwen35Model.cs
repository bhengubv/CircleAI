// Qwen35Model.cs
//
// The qwen35 architecture - the whole Bonsai 2 fleet - run one token at a time in C#, the way
// the pinned fork's graph runs it (src/models/qwen35.cpp, delta-net-base.cpp, ggml-cpu ops):
//
//   x = the token's embedding row, turned back from the table's rotation - or, for an image token,
//       the vision tower's output as it comes (ForwardImageToken), placed by its row and column
//       through the h and w sections of the interleaved multi-section RoPE
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
    private readonly MtpHead? _mtp;            // a PQ2_0-MTP file's draft head

    // One turned buffer, one quantisation and one lookup table per product width.
    private readonly Dictionary<int, InputTables> _inputs = [];

    // Working buffers, sized once.
    private readonly float[] _x, _h, _qkv, _z, _conv, _o, _beta, _alpha;
    private readonly float[] _qg, _q, _k, _v, _gate, _attn, _cos, _sin;
    private readonly float[] _mixOut, _ffnGate, _ffnUp, _ffnOut;
    private readonly sbyte[] _weights = new sbyte[Ptq1.BlockWeights]; // one decoded embedding block (both packings hold 128)
    private readonly int[] _ropeSection; // per rotary pair: which of t, h, w turns it

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
        if (c.MtpLayers == 1) _mtp = new MtpHead(file, c, c.Layers);

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
        _ropeSection = new int[c.RopeDims / 2];
        for (var pair = 0; pair < _ropeSection.Length; pair++) _ropeSection[pair] = c.RopeSection(pair);
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

    /// <summary>Whether the file carries a multi-token-prediction head (Bonsai 2's PQ2_0-MTP files).</summary>
    public bool HasMtp => _mtp is not null;

    /// <summary>A new, empty key/value cache for the MTP head, up to <paramref name="capacity"/> positions.</summary>
    public MtpState NewMtpState(int capacity)
    {
        if (_mtp is null) throw new InvalidOperationException("This model has no MTP head.");
        return new MtpState(Config, Math.Min(capacity, Config.ContextLength));
    }

    /// <summary>
    /// Runs the MTP head at <paramref name="position"/> on <paramref name="token"/> and
    /// <paramref name="previous"/> - the h_nextn of the position before it, from the trunk (or, while
    /// drafting further ahead, from the head itself) - as the fork's qwen35 MTP graph does: the
    /// token's embedding and the hidden state normed and joined, eh_proj, one full-attention block
    /// over the head's own cache, its norm and the tied head. The head's keys and values at the
    /// position are written (or written again) into <paramref name="mtp"/>.
    /// </summary>
    /// <param name="token">The token at the position.</param>
    /// <param name="previous">The hidden state of the position before.</param>
    /// <param name="mtp">The head's cache.</param>
    /// <param name="position">Where the token is.</param>
    /// <param name="logits">When given, the head's logits: its guess at the token after the next.</param>
    /// <param name="hidden">When given, the head's own h_nextn, for drafting one further.</param>
    public void ForwardMtp(int token, ReadOnlySpan<float> previous, MtpState mtp, int position, Span<float> logits = default, Span<float> hidden = default)
    {
        var m = _mtp ?? throw new InvalidOperationException("This model has no MTP head.");
        ArgumentNullException.ThrowIfNull(mtp);
        var c = Config;
        var e = c.Embedding;
        if ((uint)token >= (uint)c.Vocabulary) throw new ArgumentOutOfRangeException(nameof(token), token, $"The vocabulary holds {c.Vocabulary}.");
        if (previous.Length != e) throw new ArgumentException($"The hidden state takes {e} values.", nameof(previous));
        if ((uint)position >= (uint)mtp.Capacity) throw new ArgumentOutOfRangeException(nameof(position), position, $"The MTP cache holds {mtp.Capacity}.");
        if (logits.Length != 0 && logits.Length != c.Vocabulary) throw new ArgumentException($"Logits take {c.Vocabulary} values.", nameof(logits));
        if (hidden.Length != 0 && hidden.Length != e) throw new ArgumentException($"The hidden state takes {e} values.", nameof(hidden));

        var started = Stopwatch.GetTimestamp();
        Embed(token, m.Embedded);
        RmsNorm(m.Embedded, m.ENorm, m.Concat.AsSpan(0, e), c.RmsEps);
        RmsNorm(previous, m.HNorm, m.Concat.AsSpan(e, e), c.RmsEps);
        m.Wide.Quantize(m.Concat);
        m.EhProj.MatVec(m.Wide, m.Cur, _pool);
        m.Cur.CopyTo(m.Residual, 0);

        RmsNorm(m.Cur, m.AttnNorm, m.Normed, c.RmsEps);
        m.Narrow.Quantize(m.Normed);
        m.Q.MatVec(m.Narrow, m.Qg, _pool);
        m.K.MatVec(m.Narrow, _k, _pool);
        m.V.MatVec(m.Narrow, _v, _pool);
        // The head drafts text: its rotary position is the cell's (a conversation with a picture in it
        // places text further on - the head's drafts there may miss, and the trunk decides).
        RopeAngles(position, position, position);
        QueriesAndKeys(m.Qg, m.QNorm, m.KNorm);
        AttendFp16(mtp.Keys, mtp.Values, position);
        m.Heads.Quantize(_attn);
        m.Output.MatVec(m.Heads, m.Out, _pool);
        for (var i = 0; i < e; i++) m.Cur[i] = m.Out[i] + m.Residual[i];

        m.Cur.CopyTo(m.Residual, 0);
        RmsNorm(m.Cur, m.PostNorm, m.Normed, c.RmsEps);
        m.Narrow.Quantize(m.Normed);
        m.FfnGate.MatVec(m.Narrow, m.Ffn, _pool);
        m.FfnUp.MatVec(m.Narrow, m.Up, _pool);
        for (var i = 0; i < m.Ffn.Length; i++) m.Ffn[i] = Silu(m.Ffn[i]) * m.Up[i];
        m.Hidden.Quantize(m.Ffn);
        m.FfnDown.MatVec(m.Hidden, m.Out, _pool);
        for (var i = 0; i < e; i++) m.Cur[i] = m.Out[i] + m.Residual[i];

        RmsNorm(m.Cur, m.HeadNorm ?? _outputNorm, _h, c.RmsEps);
        if (hidden.Length != 0) _h.CopyTo(hidden);
        if (logits.Length != 0) _embedding.MatVec(Prepare(_h, _embedding.Input), logits, _pool);
        _tMtp += Stopwatch.GetTimestamp() - started;
    }

    private long _tMtp;

    /// <summary>The time the MTP head has taken since the last call, and the count starts again.</summary>
    public TimeSpan TakeMtpTime()
    {
        var time = TimeSpan.FromSeconds((double)_tMtp / Stopwatch.Frequency);
        _tMtp = 0;
        return time;
    }

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
    /// a prompt token whose logits nobody reads, and the head is not run. When
    /// <paramref name="nextn"/> is given (one value per embedding dimension) it receives the final
    /// hidden state after the output norm - what the fork calls h_nextn, the MTP head's input.
    /// </summary>
    public void Forward(int token, Qwen35State state, Span<float> logits = default, Span<float> nextn = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var c = Config;
        if ((uint)token >= (uint)c.Vocabulary) throw new ArgumentOutOfRangeException(nameof(token), token, $"The vocabulary holds {c.Vocabulary}.");
        Check(state, logits);
        if (nextn.Length != 0 && nextn.Length != c.Embedding) throw new ArgumentException($"The hidden state takes {c.Embedding} values.", nameof(nextn));

        var started = Stopwatch.GetTimestamp();
        Embed(token, _x);
        var p = state.RopePosition;
        Step(state, p, p, p, logits, nextn, started);
        state.RopePosition = p + 1;
    }

    /// <summary>
    /// Runs token <paramref name="index"/> of an image - its tokens row by row over a grid of
    /// <paramref name="columns"/> x <paramref name="rows"/>, each <paramref name="embedding"/> taken
    /// as the input in place of a token's row - at the state's next position, placed as the fork
    /// places image tokens (mtmd_image_tokens_get_decoder_pos): from p, where the image starts,
    /// (t, h, w) = (p, p + row, p + column). After its last token the next text sits at
    /// p + max(columns, rows). Logits as <see cref="Forward(int, Qwen35State, Span{float}, Span{float})"/>.
    /// </summary>
    public void ForwardImageToken(ReadOnlySpan<float> embedding, int index, int columns, int rows, Qwen35State state, Span<float> logits = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var c = Config;
        if (embedding.Length != c.Embedding) throw new ArgumentException($"An image token is {c.Embedding} values, not {embedding.Length}.", nameof(embedding));
        if (columns <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(columns), $"A grid of {columns}x{rows} holds no tokens.");
        if ((uint)index >= (uint)(columns * rows)) throw new ArgumentOutOfRangeException(nameof(index), index, $"The image holds {columns * rows} tokens.");
        Check(state, logits);

        var started = Stopwatch.GetTimestamp();
        embedding.CopyTo(_x); // the primal basis already: the Hadamard turn belongs to the token table alone
        var p = state.RopePosition; // where the image starts, until its last token has run
        Step(state, p, p + index / columns, p + index % columns, logits, default, started);
        if (index == columns * rows - 1) state.RopePosition = p + Math.Max(columns, rows);
    }

    private void Check(Qwen35State state, Span<float> logits)
    {
        if (state.Position >= state.Capacity) throw new InvalidOperationException($"The state holds {state.Capacity} tokens and is full.");
        if (logits.Length != 0 && logits.Length != Config.Vocabulary) throw new ArgumentException($"Logits take {Config.Vocabulary} values.", nameof(logits));
    }

    // One position through every layer, its input in _x and its rotary position (t, h, w); then the
    // head when logits (or the MTP head's input, h_nextn) are wanted.
    private void Step(Qwen35State state, int t, int h, int w, Span<float> logits, Span<float> nextn, long started)
    {
        var c = Config;
        RopeAngles(t, h, w);
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

        if (logits.Length != 0 || nextn.Length != 0)
        {
            var head = Stopwatch.GetTimestamp();
            RmsNorm(_x, _outputNorm, _h, c.RmsEps);
            if (nextn.Length != 0) _h.CopyTo(nextn);
            if (logits.Length != 0) _embedding.MatVec(Prepare(_h, _embedding.Input), logits, _pool);
            _tHead += Stopwatch.GetTimestamp() - head;
        }
        _tTotal += Stopwatch.GetTimestamp() - started;
        _tokens++;
    }

    /// <summary>
    /// Runs <paramref name="tokens"/> at the state's next positions, as <see cref="Forward"/> would run
    /// them one after another, but layer by layer: every product reads its weights once for all of
    /// them. Each token's results equal its own Forward's to the last bit - the per-token steps are
    /// the same code in the same order, and the products sum each row as the single product does.
    /// </summary>
    /// <param name="tokens">The tokens, in order.</param>
    /// <param name="state">The state they extend.</param>
    /// <param name="logits">Per token: its next-token logits, or null when nobody reads them.</param>
    /// <param name="nextn">Per token: its h_nextn (see <see cref="Forward"/>), or null.</param>
    public void ForwardBatch(ReadOnlySpan<int> tokens, Qwen35State state, IReadOnlyList<float[]?> logits, IReadOnlyList<float[]?>? nextn = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logits);
        var c = Config;
        var n = tokens.Length;
        if (n == 0) return;
        if (logits.Count != n || (nextn is not null && nextn.Count != n)) throw new ArgumentException("One logits (and h) slot per token.");
        if (state.Position + n > state.Capacity) throw new InvalidOperationException($"The state holds {state.Capacity} tokens; {n} more do not fit.");
        foreach (var token in tokens)
            if ((uint)token >= (uint)c.Vocabulary) throw new ArgumentOutOfRangeException(nameof(tokens), token, $"The vocabulary holds {c.Vocabulary}.");

        var started = Stopwatch.GetTimestamp();
        var b = Batch(n);
        for (var i = 0; i < n; i++) Embed(tokens[i], b.X[i]);
        var position = state.Position;
        var rope = state.RopePosition; // text: each token one further, as Forward places it
        for (var il = 0; il < c.Layers; il++)
        {
            var layer = _layers[il];
            for (var i = 0; i < n; i++) RmsNorm(b.X[i], layer.AttnNorm, b.H[i], c.RmsEps);
            if (c.IsRecurrent(il))
            {
                Products(b.H, n, layer.Qkv!.Input, (layer.Qkv!, b.Qkv), (layer.Z!, b.Z));
                for (var i = 0; i < n; i++)
                {
                    b.H[i].CopyTo(_h, 0);
                    b.Qkv[i].CopyTo(_qkv, 0);
                    b.Z[i].CopyTo(_z, 0);
                    DeltaCore(il, layer, state);
                    _o.CopyTo(b.O[i], 0);
                }
                Products(b.O, n, layer.SsmOut!.Input, (layer.SsmOut!, b.Mix));
            }
            else
            {
                Products(b.H, n, layer.Q!.Input, (layer.Q!, b.Qg), (layer.K!, b.K), (layer.V!, b.V));
                for (var i = 0; i < n; i++)
                {
                    b.Qg[i].CopyTo(_qg, 0);
                    b.K[i].CopyTo(_k, 0);
                    b.V[i].CopyTo(_v, 0);
                    RopeAngles(rope + i, rope + i, rope + i);
                    AttentionCore(il, layer, state, position + i);
                    _attn.CopyTo(b.Attn[i], 0);
                }
                Products(b.Attn, n, layer.Output!.Input, (layer.Output!, b.Mix));
            }
            for (var i = 0; i < n; i++)
            {
                Add(b.X[i], b.Mix[i]);
                RmsNorm(b.X[i], layer.PostNorm, b.H[i], c.RmsEps);
            }
            Products(b.H, n, layer.FfnGate.Input, (layer.FfnGate, b.Gate), (layer.FfnUp, b.Up));
            for (var i = 0; i < n; i++)
            {
                var g = b.Gate[i];
                var u = b.Up[i];
                for (var j = 0; j < g.Length; j++) g[j] = Silu(g[j]) * u[j];
            }
            Products(b.Gate, n, layer.FfnDown.Input, (layer.FfnDown, b.Mix));
            for (var i = 0; i < n; i++) Add(b.X[i], b.Mix[i]);
        }
        state.Position += n;
        state.RopePosition = rope + n;

        var head = Stopwatch.GetTimestamp();
        var heads = 0;
        for (var i = 0; i < n; i++)
        {
            if (logits[i] is null && nextn?[i] is null) continue;
            RmsNorm(b.X[i], _outputNorm, b.H[i], c.RmsEps);
            if (nextn?[i] is { } h) b.H[i].CopyTo(h, 0);
            if (logits[i] is not null) b.Head[heads++] = i;
        }
        if (heads > 0)
        {
            var inputs = new float[heads][];
            var outputs = new float[heads][];
            for (var j = 0; j < heads; j++)
            {
                inputs[j] = b.H[b.Head[j]];
                outputs[j] = logits[b.Head[j]]!;
                if (outputs[j].Length != c.Vocabulary) throw new ArgumentException($"Logits take {c.Vocabulary} values.", nameof(logits));
            }
            Products(inputs, heads, _embedding.Input, (_embedding, outputs));
        }
        _tHead += Stopwatch.GetTimestamp() - head;
        _tTotal += Stopwatch.GetTimestamp() - started;
        _tokens += n;
    }

    // ---- the batch's buffers and products ----

    private BatchBuffers? _batch;

    private BatchBuffers Batch(int n)
    {
        if (_batch is null || _batch.Size < n) _batch = new BatchBuffers(Config, Math.Max(n, _batch?.Size ?? 0));
        return _batch;
    }

    // Per-token working buffers for ForwardBatch, sized for the largest batch seen.
    private sealed class BatchBuffers
    {
        public BatchBuffers(Qwen35Config c, int size)
        {
            Size = size;
            float[][] Rows(int width)
            {
                var rows = new float[size][];
                for (var i = 0; i < size; i++) rows[i] = new float[width];
                return rows;
            }
            X = Rows(c.Embedding);
            H = Rows(c.Embedding);
            Qkv = Rows(c.ConvChannels);
            Z = Rows(c.SsmInner);
            O = Rows(c.SsmInner);
            Qg = Rows(2 * c.Heads * c.HeadDim);
            K = Rows(c.KvHeads * c.HeadDim);
            V = Rows(c.KvHeads * c.HeadDim);
            Attn = Rows(c.Heads * c.HeadDim);
            Mix = Rows(c.Embedding);
            Gate = Rows(c.FeedForward);
            Up = Rows(c.FeedForward);
            Head = new int[size];
        }

        public int Size { get; }
        public float[][] X { get; }
        public float[][] H { get; }
        public float[][] Qkv { get; }
        public float[][] Z { get; }
        public float[][] O { get; }
        public float[][] Qg { get; }
        public float[][] K { get; }
        public float[][] V { get; }
        public float[][] Attn { get; }
        public float[][] Mix { get; }
        public float[][] Gate { get; }
        public float[][] Up { get; }
        public int[] Head { get; }
    }

    // The products of several weights that read the same activations, for each of the first n
    // inputs: each input turned and quantised once, its tables shared by every weight that reads it.
    // PQ2_0 builds one table per input and walks each weight once for all of them (Pq2BatchLut);
    // PTQ1_0 runs the inputs one after another through its own tables.
    private void Products(float[][] inputs, int n, InputTransform? transform, params (TernaryMatrix Matrix, float[][] Outputs)[] products)
    {
        var started = Stopwatch.GetTimestamp();
        if (Packing == GgmlType.PQ2_0 && n > 1 && _inputs.ContainsKey(inputs[0].Length))
        {
            var batch = BatchTables(inputs[0].Length, n);
            for (var i = 0; i < n; i++)
            {
                var tables = _inputs[inputs[i].Length];
                if (transform is null) inputs[i].AsSpan(0, tables.Turned.Length).CopyTo(tables.Turned);
                else transform.Apply(inputs[i], tables.Turned);
                batch.Quantize(i, tables.Turned);
            }
            batch.Build(n, _pool);
            _tTables += Stopwatch.GetTimestamp() - started;
            var multiplied = Stopwatch.GetTimestamp();
            foreach (var (matrix, outputs) in products) matrix.MatVec(batch, n, outputs, _pool);
            _tProducts += Stopwatch.GetTimestamp() - multiplied;
            return;
        }
        for (var i = 0; i < n; i++)
        {
            var tables = Tables(inputs[i], transform);
            foreach (var (matrix, outputs) in products) Product(matrix, tables, outputs[i]);
        }
    }

    private readonly Dictionary<int, Pq2BatchLut> _batchTables = [];

    private Pq2BatchLut BatchTables(int width, int n)
    {
        if (!_batchTables.TryGetValue(width, out var tables) || tables.Inputs < n)
            _batchTables[width] = tables = new Pq2BatchLut(width, Math.Max(n, tables?.Inputs ?? 0));
        return tables;
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
        var lut = Tables(_h, layer.Qkv!.Input);
        Product(layer.Qkv, lut, _qkv);
        Product(layer.Z!, lut, _z);
        DeltaCore(il, layer, state);
        Product(layer.SsmOut!, Tables(_o, layer.SsmOut!.Input), _mixOut);
    }

    // One token's step through a DeltaNet layer, between its products: beta and alpha from _h, the
    // conv over _qkv, the delta rule and the gated norm with _z, into _o. The batched forward runs
    // the same step for each of its tokens in turn.
    private void DeltaCore(int il, Layer layer, Qwen35State state)
    {
        var c = Config;
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
        var lut = Tables(_h, layer.Q!.Input);
        Product(layer.Q, lut, _qg);
        Product(layer.K!, lut, _k);
        Product(layer.V!, lut, _v);
        AttentionCore(il, layer, state, state.Position);
        Product(layer.Output!, Tables(_attn, layer.Output!.Input), _mixOut);
    }

    // One token's attention at <paramref name="position"/>, between its products: q [| gate], k and v
    // from _qg, _k, _v; norms, RoPE, this token's keys and values into the cache, attention over every
    // position up to it, the gate - into _attn.
    private void AttentionCore(int il, Layer layer, Qwen35State state, int position)
    {
        var c = Config;
        var hd = c.HeadDim;
        var started = Stopwatch.GetTimestamp();
        QueriesAndKeys(_qg, layer.QNorm!, layer.KNorm!);

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
            return;
        }

        AttendFp16(state.Keys[il], state.Values[il], position);
        _tAttention += Stopwatch.GetTimestamp() - started;
    }

    // attn_q gives each head [q | gate]: q into _q and the gate into _gate; q and k (in _k) are
    // RMS-normalised per head; RoPE turns both by the angles RopeAngles last set - the caller sets
    // them for this token's rotary position (Step once per token, ForwardBatch and the MTP head per
    // token), which an image's tokens make differ from the cell the keys go in.
    private void QueriesAndKeys(float[] qg, float[] qNorm, float[] kNorm)
    {
        var c = Config;
        var hd = c.HeadDim;
        for (var h = 0; h < c.Heads; h++)
        {
            var q = _q.AsSpan(h * hd, hd);
            qg.AsSpan(h * 2 * hd, hd).CopyTo(q);
            RmsNorm(q, qNorm, q, c.RmsEps);
            qg.AsSpan(h * 2 * hd + hd, hd).CopyTo(_gate.AsSpan(h * hd, hd));
        }
        for (var g = 0; g < c.KvHeads; g++)
        {
            var k = _k.AsSpan(g * hd, hd);
            RmsNorm(k, kNorm, k, c.RmsEps);
        }

        for (var h = 0; h < c.Heads; h++) Rope(_q.AsSpan(h * hd, hd));
        for (var g = 0; g < c.KvHeads; g++) Rope(_k.AsSpan(g * hd, hd));
    }

    // This token's keys and values (_k, _v) into an fp16 cache at the position, as the fork's default
    // cache keeps them; each query head (_q) attends over every position so far through its group's
    // key/value head; the result, times sigmoid of the gate, into _attn.
    private void AttendFp16(Half[] keys, Half[] values, int position)
    {
        var c = Config;
        var hd = c.HeadDim;
        var count = position + 1;
        var scores = Scores(count);
        var scale = 1.0f / MathF.Sqrt(hd);
        var groupSize = c.Heads / c.KvHeads;
        var kvWidth = c.KvHeads * hd;
        for (var i = 0; i < kvWidth; i++)
        {
            keys[position * kvWidth + i] = (Half)_k[i];
            values[position * kvWidth + i] = (Half)_v[i];
        }

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

    // ggml's IMROPE cache (ggml_mrope_cache_init, is_imrope): each component's angle starts at its
    // position and is multiplied by base^(-2/dims) pair by pair; each pair takes the angle of the
    // component its section names. For text t = h = w, so every pair's angle is the one rotate-half
    // RoPE gives - the same multiplications in the same order.
    private void RopeAngles(int t, int h, int w)
    {
        float byT = t, byH = h, byW = w;
        var thetaScale = MathF.Pow(Config.RopeBase, -2.0f / Config.RopeDims);
        for (var i = 0; i < _cos.Length; i++)
        {
            var theta = _ropeSection[i] switch { 1 => byH, 2 => byW, _ => byT };
            _cos[i] = MathF.Cos(theta);
            _sin[i] = MathF.Sin(theta);
            byT *= thetaScale;
            byH *= thetaScale;
            byW *= thetaScale;
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

        // The products with several PQ2_0 inputs at once, each equal to its own MatVec.
        public void MatVec(Pq2BatchLut tables, int n, float[][] outputs, WorkerPool? pool) =>
            tables.MatVec(Bytes, Scales, n, outputs, pool);
    }

    // A Q8_0 weight (the MTP head's block): its bytes stay in the mapped file; block scales decoded once.
    private sealed class Q8Matrix
    {
        private readonly GgufFile _file;
        private readonly GgufTensor _tensor;

        public Q8Matrix(GgufFile file, string name, int columns, int rows)
        {
            _file = file;
            _tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
            if (_tensor.Type != GgmlType.Q8_0) throw new NotSupportedException($"{name} is {_tensor.Type}; the MTP head is read as Q8_0.");
            if (_tensor.RowLength != columns || _tensor.Rows != rows)
                throw new InvalidDataException($"{name} is {_tensor.RowLength}x{_tensor.Rows}, not {columns}x{rows}.");
            Scales = Q8Weights.BlockScales(file.Bytes(_tensor));
        }

        public float[] Scales { get; }

        public void MatVec(Q8Activations x, Span<float> output, WorkerPool? pool) =>
            Q8Weights.MatVec(_file.Bytes(_tensor), Scales, x, output, pool);
    }

    // The multi-token-prediction head of a PQ2_0-MTP file (blk.{n_layer}): a full-attention block
    // with its own Q8_0 weights, fed [rms(embedding) * enorm | rms(h) * hnorm] through eh_proj, and
    // the trunk's tied head after its own norm - the fork's llama_model_qwen35::graph_mtp.
    private sealed class MtpHead
    {
        public MtpHead(GgufFile file, Qwen35Config c, int il)
        {
            string Name(string part) => $"blk.{il}.{part}";
            var e = c.Embedding;
            var qWidth = c.Heads * c.HeadDim;
            ENorm = Floats(file, Name("nextn.enorm.weight"), e);
            HNorm = Floats(file, Name("nextn.hnorm.weight"), e);
            EhProj = new Q8Matrix(file, Name("nextn.eh_proj.weight"), 2 * e, e);
            AttnNorm = Floats(file, Name("attn_norm.weight"), e);
            Q = new Q8Matrix(file, Name("attn_q.weight"), e, 2 * qWidth);
            K = new Q8Matrix(file, Name("attn_k.weight"), e, c.KvHeads * c.HeadDim);
            V = new Q8Matrix(file, Name("attn_v.weight"), e, c.KvHeads * c.HeadDim);
            Output = new Q8Matrix(file, Name("attn_output.weight"), qWidth, e);
            QNorm = Floats(file, Name("attn_q_norm.weight"), c.HeadDim);
            KNorm = Floats(file, Name("attn_k_norm.weight"), c.HeadDim);
            PostNorm = Floats(file, Name("post_attention_norm.weight"), e);
            FfnGate = new Q8Matrix(file, Name("ffn_gate.weight"), e, c.FeedForward);
            FfnUp = new Q8Matrix(file, Name("ffn_up.weight"), e, c.FeedForward);
            FfnDown = new Q8Matrix(file, Name("ffn_down.weight"), c.FeedForward, e);
            if (file.Tensor(Name("nextn.embed_tokens.weight")) is not null || file.Tensor(Name("nextn.shared_head_head.weight")) is not null)
                throw new NotSupportedException("An MTP head with its own embedding or output table is not read: the fleet's heads share the trunk's.");
            HeadNorm = file.Tensor(Name("nextn.shared_head_norm.weight")) is not null
                ? Floats(file, Name("nextn.shared_head_norm.weight"), e)
                : null;

            Concat = new float[2 * e];
            Cur = new float[e];
            Residual = new float[e];
            Normed = new float[e];
            Embedded = new float[e];
            Qg = new float[2 * qWidth];
            Ffn = new float[c.FeedForward];
            Up = new float[c.FeedForward];
            Out = new float[e];
            Wide = new Q8Activations(2 * e);
            Narrow = new Q8Activations(e);
            Heads = new Q8Activations(qWidth);
            Hidden = new Q8Activations(c.FeedForward);
        }

        public float[] ENorm { get; }
        public float[] HNorm { get; }
        public Q8Matrix EhProj { get; }
        public float[] AttnNorm { get; }
        public Q8Matrix Q { get; }
        public Q8Matrix K { get; }
        public Q8Matrix V { get; }
        public Q8Matrix Output { get; }
        public float[] QNorm { get; }
        public float[] KNorm { get; }
        public float[] PostNorm { get; }
        public Q8Matrix FfnGate { get; }
        public Q8Matrix FfnUp { get; }
        public Q8Matrix FfnDown { get; }
        public float[]? HeadNorm { get; }

        public float[] Concat { get; }
        public float[] Cur { get; }
        public float[] Residual { get; }
        public float[] Normed { get; }
        public float[] Embedded { get; }
        public float[] Qg { get; }
        public float[] Ffn { get; }
        public float[] Up { get; }
        public float[] Out { get; }
        public Q8Activations Wide { get; }
        public Q8Activations Narrow { get; }
        public Q8Activations Heads { get; }
        public Q8Activations Hidden { get; }
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
