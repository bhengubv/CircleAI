// EndOfTurnDetectors.cs
//
// (3.3.0) End-of-turn detectors: null default, a rule-based detector
// using punctuation + trailing-silence heuristics, and a smart-turn
// wrapper that delegates to a host-supplied model runner.

using System;
using System.Linq;

namespace CircleAI.Speech;

/// <summary>(3.3.0) Always says "they finished" — DI default.</summary>
public sealed class NullEndOfTurnDetector : IEndOfTurnDetector
{
    public static readonly NullEndOfTurnDetector Instance = new();

    public string BackendId => "null";

    public EndOfTurnResult Predict(string partialTranscript, TimeSpan trailingSilence)
        => new(IsComplete: true, Confidence: 1f, WaitMoreMs: 0);

    public void Reset() { }
}

/// <summary>
/// (3.3.0) Rule-based detector. Considers a turn complete when the
/// transcript ends with terminal punctuation AND the user has been
/// silent for at least the minimum hangover, OR when silence exceeds
/// the maximum-wait ceiling regardless of text. Recognises common
/// "thinking" connectors (and, but, so, um, like…) to extend the wait
/// when present at the tail.
/// </summary>
public sealed class RuleBasedEndOfTurnDetector : IEndOfTurnDetector
{
    private static readonly string[] TerminalPunctuation = { ".", "!", "?", "。", "！", "？" };
    private static readonly string[] HangingWords =
    {
        "and", "but", "so", "or", "because", "if", "when", "while",
        "though", "however", "um", "uh", "like", "you", "the", "a", "an",
    };

    private readonly TimeSpan _minSilence;
    private readonly TimeSpan _hangingSilence;
    private readonly TimeSpan _maxSilence;

    public RuleBasedEndOfTurnDetector(
        TimeSpan? minSilence     = null,
        TimeSpan? hangingSilence = null,
        TimeSpan? maxSilence     = null)
    {
        _minSilence     = minSilence     ?? TimeSpan.FromMilliseconds(400);
        _hangingSilence = hangingSilence ?? TimeSpan.FromMilliseconds(900);
        _maxSilence     = maxSilence     ?? TimeSpan.FromMilliseconds(2500);
    }

    public string BackendId => "rules";

    public EndOfTurnResult Predict(string partialTranscript, TimeSpan trailingSilence)
    {
        var text = (partialTranscript ?? "").Trim();
        if (trailingSilence >= _maxSilence)
        {
            return new EndOfTurnResult(IsComplete: true, Confidence: 0.7f, WaitMoreMs: 0);
        }

        if (text.Length == 0)
        {
            return new EndOfTurnResult(IsComplete: false, Confidence: 0.2f,
                WaitMoreMs: (int)Math.Max(150, (_minSilence - trailingSilence).TotalMilliseconds));
        }

        var endsTerminal = TerminalPunctuation.Any(p => text.EndsWith(p, StringComparison.Ordinal));
        var lastWord = text.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var endsHanging = HangingWords.Contains(lastWord.TrimEnd('.', ',', '!', '?').ToLowerInvariant());

        if (endsHanging)
        {
            var remaining = _hangingSilence - trailingSilence;
            if (remaining <= TimeSpan.Zero)
            {
                return new EndOfTurnResult(IsComplete: true, Confidence: 0.6f, WaitMoreMs: 0);
            }
            return new EndOfTurnResult(IsComplete: false, Confidence: 0.4f,
                WaitMoreMs: (int)Math.Ceiling(remaining.TotalMilliseconds));
        }

        if (endsTerminal && trailingSilence >= _minSilence)
        {
            return new EndOfTurnResult(IsComplete: true, Confidence: 0.9f, WaitMoreMs: 0);
        }

        if (trailingSilence >= _minSilence)
        {
            return new EndOfTurnResult(IsComplete: true, Confidence: 0.75f, WaitMoreMs: 0);
        }

        var ms = (int)Math.Max(50, (_minSilence - trailingSilence).TotalMilliseconds);
        return new EndOfTurnResult(IsComplete: false, Confidence: 0.6f, WaitMoreMs: ms);
    }

    public void Reset() { }
}

/// <summary>(3.3.0) Host-supplied semantic turn model.</summary>
public interface ITurnModelRunner
{
    /// <summary>Score the current state; 0..1 = probability the turn is complete.</summary>
    float ScoreCompletion(string partialTranscript, TimeSpan trailingSilence);
}

/// <summary>
/// (3.3.0) Smart-turn wrapper. Uses the supplied semantic model when
/// present; otherwise falls back to <see cref="RuleBasedEndOfTurnDetector"/>.
/// </summary>
public sealed class SmartTurnDetector : IEndOfTurnDetector
{
    private readonly ITurnModelRunner? _runner;
    private readonly RuleBasedEndOfTurnDetector _fallback;
    private readonly float _threshold;

    public SmartTurnDetector(ITurnModelRunner? runner = null, float threshold = 0.5f)
    {
        _runner    = runner;
        _fallback  = new RuleBasedEndOfTurnDetector();
        _threshold = threshold;
    }

    public string BackendId => _runner is null ? "smart-turn (fallback)" : "smart-turn-v2";

    public EndOfTurnResult Predict(string partialTranscript, TimeSpan trailingSilence)
    {
        if (_runner is null)
        {
            return _fallback.Predict(partialTranscript, trailingSilence);
        }

        var prob = Math.Clamp(_runner.ScoreCompletion(partialTranscript, trailingSilence), 0f, 1f);
        if (prob >= _threshold)
        {
            return new EndOfTurnResult(IsComplete: true, Confidence: prob, WaitMoreMs: 0);
        }
        var waitMs = (int)Math.Round((1f - prob) * 1000f);
        return new EndOfTurnResult(IsComplete: false, Confidence: prob, WaitMoreMs: waitMs);
    }

    public void Reset() => _fallback.Reset();
}
