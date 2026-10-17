// AnswerFixesTests.cs
//
// THE WRONG ANSWERS FROM THE P30'S QUALITY RUN ON 2026-10-17, EACH PINNED TO ITS FIX.
// Health and meal tiles answered out of the wrong skill; a garble answered "No."; "Remember
// that..." answered with nonsense and saved nothing; and the checker passed three of those
// and failed a right answer.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Quality;
using CircleAI.Assistant.Requests;
using CircleAI.Memory;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class AnswerFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "circleai-answerfixes-" + Guid.NewGuid().ToString("N"));
    private MemoryService? _memory;

    private MemoryService Memory => _memory ??= new MemoryService(_dir);

    public void Dispose()
    {
        _memory?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static AskRecord Ask(string said, string caller = "app:test") =>
        AskRecord.Accept(Guid.NewGuid().ToString("N"),
            new AskSubmission(caller, null, string.Empty, AskLane.Foreground, said), DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------- a question still to come

    [Theory]
    [InlineData("I have a health question", "Sure. What's your health question?")]
    [InlineData("I have a legal question", "Sure. What's your legal question?")]
    [InlineData("Hey, B. I have a legal question.", "Sure. What's your legal question?")]
    [InlineData("I have a question", "Sure. What's your question?")]
    [InlineData("I have a question about my job or my rights at work",
                "Sure. What's your question about your job or your rights at work?")]
    [InlineData("Help me deal with a government office", "Sure. What do you need to sort out with the government office?")]
    [InlineData("Help me with my CV", "Sure. What do you need to sort out with your CV?")]
    public async Task A_tile_opener_is_answered_by_asking_for_the_question(string said, string expected)
    {
        var answer = await new QuestionOpenerShortcut(() => "Hey B").TryAnswerAsync(Ask(said), CancellationToken.None);
        Assert.Equal(expected, answer);
    }

    [Theory]
    [InlineData("I have a question: what is the capital of France?")]
    [InlineData("Help me plan meals")]
    [InlineData("What is the capital of Kenya?")]
    public async Task A_question_that_comes_with_it_goes_on_to_the_model(string said)
    {
        Assert.Null(await new QuestionOpenerShortcut().TryAnswerAsync(Ask(said), CancellationToken.None));
    }

    // ---------------------------------------------------------------- remember and recall

    [Fact]
    public async Task Remember_writes_it_down_and_says_so_in_the_second_person()
    {
        var remember = new RememberShortcut(_ => Memory, () => "Hey B");

        var answer = await remember.TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);

        Assert.Equal("OK, I'll remember that your clinic appointment moved to Friday.", answer);
        var all = await Memory.AllAsync();
        Assert.Contains(all, a => a.Text == "My clinic appointment moved to Friday.");
    }

    [Fact]
    public async Task Remember_after_the_wake_phrase_is_still_an_instruction()
    {
        var answer = await new RememberShortcut(_ => Memory, () => "Hey B")
            .TryAnswerAsync(Ask("Hey, B. Please remember that I am allergic to penicillin."), CancellationToken.None);
        Assert.Equal("OK, I'll remember that you are allergic to penicillin.", answer);
    }

    [Fact]
    public async Task Recall_answers_from_what_it_was_told()
    {
        var remember = new RememberShortcut(_ => Memory);
        var recall = new RecallShortcut(_ => Memory);
        await remember.TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);

        var answer = await recall.TryAnswerAsync(Ask("When is my clinic appointment?"), CancellationToken.None);

        Assert.Equal("You told me your clinic appointment moved to Friday.", answer);
    }

    [Theory]
    [InlineData("What is the capital of Kenya?")]
    [InlineData("How do I plant maize?")]
    [InlineData("When is my dentist visit?")]
    public async Task Recall_leaves_every_other_question_to_the_model(string said)
    {
        await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask("Remember that my clinic appointment moved to Friday"), CancellationToken.None);
        await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask("Remember that I planted maize in the back garden"), CancellationToken.None);

        Assert.Null(await new RecallShortcut(_ => Memory).TryAnswerAsync(Ask(said), CancellationToken.None));
    }

    [Theory]
    [InlineData("Do you remember my name?")]
    [InlineData("Remember when we went to the beach")]
    [InlineData("Remember to buy milk")]
    [InlineData("What is the capital of France")]
    public async Task Only_an_instruction_to_remember_is_written_down(string said)
    {
        Assert.Null(await new RememberShortcut(_ => Memory).TryAnswerAsync(Ask(said), CancellationToken.None));
        Assert.Empty(await Memory.AllAsync());
    }

    [Fact]
    public async Task A_save_that_fails_is_never_reported_as_saved()
    {
        Assert.Equal(AssistantPersona.CouldNotRemember,
            await new RememberShortcut(_ => new ThrowingMemory()).TryAnswerAsync(Ask("Remember that my car is red"), CancellationToken.None));
        Assert.Equal(AssistantPersona.CouldNotRemember,
            await new RememberShortcut(_ => null).TryAnswerAsync(Ask("Remember that my car is red"), CancellationToken.None));
    }

    [Theory]
    [InlineData("my clinic appointment moved to Friday", "your clinic appointment moved to Friday")]
    [InlineData("I am allergic to penicillin", "you are allergic to penicillin")]
    [InlineData("your wake word is Hey B", "my wake word is Hey B")]
    [InlineData("Thabo is my brother and I'm his sister", "Thabo is your brother and you're his sister")]
    public void What_a_person_says_about_themselves_is_turned_round(string said, string back)
    {
        Assert.Equal(back, Said.ToSecondPerson(said));
    }

    // ---------------------------------------------------------------- the answer guard

    [Theory]
    [InlineData("Placeculeeai.", "No.", AssistantPersona.DidNotCatchThat)]
    [InlineData("Help me plan meals", "Yes.", AssistantPersona.DidNotCatchThat)]
    [InlineData("Is Paris in France?", "Yes.", "Yes.")]
    [InlineData("Hey, B. Is it Friday?", "No.", "No.")]
    [InlineData("You're Circle AI?", "Yes.", "Yes.")]
    [InlineData("Placeculeeai.", "No, it is Paris.", "No, it is Paris.")]
    [InlineData("Help me plan meals", "Based on your background, start with pap and beans.", "Start with pap and beans.")]
    [InlineData("Help me plan meals", "Here is the summary of the facts provided: Use cheap staples like samp.", "Use cheap staples like samp.")]
    public void A_bare_yes_or_no_answers_only_a_yes_or_no_question(string asked, string answer, string expected)
    {
        Assert.Equal(expected, AnswerGuard.Check(asked, answer));
    }

    // ---------------------------------------------------------------- the checker

    private static RecordedQuestion Q(string id) => RecordedQuestions.Load().Single(q => q.Id == id);

    [Fact]
    public void A_numbered_list_is_one_sentence()
    {
        Assert.Equal(1, AnswerExpectation.Sentences("Here are three common colors: 1. Red 2. Blue 3. Green"));
        Assert.True(AnswerExpectation.Check(Q("colours"), "Here are three common colors: 1. Red 2. Blue 3. Green").Passed);
        Assert.Equal(2, AnswerExpectation.Sentences("It is 3.5 km away. Walk north."));
    }

    [Fact]
    public void The_answers_that_wrongly_passed_now_fail()
    {
        Assert.False(AnswerExpectation.Check(Q("garbled"), "No.").Passed);
        Assert.True(AnswerExpectation.Check(Q("garbled"), AssistantPersona.DidNotCatchThat).Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-health"),
            "Yes. I can summarize the background as \"I can provide basic animal care in South Africa.\"").Passed);
        Assert.False(AnswerExpectation.Check(Q("remember-clinic"),
            "I can't remember specific appointments, but I can tell you what happened: it was a Friday.").Passed);
        Assert.True(AnswerExpectation.Check(Q("remember-clinic"),
            "OK, I'll remember that your clinic appointment moved to Friday.").Passed);
        Assert.True(AnswerExpectation.Check(Q("recall-clinic"), "You told me your clinic appointment moved to Friday.").Passed);
        Assert.False(AnswerExpectation.Check(Q("tile-meals"),
            "Here is the summary of the facts provided: Feeding strategy: Use cheap staples like maize.").Passed);
    }

    // ---------------------------------------------------------------- the notes

    [Fact]
    public async Task A_meal_question_gets_the_meal_skill_not_the_budget()
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("Help me plan meals");

        Assert.Contains("feeding-your-family-well", block);
        Assert.DoesNotContain("plan-your-money-this-month", block);
        Assert.DoesNotContain("**", block);
    }

    [Fact]
    public async Task A_health_question_is_not_answered_out_of_the_animal_skill()
    {
        var block = await new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync("I have a health question");

        Assert.DoesNotContain("looking-after-your-animals", block);
    }

    private sealed class ThrowingMemory : IMemoryService
    {
        public Task<RecallResult> RecallAsync(Situation situation, RecallBudget? budget = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task RememberAsync(MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<LearnReport> LearnAsync(string wasSaid, string? subject = null, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<System.Collections.Generic.IReadOnlyList<MemoryAtom>> AllAsync(int limit = 200, CancellationToken ct = default)
            => throw new IOException("disk");
        public Task<int> CountAsync(CancellationToken ct = default) => throw new IOException("disk");
    }
}
