// IvrLoopDetector.cs
//
// (3.3.0) Detect when an outbound call has landed in an IVR loop —
// repeating prompts, looping menus, the AI pressing the same digit
// over and over. Surfaces a verdict that the orchestrator can act on
// (escalate to a human, abandon, or try a different path).

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One observation in the IVR conversation.</summary>
/// <param name="Speech">Text heard from the IVR.</param>
/// <param name="DtmfPressed">Digits the AI sent in response, if any.</param>
/// <param name="At">When this round happened.</param>
public sealed record IvrRound(string Speech, string? DtmfPressed, DateTimeOffset At);

/// <summary>(3.3.0) Verdict on IVR navigation health.</summary>
/// <param name="IsLooping">True if the navigator looks stuck.</param>
/// <param name="LoopLength">Estimated length of the repeating cycle (number of rounds).</param>
/// <param name="Reason">Human-readable reason.</param>
public sealed record IvrLoopVerdict(bool IsLooping, int LoopLength, string Reason);

/// <summary>(3.3.0) Records IVR rounds and surfaces a loop verdict.</summary>
public sealed class IvrLoopDetector
{
    private readonly List<IvrRound> _rounds = new();
    private readonly int _maxRoundsToTrack;
    private readonly int _minRoundsForLoop;
    private readonly double _similarityThreshold;
    private readonly object _gate = new();

    public IvrLoopDetector(
        int    maxRoundsToTrack    = 32,
        int    minRoundsForLoop    = 2,
        double similarityThreshold = 0.85)
    {
        _maxRoundsToTrack    = maxRoundsToTrack;
        _minRoundsForLoop    = minRoundsForLoop;
        _similarityThreshold = similarityThreshold;
    }

    /// <summary>(3.3.0) Append one round and return the current verdict.</summary>
    public IvrLoopVerdict Observe(IvrRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        lock (_gate)
        {
            _rounds.Add(round);
            while (_rounds.Count > _maxRoundsToTrack)
            {
                _rounds.RemoveAt(0);
            }
            return Evaluate();
        }
    }

    /// <summary>(3.3.0) Current verdict without adding a new round.</summary>
    public IvrLoopVerdict CurrentVerdict()
    {
        lock (_gate) return Evaluate();
    }

    /// <summary>(3.3.0) Drop all history.</summary>
    public void Reset()
    {
        lock (_gate) _rounds.Clear();
    }

    private IvrLoopVerdict Evaluate()
    {
        // Strong signal first — same DTMF + similar prompt three times in a row.
        if (_rounds.Count >= 3)
        {
            var tail = _rounds.TakeLast(3).ToArray();
            if (tail.All(r => r.DtmfPressed == tail[0].DtmfPressed) &&
                tail.All(r => SimilarTo(r.Speech, tail[0].Speech)))
            {
                return new IvrLoopVerdict(true, 1, "Same prompt-and-press triple in a row.");
            }
        }

        if (_rounds.Count < _minRoundsForLoop * 2)
        {
            return new IvrLoopVerdict(false, 0, "Not enough rounds to evaluate.");
        }

        // Look for a repeating cycle of length L in the last N rounds.
        for (int L = _minRoundsForLoop; L <= _rounds.Count / 2; L++)
        {
            var tail = _rounds.Skip(_rounds.Count - 2 * L).ToArray();
            bool looped = true;
            for (int i = 0; i < L; i++)
            {
                if (!SimilarTo(tail[i].Speech, tail[L + i].Speech) ||
                    tail[i].DtmfPressed != tail[L + i].DtmfPressed)
                {
                    looped = false;
                    break;
                }
            }
            if (looped)
            {
                return new IvrLoopVerdict(true, L, $"Detected repeating cycle of length {L}.");
            }
        }
        return new IvrLoopVerdict(false, 0, "No loop detected.");
    }

    private bool SimilarTo(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a is null || b is null) return false;
        // Cheap Jaccard over word sets.
        var setA = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        if (setA.Count == 0 || setB.Count == 0) return false;
        var inter = setA.Count(w => setB.Contains(w));
        var union = setA.Union(setB, StringComparer.OrdinalIgnoreCase).Count();
        return (double)inter / union >= _similarityThreshold;
    }
}
