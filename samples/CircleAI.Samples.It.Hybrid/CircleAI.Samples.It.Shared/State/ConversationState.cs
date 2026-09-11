// ConversationState.cs
//
// One answer to "what is this conversation", and the short-term half of memory.
//
// WHY A STORE AT ALL. Turn state was scattered: a phase in VoiceMark for the
// animation, a transcript in a component field, a reply in another, history
// inside ItSession where no screen can see it, and the conversation loop in
// MainLayout reading a bool it set through a Progress<T> that does not run
// inline. Five owners of one conversation, which is how a turn came to report
// "heard" to one place and nothing to another.
//
// AND IT IS THE CACHE THE MEMORY LOOP NEEDS. What somebody said thirty seconds
// ago is short-term memory; what they told the phone last week is long-term.
// Until now the app wrote every utterance to the long-term store and never read
// it back, so it accumulated everything and remembered nothing. Holding the
// recent exchanges HERE gives the effects something to flush from, and somewhere
// to put what comes back — see MemoryEffects.
//
// Fluxor rather than another hand-rolled observable: VoiceMark is already one,
// and a second would be a third owner of the same conversation. The store gives
// one place to read from, one place to change it, and an effect boundary where
// the long-term store can be reached without a component knowing it exists.

using System.Collections.Generic;
using Fluxor;

namespace CircleAI.Samples.It.Shared.State;

/// <summary>One completed exchange, as the store remembers it.</summary>
/// <param name="Said">What the person said.</param>
/// <param name="Replied">What the assistant answered, or null if it never did.</param>
/// <param name="Language">The language tag the turn ran in.</param>
/// <param name="WhenUtc">When it finished, for ordering and for ageing out.</param>
public sealed record Exchange(string Said, string? Replied, string? Language, System.DateTimeOffset WhenUtc);

/// <summary>The conversation, as everything that draws or reasons about it sees it.</summary>
/// <param name="Phase">What the microphone and the model are doing.</param>
/// <param name="Level">Microphone level 0..1, or zero when nothing measures one.</param>
/// <param name="Heard">The transcript of the turn in progress.</param>
/// <param name="Reply">The answer so far, as it streams.</param>
/// <param name="Language">The language this turn is being answered in.</param>
/// <param name="Detail">Why a turn ended early, in words a person can act on.</param>
/// <param name="Recent">
/// The short-term cache, newest last. Bounded on purpose — see <see cref="Keep"/>.
/// </param>
/// <remarks>
/// WHAT LONG-TERM MEMORY OFFERED IS NOT HERE, THOUGH IT ONCE WAS. A Recalled
/// field sat in this record claiming "the prompt and the screen read the same
/// list", and on the phone it was empty every turn: the only action that filled
/// it was dispatched by nothing. The recall lives in the turn, which needs it
/// inline to build the prompt and cannot push it back here — that code is a
/// singleton and this store is scoped, so the dispatch would reach a different
/// store than the screen reads. A field that cannot be filled honestly is worse
/// than no field, because the next person to read it believes it.
/// </remarks>
[FeatureState]
public sealed record ConversationState(
    TurnPhase Phase,
    double Level,
    string? Heard,
    string? Reply,
    string? Language,
    string? Detail,
    IReadOnlyList<Exchange> Recent)
{
    /// <summary>
    /// How many exchanges the short-term cache holds before the oldest falls out.
    /// </summary>
    /// <remarks>
    /// SMALL, BECAUSE IT IS SHORT-TERM MEMORY AND NOT A TRANSCRIPT. Everything
    /// here is a candidate for the model's context, and this phone decodes about
    /// seven tokens a second — a cache that grows without bound is a prompt that
    /// grows without bound, which is the latency bug ItSession's own history
    /// bound was added to prevent. What falls out of here is not lost: it has
    /// already been flushed to the long-term store, which is the whole point of
    /// the loop.
    /// </remarks>
    public const int Keep = 6;

    /// <summary>Fluxor needs a parameterless constructor to seed the feature.</summary>
    private ConversationState() : this(
        TurnPhase.Idle, 0, null, null, null, null, []) { }

    /// <summary>Whether a turn is under way, from wherever it was started.</summary>
    public bool Busy => Phase != TurnPhase.Idle;
}
