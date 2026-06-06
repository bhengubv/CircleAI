// CompanionRuntimeOptions.cs
//
// Tunable settings for CompanionRuntime. All have sensible defaults so a
// host can do `services.AddCompanionRuntime()` and get a working pipeline
// out of the box.

using System;

namespace CircleAI.Memory.Runtime;

/// <summary>
/// Configuration for <see cref="CompanionRuntime"/>.
/// </summary>
public sealed class CompanionRuntimeOptions
{
    /// <summary>
    /// Cadence for the daily-tier consolidation pass. Default: every 6 hours.
    /// Setting this to <see cref="TimeSpan.Zero"/> disables automatic daily ticks.
    /// </summary>
    public TimeSpan DailyTickInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Cadence for the weekly-tier consolidation pass. Default: every 24 hours.
    /// </summary>
    public TimeSpan WeeklyTickInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Cadence for the monthly-tier (persona-delta) consolidation pass.
    /// Default: every 48 hours.
    /// </summary>
    public TimeSpan MonthlyTickInterval { get; init; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Cadence at which the runtime broadcasts its sync state vector to peers.
    /// Default: every 5 minutes. Setting to <see cref="TimeSpan.Zero"/> disables
    /// periodic sync (the engine still responds to inbound envelopes; only the
    /// initiating Announce broadcasts are suppressed).
    /// </summary>
    public TimeSpan SyncBroadcastInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initial delay before the first consolidator tick after StartAsync.
    /// Default: 30 seconds. Keeps startup quiet.
    /// </summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When true, the runtime runs an OnDemand consolidation pass during
    /// StartAsync to catch up anything pending before the timer cadence kicks
    /// in. Default: true.
    /// </summary>
    public bool CatchUpOnStart { get; init; } = true;
}
