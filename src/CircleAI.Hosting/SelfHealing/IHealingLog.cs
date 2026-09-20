// IHealingLog.cs
//
// The durable record of every failure the app met and what became of it — so no
// error is ever lost, whether the loop healed it or handed it on. Append-only in
// spirit; the one mutation is a person marking an escalation handled.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>What became of a failure the loop met.</summary>
public enum HealingOutcome
{
    /// <summary>Written down, nothing more (autonomy was off).</summary>
    Recorded,

    /// <summary>The app fixed it itself with a safe, reversible action.</summary>
    Healed,

    /// <summary>Handed to a person — a patch, a defer, or nothing safe to do.</summary>
    Escalated,

    /// <summary>A safe fix was tried and did not take; handed on.</summary>
    Failed,

    /// <summary>The brain was not ready to diagnose; handed on rather than waited on.</summary>
    AwaitingBrain,
}

/// <summary>One failure and its disposition. Plain data.</summary>
/// <param name="Id">Stable id (used to mark handled).</param>
/// <param name="At">When it was recorded (UTC).</param>
/// <param name="Signature">A stable key for the failure, for de-duplication.</param>
/// <param name="Source">Where it came from — a screen, a component.</param>
/// <param name="Message">The error message.</param>
/// <param name="Kind">The verdict kind: quick-fix / patch / defer / unclassified.</param>
/// <param name="Category">The verdict's short category label.</param>
/// <param name="Summary">One honest sentence about it.</param>
/// <param name="RecommendedAction">The concrete next step, when the analyst gave one.</param>
/// <param name="ActionTaken">What the loop actually did.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="NeedsHuman">True when it is waiting for a person.</param>
/// <param name="Handled">True once a person has marked it done.</param>
public sealed record HealingRecord(
    string Id,
    DateTimeOffset At,
    string Signature,
    string Source,
    string Message,
    string Kind,
    string Category,
    string Summary,
    string? RecommendedAction,
    string ActionTaken,
    HealingOutcome Outcome,
    bool NeedsHuman,
    bool Handled);

/// <summary>The durable store of healing events.</summary>
public interface IHealingLog
{
    /// <summary>Record a healing event. Must not throw — a lost log entry is bad, a thrown one is worse.</summary>
    Task AppendAsync(HealingRecord record, CancellationToken ct = default);

    /// <summary>The most recent events, newest first.</summary>
    Task<IReadOnlyList<HealingRecord>> RecentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Mark one record handled by a person.</summary>
    Task MarkHandledAsync(string id, CancellationToken ct = default);

    /// <summary>How many events since <paramref name="since"/> — for the health glance.</summary>
    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}
