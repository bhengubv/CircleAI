// ConversationActions.cs
//
// Everything that can happen to a conversation, named.
//
// One action per thing that actually happens, rather than a setter per field:
// the point of naming them is that a log of actions reads as an account of the
// turn, and an effect can hang off "this exchange finished" without every caller
// having to remember to also write it down.

namespace CircleAI.Assistant;

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

// THERE IS NO RECALL ACTION HERE ANY MORE, AND THAT IS THE FIX.
//
// RecallWanted and Recalled both lived here, with an effect behind the first and
// a state field behind the second, and nothing in the app ever dispatched
// either. The recall that reaches the model has always been inline in
// DeviceConversation, because the prompt is built on the next line and an effect
// cannot hand a value back to code that is mid-await.
//
// Pushing the result in from there does not work either: DeviceConversation is a
// singleton and this store is SCOPED - AddFluxor registers IDispatcher, IStore
// and IState<T> as Scoped, checked rather than assumed - so a dispatch from the
// turn would land in the root scope's store and never reach the screen's. That
// is the same invisibility as the dead effect, one layer down.
//
// So the turn owns recall and says so in the log, and this store owns what it
// can honestly own. A screen that one day wants to show remembered facts gets
// them through TurnState, which already crosses that boundary every turn.

/// <summary>Start again: a new conversation, nothing carried over but long-term memory.</summary>
public sealed record ConversationCleared;
