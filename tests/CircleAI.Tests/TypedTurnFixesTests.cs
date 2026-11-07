// TypedTurnFixesTests.cs
//
// What the release test of 2026-11-07 found in the typed chat on the P30, each held here so it stays found. "Name
// one red fruit" came back with an old question about a bird answered too, because the link filed every sentence
// anybody typed as something known about them; and a question about a black cat waited 15,3 s for 592 characters
// on vets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class TypedTurnFixesTests
{
    private static AtomCandidate[] Read(string said) =>
        [.. new CueExtractor().Extract(new EpisodicMemoryEntry { UserText = said })];

    [Theory]
    [InlineData("Name one red fruit")]
    [InlineData("Name one yellow fruit.")]
    [InlineData("Give me three names for a black cat")]
    [InlineData("Three names for a black cat.")]
    [InlineData("What is my name?")]
    [InlineData("Which of those three is the most popular?")]
    [InlineData("Thanks, that is all I needed for today.")]
    [InlineData("I have a health question")]
    public void What_is_asked_is_not_kept_as_something_known(string said) => Assert.Empty(Read(said));

    [Theory]
    [InlineData("My name is Thabo")]
    [InlineData("my name's Thabo")]
    [InlineData("My daughter's name is Lindiwe and she turns seven next week.")]
    [InlineData("I live in Durban")]
    [InlineData("I work at Shoprite")]
    [InlineData("I am a plumber")]
    [InlineData("I'm allergic to penicillin")]
    public void What_somebody_says_about_themselves_is_kept_as_a_fact(string said)
    {
        var candidate = Assert.Single(Read(said));

        Assert.Equal(AtomKind.Fact, candidate.Atom.Kind);
        Assert.True(candidate.Certain);
    }

    [Theory]
    [InlineData("My tap")]
    [InlineData("My kitchen tap won't stop dripping")]
    [InlineData("I live")]
    public void Its_own_cue_is_not_enough_without_the_rest_of_a_fact(string said) => Assert.Empty(Read(said).Where(c => c.Certain));

    // As the link filed them: the words and nothing to say why.
    private static MemoryAtom Bare(string text) => new() { Text = text };

    [Theory]
    [InlineData("Name one yellow fruit", false)]
    [InlineData("What is my name?", false)]
    [InlineData("My name is Thabo", true)]
    [InlineData("Never restart the phone without asking me", true)]
    public void What_the_link_filed_unjudged_stands_only_if_it_would_be_kept_today(string text, bool stands) =>
        Assert.Equal(stands, new AtomLearner().Stands(Bare(text)));

    [Fact]
    public void An_atom_with_something_to_say_for_itself_is_not_read_again()
    {
        var learner = new AtomLearner();

        Assert.True(learner.Stands(new MemoryAtom { Kind = AtomKind.Fact, Text = "The gate code is 4417." }));
        Assert.True(learner.Stands(new MemoryAtom { Text = "Used the back road", SourceEpisode = Guid.NewGuid() }));
        Assert.True(learner.Stands(new MemoryAtom { Text = "Used the back road", Outcome = DecisionOutcome.Failed }));
        Assert.True(learner.Stands(new MemoryAtom { Text = "Used the back road", Challenge = "The bridge was out" }));
    }

    [Fact]
    public async Task What_is_said_goes_through_the_memorys_judgement_and_what_is_asked_leaves_nothing()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"circleai-typed-{Guid.NewGuid():N}");
        try
        {
            using var memory = new MemoryService(folder);

            await memory.LearnAsync("Name one red fruit");
            await memory.LearnAsync("My name is Thabo");

            Assert.Equal("My name is Thabo", Assert.Single(await memory.AllAsync()).Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class Knowing(params string[] facts) : CircleAI.Assistant.IRemembers
    {
        public int Asked { get; private set; }

        public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CircleAI.Assistant.Remembered>> RecallAsync(string about, int limit = 4, CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<CircleAI.Assistant.Remembered>>([.. facts.Select(f => new CircleAI.Assistant.Remembered(f))]);
        }
    }

    [Theory]
    [InlineData("My test word is mango")]
    [InlineData("My name is Thabo")]
    [InlineData("I live in Durban")]
    public async Task Somebody_telling_it_something_is_not_asking_what_it_knows(string said)
    {
        var memory = new Knowing("The test code is blue 7.");

        Assert.Empty(await CircleAI.Assistant.Recalling.AboutAsync(memory, said));
        Assert.Equal(0, memory.Asked);
    }

    // Measured on the P30 on 2026-11-07: "test" alone put the code in front of the word, and the 2B asked what
    // the heading meant. "Name" alone would do the same for anybody who has told it their name.
    [Theory]
    [InlineData("Name one red fruit", "My name is Thabo")]
    [InlineData("Is this a good test for a child?", "The test code is blue 7.")]
    public async Task A_fact_with_one_word_in_common_stays_out_of_the_question(string asked, string fact) =>
        Assert.Empty(await CircleAI.Assistant.Recalling.AboutAsync(new Knowing(fact), asked));

    [Theory]
    [InlineData("What is my name?", "My name is Thabo")]
    [InlineData("What should I cook for Lindiwe?", "My daughter's name is Lindiwe and she turns seven next week.")]
    [InlineData("What colour is the test code?", "The test code is blue 7.")]
    [InlineData("Is penicillin safe for me?", "I'm allergic to penicillin")]
    public async Task A_fact_about_the_question_comes_with_it_in_the_persons_own_voice(string asked, string fact)
    {
        var known = await CircleAI.Assistant.Recalling.AboutAsync(new Knowing(fact), asked);

        var composed = CircleAI.Assistant.Recalling.Ask(asked, known);

        Assert.Contains(fact, composed);
        Assert.DoesNotContain("them", composed);
        Assert.EndsWith(asked, composed);
        Assert.Equal(asked, CircleAI.Assistant.Recalling.Question(composed));
    }

    private static Task<string> Notes(string asked) =>
        new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600).BuildContextAsync(asked);

    [Theory]
    [InlineData("Give me three names for a black cat")]
    [InlineData("What colour is a black cat?")]
    [InlineData("I don't know how to tell my kids.")]
    [InlineData("My kitchen tap won't stop dripping.")]
    public async Task A_word_in_common_is_not_a_typed_turns_subject_either(string asked) =>
        Assert.Empty(await Notes(asked));

    [Theory]
    [InlineData("How do I vaccinate my dog?", "looking-after-your-animals")]
    [InlineData("How do I plant maize?", "growing-food-at-home")]
    [InlineData("Help me plan meals today", "feeding-your-family-well")]
    [InlineData("Help me plan my money this month", "plan-your-money-this-month")]
    public async Task A_typed_question_about_a_skill_still_gets_it(string asked, string skill) =>
        Assert.Contains(skill, await Notes(asked));

    [Fact]
    public async Task A_typed_turn_keeps_the_longer_note_a_spoken_one_goes_without()
    {
        var builder = new SkillContextBuilder(ConsumerSkillPack.Shared, maxChars: 600);

        var typed = await builder.BuildContextAsync("How do I plant maize?", spoken: false, CancellationToken.None);
        var spoken = await builder.BuildContextAsync("How do I plant maize?", spoken: true, CancellationToken.None);

        Assert.True(typed.Length > spoken.Length, $"{typed.Length} typed, {spoken.Length} spoken");
    }
}
