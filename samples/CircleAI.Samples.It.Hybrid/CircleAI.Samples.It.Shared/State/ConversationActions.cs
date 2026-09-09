// ConversationActions.cs
//
// Everything that can happen to a conversation, named.
//
// One action per thing that actually happens, rather than a setter per field:
// the point of naming them is that a log of actions reads as an account of the
// turn, and an effect can hang off "this exchange finished" without every caller
// having to remember to also write it down.

namespace CircleAI.Samples.It.Shared.State;

/// <summary>A turn has begun — from a press, or from the phone hearing its name.</summary>
/// <param name="Woken">
/// True when the wake word started it. The conversation loop treats the two
/// differently: a press buys one turn, a wake opens a conversation.
/// </param>
public sealed record TurnStarted(bool Woken);

/// <summary>The turn reported something: a phase, a level, a transcript, a fragment.</summary>
/// <remarks>
/// ONE ACTION FOR THE WHOLE REPORT, because that is how it arrives. TurnState is
/// what every head already produces; splitting it into four actions here would
/// mean four dispatches per fragment on a path that fires many times a second.
/// </remarks>
public sealed record TurnProgressed(TurnState Turn);

/// <summary>
/// The turn is over. If anything was said and answered, it joins the cache.
/// </summary>
/// <remarks>
/// The reducer adds it to the short-term cache; MemoryEffects is what carries it
/// through to the long-term store. Two steps rather than one so the UI updates
/// immediately and the disk write happens behind it.
/// </remarks>
public sealed record TurnEnded(string? Said, string? Replied, string? Language, string? Detail);

/// <summary>Ask long-term memory what is worth knowing before answering this.</summary>
public sealed record RecallWanted(string About);

/// <summary>What long-term memory offered, or an empty list when it had nothing.</summary>
public sealed record Recalled(System.Collections.Generic.IReadOnlyList<Remembered> Facts);

/// <summary>Start again: a new conversation, nothing carried over but long-term memory.</summary>
public sealed record ConversationCleared;
