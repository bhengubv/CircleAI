// RecallingTests.cs
//
// What long-term memory contributes to an answer, pinned without a phone.
//
// These exist because the read side has now been half-wired twice. First a
// RecallWanted action nobody dispatched, so the store's own field was
// permanently empty; then a recall wired to the microphone only, so typing "my
// name is Thabo" was learned and typing "what is my name?" was asked cold - the
// exact "half a person" the write side's comment warns about, in the other
// direction. Both times the code read as though it worked.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class RecallingTests
{
    // ---- what reaches the model -----------------------------------------

    [Fact]
    public void Nothing_known_leaves_the_question_exactly_as_asked()
    {
        // NOT AN EMPTY HEADING. "Things you already know about them:" followed
        // by nothing invites a small model to fill the gap, and inventing a fact
        // about somebody is the one failure this feature must not cause.
        Assert.Equal("what is my name?", Recalling.Ask("what is my name?", []));
        Assert.Equal("what is my name?", Recalling.Ask("what is my name?", null));
    }

    [Fact]
    public void What_is_known_goes_in_front_of_the_question()
    {
        var asked = Recalling.Ask("what is my name?", [new Remembered("their name is Thabo")]);

        Assert.StartsWith("Things you already know about them:", asked);
        Assert.Contains("- their name is Thabo", asked);

        // THE QUESTION IS STILL LAST. A model reads to the end and answers what
        // it finds there; burying the question above the facts changes what it
        // thinks it was asked.
        Assert.EndsWith("what is my name?", asked);
    }

    [Fact]
    public void The_preamble_is_bounded()
    {
        // Every fact is tokens read before the model reaches the question, on a
        // phone that decodes about seven tokens a second.
        var many = new List<Remembered>();
        for (var i = 0; i < 20; i++) many.Add(new Remembered($"fact {i}"));

        var lines = Recalling.Ask("go on then", many).Split('\n');
        var bullets = 0;
        foreach (var l in lines) if (l.TrimStart().StartsWith("- ")) bullets++;

        Assert.Equal(Recalling.Keep, bullets);
    }

    [Fact]
    public void Blank_facts_are_not_bulleted_into_the_prompt()
    {
        // A store that hands back an empty atom would otherwise produce "- " on
        // its own line, which reads to a model as a fact it cannot see.
        var asked = Recalling.Ask("hello", [new Remembered("   "), new Remembered("they drive a bakkie")]);

        Assert.DoesNotContain("- " + Environment.NewLine, asked);
        Assert.Contains("- they drive a bakkie", asked);
    }

    [Fact]
    public void All_blank_facts_are_the_same_as_no_facts()
    {
        Assert.Equal("hello", Recalling.Ask("hello", [new Remembered(""), new Remembered("  ")]));
    }

    // ---- what must never happen to a turn --------------------------------

    [Fact]
    public async Task A_store_that_throws_never_reaches_the_turn()
    {
        var known = await Recalling.AboutAsync(new ThrowingMemory(), "what is my name?");
        Assert.Empty(known);
    }

    [Fact]
    public async Task A_store_that_hangs_is_abandoned_not_waited_for()
    {
        // The budget is what stops a healthy-but-slow store from holding up an
        // answer somebody is waiting for.
        var started = DateTimeOffset.UtcNow;

        var known = await Recalling.AboutAsync(new HangingMemory(), "what is my name?");

        Assert.Empty(known);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3),
            "recall should abandon at its budget, not wait for the store");
    }

    [Fact]
    public async Task A_head_with_no_memory_recalls_nothing_and_does_not_throw()
    {
        // The browser. Offering nothing is correct; throwing is not.
        Assert.Empty(await Recalling.AboutAsync(null, "what is my name?"));
    }

    [Fact]
    public async Task Nothing_said_is_not_a_question_to_the_store()
    {
        var store = new CountingMemory();

        Assert.Empty(await Recalling.AboutAsync(store, "   "));
        Assert.Equal(0, store.Asked);
    }

    [Fact]
    public async Task It_asks_for_no_more_than_it_will_use()
    {
        // Fetching twenty to print four is work the phone does not need to do.
        var store = new CountingMemory();

        await Recalling.AboutAsync(store, "what is my name?");

        Assert.Equal(Recalling.Keep, store.LimitAsked);
    }

    // ---- fakes ------------------------------------------------------------

    private sealed class ThrowingMemory : IRemembers
    {
        public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Remembered>> RecallAsync(
            string about, int limit = 4, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");
    }

    private sealed class HangingMemory : IRemembers
    {
        public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<IReadOnlyList<Remembered>> RecallAsync(
            string about, int limit = 4, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return [];
        }
    }

    private sealed class CountingMemory : IRemembers
    {
        public int Asked { get; private set; }
        public int LimitAsked { get; private set; }

        public Task LearnAsync(string said, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Remembered>> RecallAsync(
            string about, int limit = 4, CancellationToken ct = default)
        {
            Asked++;
            LimitAsked = limit;
            return Task.FromResult<IReadOnlyList<Remembered>>([]);
        }
    }
}
