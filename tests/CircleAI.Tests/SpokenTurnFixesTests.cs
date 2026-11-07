// SpokenTurnFixesTests.cs
//
// What the bar of 2026-11-07 found on the P30, each held here so it stays found: 122 turns, 49 wrong, and most
// of the wrong ones not the brain's fault. The voice going quiet for good, a sum read out as symbols, a
// timer that was never set, today's date for Christmas, a question cut off by its own pause.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Requests;
using CircleAI.Skills;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SpokenTurnFixesTests
{
    private static async Task<List<string>> Said(params string[] fragments)
    {
        var said = new List<string>();
        await using (var mouth = new SpokenReply((sentence, _) => { lock (said) said.Add(sentence); return Task.CompletedTask; }))
        {
            foreach (var fragment in fragments) mouth.Push(fragment);
            await mouth.CompleteAsync();
        }
        return said;
    }

    [Fact]
    public async Task Code_is_written_but_not_read_out()
    {
        var said = await Said("Here it is.\n```python\ns = input(); print(s[::-1])\n```\n");

        Assert.Equal(["Here it is.", AssistantPersona.CodeLeftOut], said);
    }

    [Fact]
    public async Task A_fence_split_across_fragments_is_still_a_fence()
    {
        var said = await Said("Try this. `", "``py", "thon\nprint(1)\n`", "``\n", "That is all.");

        Assert.Equal(["Try this.", "That is all.", AssistantPersona.CodeLeftOut], said);
    }

    [Fact]
    public async Task A_tool_call_with_nobody_to_make_it_is_never_said()
    {
        var said = await Said("<tool_call>\n<function=web_search>\n<parameter=query>\npopulation\n</parameter>\n</function>\n</tool_call>");

        Assert.Equal([AssistantPersona.CannotDoThatYet], said);
    }

    [Fact]
    public async Task Plain_words_go_through_as_they_were()
    {
        Assert.Equal(["The sky is blue.", "It scatters light."], await Said("The sky is blue. ", "It scatters light."));
    }

    [Theory]
    [InlineData("$3 \\times 2 = 6$ legs", "3 times 2 equals 6 legs")]
    [InlineData("That is $6 + 8 = 14$.", "That is 6 plus 8 equals 14.")]
    [InlineData("\\frac{1}{2} of it", "1 over 2 of it")]
    [InlineData("It costs R5.", "It costs R5.")]
    public void A_sum_is_said_in_words(string piece, string heard) => Assert.Equal(heard, PlainReply.ForTheEar(piece));

    [Theory]
    [InlineData("```")]
    [InlineData("...")]
    [InlineData("()")]
    public void What_has_no_sound_in_it_is_not_given_to_a_voice(string piece) => Assert.Equal(string.Empty, PlainReply.ForTheEar(piece));

    [Theory]
    [InlineData("What is the", true)]
    [InlineData("What is the tallest?", true)]
    [InlineData("Who was the first person to walk on", true)]
    [InlineData("Um,", true)]
    [InlineData("My name is", true)]
    [InlineData("The capital of Peru is", true)]
    [InlineData("What is", true)]
    [InlineData("What time is", true)]
    [InlineData("How old is", true)]
    [InlineData("What is her", true)]
    [InlineData("Is that", true)]
    [InlineData("It will be", true)]
    [InlineData("How do I get to", true)]
    [InlineData("What is the weather in", true)]
    [InlineData("What's the Zulu word for", true)]
    [InlineData("I would like", true)]
    [InlineData("I live in", true)]
    [InlineData("Tell me what", true)]
    [InlineData("I want to know how", true)]
    [InlineData("And so", true)]
    [InlineData("Name one kind of bird.", false)]
    [InlineData("What is the capital of Peru?", false)]
    [InlineData("", false)]
    public void Words_that_stop_on_a_word_that_cannot_end_a_sentence_look_unfinished(string words, bool open) =>
        Assert.Equal(open, TurnEnd.LooksUnfinished(words));

    // Measured on the P30 on 2026-11-07: the first held the microphone 1,5 s past its last word, 3,24 s to the first
    // sound. Each of these ends on a word the list held because some other sentence goes on from it.
    [Theory]
    [InlineData("What could I get her?")]
    [InlineData("What should I do?")]
    [InlineData("What does a plumber do?")]
    [InlineData("What could it be?")]
    [InlineData("What's going on?")]
    [InlineData("What is that?")]
    [InlineData("Why is that?")]
    [InlineData("Where are you from?")]
    [InlineData("What are you looking at?")]
    [InlineData("Who should I talk to?")]
    [InlineData("What is it for?")]
    [InlineData("What is it like?")]
    [InlineData("What's the weather like?")]
    [InlineData("Give it to her.")]
    [InlineData("Turn it on.")]
    [InlineData("Go on.")]
    [InlineData("Hold on")]
    [InlineData("Yes it is.")]
    [InlineData("I think so.")]
    [InlineData("Why?")]
    [InlineData("How?")]
    [InlineData("But why?")]
    [InlineData("Tell me why.")]
    public void A_sentence_that_ends_on_a_word_that_sometimes_goes_on_is_finished(string words) =>
        Assert.False(TurnEnd.LooksUnfinished(words));

    [Fact]
    public void The_gate_waits_longer_when_it_is_told_to()
    {
        var gate = new SpeechGate { Hangover = TimeSpan.FromMilliseconds(300), LearnsWhileOpen = false };
        var quiet = new float[1600];
        var loud = Enumerable.Repeat(0.2f, 1600).ToArray();

        for (var i = 0; i < 40; i++) gate.Wants(quiet);
        gate.Wants(loud);
        Assert.True(gate.Open);

        gate.Hold = TimeSpan.FromMilliseconds(300);
        for (var i = 0; i < 3; i++) gate.Wants(quiet);
        Assert.True(gate.Open);
        for (var i = 0; i < 3; i++) gate.Wants(quiet);
        Assert.False(gate.Open);
    }

    private static AskRecord Asked(string words) =>
        AskRecord.Accept("id", new AskSubmission("internal:assistant", null, "s", AskLane.Assistant, words, words, Spoken: true), DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("Set a timer for ten minutes.", AssistantPersona.NoTimersYet)]
    [InlineData("Said a timer for ten minutes.", AssistantPersona.NoTimersYet)]
    [InlineData("Wake me up at six tomorrow morning.", AssistantPersona.NoTimersYet)]
    [InlineData("Remind me to call Sipho at three.", AssistantPersona.NoRemindersYet)]
    [InlineData("Play some jazz.", AssistantPersona.NoMusicYet)]
    [InlineData("Call my mother.", AssistantPersona.NoMessagesYet)]
    [InlineData("Turn on the flashlight.", AssistantPersona.NoSettingsYet)]
    [InlineData("Turn the volume down.", AssistantPersona.NoSettingsYet)]
    public async Task What_it_cannot_do_it_says_it_cannot(string words, string answer) =>
        Assert.Equal(answer, await new NotYetShortcut().TryAnswerAsync(Asked(words), CancellationToken.None));

    [Theory]
    [InlineData("How do you make pap?")]
    [InlineData("Why do people play chess?")]
    [InlineData("What is a timer?")]
    public async Task A_question_about_those_things_is_still_a_question(string words) =>
        Assert.Null(await new NotYetShortcut().TryAnswerAsync(Asked(words), CancellationToken.None));

    [Theory]
    [InlineData("Stop.", AssistantPersona.LeftIt)]
    [InlineData("Never mind.", AssistantPersona.LeftIt)]
    [InlineData("Thanks.", AssistantPersona.Welcome)]
    [InlineData("Thank you very much.", AssistantPersona.Welcome)]
    public async Task A_word_gets_a_word(string words, string answer) =>
        Assert.Equal(answer, await new MannersShortcut().TryAnswerAsync(Asked(words), CancellationToken.None));

    [Fact]
    public async Task Thanks_with_more_to_it_is_a_sentence() =>
        Assert.Null(await new MannersShortcut().TryAnswerAsync(Asked("You know what, thanks for listening."), CancellationToken.None));

    [Fact]
    public async Task What_day_christmas_falls_on_is_not_todays_date()
    {
        var clock = new DeviceClockShortcut();
        Assert.Null(await clock.TryAnswerAsync(Asked("What day does Christmas fall on this year?"), CancellationToken.None));
        Assert.NotNull(await clock.TryAnswerAsync(Asked("What's the date today?"), CancellationToken.None));
        Assert.NotNull(await clock.TryAnswerAsync(Asked("What day is it?"), CancellationToken.None));
    }

    [Theory]
    [InlineData("What day does Christmas fall on this year?", "Christmas falls on a Friday this year.")]
    [InlineData("What day is Heritage Day?", "Heritage Day falls on a Friday next year.")]
    [InlineData("Which day is New Year's Day?", "New Year's Day falls on a Friday next year.")]
    public void The_day_a_date_falls_on_is_worked_out(string words, string answer) =>
        Assert.Equal(answer, CalendarFactsShortcut.DayOfTheWeek(words, new DateTime(2026, 11, 7)));

    [Fact]
    public void A_day_nobody_keeps_is_not_answered() =>
        Assert.Null(CalendarFactsShortcut.DayOfTheWeek("What day is your birthday?", new DateTime(2026, 11, 7)));

    [Theory]
    [InlineData("No, I meant eight times nine.", "72")]
    [InlineData("Sorry, I mean 8 times 9", "72")]
    [InlineData("And 6 plus 7?", "13")]
    public void A_correction_is_the_same_sum(string words, string answer)
    {
        Assert.True(ArithmeticIntent.TryAnswer(words, out var said));
        Assert.Equal(answer, said);
    }

    private sealed class OneSkill : ISkillStore
    {
        private static readonly SkillSummary Raising = new("raising.children", "Raising children", "Bringing up kids on a small budget", ["kids", "children", "grant"], default);

        public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SkillSummary>>([Raising]);

        public Task<IReadOnlyList<SkillSummary>> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SkillSummary>>([Raising]);

        public Task<SkillDetail?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<SkillDetail?>(new SkillDetail(Raising.Id, Raising.Name, Raising.Description,
                "Register the birth at home affairs. Apply for the grant at SASSA. " + string.Join(" ", Enumerable.Repeat("Keep the slip you are given.", 30)),
                Raising.Tags, default, DateTimeOffset.UnixEpoch));

        public Task<SkillDetail> UpsertAsync(string? id, SkillDraft draft, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task One_word_in_common_is_not_a_spoken_turns_subject()
    {
        var builder = new SkillContextBuilder(new OneSkill());

        Assert.Empty(await builder.BuildContextAsync("I don't know how to tell my kids.", spoken: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_spoken_turn_named_by_the_skill_gets_a_moments_reading_of_it()
    {
        var block = await new SkillContextBuilder(new OneSkill()).BuildContextAsync("How do I get the grant for my children?", spoken: true, CancellationToken.None);

        Assert.Contains("grant", block, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(block.Length, 1, 240);
    }
}
