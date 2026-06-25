// FalseInterruptionTracker.cs
//
// (3.3.0) Counts how often the barge-in controller paused and then
// resumed (false alarm) versus cancelled (real interruption). High
// false-alarm rates suggest the VAD threshold is too sensitive.

using System;
using System.Collections.Generic;
using System.Threading;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Counters for false-interruption monitoring.</summary>
public sealed record InterruptionStats(
    long  TotalPauseEvents,
    long  ConfirmedBargeIns,
    long  FalseAlarms,
    float FalseAlarmRate);

/// <summary>(3.3.0) Tracks barge-in transitions and surfaces a false-alarm rate.</summary>
public interface IFalseInterruptionTracker
{
    /// <summary>Record one transition emitted by <see cref="BargeInController"/>.</summary>
    void Record(BargeInTransition transition);

    /// <summary>Current cumulative stats.</summary>
    InterruptionStats GetStats();

    /// <summary>Reset all counters.</summary>
    void Reset();
}

/// <summary>(3.3.0) Default in-memory tracker. Thread-safe.</summary>
public sealed class InMemoryFalseInterruptionTracker : IFalseInterruptionTracker
{
    private long _totalPauses;
    private long _confirmed;
    private long _falseAlarms;

    public void Record(BargeInTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        switch (transition.To)
        {
            case BargeInState.Paused:
                Interlocked.Increment(ref _totalPauses);
                break;
            case BargeInState.Cancelled:
                Interlocked.Increment(ref _confirmed);
                break;
            case BargeInState.Resumed:
                Interlocked.Increment(ref _falseAlarms);
                break;
        }
    }

    public InterruptionStats GetStats()
    {
        var totalPauses = Interlocked.Read(ref _totalPauses);
        var confirmed   = Interlocked.Read(ref _confirmed);
        var falseAlarms = Interlocked.Read(ref _falseAlarms);
        var rate        = totalPauses > 0 ? (float)falseAlarms / totalPauses : 0f;
        return new InterruptionStats(totalPauses, confirmed, falseAlarms, rate);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalPauses, 0);
        Interlocked.Exchange(ref _confirmed,   0);
        Interlocked.Exchange(ref _falseAlarms, 0);
    }
}
