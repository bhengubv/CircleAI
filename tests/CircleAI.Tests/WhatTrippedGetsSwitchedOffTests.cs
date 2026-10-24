// WhatTrippedGetsSwitchedOffTests.cs
//
// "WOLVERINE IS SUPPOSED TO DIAGNOSE AND FIX SMALL THINGS IT CAN - SWITCH ON OR OFF
//  WHAT TRIPPED FOR BETTER UX." (the owner, 2026-10-23)
//
// The API-side pipeline has 38 tests and a circuit breaker. The client-side twin is a
// document that says "PLANNING PHASE (No Code Yet)", so every one of these rules is
// new and none of them has ever been checked anywhere.
//
// THE RULE THAT MATTERS MOST IS THE ONE ABOUT COMING BACK. A switch that goes off and
// stays off is not healing - it is a product getting quietly worse, and worse in the
// one way nobody reports, because from the outside nothing is broken. Most of what is
// below is about the clock, not about the trip.
//
// STATIC STATE, SO ONE CLASS AND A CLEAN SLATE PER TEST. SelfHeal is static because
// the things that consult it - the loader, the engine choice, the recogniser - are
// static rules reached from four processes, and xUnit does not run two tests in one
// class at the same time.

using System;
using System.Linq;
using System.Reflection;
using CircleAI.SelfKnowledge;
using Xunit;

namespace CircleAI.Tests;

public sealed class WhatTrippedGetsSwitchedOffTests : IDisposable
{
    private const string Mmap = SelfHeal.Switch.MemoryMapping;

    // A FIXED CLOCK, because every rule here is about elapsed time and a test that
    // reads the wall clock is a test that passes at 23:59 and fails at 00:00.
    private static readonly DateTimeOffset Noon =
        new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public WhatTrippedGetsSwitchedOffTests() => SelfHeal.Clear();

    public void Dispose() => SelfHeal.Clear();

    // ── switching off ────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_switched_off_to_begin_with()
    {
        Assert.False(SelfHeal.IsOff(Mmap, Noon));
        Assert.Empty(SelfHeal.All());
    }

    [Fact]
    public void A_trip_switches_it_off()
    {
        SelfHeal.Trip(Mmap, "SIGSEGV in MNN::ThreadPool::enqueue", Noon);

        Assert.True(SelfHeal.IsOff(Mmap, Noon));
    }

    [Fact]
    public void The_reason_is_kept_because_it_is_the_useful_part()
    {
        var healed = SelfHeal.Trip(Mmap, "SIGSEGV in MNN::ThreadPool::enqueue", Noon);

        Assert.Equal("SIGSEGV in MNN::ThreadPool::enqueue", healed.Why);
        Assert.Equal(Mmap, healed.Name);
    }

    [Fact]
    public void A_blank_name_is_refused_rather_than_switching_off_something_unnamed()
        => Assert.ThrowsAny<ArgumentException>(() => SelfHeal.Trip("  ", "why", Noon));

    [Fact]
    public void The_name_is_the_same_switch_whatever_its_case()
    {
        SelfHeal.Trip("MMAP", "tripped", Noon);

        Assert.True(SelfHeal.IsOff("mmap", Noon));
    }

    // ── and coming back on ───────────────────────────────────────────────────

    [Fact]
    public void It_is_off_for_an_hour_the_first_time()
    {
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.True(SelfHeal.IsOff(Mmap, Noon.AddMinutes(59)));
        Assert.False(SelfHeal.IsOff(Mmap, Noon.AddHours(1)));
    }

    [Fact]
    public void It_comes_back_on_by_itself_with_nothing_running()
    {
        // THE WHOLE POINT. No timer fires, no tick happens, nothing calls anything -
        // the window simply runs out, so a process that crashed and restarted with
        // no scheduler at all still tries again.
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.False(SelfHeal.IsOff(Mmap, Noon.AddDays(1)));
    }

    [Fact]
    public void The_second_trip_holds_it_longer_than_the_first()
    {
        SelfHeal.Trip(Mmap, "once", Noon);
        var twice = SelfHeal.Trip(Mmap, "again", Noon.AddHours(2));

        Assert.Equal(2, twice.Trips);
        Assert.True(SelfHeal.IsOff(Mmap, Noon.AddHours(7)));
        Assert.False(SelfHeal.IsOff(Mmap, Noon.AddHours(8)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 24)]
    [InlineData(4, 168)]
    public void It_is_held_off_longer_each_time(int trips, int hours)
        => Assert.Equal(TimeSpan.FromHours(hours), SelfHeal.OffFor(trips));

    [Fact]
    public void It_never_stops_asking_however_many_times_it_has_tripped()
    {
        // A WEEK IS THE CAP, NOT INFINITY. An app update, a firmware update or a
        // replaced model is the thing that actually fixes one of these, and that
        // arrives on the scale of a week. A switch that stops asking is the
        // permanent degradation the whole module exists to prevent.
        Assert.Equal(TimeSpan.FromDays(7), SelfHeal.OffFor(99));
    }

    [Fact]
    public void Working_puts_it_back_on_at_once()
    {
        SelfHeal.Trip(Mmap, "tripped", Noon);
        SelfHeal.Worked(Mmap);

        Assert.False(SelfHeal.IsOff(Mmap, Noon));
    }

    [Fact]
    public void Working_forgets_the_count_so_one_bad_afternoon_is_not_held_for_ever()
    {
        SelfHeal.Trip(Mmap, "once", Noon);
        SelfHeal.Trip(Mmap, "twice", Noon);
        SelfHeal.Trip(Mmap, "three times", Noon);

        SelfHeal.Worked(Mmap);
        var after = SelfHeal.Trip(Mmap, "much later", Noon.AddDays(30));

        Assert.Equal(1, after.Trips);
        Assert.Equal(TimeSpan.FromHours(1), SelfHeal.OffFor(after.Trips));
    }

    // ── the half-open tick ───────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_due_to_retry_while_it_is_still_off()
    {
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.Empty(SelfHeal.DueToRetry(Noon.AddMinutes(30)));
    }

    [Fact]
    public void It_is_due_to_retry_once_the_window_has_run_out()
    {
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.Equal(Mmap, Assert.Single(SelfHeal.DueToRetry(Noon.AddHours(2))).Name);
    }

    [Fact]
    public void A_switch_is_offered_for_retry_once_and_not_every_tick_after_that()
    {
        // THE TICK RUNS EVERY FIFTEEN MINUTES FOR THE LIFE OF THE DEVICE. A caller
        // that announces whatever is due would write the same line four times an hour
        // for a week. Nothing silent, and nothing said for ever.
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.Single(SelfHeal.TakeDueToRetry(Noon.AddHours(2)));
        Assert.Empty(SelfHeal.TakeDueToRetry(Noon.AddHours(3)));
    }

    [Fact]
    public void Asking_without_taking_does_not_use_up_the_announcement()
    {
        SelfHeal.Trip(Mmap, "tripped", Noon);

        Assert.Single(SelfHeal.DueToRetry(Noon.AddHours(2)));
        Assert.Single(SelfHeal.TakeDueToRetry(Noon.AddHours(2)));
    }

    [Fact]
    public void The_second_time_it_breaks_is_announced_too()
    {
        // THE TRIP THAT MATTERS MOST IS NEVER THE FIRST ONE. If a fresh trip did not
        // clear the mark, the only one anybody ever heard about would be the first.
        SelfHeal.Trip(Mmap, "once", Noon);
        SelfHeal.TakeDueToRetry(Noon.AddHours(2));

        SelfHeal.Trip(Mmap, "again", Noon.AddHours(3));

        Assert.Single(SelfHeal.TakeDueToRetry(Noon.AddHours(10)));
    }

    // ── surviving a restart ──────────────────────────────────────────────────

    [Fact]
    public void What_an_earlier_run_switched_off_is_still_off()
    {
        // WITHOUT THIS THE MODULE IS A CACHE THAT DIES WITH THE PROCESS, and a phone
        // that SIGSEGVs on load crashes once per launch for ever while looking, from
        // the outside, exactly like software with no healing in it at all.
        SelfHeal.Restore(Mmap, trips: 2, lastUtc: Noon);

        Assert.True(SelfHeal.IsOff(Mmap, Noon.AddHours(1)));
    }

    [Fact]
    public void The_clock_runs_from_when_it_tripped_not_from_this_launch()
    {
        // A PHONE THAT REBOOTS OFTEN WOULD OTHERWISE NEVER RETRY ANYTHING. Restarting
        // the window at start-up means the window never finishes on the device that
        // restarts most - which is the broken one.
        SelfHeal.Restore(Mmap, trips: 1, lastUtc: Noon.AddHours(-2));

        Assert.False(SelfHeal.IsOff(Mmap, Noon));
    }

    [Fact]
    public void A_restored_count_carries_on_escalating()
    {
        SelfHeal.Restore(Mmap, trips: 2, lastUtc: Noon);
        var next = SelfHeal.Trip(Mmap, "again", Noon.AddDays(1));

        Assert.Equal(3, next.Trips);
    }

    [Fact]
    public void A_switch_that_never_tripped_is_not_restored()
    {
        SelfHeal.Restore(Mmap, trips: 0, lastUtc: Noon);

        Assert.Empty(SelfHeal.All());
    }

    // ── what the switches are ────────────────────────────────────────────────

    [Fact]
    public void Every_switch_has_one_name_and_no_two_share_it()
    {
        // ONE OWNER FOR EVERY SUBJECT. The half that trips and the half that asks "is
        // this off" agree on a string, and one fact with two owners always ends up
        // with two answers - this repository's most repeated and most expensive defect.
        var names = new[]
        {
            SelfHeal.Switch.MemoryMapping,
            SelfHeal.Switch.GpuBackend,
            SelfHeal.Switch.TranscribingWake,
            SelfHeal.Switch.PrefixCache,
            SelfHeal.Switch.MicrophoneForegroundType,
        };

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Everything_switched_off_can_be_listed_for_a_person_to_read()
    {
        SelfHeal.Trip(SelfHeal.Switch.PrefixCache, "malformed prefix cache path", Noon);
        SelfHeal.Trip(SelfHeal.Switch.GpuBackend, "OpenCL would not build", Noon);

        var all = SelfHeal.All();

        Assert.Equal(2, all.Count);
        Assert.All(all, h => Assert.False(string.IsNullOrWhiteSpace(h.Why)));
    }

    // ── the subject keys it is filed under ───────────────────────────────────

    [Fact]
    public void A_trip_is_filed_under_its_own_subject_apart_from_the_models()
    {
        var subject = SelfSubjects.Healed(Mmap);

        Assert.True(SelfSubjects.IsAboutHealing(subject));
        Assert.Equal(Mmap, SelfSubjects.HealedOf(subject));

        // AND IT IS NOT MISTAKEN FOR A MODEL FACT, which is what would make the model
        // selector read "mmap" as a model it had refused.
        Assert.False(SelfSubjects.IsAboutModel(subject));
    }

    [Fact]
    public void A_switch_name_with_a_dot_in_it_survives_the_round_trip()
    {
        // ModelOf takes everything after the LAST colon, which a dotted name does not
        // break - but HealedOf must not borrow that rule, because "wake.transcribing"
        // has no colon and a key built the other way would come back empty.
        var subject = SelfSubjects.Healed(SelfHeal.Switch.TranscribingWake);

        Assert.Equal(SelfHeal.Switch.TranscribingWake, SelfSubjects.HealedOf(subject));
    }

    [Fact]
    public void A_model_subject_is_not_read_as_a_switch()
    {
        var subject = SelfSubjects.ModelCost("Qwen3.5-0.8B");

        Assert.False(SelfSubjects.IsAboutHealing(subject));
        Assert.Null(SelfSubjects.HealedOf(subject));
    }

    // ── green or red, without anything having to fail ────────────────────────
    //
    // "you dont need to fake errors - just have Wolverine report green or red status"
    // (the owner, 2026-10-24). Every other line this thing writes is written BY a
    // failure, so the only way to see it working was to break something. These cover
    // the reading itself.

    [Fact]
    public void All_covers_every_switch_constant()
    {
        // THE DRIFT GUARD Switch.All's own comment promises. The array is written out
        // by hand because it ships into a trimmed Android head where field metadata
        // goes missing - so the reflection that would have been unsafe at run time
        // happens HERE instead, where nothing is trimmed.
        var constants = typeof(SelfHeal.Switch)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(constants);
        Assert.Equal(
            constants.OrderBy(s => s, StringComparer.Ordinal),
            SelfHeal.Switch.All.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void A_device_that_never_tripped_reports_every_switch_green()
    {
        var status = SelfHeal.Status(Noon);

        // THE WHOLE POINT: not an empty list. An empty status reads exactly like a
        // status that could not be taken, which is how this half stayed invisible.
        Assert.Equal(SelfHeal.Switch.All.Length, status.Switches.Count);
        Assert.All(status.Switches, s => Assert.Equal(SelfHeal.HealthLight.Green, s.Light));
        Assert.All(status.Switches, s => Assert.Equal(0, s.Trips));
        Assert.Equal(SelfHeal.HealthLight.Green, status.Overall);
    }

    [Fact]
    public void The_green_line_says_how_many_switches_it_actually_looked_at()
    {
        // A reader has to be able to tell the reading ran. The count is what says so.
        var line = SelfHeal.Status(Noon).OneLine;

        Assert.Contains("GREEN", line);
        Assert.Contains(SelfHeal.Switch.All.Length.ToString(), line);
    }

    [Fact]
    public void A_switch_that_is_off_reports_red_and_when_it_comes_back()
    {
        SelfHeal.Trip(Mmap, "mmap SIGSEGV'd the threadpool", Noon);

        var status = SelfHeal.Status(Noon);
        var mmap = status.Switches.Single(s => s.Name == Mmap);

        Assert.Equal(SelfHeal.HealthLight.Red, mmap.Light);
        Assert.Equal(SelfHeal.HealthLight.Red, status.Overall);
        Assert.Equal(1, mmap.Trips);
        Assert.Equal("mmap SIGSEGV'd the threadpool", mmap.Why);
        Assert.Equal(Noon + SelfHeal.OffFor(1), mmap.BackOnUtc);

        // The other four are untouched, and still say so.
        Assert.Equal(
            SelfHeal.Switch.All.Length - 1,
            status.Switches.Count(s => s.Light == SelfHeal.HealthLight.Green));
    }

    [Fact]
    public void A_switch_whose_window_ran_out_is_amber_not_green()
    {
        SelfHeal.Trip(Mmap, "mmap SIGSEGV'd the threadpool", Noon);

        // One hour later the clock has already put it back on - but nothing has run
        // through it, so calling it green would lose this device's history of it.
        var status = SelfHeal.Status(Noon + SelfHeal.OffFor(1) + TimeSpan.FromMinutes(1));
        var mmap = status.Switches.Single(s => s.Name == Mmap);

        Assert.Equal(SelfHeal.HealthLight.Amber, mmap.Light);
        Assert.Equal(SelfHeal.HealthLight.Amber, status.Overall);
        Assert.Null(mmap.BackOnUtc);
        Assert.Equal(1, mmap.Trips);
    }

    [Fact]
    public void A_switch_that_worked_goes_back_to_green()
    {
        SelfHeal.Trip(Mmap, "mmap SIGSEGV'd the threadpool", Noon);
        SelfHeal.Worked(Mmap);

        var status = SelfHeal.Status(Noon);

        Assert.Equal(SelfHeal.HealthLight.Green, status.Overall);
        Assert.All(status.Switches, s => Assert.Equal(SelfHeal.HealthLight.Green, s.Light));
    }

    [Fact]
    public void Red_outranks_amber_in_the_overall_light()
    {
        SelfHeal.Trip(Mmap, "mmap SIGSEGV'd the threadpool", Noon);
        SelfHeal.Trip(SelfHeal.Switch.GpuBackend, "OpenCL would not build", Noon);

        // mmap's hour is up (amber); the GPU tripped much later and is still off (red).
        var at = Noon + SelfHeal.OffFor(1) + TimeSpan.FromMinutes(1);
        SelfHeal.Trip(SelfHeal.Switch.GpuBackend, "OpenCL would not build", at);

        var status = SelfHeal.Status(at);

        Assert.Equal(SelfHeal.HealthLight.Amber, status.Switches.Single(s => s.Name == Mmap).Light);
        Assert.Equal(SelfHeal.HealthLight.Red,
            status.Switches.Single(s => s.Name == SelfHeal.Switch.GpuBackend).Light);
        Assert.Equal(SelfHeal.HealthLight.Red, status.Overall);
    }

    [Fact]
    public void The_line_names_what_is_not_green()
    {
        SelfHeal.Trip(Mmap, "mmap SIGSEGV'd the threadpool", Noon);

        var line = SelfHeal.Status(Noon).OneLine;

        // "WOLVERINE RED" on its own sends a person to the source to find out which of
        // five things it meant.
        Assert.Contains("RED", line);
        Assert.Contains(Mmap, line);
    }

    [Fact]
    public void A_restored_switch_is_red_in_the_status_so_a_relaunch_is_not_silently_green()
    {
        // THE RELAUNCH CASE. SelfModel.LoadAsync calls Restore, and the service takes
        // its startup reading on that load's continuation precisely so a device holding
        // a week-long trip does not report green on every launch.
        SelfHeal.Restore(Mmap, trips: 4, lastUtc: Noon);

        var status = SelfHeal.Status(Noon + TimeSpan.FromHours(1));
        var mmap = status.Switches.Single(s => s.Name == Mmap);

        Assert.Equal(SelfHeal.HealthLight.Red, mmap.Light);
        Assert.Equal(4, mmap.Trips);
        Assert.Equal(SelfHeal.HealthLight.Red, status.Overall);
    }
}
