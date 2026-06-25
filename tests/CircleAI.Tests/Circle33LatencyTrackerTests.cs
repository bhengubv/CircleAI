// Circle33LatencyTrackerTests.cs
//
// (3.3.0) Tests for the latency tracker.

using System;
using System.Linq;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33LatencyTrackerTests
{
    [Fact]
    public void NoObservations_SnapshotIsNull()
    {
        var t = new LatencyTracker();
        Assert.Null(t.Snapshot(LatencyStage.LlmFirstToken));
    }

    [Fact]
    public void Snapshot_KnownObservations_PercentilesMatch()
    {
        var t = new LatencyTracker();
        for (int i = 1; i <= 100; i++)
        {
            t.Record(LatencyStage.LlmFirstToken, TimeSpan.FromMilliseconds(i));
        }

        var snap = t.Snapshot(LatencyStage.LlmFirstToken);
        Assert.NotNull(snap);
        Assert.Equal(100, snap!.Samples);
        Assert.Equal(1,   snap.Min.TotalMilliseconds);
        Assert.Equal(50,  snap.P50.TotalMilliseconds);
        Assert.Equal(95,  snap.P95.TotalMilliseconds);
        Assert.Equal(99,  snap.P99.TotalMilliseconds);
        Assert.Equal(100, snap.Max.TotalMilliseconds);
    }

    [Fact]
    public void Record_RespectsWindowSize()
    {
        var t = new LatencyTracker(windowSize: 10);
        for (int i = 1; i <= 50; i++)
        {
            t.Record("stage", TimeSpan.FromMilliseconds(i));
        }

        var snap = t.Snapshot("stage");
        Assert.NotNull(snap);
        Assert.Equal(10, snap!.Samples);
        Assert.Equal(41, snap.Min.TotalMilliseconds);
        Assert.Equal(50, snap.Max.TotalMilliseconds);
    }

    [Fact]
    public void NegativeLatency_IsIgnored()
    {
        var t = new LatencyTracker();
        t.Record("stage", TimeSpan.FromMilliseconds(-5));
        Assert.Null(t.Snapshot("stage"));
    }

    [Fact]
    public void EmptyStage_Throws()
    {
        var t = new LatencyTracker();
        Assert.Throws<ArgumentException>(() => t.Record("", TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void SnapshotAll_ReturnsEveryStage()
    {
        var t = new LatencyTracker();
        t.Record("a", TimeSpan.FromMilliseconds(10));
        t.Record("b", TimeSpan.FromMilliseconds(20));

        var all = t.SnapshotAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Stage == "a");
        Assert.Contains(all, s => s.Stage == "b");
    }

    [Fact]
    public void Reset_ClearsOneStage()
    {
        var t = new LatencyTracker();
        t.Record("stage", TimeSpan.FromMilliseconds(5));
        t.Reset("stage");
        Assert.Null(t.Snapshot("stage"));
    }

    [Fact]
    public void ResetAll_ClearsAllStages()
    {
        var t = new LatencyTracker();
        t.Record("a", TimeSpan.FromMilliseconds(5));
        t.Record("b", TimeSpan.FromMilliseconds(10));
        t.ResetAll();
        Assert.Empty(t.SnapshotAll());
    }
}
