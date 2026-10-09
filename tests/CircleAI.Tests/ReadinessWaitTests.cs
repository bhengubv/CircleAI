// ReadinessWaitTests.cs
//
// The question is the instruction to start, and then to wait for what started.
//
// Until 2026-10-09 CircleNeuronLinkService answered a question by calling Start()
// on the brain and checking IsReady in the very next statement. A cold model load
// is 13-23 s on the P30, so on any phone where the brain was not already resident
// the check could not pass, and the person got
//
//     brain warming up, try again shortly
//
// Asking again inside that window returned the same sentence. It is the same
// defect as the "Turn it on" button it sat next to: the app knowing exactly what
// is happening and handing the work back to the person.
//
// THESE TESTS EXIST BECAUSE THE FIRST FIX COULD NOT BE TESTED. It was a poll loop
// inside the service - private, static, on an Android Service - so the only
// evidence was me watching a phone answer twice. A budget with a poll interval
// has an off-by-one at each end and a first-check-before-first-sleep that is easy
// to get wrong, and "it worked on the device I tried it on" does not cover any of
// them.
//
// THE CLOCK AND THE SLEEP ARE INJECTED so a 45 second budget is asserted in
// microseconds. A test that really waited 45 seconds would be deleted by the
// first person whose suite it slowed down.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ReadinessWaitTests
{
    /// <summary>A clock that only moves when the code under test sleeps.</summary>
    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 10, 9, 19, 0, 0, DateTimeKind.Utc);

        public int Sleeps { get; private set; }
        public TimeSpan Slept { get; private set; }

        public DateTime Now() => _now;

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            Sleeps++;
            Slept += d;
            _now += d;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Something_already_up_is_not_made_to_wait()
    {
        // THE COMMON CASE, AND THE ONE A BUG HERE TAXES HARDEST. A healthy phone
        // answers every turn through this; sleeping first would add the poll
        // interval to every single question for nothing.
        var clock = new FakeClock();

        var up = await ReadinessWait.UntilAsync(
            ready: () => true,
            budget: TimeSpan.FromSeconds(45),
            now: clock.Now, delay: clock.Delay);

        Assert.True(up);
        Assert.Equal(0, clock.Sleeps);
    }

    [Fact]
    public async Task A_brain_that_comes_up_inside_the_budget_is_waited_for()
    {
        // THE WHOLE POINT. 13 to 23 seconds is a normal cold load on the P30 and
        // used to be an automatic "try again shortly".
        var clock = new FakeClock();
        var ready = false;
        var checks = 0;

        var up = await ReadinessWait.UntilAsync(
            ready: () => { if (++checks >= 60) ready = true; return ready; },
            budget: TimeSpan.FromSeconds(45),
            now: clock.Now, delay: clock.Delay);

        Assert.True(up, "a brain that came up at ~15 s was refused");
        Assert.Equal(TimeSpan.FromSeconds(14.75), clock.Slept);
    }

    [Fact]
    public async Task A_brain_that_never_comes_up_gives_up_inside_the_budget()
    {
        // THE BUDGET IS NOT OPTIONAL. This runs on a binder thread and the pool is
        // 16; a turn that waited forever would take the link down for every
        // caller, which is a worse failure than the one being fixed.
        var clock = new FakeClock();

        var up = await ReadinessWait.UntilAsync(
            ready: () => false,
            budget: TimeSpan.FromSeconds(45),
            now: clock.Now, delay: clock.Delay);

        Assert.False(up);
        Assert.True(clock.Slept <= TimeSpan.FromSeconds(45),
            $"overran its own budget by {(clock.Slept - TimeSpan.FromSeconds(45)).TotalSeconds:N2}s");
        Assert.True(clock.Slept >= TimeSpan.FromSeconds(44.5),
            "gave up well before the budget, which is the old bug in slower clothing");
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_not_ready_rather_than_blowing_up()
    {
        // THE PROBE READS A NODE THAT MAY NOT EXIST YET. During startup the thing
        // being asked about is half-built, and a throw there is the normal case -
        // reporting it as an error would turn "still loading" into a crash on the
        // one path where a person is already waiting.
        var clock = new FakeClock();
        var checks = 0;

        var up = await ReadinessWait.UntilAsync(
            ready: () => ++checks < 5 ? throw new InvalidOperationException("no node yet") : true,
            budget: TimeSpan.FromSeconds(45),
            now: clock.Now, delay: clock.Delay);

        Assert.True(up);
        Assert.Equal(4, clock.Sleeps);
    }

    [Fact]
    public async Task Cancelling_answers_no_rather_than_throwing()
    {
        // Every caller is on a path where the alternative to an answer is a person
        // being told to ask again; an exception there just becomes a worse
        // sentence.
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var up = await ReadinessWait.UntilAsync(
            ready: () => false,
            budget: TimeSpan.FromSeconds(45),
            now: clock.Now, delay: clock.Delay, ct: cts.Token);

        Assert.False(up);
        Assert.Equal(0, clock.Sleeps);
    }

    [Fact]
    public async Task A_cancelled_token_still_reports_something_that_is_already_up()
    {
        // READY BEATS CANCELLED. The answer is in hand; throwing it away because a
        // token flipped would be inventing a failure out of a success.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.True(await ReadinessWait.UntilAsync(
            ready: () => true, budget: TimeSpan.FromSeconds(45), ct: cts.Token));
    }

    [Fact]
    public async Task A_zero_budget_still_checks_once()
    {
        // "Do not wait" is not "do not look". A caller that computes a budget badly
        // should get the honest current state, not an automatic no.
        Assert.True(await ReadinessWait.UntilAsync(
            ready: () => true, budget: TimeSpan.Zero));

        Assert.False(await ReadinessWait.UntilAsync(
            ready: () => false, budget: TimeSpan.Zero));
    }

    [Fact]
    public async Task It_says_it_is_waiting_once_and_not_once_per_poll()
    {
        // A LINE PER POLL IS 180 LINES FOR ONE TURN, and on Android chatty then
        // drops the ones that matter - which is how the original stall went three
        // days without a single log line explaining it.
        var clock = new FakeClock();
        var said = new List<string>();
        var checks = 0;

        await ReadinessWait.UntilAsync(
            ready: () => ++checks >= 40,
            budget: TimeSpan.FromSeconds(45),
            say: said.Add,
            now: clock.Now, delay: clock.Delay);

        Assert.Equal(2, said.Count);
        Assert.Contains("not up yet", said[0]);
        Assert.Contains("up now", said[1]);

        // BOTH LINES MUST READ AS FRAGMENTS after a caller's subject, because
        // that is how they are used: the service logs "the brain is " + line.
        // The first version said "it came up", which printed on the P30 as
        // "serve: the brain is it came up; carrying on".
        foreach (var line in said)
            Assert.False(line.StartsWith("it ", StringComparison.Ordinal),
                $"'the brain is {line}' does not read as a sentence");
    }

    [Fact]
    public async Task Nothing_is_said_when_there_was_nothing_to_wait_for()
    {
        // Silence is right here: a healthy turn is not news, and a "waiting…" line
        // on every question is how a log stops being read.
        var said = new List<string>();

        await ReadinessWait.UntilAsync(
            ready: () => true, budget: TimeSpan.FromSeconds(45), say: said.Add);

        Assert.Empty(said);
    }
}
