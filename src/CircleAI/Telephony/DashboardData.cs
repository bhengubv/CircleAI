// DashboardData.cs
//
// (3.3.0) Data model for the built-in voice-loop dashboard. Hosts can
// render this via any UI stack (Blazor, ASP.NET MVC, the existing
// BigBruh! console) — the contracts here describe what the dashboard
// needs to show: live calls, recent calls, agent health, cost spend,
// percentile latency.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One row in the live-calls panel.</summary>
public sealed record LiveCallRow(
    string         CallId,
    string         Carrier,
    string         From,
    string         To,
    CallStatus     Status,
    DateTimeOffset StartedAtUtc,
    TimeSpan       Duration,
    decimal        CostSoFar);

/// <summary>(3.3.0) One row in the recent-calls panel.</summary>
public sealed record RecentCallRow(
    string         CallId,
    string         Carrier,
    string         From,
    string         To,
    CallStatus     FinalStatus,
    DateTimeOffset EndedAtUtc,
    TimeSpan       Duration,
    decimal        TotalCost);

/// <summary>(3.3.0) Agent health summary row.</summary>
public sealed record AgentHealthRow(
    string  AgentLabel,
    string  Health,                  // "Healthy" / "Degraded" / "CoolingDown"
    int     ConsecutiveFailures);

/// <summary>(3.3.0) Top-of-page summary card.</summary>
public sealed record DashboardSummary(
    int     LiveCallCount,
    decimal CurrentSpendUsd,
    int     CallsLast24h,
    float   PauseFalseAlarmRate);

/// <summary>(3.3.0) Full dashboard snapshot.</summary>
public sealed record DashboardSnapshot(
    DashboardSummary               Summary,
    IReadOnlyList<LiveCallRow>     LiveCalls,
    IReadOnlyList<RecentCallRow>   RecentCalls,
    IReadOnlyList<AgentHealthRow>  AgentHealth,
    IReadOnlyList<LatencySnapshot> LatencyByStage);

/// <summary>(3.3.0) Dashboard data source: hosts compose live + recent + health + latency feeds.</summary>
public interface IDashboardDataSource
{
    ValueTask<DashboardSnapshot> SnapshotAsync(CancellationToken ct = default);
}

/// <summary>(3.3.0) Default composed data source — pulls from supplied stores/services.</summary>
public sealed class DefaultDashboardDataSource : IDashboardDataSource
{
    private readonly Func<IReadOnlyList<LiveCallRow>>     _liveCalls;
    private readonly Func<IReadOnlyList<RecentCallRow>>   _recentCalls;
    private readonly Func<IReadOnlyList<AgentHealthRow>>  _agentHealth;
    private readonly Func<IReadOnlyList<LatencySnapshot>> _latency;
    private readonly Func<DashboardSummary>               _summary;

    public DefaultDashboardDataSource(
        Func<IReadOnlyList<LiveCallRow>>     liveCalls,
        Func<IReadOnlyList<RecentCallRow>>   recentCalls,
        Func<IReadOnlyList<AgentHealthRow>>  agentHealth,
        Func<IReadOnlyList<LatencySnapshot>> latency,
        Func<DashboardSummary>               summary)
    {
        _liveCalls   = liveCalls   ?? throw new ArgumentNullException(nameof(liveCalls));
        _recentCalls = recentCalls ?? throw new ArgumentNullException(nameof(recentCalls));
        _agentHealth = agentHealth ?? throw new ArgumentNullException(nameof(agentHealth));
        _latency     = latency     ?? throw new ArgumentNullException(nameof(latency));
        _summary     = summary     ?? throw new ArgumentNullException(nameof(summary));
    }

    public ValueTask<DashboardSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var snap = new DashboardSnapshot(
            Summary:        _summary(),
            LiveCalls:      _liveCalls(),
            RecentCalls:    _recentCalls(),
            AgentHealth:    _agentHealth(),
            LatencyByStage: _latency());
        return ValueTask.FromResult(snap);
    }
}
