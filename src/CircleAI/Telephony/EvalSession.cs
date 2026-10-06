// EvalSession.cs
//
// (3.3.0) Drive an end-to-end voice-pipeline test against a real LLM
// without needing a carrier minute. The harness feeds a scripted
// conversation (user utterances) through the same pipeline production
// uses, then collects everything the AI said back for assertion.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One scripted turn from a fake caller.</summary>
/// <param name="UserTranscript">What the caller said (already-transcribed).</param>
/// <param name="ExpectedKeywords">Optional keywords the AI's response should include.</param>
public sealed record EvalTurn(string UserTranscript, IReadOnlyList<string>? ExpectedKeywords = null);

/// <summary>(3.3.0) Outcome of one eval turn.</summary>
public sealed record EvalTurnResult(
    string                AssistantResponse,
    IReadOnlyList<string> MissingKeywords,
    TimeSpan              Latency);

/// <summary>(3.3.0) Overall eval result.</summary>
public sealed record EvalRunResult(
    IReadOnlyList<EvalTurnResult> Turns,
    bool                          AllKeywordsHit,
    TimeSpan                      TotalLatency);

/// <summary>(3.3.0) Function that runs one turn through the AI under test.</summary>
public delegate Task<string> EvalTurnHandler(string userTranscript, CancellationToken ct);

/// <summary>(3.3.0) Drives an EvalSession against a real LLM-based handler.</summary>
public sealed class EvalSession
{
    private readonly EvalTurnHandler _handler;

    public EvalSession(EvalTurnHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>(3.3.0) Run the script and assemble results.</summary>
    public async Task<EvalRunResult> RunAsync(
        IReadOnlyList<EvalTurn> script,
        CancellationToken       ct = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var results = new List<EvalTurnResult>(script.Count);
        var total   = TimeSpan.Zero;
        bool allHit = true;
        foreach (var turn in script)
        {
            var started = DateTime.UtcNow;
            var response = await _handler(turn.UserTranscript, ct).ConfigureAwait(false);
            var elapsed  = DateTime.UtcNow - started;
            total += elapsed;

            var missing = new List<string>();
            if (turn.ExpectedKeywords is not null)
            {
                foreach (var kw in turn.ExpectedKeywords)
                {
                    if (response.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        missing.Add(kw);
                    }
                }
            }
            if (missing.Count > 0) allHit = false;
            results.Add(new EvalTurnResult(response, missing, elapsed));
        }
        return new EvalRunResult(results, allHit, total);
    }
}
