// Circle33DashboardTests.cs
//
// (3.3.0) Tests for dashboard data source.

using System;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33DashboardTests
{
    [Fact]
    public async Task SnapshotAsync_AssemblesAllSections()
    {
        var src = new DefaultDashboardDataSource(
            liveCalls:   () => new[] { new LiveCallRow("c1", "twilio", "+1", "+2", CallStatus.Active, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), 0.05m) },
            recentCalls: () => new[] { new RecentCallRow("c0", "twilio", "+1", "+2", CallStatus.EndedByCaller, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), 0.20m) },
            agentHealth: () => new[] { new AgentHealthRow("primary", "Healthy", 0) },
            latency:     () => new[] { new LatencySnapshot("llm.first_token", 10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(160), TimeSpan.FromMilliseconds(190), TimeSpan.FromMilliseconds(200)) },
            summary:     () => new DashboardSummary(LiveCallCount: 1, CurrentSpendUsd: 1.25m, CallsLast24h: 32, PauseFalseAlarmRate: 0.15f));

        var snap = await src.SnapshotAsync();

        Assert.Single(snap.LiveCalls);
        Assert.Single(snap.RecentCalls);
        Assert.Single(snap.AgentHealth);
        Assert.Single(snap.LatencyByStage);
        Assert.Equal(1, snap.Summary.LiveCallCount);
        Assert.Equal(1.25m, snap.Summary.CurrentSpendUsd);
    }

    [Fact]
    public void Constructor_NullLiveCalls_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DefaultDashboardDataSource(
            liveCalls:   null!,
            recentCalls: Array.Empty<RecentCallRow>,
            agentHealth: Array.Empty<AgentHealthRow>,
            latency:     Array.Empty<LatencySnapshot>,
            summary:     () => new DashboardSummary(0, 0m, 0, 0f)));
    }

    [Fact]
    public void Summary_Defaults_ZeroWhenNoActivity()
    {
        var s = new DashboardSummary(0, 0m, 0, 0f);
        Assert.Equal(0, s.LiveCallCount);
        Assert.Equal(0m, s.CurrentSpendUsd);
    }

    [Fact]
    public async Task SnapshotAsync_PicksUpFreshValuesEachCall()
    {
        int liveCount = 0;
        var src = new DefaultDashboardDataSource(
            liveCalls:   () => Array.Empty<LiveCallRow>(),
            recentCalls: () => Array.Empty<RecentCallRow>(),
            agentHealth: () => Array.Empty<AgentHealthRow>(),
            latency:     () => Array.Empty<LatencySnapshot>(),
            summary:     () => new DashboardSummary(LiveCallCount: liveCount++, 0m, 0, 0f));

        var first  = await src.SnapshotAsync();
        var second = await src.SnapshotAsync();

        Assert.Equal(0, first.Summary.LiveCallCount);
        Assert.Equal(1, second.Summary.LiveCallCount);
    }
}
