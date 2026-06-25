// Circle33FalseInterruptionTests.cs
//
// (3.3.0) Tests for false-interruption tracker.

using System;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33FalseInterruptionTests
{
    private static BargeInTransition T(BargeInState from, BargeInState to)
        => new(from, to, DateTimeOffset.UtcNow, "test");

    [Fact]
    public void Empty_ReturnsZeroes()
    {
        var t = new InMemoryFalseInterruptionTracker();
        var s = t.GetStats();
        Assert.Equal(0L, s.TotalPauseEvents);
        Assert.Equal(0L, s.ConfirmedBargeIns);
        Assert.Equal(0L, s.FalseAlarms);
        Assert.Equal(0f, s.FalseAlarmRate);
    }

    [Fact]
    public void PauseThenResume_CountsAsFalseAlarm()
    {
        var t = new InMemoryFalseInterruptionTracker();
        t.Record(T(BargeInState.Speaking,  BargeInState.Paused));
        t.Record(T(BargeInState.Paused,    BargeInState.Resumed));

        var s = t.GetStats();
        Assert.Equal(1L, s.TotalPauseEvents);
        Assert.Equal(1L, s.FalseAlarms);
        Assert.Equal(0L, s.ConfirmedBargeIns);
        Assert.Equal(1f, s.FalseAlarmRate);
    }

    [Fact]
    public void PauseThenCancelled_CountsAsConfirmed()
    {
        var t = new InMemoryFalseInterruptionTracker();
        t.Record(T(BargeInState.Speaking,  BargeInState.Paused));
        t.Record(T(BargeInState.Paused,    BargeInState.Cancelled));

        var s = t.GetStats();
        Assert.Equal(1L, s.TotalPauseEvents);
        Assert.Equal(1L, s.ConfirmedBargeIns);
        Assert.Equal(0L, s.FalseAlarms);
        Assert.Equal(0f, s.FalseAlarmRate);
    }

    [Fact]
    public void Mixed_RateIsFraction()
    {
        var t = new InMemoryFalseInterruptionTracker();
        for (int i = 0; i < 4; i++) t.Record(T(BargeInState.Speaking, BargeInState.Paused));
        t.Record(T(BargeInState.Paused, BargeInState.Resumed));
        t.Record(T(BargeInState.Paused, BargeInState.Resumed));
        t.Record(T(BargeInState.Paused, BargeInState.Resumed));
        t.Record(T(BargeInState.Paused, BargeInState.Cancelled));

        var s = t.GetStats();
        Assert.Equal(4L, s.TotalPauseEvents);
        Assert.Equal(3L, s.FalseAlarms);
        Assert.Equal(1L, s.ConfirmedBargeIns);
        Assert.Equal(0.75f, s.FalseAlarmRate);
    }

    [Fact]
    public void Reset_ZeroesCounters()
    {
        var t = new InMemoryFalseInterruptionTracker();
        t.Record(T(BargeInState.Speaking, BargeInState.Paused));
        t.Record(T(BargeInState.Paused,   BargeInState.Cancelled));
        t.Reset();

        var s = t.GetStats();
        Assert.Equal(0L, s.TotalPauseEvents);
        Assert.Equal(0L, s.ConfirmedBargeIns);
        Assert.Equal(0L, s.FalseAlarms);
    }
}
