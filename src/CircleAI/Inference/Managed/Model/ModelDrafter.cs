// ModelDrafter.cs
//
// A SMALLER MODEL OF THE SAME VOCABULARY DRAFTS FOR A BIGGER ONE (the 2B for the 27B). For a trunk whose layers stream
// from flash every word costs a whole pass over its weights - 6.5 GB, about 33 s on the P30 - and checking five drafted
// tokens costs that same pass. The drafter keeps its own state over the conversation, drafts greedily from it, and after
// each check steps back to exactly the tokens the trunk kept; the reply is still the trunk's, token for token
// (Speculator), the drafts only decide how many tokens one pass yields.
//
// It reads tokens, not the trunk's hidden states, so the trunk's prompt goes in whole and the drafter catches up on it
// on its own (Sync): from the longest start it shares with the history - its primed start, when the system turn is the
// same - so a new question costs it the question, not the conversation.

namespace CircleAI.Inference.Managed.Model;

/// <summary>A smaller model with the trunk's vocabulary as a draft source, with its own state over the conversation.</summary>
public sealed class ModelDrafter : IDraftSource
{
    private const int CatchUpTogether = 64; // tokens per batch while catching up

    private readonly Qwen35Model _model;
    private readonly Qwen35State _state;
    private readonly List<int> _seen = [];  // the tokens its state holds, in order
    private readonly float[] _logits;
    private int _draftedFrom = -1;          // where the last drafts started: its state is checkpointed there

    /// <summary>
    /// A drafter over <paramref name="model"/>, for up to <paramref name="capacity"/> positions, its attention layers holding
    /// their keys and values as <paramref name="kvMode"/> says.
    /// </summary>
    public ModelDrafter(Qwen35Model model, int capacity,
        CircleAI.Core.KvCompressionMode kvMode = CircleAI.Core.KvCompressionMode.Off,
        CircleAI.Core.Compression.TurboQuantForm keyForm = CircleAI.Core.Compression.TurboQuantForm.Mse)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _state = model.NewState(capacity, kvMode, keyForm);
        _logits = new float[model.Config.Vocabulary];
    }

    /// <summary>The model that drafts.</summary>
    public Qwen35Model Model => _model;

    /// <summary>Tokens its state holds.</summary>
    public int Position => _seen.Count;

    /// <inheritdoc/>
    public bool NeedsHidden => false;

    /// <inheritdoc/>
    public void Observe(ReadOnlySpan<int> tokens, int position, IReadOnlyList<float[]> hidden)
    {
        StepBack();
        if (position != _seen.Count)
        {
            // Out of step with the trunk: nothing is drafted until it catches up again (Sync).
            Forget();
            return;
        }
        Run(tokens);
    }

    /// <inheritdoc/>
    public int Draft(int token, int position, Span<int> drafts)
    {
        StepBack();
        if (drafts.Length == 0 || position != _seen.Count || position + drafts.Length > _state.Capacity) return 0;
        _state.Checkpoint();
        _draftedFrom = position;
        var next = token;
        for (var i = 0; i < drafts.Length; i++)
        {
            _model.Forward(next, _state, _logits);
            _seen.Add(next);
            next = drafts[i] = ArgMax(_logits);
        }
        return drafts.Length;
    }

    /// <inheritdoc/>
    public void Forget()
    {
        _draftedFrom = -1;
        _state.Reset();
        _seen.Clear();
    }

    /// <summary>
    /// Brings its state to hold exactly <paramref name="history"/>: from the longest start the two share - all it holds,
    /// its primed start, or nothing - it runs the rest, and marks its primed start where the system turn ends
    /// (<paramref name="primedAt"/>, or -1).
    /// </summary>
    public void Sync(IReadOnlyList<int> history, int primedAt)
    {
        ArgumentNullException.ThrowIfNull(history);
        StepBack();
        var shared = 0;
        while (shared < _seen.Count && shared < history.Count && _seen[shared] == history[shared]) shared++;
        if (shared < _seen.Count)
        {
            if (_state.PrimedPosition >= 0 && _state.PrimedPosition <= shared)
            {
                _state.RewindToPrimed();
                _seen.RemoveRange(_state.Position, _seen.Count - _state.Position);
            }
            else
            {
                Forget();
            }
        }
        if (history.Count > _state.Capacity) return; // it cannot hold the conversation: it drafts nothing (Draft refuses)

        var at = _seen.Count;
        if (primedAt > at && primedAt <= history.Count)
        {
            RunRange(history, at, primedAt);
            _state.MarkPrimed();
            at = primedAt;
        }
        RunRange(history, at, history.Count);
    }

    // The drafts went past what the trunk kept: back to where they started.
    private void StepBack()
    {
        if (_draftedFrom < 0) return;
        _state.RestoreCheckpoint();
        _seen.RemoveRange(_draftedFrom, _seen.Count - _draftedFrom);
        _draftedFrom = -1;
    }

    private void RunRange(IReadOnlyList<int> history, int from, int to)
    {
        var tokens = new int[Math.Min(CatchUpTogether, Math.Max(0, to - from))];
        for (var at = from; at < to; at += tokens.Length)
        {
            var count = Math.Min(tokens.Length, to - at);
            for (var i = 0; i < count; i++) tokens[i] = history[at + i];
            Run(tokens.AsSpan(0, count));
        }
    }

    // Tokens through the drafter, no logits wanted.
    private void Run(ReadOnlySpan<int> tokens)
    {
        if (tokens.Length == 0) return;
        if (_seen.Count + tokens.Length > _state.Capacity) throw new InvalidOperationException("The drafter's state is full.");
        if (tokens.Length == 1) _model.Forward(tokens[0], _state);
        else _model.ForwardBatch(tokens, _state, new float[tokens.Length][]);
        foreach (var token in tokens) _seen.Add(token);
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
