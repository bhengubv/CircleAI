// ColdTurnTests.cs
//
// "Voice still doesn't respond to me."
//
// It did not, and here is exactly how. Measured on a P30 on 2026-10-10: the
// phone heard its name, said hello, heard the question, played the earcon - and
// then WakeTurn.AskAsync found the brain not resident, logged "brain is not
// ready - nothing to ask", returned null, and the turn ended without a sound.
// It never asked the brain to start. It never said anything.
//
// The link path had already been fixed for the same fault a day earlier and the
// wake path never got the fix, because the rule lived in a private static on an
// Android Service that no test leg can see. So the rule now lives in ColdTurn,
// and these are the assertions that could not previously exist.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class ColdTurnTests
{
    /// <summary>A sleep that returns at once, so a 45 second budget costs nothing.</summary>
    private static Task Instant(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <summary>A sleep that never returns - the turn finishes before any filler.</summary>
    private static Task Never(TimeSpan _, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct);

    private sealed class Voice
    {
        public List<string> Said { get; } = [];
        public Task Say(string line, CancellationToken _) { Said.Add(line); return Task.CompletedTask; }
    }

    /// <summary>A clock that moves a stated amount every time it is read.</summary>
    private static Func<DateTime> Ticking(TimeSpan per)
    {
        var at = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        return () => { var was = at; at += per; return was; };
    }

    private static Func<CancellationToken, Task<string?>> Answers(string text) =>
        _ => Task.FromResult<string?>(text);

    // ── the brain is already up: nothing is started, nothing is said ──────────

    [Fact]
    public async Task A_warm_brain_is_asked_at_once_and_never_restarted()
    {
        // THE COMMONEST TURN AND THE ONE A BUG HERE RUINS. Starting something
        // already running is noise, and a filler on a fast answer makes every
        // good turn a second slower.
        var voice = new Voice();
        var started = 0;

        var r = await ColdTurn.AnswerAsync(
            ready: () => true,
            start: () => started++,
            ask: Answers("Three colors: red, blue, green."),
            say: voice.Say,
            delay: Never);

        Assert.Equal("Three colors: red, blue, green.", r.Answer);
        Assert.Null(r.Excuse);
        Assert.False(r.Filled);
        Assert.Equal(0, started);
        Assert.Empty(voice.Said);
    }

    [Fact]
    public async Task A_warm_brain_that_thinks_too_long_says_so_and_does_not_claim_to_be_down()
    {
        // ONE LINE PER CAUSE. "My brain is still coming up" while it is up and
        // generating is a lie about a state the person can check by asking again.
        var voice = new Voice();

        var r = await ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: Answers("Cape Town."),
            say: voice.Say,
            delay: Instant);

        Assert.True(r.Filled);
        Assert.Equal([AssistantPersona.Thinking], voice.Said);
        Assert.Equal("Cape Town.", r.Answer);
    }

    // ── the brain is down: the question is the instruction to start ───────────

    [Fact]
    public async Task A_cold_brain_is_started_rather_than_reported_as_unavailable()
    {
        // THE WHOLE DEFECT IN ONE ASSERTION. WakeTurn checked IsReady, found
        // false, and returned null - so the brain it was waiting for had nobody
        // left to start it, and the turn died of a condition it could have fixed.
        var started = 0;
        var up = false;

        var r = await ColdTurn.AnswerAsync(
            ready: () => up,
            start: () => { started++; up = true; },
            ask: Answers("It is nine o'clock."),
            delay: Instant,
            now: Ticking(TimeSpan.FromMilliseconds(10)));

        Assert.Equal(1, started);
        Assert.Equal("It is nine o'clock.", r.Answer);
    }

    [Fact]
    public async Task A_cold_brain_gets_the_coming_up_line_not_the_thinking_one()
    {
        var voice = new Voice();
        var checks = 0;

        var r = await ColdTurn.AnswerAsync(
            ready: () => ++checks > 3,          // comes up on the fourth look
            start: null,
            ask: Answers("Done."),
            say: voice.Say,
            delay: Instant,
            now: Ticking(TimeSpan.FromMilliseconds(10)));

        Assert.Equal([AssistantPersona.NotUpYet], voice.Said);
        Assert.Equal("Done.", r.Answer);
    }

    [Fact]
    public async Task A_brain_that_never_comes_up_is_told_to_the_person_out_loud()
    {
        // THE SENTENCE THAT SILENCE WAS STANDING IN FOR.
        var voice = new Voice();

        var r = await ColdTurn.AnswerAsync(
            ready: () => false,
            start: null,
            ask: _ => throw new InvalidOperationException("must not be asked"),
            say: voice.Say,
            budget: TimeSpan.FromSeconds(45),
            delay: Instant,
            now: Ticking(TimeSpan.FromSeconds(5)));

        Assert.Null(r.Answer);
        Assert.Equal(AssistantPersona.CouldNotStart, r.Excuse);
        Assert.Contains(AssistantPersona.CouldNotStart, voice.Said);
    }

    [Fact]
    public async Task The_holding_line_finishes_before_the_excuse_begins()
    {
        // TWO SOUNDS AT ONCE IS WORSE THAN EITHER. Both go through one voice, so
        // the filler must be awaited to completion before anything else speaks -
        // and on this path the filler is still playing when the budget expires.
        var order = new List<string>();
        var holding = new TaskCompletionSource();

        async Task Say(string line, CancellationToken ct)
        {
            order.Add("start:" + line);
            if (line == AssistantPersona.NotUpYet) await holding.Task;
            order.Add("end:" + line);
        }

        var turn = ColdTurn.AnswerAsync(
            ready: () => false,
            start: null,
            ask: Answers("never"),
            say: Say,
            delay: Instant,
            now: Ticking(TimeSpan.FromSeconds(10)));

        // The filler is mid-sentence and the wait has already run out.
        Assert.False(turn.IsCompleted);
        holding.SetResult();
        await turn;

        Assert.Equal(
        [
            "start:" + AssistantPersona.NotUpYet,
            "end:" + AssistantPersona.NotUpYet,
            "start:" + AssistantPersona.CouldNotStart,
            "end:" + AssistantPersona.CouldNotStart,
        ], order);
    }

    [Fact]
    public async Task It_stops_waiting_the_moment_the_brain_says_it_has_given_up()
    {
        // CircleNeuronService's own remarks: a terminal failure was reported in
        // about two seconds and the caller still waited its full timeout,
        // "because polling a ready-flag cannot tell the difference between
        // still-loading and never-going-to."
        var looks = 0;

        var r = await ColdTurn.AnswerAsync(
            ready: () => { looks++; return false; },
            start: null,
            ask: Answers("never"),
            budget: TimeSpan.FromSeconds(45),
            givenUp: () => looks >= 2,
            delay: Instant,
            now: Ticking(TimeSpan.FromMilliseconds(1)));

        Assert.Equal(AssistantPersona.CouldNotStart, r.Excuse);
        Assert.True(looks <= 3, $"gave up after {looks} looks, not the whole budget");
    }

    // ── a caller that streams is judged on its first sound ───────────────────

    [Fact]
    public async Task A_streamed_answer_already_being_spoken_is_not_interrupted()
    {
        // THE DEFECT THIS PARAMETER EXISTS FOR. WakeTurn pushes sentences into
        // SpokenReply as the model writes them, so by the time ask() returns
        // several may already have been heard - and a filler decided on "has ask
        // returned" would queue "let me think" behind the first sentence, in the
        // middle of the answer.
        var voice = new Voice();
        var slow = new TaskCompletionSource<string?>();

        var turn = ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: _ => slow.Task,
            say: voice.Say,
            started: Task.CompletedTask,       // the first sentence is already out
            delay: Instant);

        slow.SetResult("…and that is why.");
        var r = await turn;

        Assert.Empty(voice.Said);
        Assert.False(r.Filled);
        Assert.Equal("…and that is why.", r.Answer);
    }

    [Fact]
    public async Task A_stream_that_produces_nothing_at_all_still_ends_in_a_sentence()
    {
        // started never completes, because nothing was ever spoken. Racing it
        // alone would hang the turn; racing it WITH the answer is why both are
        // waited on.
        var voice = new Voice();

        var r = await ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: Answers(""),
            say: voice.Say,
            started: new TaskCompletionSource().Task,
            delay: Never);

        Assert.False(r.Answered);
        Assert.Equal([AssistantPersona.CouldNotStart], voice.Said);
    }

    // ── nothing ends in silence ──────────────────────────────────────────────

    [Fact]
    public async Task A_brain_that_answers_with_nothing_still_makes_a_sound()
    {
        var voice = new Voice();

        var r = await ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: Answers("   "),
            say: voice.Say,
            delay: Never);

        Assert.False(r.Answered);
        Assert.Equal([AssistantPersona.CouldNotStart], voice.Said);
    }

    [Theory]
    [InlineData(true, "an answer")]
    [InlineData(true, "")]
    [InlineData(false, "an answer")]
    public async Task No_outcome_is_silent(bool up, string answer)
    {
        // THE GUARD. "Produced nothing and explained nothing" is the defect this
        // type removes; it must not come back as a fourth path through it.
        var r = await ColdTurn.AnswerAsync(
            ready: () => up,
            start: null,
            ask: Answers(answer),
            delay: Instant,
            now: Ticking(TimeSpan.FromMilliseconds(10)));

        Assert.False(r.Silent);
    }

    // ── it stays out of the way ──────────────────────────────────────────────

    [Fact]
    public async Task A_cancelled_turn_says_nothing_to_a_person_who_has_left()
    {
        var voice = new Voice();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await ColdTurn.AnswerAsync(
            ready: () => false,
            start: null,
            ask: Answers("never"),
            say: voice.Say,
            delay: Instant,
            ct: cts.Token);

        Assert.Empty(voice.Said);
        Assert.Null(r.Answer);
    }

    [Fact]
    public async Task A_brain_that_fails_mid_answer_is_not_dressed_up_as_slowness()
    {
        // IT OWNS READINESS, NOT GENERATION. Swallowing this would turn every
        // crash into "ask me again in a moment" for ever, and the caller already
        // has a handler that logs the real fault.
        var voice = new Voice();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: _ => throw new InvalidOperationException("native abort"),
            say: voice.Say,
            delay: Never));
    }

    [Fact]
    public async Task A_voice_that_cannot_speak_does_not_lose_the_answer()
    {
        var r = await ColdTurn.AnswerAsync(
            ready: () => true,
            start: null,
            ask: Answers("Twenty-six degrees."),
            say: (_, _) => throw new InvalidOperationException("no audio device"),
            delay: Instant);

        Assert.Equal("Twenty-six degrees.", r.Answer);
    }

    [Fact]
    public async Task A_start_that_throws_does_not_end_the_turn_that_needed_it()
    {
        // The wait below is the real test of whether the start worked. A service
        // that reported failure and came up anyway is a phone answering a
        // question, which is the only thing being measured.
        var looks = 0;

        var r = await ColdTurn.AnswerAsync(
            ready: () => ++looks > 2,
            start: () => throw new InvalidOperationException("background start refused"),
            ask: Answers("Yes."),
            delay: Instant,
            now: Ticking(TimeSpan.FromMilliseconds(10)));

        Assert.Equal("Yes.", r.Answer);
    }

    [Fact]
    public async Task A_caller_with_no_voice_gets_the_sentence_back_instead()
    {
        // The link has no speaker - the app on the other end does the talking -
        // so the excuse has to travel rather than be said.
        var r = await ColdTurn.AnswerAsync(
            ready: () => false,
            start: null,
            ask: Answers("never"),
            say: null,
            delay: Instant,
            now: Ticking(TimeSpan.FromSeconds(10)));

        Assert.Equal(AssistantPersona.CouldNotStart, r.Excuse);
        Assert.False(r.Filled);
    }

    [Fact]
    public void The_lines_it_speaks_name_no_machinery_and_quote_no_figures()
    {
        // SHIP THE DISH. Spoken to somebody who does not know what a model is.
        foreach (var line in new[]
                 { AssistantPersona.Thinking, AssistantPersona.NotUpYet, AssistantPersona.CouldNotStart })
        {
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.DoesNotMatch(@"\d", line);
            Assert.DoesNotContain("model", line, StringComparison.OrdinalIgnoreCase);
        }

        // And the two waits are different sentences, because they are different
        // causes and one of them would otherwise be a lie.
        Assert.NotEqual(AssistantPersona.Thinking, AssistantPersona.NotUpYet);
    }
}
