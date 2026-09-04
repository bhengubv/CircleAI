// WakeCalibrationTests.cs
//
// The per-device wake tuning, which for its whole life recorded nothing.
//
// WHY THIS FILE EXISTS. WakeCalibration was written so a phone that
// consistently under-scores could be nudged once, instead of loosening the
// default for everybody. Load was called at every start; Save was called from
// nowhere in the repository. So the file never appeared, HasEvidence was false
// on every device ever shipped, and the acceptance gate every phone ran on was
// one measurement taken on one P30 with one voice in one room.
//
// The counters are the only thing that can ever turn that number from inherited
// into measured, so they are worth pinning: an accumulator that silently loses
// the lowest score is worse than no accumulator, because it reads as evidence.

using System;
using System.IO;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class WakeCalibrationTests
{
    [Fact]
    public void A_fresh_calibration_admits_it_knows_nothing()
    {
        var fresh = new WakeCalibration();

        Assert.False(fresh.HasEvidence);
        Assert.True(fresh.IsDefault);
        Assert.Null(fresh.LowestWakeScore);
        Assert.Null(fresh.HighestWakeScore);
    }

    [Fact]
    public void The_first_wake_sets_both_ends_of_the_range()
    {
        var after = new WakeCalibration().WithWake(0.31);

        Assert.Equal(1, after.Wakes);
        Assert.Equal(0.31, after.LowestWakeScore);
        Assert.Equal(0.31, after.HighestWakeScore);
        Assert.True(after.HasEvidence);
    }

    [Fact]
    public void The_range_widens_and_never_narrows()
    {
        // THE LOWEST SCORE IS THE WHOLE POINT. It is the margin the owner
        // actually has over the gate, so an accumulator that kept only the most
        // recent - or only the best - would report a comfortable phone as
        // comfortable right up until it stopped waking.
        var cal = new WakeCalibration()
            .WithWake(0.37)
            .WithWake(0.29)
            .WithWake(0.42)
            .WithWake(0.33);

        Assert.Equal(4, cal.Wakes);
        Assert.Equal(0.29, cal.LowestWakeScore);
        Assert.Equal(0.42, cal.HighestWakeScore);
    }

    [Fact]
    public void Evidence_is_a_different_question_from_tuning()
    {
        // IsDefault asks whether anybody OVERRODE the tuning; HasEvidence asks
        // whether anything was ever measured. A phone with 40 recorded wakes and
        // no override is both default and well evidenced, and reading one for
        // the other is how "we have calibration" gets said about a phone that
        // has never written a number.
        var measured = new WakeCalibration().WithWake(0.3);

        Assert.True(measured.IsDefault);
        Assert.True(measured.HasEvidence);
    }

    [Fact]
    public void A_veto_counts_without_touching_the_wake_scores()
    {
        var cal = new WakeCalibration().WithWake(0.3).WithVeto();

        Assert.Equal(1, cal.Wakes);
        Assert.Equal(1, cal.Vetoes);
        Assert.Equal(0.3, cal.LowestWakeScore);
    }

    [Fact]
    public void What_is_recorded_survives_a_restart()
    {
        // The reason it is a file at all: tuning that dies with the process is
        // not tuning, it is a variable.
        var path = Path.Combine(Path.GetTempPath(), $"wake-cal-{Guid.NewGuid():N}.json");
        try
        {
            new WakeCalibration().WithWake(0.37).WithWake(0.29).Save(path);

            var reloaded = WakeCalibration.Load(path);

            Assert.Equal(2, reloaded.Wakes);
            Assert.Equal(0.29, reloaded.LowestWakeScore);
            Assert.Equal(0.37, reloaded.HighestWakeScore);
            Assert.True(reloaded.HasEvidence);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void An_unreadable_file_is_not_a_reason_to_stop_listening()
    {
        // A corrupt tuning file must cost the tuning, never the wake word.
        var path = Path.Combine(Path.GetTempPath(), $"wake-cal-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");

            var loaded = WakeCalibration.Load(path);

            Assert.False(loaded.HasEvidence);
            Assert.True(loaded.IsDefault);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_missing_file_reads_as_a_phone_that_has_not_been_measured()
    {
        var loaded = WakeCalibration.Load(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"));

        Assert.False(loaded.HasEvidence);
        Assert.Equal(0, loaded.Wakes);
    }
}
