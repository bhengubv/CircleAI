// IHealingView.cs
//
// What the Wolverine dashboard reads and drives — the browser-safe face of the
// self-healing loop. The loop's logic lives in the product (CircleAI.Hosting); a
// head implements this over it (DeviceHealing), exactly as IBrain/DeviceBrain and
// IRemembers/DeviceMemory do. The dashboard never touches the product types.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>One healing event, flattened for the screen.</summary>
/// <param name="Id">Stable id, so a row can be marked handled.</param>
/// <param name="At">When it happened.</param>
/// <param name="Title">The failure, in a line a person recognises.</param>
/// <param name="Detail">What the loop understood and did about it.</param>
/// <param name="Action">The concrete next step, when there is one to show.</param>
/// <param name="Outcome">"Healed", "Escalated", "Failed", "Awaiting brain", "Recorded".</param>
/// <param name="NeedsHuman">True when this is waiting for a person.</param>
public sealed record HealingItem(
    string Id,
    DateTimeOffset At,
    string Title,
    string Detail,
    string? Action,
    string Outcome,
    bool NeedsHuman);

/// <summary>The one-glance health of the app's self-healing.</summary>
/// <param name="RecentFailures">Failures seen in the recent window.</param>
/// <param name="Healed">How many the app fixed itself.</param>
/// <param name="NeedsYou">How many are waiting for a person.</param>
/// <param name="LastHealed">When the app last fixed something, or null.</param>
/// <param name="Autonomy">The current dial.</param>
public sealed record HealthSummary(
    int RecentFailures,
    int Healed,
    int NeedsYou,
    DateTimeOffset? LastHealed,
    AutonomyLevel Autonomy);

/// <summary>The dashboard's read/drive surface over the self-healing loop.</summary>
public interface IHealingView
{
    /// <summary>Raised when a healing happens or the dial changes — the screen re-reads.</summary>
    event Action? Changed;

    /// <summary>The one-glance health.</summary>
    Task<HealthSummary> HealthAsync(CancellationToken ct = default);

    /// <summary>What the app fixed itself, newest first.</summary>
    Task<IReadOnlyList<HealingItem>> HealedAsync(CancellationToken ct = default);

    /// <summary>What is waiting for a person, newest first.</summary>
    Task<IReadOnlyList<HealingItem>> NeedsYouAsync(CancellationToken ct = default);

    /// <summary>The current autonomy dial.</summary>
    Task<AutonomyLevel> AutonomyAsync(CancellationToken ct = default);

    /// <summary>Set the autonomy dial.</summary>
    Task SetAutonomyAsync(AutonomyLevel level, CancellationToken ct = default);

    /// <summary>Mark one escalated item as handled, so it leaves the "needs you" list.</summary>
    Task MarkHandledAsync(string id, CancellationToken ct = default);
}
