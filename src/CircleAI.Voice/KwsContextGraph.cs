#nullable enable

// KwsContextGraph.cs
//
// The keyword trie the beam search scores against — an Aho-Corasick automaton
// with a boost on every arc, ported structurally from sherpa-onnx's ContextGraph
// so the numbers come out the same rather than merely similar.
//
// WHY A GRAPH AND NOT AN INDEX PER KEYWORD. The obvious shape — one "how many
// tokens have I matched" counter per phrase — looks equivalent and is not, for
// two reasons that are the whole difference between spotting a keyword and
// hallucinating one:
//
//   THE BOOST IS A LOAN, NOT A GIFT. Advancing a token adds TokenScore. Falling
//   off the phrase subtracts everything accumulated so far (NodeScore of where
//   you land minus NodeScore of where you were, which from the root is exactly
//   minus what you were given). A counter that just resets to zero lets a path
//   collect a bonus for a wrong guess and keep it, so half-matches of the wrong
//   phrase float at the top of the beam forever. Repaying the loan is what makes
//   a phrase that ISN'T there fall out of the search.
//
//   COMPLETING PAYS A LUMP SUM. An end node carries OutputScore = its NodeScore,
//   so finishing a phrase hands back the whole boost a second time. That is what
//   pushes a genuine completion to the TOP of the beam — which matters because
//   detection is only ever tested on the leading hypothesis.
//
// FAIL LINKS handle the overlap that a per-keyword counter cannot: after "HEY B"
// fails on its second token, "B" may still be alive, and Aho-Corasick lands on
// that suffix in one step instead of losing it. OUTPUT LINKS catch a keyword
// that finishes inside a longer one.
//
// Scores default from the graph but every phrase may override its own boost and
// its own acceptance threshold, so a phrase that is hard to hear can be given
// more help without loosening the others.

using System;
using System.Collections.Generic;

namespace CircleAI.Voice;

/// <summary>One node of the keyword trie: a token position inside some phrase.</summary>
public sealed class KwsContextState
{
    /// <summary>The token that leads here. -1 marks the root.</summary>
    public int Token { get; internal set; } = -1;

    /// <summary>Boost added for taking this arc.</summary>
    public float TokenScore { get; internal set; }

    /// <summary>Total boost accumulated from the root to here.</summary>
    public float NodeScore { get; internal set; }

    /// <summary>Bonus paid on arrival if a phrase ends here (or ends in a suffix).</summary>
    public float OutputScore { get; internal set; }

    /// <summary>Depth — how many tokens of the phrase are matched at this node.</summary>
    public int Level { get; internal set; }

    /// <summary>Mean acoustic probability this phrase must reach to fire.</summary>
    public float AcThreshold { get; internal set; }

    /// <summary>True when a phrase finishes here.</summary>
    public bool IsEnd { get; internal set; }

    /// <summary>Phrase text, on an end node.</summary>
    public string Phrase { get; internal set; } = string.Empty;

    /// <summary>Phrase this node is a prefix of, for progress reporting.</summary>
    public string PrefixPhrase { get; internal set; } = string.Empty;

    /// <summary>Length of <see cref="PrefixPhrase"/> in tokens, for progress reporting.</summary>
    public int PrefixLength { get; internal set; }

    internal readonly Dictionary<int, KwsContextState> Next = new();

    /// <summary>Longest proper suffix of this node that is also in the trie.</summary>
    internal KwsContextState Fail = null!;

    /// <summary>Nearest end node reachable by following fail links.</summary>
    internal KwsContextState? Output;
}

/// <summary>
/// Aho-Corasick trie over keyword token sequences, scoring each arc.
/// </summary>
public sealed class KwsContextGraph
{
    private readonly KwsContextState _root = new();
    private readonly float _contextScore;
    private readonly float _acThreshold;

    /// <summary>Builds the graph.</summary>
    /// <param name="tokenIds">One token-id sequence per phrase.</param>
    /// <param name="contextScore">Default per-token boost.</param>
    /// <param name="acThreshold">Default acceptance threshold.</param>
    /// <param name="scores">Optional per-phrase boost override; 0 means "use the default".</param>
    /// <param name="phrases">Optional per-phrase text.</param>
    /// <param name="acThresholds">Optional per-phrase threshold override; 0 means "use the default".</param>
    public KwsContextGraph(
        IReadOnlyList<IReadOnlyList<int>> tokenIds,
        float contextScore,
        float acThreshold,
        IReadOnlyList<float>? scores = null,
        IReadOnlyList<string>? phrases = null,
        IReadOnlyList<float>? acThresholds = null)
    {
        _contextScore = contextScore;
        _acThreshold  = acThreshold;
        _root.Fail    = _root;
        Build(tokenIds, scores, phrases, acThresholds);
    }

    /// <summary>Where every hypothesis starts.</summary>
    public KwsContextState Root => _root;

    private void Build(
        IReadOnlyList<IReadOnlyList<int>> tokenIds,
        IReadOnlyList<float>? scores,
        IReadOnlyList<string>? phrases,
        IReadOnlyList<float>? acThresholds)
    {
        for (var i = 0; i < tokenIds.Count; i++)
        {
            var node = _root;

            // Zero means "unset" rather than "no boost" — the same convention the
            // keywords file uses, where an absent ":" simply leaves the default.
            var score = scores is null || scores.Count == 0 ? 0f : scores[i];
            if (score == 0f) score = _contextScore;

            var threshold = acThresholds is null || acThresholds.Count == 0 ? 0f : acThresholds[i];
            if (threshold == 0f) threshold = _acThreshold;

            var phrase = phrases is null || phrases.Count == 0 ? string.Empty : phrases[i];
            var length = tokenIds[i].Count;

            for (var j = 0; j < length; j++)
            {
                var token = tokenIds[i][j];
                var isEnd = j == length - 1;

                if (!node.Next.TryGetValue(token, out var child))
                {
                    child = new KwsContextState
                    {
                        Token       = token,
                        TokenScore  = score,
                        NodeScore   = node.NodeScore + score,
                        OutputScore = isEnd ? node.NodeScore + score : 0f,
                        Level       = j + 1,
                        AcThreshold = isEnd ? threshold : 0f,
                        IsEnd       = isEnd,
                        Phrase      = isEnd ? phrase : string.Empty,
                    };
                    node.Next[token] = child;
                }
                else
                {
                    // A shared prefix takes the most generous boost of the phrases
                    // through it, so adding a phrase can never make another one
                    // harder to hear.
                    child.TokenScore  = Math.Max(score, child.TokenScore);
                    child.NodeScore   = node.NodeScore + child.TokenScore;
                    child.IsEnd       = isEnd || child.IsEnd;
                    child.OutputScore = child.IsEnd ? child.NodeScore : 0f;
                    if (isEnd)
                    {
                        child.Phrase      = phrase;
                        child.AcThreshold = threshold;
                    }
                }

                if (child.PrefixPhrase.Length == 0)
                {
                    child.PrefixPhrase = phrase;
                    child.PrefixLength = length;
                }

                node = child;
            }
        }

        FillFailOutput();
    }

    /// <summary>
    /// Advances one token: returns the boost to apply, the node landed on, and the
    /// phrase completed here if any.
    /// </summary>
    /// <remarks>
    /// The returned score is a DELTA on the accumulated boost, and it goes
    /// negative when the token walks off the phrase — that clawback is the whole
    /// mechanism by which a wrong path stops looking attractive.
    /// </remarks>
    public (float Score, KwsContextState State, KwsContextState? Matched) ForwardOneStep(
        KwsContextState state, int token)
    {
        KwsContextState node;
        float score;

        if (state.Next.TryGetValue(token, out var direct))
        {
            node  = direct;
            score = node.TokenScore;
        }
        else
        {
            node = state.Fail;
            while (!node.Next.ContainsKey(token))
            {
                node = node.Fail;
                if (node.Token == -1) break;        // root: nowhere left to fall back to
            }
            if (node.Next.TryGetValue(token, out var viaFail)) node = viaFail;

            // Negative whenever we drop back — the accumulated bonus is repaid.
            score = node.NodeScore - state.NodeScore;
        }

        var matched = node.IsEnd ? node : node.Output;
        return (score + node.OutputScore, node, matched);
    }

    /// <summary>Is this state the end of a phrase — directly, or via a suffix?</summary>
    public (bool Matched, KwsContextState? State) IsMatched(KwsContextState state) =>
        state.IsEnd ? (true, state)
        : state.Output is not null ? (true, state.Output)
        : (false, null);

    /// <summary>Breadth-first construction of the fail and output links.</summary>
    private void FillFailOutput()
    {
        var queue = new Queue<KwsContextState>();
        foreach (var child in _root.Next.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (token, child) in current.Next)
            {
                var fail = current.Fail;
                if (fail.Next.TryGetValue(token, out var direct)) fail = direct;
                else
                {
                    fail = fail.Fail;
                    while (!fail.Next.ContainsKey(token))
                    {
                        fail = fail.Fail;
                        if (fail.Token == -1) break;
                    }
                    if (fail.Next.TryGetValue(token, out var viaFail)) fail = viaFail;
                }
                child.Fail = fail;

                // Walk the suffix chain to the nearest phrase end, so a keyword
                // finishing INSIDE a longer one is not swallowed by it.
                var output = fail;
                while (!output.IsEnd)
                {
                    output = output.Fail;
                    if (output.Token == -1) { output = null!; break; }
                }
                child.Output = output;
                child.OutputScore += output?.OutputScore ?? 0f;

                queue.Enqueue(child);
            }
        }
    }
}
