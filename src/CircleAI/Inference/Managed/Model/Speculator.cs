// Speculator.cs
//
// LOSSLESS SPECULATIVE DECODING (S5): something cheap drafts the next few tokens, the trunk checks
// them all in one pass, and the reply keeps exactly the tokens plain decoding would have produced.
//
//   draft     the source proposes d1..dk after the token just drawn (the MTP head: each from the
//             one before and its own hidden state)
//   verify    the trunk runs [token, d1..dk] in one batched pass (Qwen35Model.ForwardBatch): the
//             logits at every position are bit for bit the ones plain decoding computes there
//   draw      the reply draws from those logits in order, through the very step plain decoding
//             uses (sampler, tool-call form, stops, length); a draft stays only while it is the
//             token drawn, and the first token drawn that differs is the next token
//   roll back the trunk ran past what it keeps: the Gated DeltaNet layers go back to a checkpoint
//             taken before the pass (Qwen35State.Checkpoint - its own copy, beside the turn's mark
//             and the primed start) and the kept tokens run again; the attention caches are
//             written again where they are read next
//
// Because every token is drawn by the same step from the same logits in the same order, the reply
// - greedy or sampled with a seed - is the plain reply, token for token. The drafts only decide
// how many tokens one pass of the trunk yields.

namespace CircleAI.Inference.Managed.Model;

/// <summary>Proposes the tokens after a token, for the trunk to check (the MTP head; a drafter model).</summary>
public interface IDraftSource
{
    /// <summary>
    /// The trunk has run (and keeps) <paramref name="tokens"/> at <paramref name="position"/> onward;
    /// <paramref name="hidden"/>[i] is its h_nextn after tokens[i].
    /// </summary>
    void Observe(ReadOnlySpan<int> tokens, int position, IReadOnlyList<float[]> hidden);

    /// <summary>
    /// Up to <paramref name="drafts"/>.Length proposed tokens after <paramref name="token"/>, which
    /// sits at <paramref name="position"/> and the trunk has not run yet. Returns how many it wrote.
    /// </summary>
    int Draft(int token, int position, Span<int> drafts);

    /// <summary>Forgets what it carried: the state it observed was replaced.</summary>
    void Forget();
}

/// <summary>Draft-and-verify decoding over a <see cref="Qwen35Model"/>, lossless by construction.</summary>
public sealed class Speculator
{
    private readonly Qwen35Model _model;
    private readonly IDraftSource _source;
    private readonly int[] _tokens;
    private readonly float[][] _logits;
    private readonly float[][] _hidden;
    private readonly float[] _one;

    /// <summary>Checks up to <paramref name="drafts"/> tokens from <paramref name="source"/> per pass of the trunk.</summary>
    public Speculator(Qwen35Model model, IDraftSource source, int drafts)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(drafts, 1);
        _model = model;
        _source = source;
        Drafts = drafts;
        var c = model.Config;
        _tokens = new int[drafts + 1];
        _logits = new float[drafts + 1][];
        _hidden = new float[drafts + 1][];
        for (var i = 0; i <= drafts; i++)
        {
            _logits[i] = new float[c.Vocabulary];
            _hidden[i] = new float[c.Embedding];
        }
        _one = new float[c.Embedding];
    }

    /// <summary>Tokens drafted per pass, at most.</summary>
    public int Drafts { get; }

    /// <summary>Drafts proposed since the counts were last taken.</summary>
    public long Drafted { get; private set; }

    /// <summary>Drafts the trunk kept since the counts were last taken.</summary>
    public long Accepted { get; private set; }

    /// <summary>The counts so far, which start again.</summary>
    public (long Drafted, long Accepted) TakeCounts()
    {
        var counts = (Drafted, Accepted);
        Drafted = Accepted = 0;
        return counts;
    }

    /// <summary>Forgets what the draft source carried, when the state was replaced.</summary>
    public void Forget() => _source.Forget();

    /// <summary>Runs one token on the trunk, as Forward does, and lets the draft source see it.</summary>
    public void Run(int token, Qwen35State state, Span<float> logits = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var position = state.Position;
        _model.Forward(token, state, logits, _one);
        _source.Observe([token], position, [_one]);
    }

    /// <summary>
    /// One draft-and-verify step from <paramref name="token"/>, which the reply has just drawn and the
    /// trunk has not run: the tokens the trunk keeps are added to <paramref name="history"/>, and
    /// <paramref name="step"/> - the reply's own drawing step - is called on each position's logits in
    /// order. Returns the next token drawn and whether the reply ended with it (then not run).
    /// </summary>
    public (int Token, bool End) Step(int token, Qwen35State state, List<int> history, Func<float[], (int Token, bool End)> step)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(step);
        var position = state.Position;
        var room = Math.Min(Drafts, state.Capacity - position - 1);
        var drafted = room > 0 ? _source.Draft(token, position, _tokens.AsSpan(1, room)) : 0;
        if (drafted <= 0)
        {
            Run(token, state, _logits[0]);
            history.Add(token);
            return step(_logits[0]);
        }

        var n = drafted + 1;
        _tokens[0] = token;
        state.Checkpoint();
        _model.ForwardBatch(_tokens.AsSpan(0, n), state, _logits.AsSpan(0, n).ToArray(), _hidden.AsSpan(0, n).ToArray());

        var at = 0;
        (int Token, bool End) drawn;
        while (true)
        {
            drawn = step(_logits[at]);
            if (!drawn.End && at < drafted && drawn.Token == _tokens[at + 1])
            {
                at++;
                continue;
            }
            break;
        }
        var kept = at + 1;
        Drafted += drafted;
        Accepted += at;

        if (kept < n)
        {
            // The trunk ran past what it keeps: back to the checkpoint, and the kept tokens again.
            state.RestoreCheckpoint();
            _model.ForwardBatch(_tokens.AsSpan(0, kept), state, new float[kept][], _hidden.AsSpan(0, kept).ToArray());
        }
        for (var i = 0; i < kept; i++) history.Add(_tokens[i]);
        _source.Observe(_tokens.AsSpan(0, kept), position, _hidden.AsSpan(0, kept).ToArray());
        return drawn;
    }
}

/// <summary>
/// The MTP head as a draft source: it keeps its own key/value cache over the conversation, filled from
/// the trunk's hidden states as the trunk runs (the fork's catch-up), and drafts each token from the
/// one before and its own hidden state.
/// </summary>
public sealed class MtpDrafter : IDraftSource
{
    private readonly Qwen35Model _model;
    private readonly MtpState _cache;
    private readonly float[] _last;      // the trunk's h_nextn at _lastAt
    private readonly float[] _logits;
    private float[][] _own;              // the head's own h_nextn per draft
    private int _lastAt = -1;
    private int _draftedAt = -1;         // where the last draft pass wrote the head's entry from the trunk's own state
    private int _draftedToken = -1;

    /// <summary>A drafter over <paramref name="model"/>'s MTP head, for up to <paramref name="capacity"/> positions.</summary>
    public MtpDrafter(Qwen35Model model, int capacity)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.HasMtp) throw new InvalidOperationException("This model has no MTP head.");
        _model = model;
        _cache = model.NewMtpState(capacity);
        var c = model.Config;
        _last = new float[c.Embedding];
        _logits = new float[c.Vocabulary];
        _own = [];
    }

    /// <inheritdoc/>
    public void Observe(ReadOnlySpan<int> tokens, int position, IReadOnlyList<float[]> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        for (var i = 0; i < tokens.Length; i++)
        {
            var at = position + i;
            if (at >= _cache.Capacity) break;
            // The head's entry here, from the token and the trunk's hidden state at the position
            // before - unless the last draft pass already wrote it from the very same two.
            if (i == 0 && at == _draftedAt && tokens[0] == _draftedToken) continue;
            _model.ForwardMtp(tokens[i], i > 0 ? hidden[i - 1] : Previous(at), _cache, at);
        }
        _draftedAt = -1;
        if (tokens.Length > 0)
        {
            hidden[tokens.Length - 1].CopyTo(_last, 0);
            _lastAt = position + tokens.Length - 1;
        }
    }

    /// <inheritdoc/>
    public int Draft(int token, int position, Span<int> drafts)
    {
        if (drafts.Length == 0 || position + drafts.Length > _cache.Capacity) return 0;
        if (_own.Length < drafts.Length)
        {
            _own = new float[drafts.Length][];
            for (var i = 0; i < _own.Length; i++) _own[i] = new float[_last.Length];
        }
        ReadOnlySpan<float> previous = Previous(position);
        for (var i = 0; i < drafts.Length; i++)
        {
            _model.ForwardMtp(i == 0 ? token : drafts[i - 1], previous, _cache, position + i, _logits, _own[i]);
            drafts[i] = ArgMax(_logits);
            previous = _own[i];
        }
        _draftedAt = position;
        _draftedToken = token;
        return drafts.Length;
    }

    /// <inheritdoc/>
    public void Forget()
    {
        _lastAt = -1;
        _draftedAt = -1;
    }

    // The trunk's hidden state at the position before, or zeros when it is not known (the fork's
    // carry starts at zeros).
    private float[] Previous(int position)
    {
        if (_lastAt != position - 1) Array.Clear(_last);
        return _last;
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }
}
