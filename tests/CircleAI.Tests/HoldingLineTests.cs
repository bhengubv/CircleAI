// HoldingLineTests.cs
//
// Silence is the worst answer, and it is the one the P30 gives today.
//
// Measured 2026-10-10: a question asked while the brain is not resident waits
// up to FORTY-FIVE SECONDS with no sound at all, then answers or fails. From
// across the room that is a device ignoring you - the same complaint already
// made about the dead-weight offer, in the same words.
//
// But an assistant that says "give me a moment" before EVERY answer is worse
// than one that pauses: it adds a second to every fast turn and teaches people
// to talk over it. So the whole value is in the timing, and the timing is what
// these pin - with an injected clock, so a 45 second budget is asserted in
// microseconds.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class HoldingLineTests
{
    /// <summary>A clock that only moves when the code under test waits.</summary>
    private sealed class FakeClock
    {
        public int Sleeps { get; private set; }
        public TimeSpan Slept { get; private set; }

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            Sleeps++;
            Slept += d;
            return Task.CompletedTask;
        }
    }

    private static (Func<string, CancellationToken, Task> Say, List<string> Said) Recorder()
    {
        var said = new List<string>();
        return ((line, _) => { said.Add(line); return Task.CompletedTask; }, said);
    }

    [Fact]
    public async Task A_fast_answer_is_never_talked_over()
    {
        // THE COMMONEST CASE AND THE ONE A BUG HERE RUINS. Most turns on a warm
        // phone produce their first fragment quickly; filling that gap would
        // add a second to every single answer.
        var (say, said) = Recorder();
        var clock = new FakeClock();

        var spoke = await HoldingLine.SpeakIfSlowAsync(
            Task.CompletedTask, say, AssistantPersona.NotUpYet, delay: clock.Delay);

        Assert.False(spoke);
        Assert.Empty(said);
        Assert.Equal(0, clock.Sleeps);
    }

    [Fact]
    public async Task A_slow_answer_gets_a_sound_rather_than_silence()
    {
        var (say, said) = Recorder();
        var clock = new FakeClock();
        var never = new TaskCompletionSource();

        var spoke = await HoldingLine.SpeakIfSlowAsync(
            never.Task, say, AssistantPersona.NotUpYet, delay: clock.Delay);

        Assert.True(spoke);
        Assert.Equal([AssistantPersona.NotUpYet], said);
    }

    [Fact]
    public async Task It_waits_the_patience_window_and_not_the_whole_turn()
    {
        // It must settle the question "is this slow?" and hand control back -
        // not sit on the answer. The caller still owns awaiting the reply.
        var clock = new FakeClock();
        var (say, _) = Recorder();

        await HoldingLine.SpeakIfSlowAsync(
            new TaskCompletionSource().Task, say, "x", delay: clock.Delay);

        Assert.Equal(1, clock.Sleeps);
        Assert.Equal(HoldingLine.Patience, clock.Slept);
    }

    [Fact]
    public async Task An_answer_that_lands_during_the_window_still_wins()
    {
        // Task.WhenAny resolves to the timer when both complete together, so
        // the check is repeated after the wait. Without that, a turn that
        // finished inside the window gets talked over by its own filler.
        var (say, said) = Recorder();
        var arrived = new TaskCompletionSource();

        var spoke = await HoldingLine.SpeakIfSlowAsync(
            arrived.Task, say, AssistantPersona.NotUpYet,
            delay: (_, _) => { arrived.SetResult(); return Task.CompletedTask; });

        Assert.False(spoke);
        Assert.Empty(said);
    }

    [Fact]
    public async Task A_cancelled_turn_says_nothing()
    {
        var (say, said) = Recorder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var spoke = await HoldingLine.SpeakIfSlowAsync(
            new TaskCompletionSource().Task, say, AssistantPersona.NotUpYet,
            delay: (_, _) => Task.CompletedTask, ct: cts.Token);

        Assert.False(spoke);
        Assert.Empty(said);
    }

    [Fact]
    public async Task A_voice_that_fails_does_not_take_the_turn_down()
    {
        // This exists to improve a turn that is already going badly. Failing
        // the turn because the consolation prize could not be delivered would
        // be absurd.
        var clock = new FakeClock();

        var spoke = await HoldingLine.SpeakIfSlowAsync(
            new TaskCompletionSource().Task,
            (_, _) => throw new InvalidOperationException("no audio device"),
            AssistantPersona.NotUpYet,
            delay: clock.Delay);

        Assert.False(spoke);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Nothing_to_say_means_nothing_is_said(string? line)
    {
        var (say, said) = Recorder();
        var clock = new FakeClock();

        Assert.False(await HoldingLine.SpeakIfSlowAsync(
            new TaskCompletionSource().Task, say, line!, delay: clock.Delay));
        Assert.Empty(said);
        Assert.Equal(0, clock.Sleeps);
    }

    [Fact]
    public void The_line_it_speaks_is_the_personas_and_names_no_machinery()
    {
        // SHIP THE DISH. It is spoken aloud to somebody who does not know what
        // a model is, so no ids, no numbers, no "loading".
        var said = AssistantPersona.NotUpYet;

        Assert.False(string.IsNullOrWhiteSpace(said));
        Assert.DoesNotContain("model", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loading", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\d", said);
    }
}
