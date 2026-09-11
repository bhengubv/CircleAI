// ConversationStoreTests.cs
//
// The store and the memory loop, pinned without a phone, a microphone or a model.
//
// These exist because they did not. Fluxor and the memory loop shipped with
// twelve tests for "play Coldplay" and none for either of them, and the cost was
// immediate: a RecallWanted action nobody dispatched, an effect behind it that
// never ran, and a ConversationState.Recalled that was permanently empty on the
// device while the commit message said the store owned it. A reducer is a pure
// function of two arguments — there was never an excuse.
//
// WHAT EACH OF THESE IS GUARDING is written above it, because the claims they
// pin are safety claims and not style ones: what gets fed back to a small model,
// and what is allowed to grow without bound in front of a seven-tokens-a-second
// decoder.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fluxor;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class ConversationStoreTests
{
    private static ConversationState Fresh() => new(
        TurnPhase.Idle, 0, null, null, null, null, []);

    // ---- the write half of the loop -------------------------------------

    [Fact]
    public async Task Only_the_person_is_learned_never_the_answer()
    {
        // THE ONE THAT MATTERS. Feeding a small model its own replies back is how
        // a wrong answer becomes a remembered fact, and this app produced a
        // fabricated statement of law the day before this loop was closed.
        var store = new RecordingMemory();
        var effects = new MemoryEffects(store);

        await effects.OnTurnEnded(
            new TurnEnded("what time do the shops close", "Most close at six.", "en", null),
            new NullDispatcher());

        Assert.Equal(["what time do the shops close"], store.Learned);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Silence_is_not_learned(string? said)
    {
        // A cancelled turn, or one that heard a quiet room, must not write an
        // empty memory that reads back later as though somebody had said it.
        var store = new RecordingMemory();
        var effects = new MemoryEffects(store);

        await effects.OnTurnEnded(new TurnEnded(said, "anything", "en", null), new NullDispatcher());

        Assert.Empty(store.Learned);
    }

    [Fact]
    public async Task A_store_that_throws_does_not_take_down_a_turn_that_worked()
    {
        // The answer has already been spoken by the time this runs. A disk that
        // could not be written is never worth failing it after the fact.
        var effects = new MemoryEffects(new ThrowingMemory());

        var ended = await Record.ExceptionAsync(() => effects.OnTurnEnded(
            new TurnEnded("hello", "Hello.", "en", null), new NullDispatcher()));

        Assert.Null(ended);
    }

    // ---- the boundary the read half must not cross -----------------------

    [Fact]
    public void The_store_is_scoped_so_no_singleton_may_hold_a_dispatcher()
    {
        // THE TEST THAT WOULD HAVE STOPPED A WASTED BUILD. Recall was briefly
        // "fixed" by injecting IDispatcher into DeviceConversation - which is a
        // SINGLETON - so the dispatch would have reached the root scope's store
        // while the screen read its own, and looked exactly as dead as the
        // effect it replaced.
        //
        // Pinned as a fact about Fluxor rather than a habit: if a future version
        // registers these differently, or somebody adds WithLifetime(Singleton)
        // here, this is where that decision surfaces - and on the Blazor Server
        // head a singleton store would be one conversation shared by every
        // visitor, which is a good deal worse than a missing feature.
        var services = new ServiceCollection().AddConversationStore();

        var lifetimes = services
            .Where(d => d.ServiceType == typeof(IDispatcher)
                     || d.ServiceType == typeof(IStore)
                     || d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericTypeDefinition() == typeof(IState<>))
            .Select(d => d.Lifetime)
            .ToList();

        Assert.NotEmpty(lifetimes);
        Assert.All(lifetimes, l => Assert.Equal(ServiceLifetime.Scoped, l));
    }

    [Fact]
    public void A_head_that_brings_no_memory_of_its_own_remembers_nothing()
    {
        // The browser. Fluxor silently drops an effect whose dependencies cannot
        // be resolved, so an ABSENT IRemembers would take the write half of the
        // loop down with nothing said about it - which is why RemembersNothing
        // is registered rather than left out.
        var provider = new ServiceCollection().AddConversationStore()
            .BuildServiceProvider();

        Assert.IsType<RemembersNothing>(provider.GetRequiredService<IRemembers>());
    }

    [Fact]
    public void A_head_that_registers_its_own_memory_keeps_it()
    {
        // The phone. DeviceMemory is registered before AddConversationStore and
        // the TryAdd inside must not displace it - if it did, the app would
        // remember nothing and say nothing about it.
        var provider = new ServiceCollection()
            .AddSingleton<IRemembers, RecordingMemory>()
            .AddConversationStore()
            .BuildServiceProvider();

        Assert.IsType<RecordingMemory>(provider.GetRequiredService<IRemembers>());
    }

    // ---- the short-term cache -------------------------------------------

    [Fact]
    public void The_cache_is_bounded_and_drops_the_oldest_first()
    {
        // Everything in here is a candidate for the model's context. A cache that
        // grows without bound is a prompt that grows without bound, and this
        // phone decodes about seven tokens a second.
        var s = Fresh();

        for (var i = 1; i <= ConversationState.Keep + 3; i++)
            s = ConversationReducers.OnTurnEnded(s, new TurnEnded($"question {i}", "answer", "en", null));

        Assert.Equal(ConversationState.Keep, s.Recent.Count);
        Assert.Equal("question 4", s.Recent[0].Said);
        Assert.Equal($"question {ConversationState.Keep + 3}", s.Recent[^1].Said);
    }

    [Fact]
    public void A_turn_that_heard_nothing_leaves_no_exchange_behind()
    {
        var s = ConversationReducers.OnTurnEnded(Fresh(), new TurnEnded("   ", "answer", "en", null));

        Assert.Empty(s.Recent);
    }

    [Fact]
    public void A_new_turn_cannot_inherit_the_last_ones_words()
    {
        // WHAT STOPS A DOUBLE WRITE IN A WAKE SEQUENCE. MainLayout builds
        // TurnEnded by reading Heard back off the store, so if a turn began with
        // the previous turn's transcript still in place, a second turn that
        // heard nothing would learn the first turn's words all over again.
        var after = ConversationReducers.OnTurnEnded(
            ConversationReducers.OnTurnProgressed(Fresh(),
                new TurnProgressed(new TurnState(TurnPhase.Thinking, Heard: "first question"))),
            new TurnEnded("first question", "an answer", "en", null));

        var next = ConversationReducers.OnTurnStarted(after, new TurnStarted(Woken: true));

        Assert.Null(next.Heard);
        Assert.Null(next.Reply);

        // The cache is what makes the NEXT answer make sense, so it survives.
        Assert.Single(next.Recent);
    }

    // ---- progress reports -----------------------------------------------

    [Fact]
    public void A_later_report_with_nothing_in_it_does_not_wipe_the_transcript()
    {
        // THE BUG THIS REDUCER WAS WRITTEN AROUND. The speaking phase reports
        // Heard and Reply; the Idle report that follows carries neither, and
        // taking that literally wiped the transcript off the screen the moment
        // the answer finished.
        var s = ConversationReducers.OnTurnProgressed(Fresh(),
            new TurnProgressed(new TurnState(TurnPhase.Thinking,
                Heard: "where is the nearest garage", Reply: "Two blocks up.", Language: "en")));

        var idle = ConversationReducers.OnTurnProgressed(s,
            new TurnProgressed(new TurnState(TurnPhase.Idle)));

        Assert.Equal("where is the nearest garage", idle.Heard);
        Assert.Equal("Two blocks up.", idle.Reply);
        Assert.Equal("en", idle.Language);
        Assert.Equal(TurnPhase.Idle, idle.Phase);
    }

    [Fact]
    public void Phase_and_level_are_always_taken_literally()
    {
        // The two that always describe now, and so are the two a null report is
        // never protecting.
        var s = Fresh() with { Phase = TurnPhase.Thinking, Level = 0.8 };

        var next = ConversationReducers.OnTurnProgressed(s,
            new TurnProgressed(new TurnState(TurnPhase.Idle, Level: 0)));

        Assert.Equal(TurnPhase.Idle, next.Phase);
        Assert.Equal(0, next.Level);
    }

    // ---- starting over ---------------------------------------------------

    [Fact]
    public void Clearing_empties_the_conversation_but_not_long_term_memory()
    {
        var s = Fresh() with
        {
            Heard = "something",
            Reply = "something else",
            Recent = [new Exchange("said", "replied", "en", DateTimeOffset.UtcNow)],
        };

        var next = ConversationReducers.OnCleared(s, new ConversationCleared());

        Assert.Null(next.Heard);
        Assert.Null(next.Reply);
        Assert.Empty(next.Recent);

        // Nothing here can reach the long-term store: OnCleared is a pure
        // function of the state, and the only way out is MemoryEffects.
    }

    // ---- fakes -----------------------------------------------------------

    private sealed class RecordingMemory : IRemembers
    {
        public List<string> Learned { get; } = [];

        public Task LearnAsync(string said, CancellationToken ct = default)
        {
            Learned.Add(said);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Remembered>> RecallAsync(
            string about, int limit = 4, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Remembered>>([]);
    }

    private sealed class ThrowingMemory : IRemembers
    {
        public Task LearnAsync(string said, CancellationToken ct = default)
            => throw new InvalidOperationException("disk full");

        public Task<IReadOnlyList<Remembered>> RecallAsync(
            string about, int limit = 4, CancellationToken ct = default)
            => throw new InvalidOperationException("disk full");
    }

    private sealed class NullDispatcher : IDispatcher
    {
        public event EventHandler<ActionDispatchedEventArgs>? ActionDispatched;

        public void Dispatch(object action)
            => ActionDispatched?.Invoke(this, new ActionDispatchedEventArgs(action));
    }
}
