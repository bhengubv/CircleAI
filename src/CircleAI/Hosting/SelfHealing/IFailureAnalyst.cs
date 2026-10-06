// IFailureAnalyst.cs
//
// Circle AI's self-healing SENSE, as a standalone capability.
//
// Given a failure — an exception, an error response, whatever a consumer catches —
// Circle AI CATEGORISES it, RECOMMENDS whether it is a quick-fix, a patch, or best
// deferred, and (for a patch) can DRAFT the change. It stops there: it ANALYSES and
// RECOMMENDS, it never ACTS. Executing a retry, opening a PR, deploying — that is the
// consumer's job (Wolverine, a CI bot, a person). Circle AI is the brain, not the hand.
//
// This is a capability of the PRODUCT: it depends only on the brain (IAIService), so a
// stranger who has only Circle AI gets it, and it is unit-testable with a fake brain —
// no server, no database, no Butler, no Wolverine.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>What kind of response a failure calls for — the plain-language buckets.</summary>
public enum HealingKind
{
    /// <summary>Safe, reversible, do-it-now: retry, reset a connection, refresh a cache.</summary>
    QuickFix,

    /// <summary>Needs a code change a human should review before it ships.</summary>
    Patch,

    /// <summary>Not urgent, not safe to touch automatically, or not understood — hand it on.</summary>
    Defer,
}

/// <summary>A failure handed to Circle AI for analysis. Plain data, no dependencies.</summary>
/// <param name="Message">The error/exception message — the one line a human would read first.</param>
/// <param name="StackTrace">The stack trace, if there is one.</param>
/// <param name="Source">Where it came from — an API name, a component, a screen.</param>
/// <param name="Details">Any extra context: the endpoint, status code, a payload snippet.</param>
public sealed record FailureContext(
    string Message,
    string? StackTrace = null,
    string? Source = null,
    string? Details = null);

/// <summary>Circle AI's read on a failure. A recommendation, never an action.</summary>
/// <param name="Kind">Quick-fix, patch, or defer.</param>
/// <param name="Category">A short label for the kind of problem, e.g. "transient-network",
/// "null-reference", "config", "business-logic".</param>
/// <param name="Summary">One honest sentence a human can act on.</param>
/// <param name="RecommendedAction">The concrete next step, e.g. "retry with backoff",
/// "reset the connection", "fix the null guard in X". Null when there is nothing to suggest.</param>
/// <param name="DraftFix">An optional drafted change for a <see cref="HealingKind.Patch"/> —
/// a DRAFT for a human to review, never applied by Circle AI.</param>
/// <param name="Confidence">The model's own stated confidence, 0..1. A hint for the
/// consumer's threshold, not a guarantee — the consumer decides what to trust.</param>
public sealed record HealingVerdict(
    HealingKind Kind,
    string Category,
    string Summary,
    string? RecommendedAction = null,
    string? DraftFix = null,
    double Confidence = 0.0)
{
    /// <summary>
    /// The safe answer when a failure cannot be understood: defer to a human, claim
    /// nothing. Used whenever the brain's reply cannot be parsed — Circle AI never
    /// invents a category or a fix to fill a gap.
    /// </summary>
    public static HealingVerdict NeedsAHuman(string why) =>
        new(HealingKind.Defer, "unclassified", why, RecommendedAction: null, DraftFix: null, Confidence: 0.0);
}

/// <summary>Circle AI's self-healing sense: categorise a failure and recommend what to do.</summary>
public interface IFailureAnalyst
{
    /// <summary>
    /// Analyse a failure and return a recommendation. Never throws for an analysis it
    /// cannot make — it returns <see cref="HealingVerdict.NeedsAHuman"/> instead, so a
    /// consumer's pipeline never loses an error to an exception here.
    /// </summary>
    Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default);
}
