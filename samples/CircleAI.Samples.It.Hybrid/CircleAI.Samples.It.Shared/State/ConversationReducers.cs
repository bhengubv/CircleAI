// ConversationReducers.cs
//
// How each action changes the conversation. Pure, and therefore testable without
// a phone, a microphone or a model — which is the point: these are the rules
// that decide what the screen shows and what the model is told, and until now
// they were spread across a Progress callback, two component fields and a bool.

using System.Collections.Generic;
using System.Linq;
using Fluxor;

namespace CircleAI.Samples.It.Shared.State;

public static class ConversationReducers
{
    /// <summary>A turn begins: clear the last one's words, keep the cache.</summary>
    /// <remarks>
    /// THE CACHE SURVIVES AND THE TRANSCRIPT DOES NOT. Recent exchanges are what
    /// makes the next answer make sense; the previous turn's half-finished reply
    /// on screen while a new one is being spoken is just confusing.
    /// </remarks>
    [ReducerMethod]
    public static ConversationState OnTurnStarted(ConversationState s, TurnStarted _) =>
        s with { Phase = TurnPhase.Listening, Heard = null, Reply = null, Detail = null, Level = 0 };

    /// <summary>
    /// A report from the turn. Nulls do not overwrite what is already known.
    /// </summary>
    /// <remarks>
    /// A LATER REPORT WITH A NULL FIELD IS NOT NEWS THAT THE FIELD IS EMPTY. The
    /// speaking phase reports Heard and Reply; the Idle report that follows it
    /// carries neither, and taking that literally wiped the transcript off the
    /// screen the moment the answer finished. Only Phase and Level are always
    /// authoritative, because they always describe now.
    /// </remarks>
    [ReducerMethod]
    public static ConversationState OnTurnProgressed(ConversationState s, TurnProgressed a) =>
        s with
        {
            Phase = a.Turn.Phase,
            Level = a.Turn.Level,
            Heard = a.Turn.Heard is { Length: > 0 } h ? h : s.Heard,
            Reply = a.Turn.Reply is { Length: > 0 } r ? r : s.Reply,
            Language = a.Turn.Language is { Length: > 0 } l ? l : s.Language,
            Detail = a.Turn.Detail is { Length: > 0 } d ? d : s.Detail,
        };

    /// <summary>The turn is over; a real exchange joins the short-term cache.</summary>
    /// <remarks>
    /// NOTHING SAID MEANS NOTHING REMEMBERED. A turn that heard silence, or that
    /// was cancelled, must not put an empty exchange into the cache — it would be
    /// flushed to long-term memory and read back later as though somebody had
    /// said it.
    /// </remarks>
    [ReducerMethod]
    public static ConversationState OnTurnEnded(ConversationState s, TurnEnded a)
    {
        var recent = s.Recent;

        if (!string.IsNullOrWhiteSpace(a.Said))
        {
            var kept = new List<Exchange>(recent)
            {
                new(a.Said!, a.Replied, a.Language, System.DateTimeOffset.UtcNow),
            };

            // Oldest out. What leaves has already been flushed to the long-term
            // store by the effect, so this drops nothing that was worth keeping.
            if (kept.Count > ConversationState.Keep)
                kept.RemoveRange(0, kept.Count - ConversationState.Keep);

            recent = kept;
        }

        return s with
        {
            Phase = TurnPhase.Idle,
            Level = 0,
            Detail = a.Detail is { Length: > 0 } d ? d : s.Detail,
            Recent = recent,
        };
    }

    /// <summary>What long-term memory had to offer this turn.</summary>
    [ReducerMethod]
    public static ConversationState OnRecalled(ConversationState s, Recalled a) =>
        s with { Recalled = a.Facts };

    /// <summary>
    /// A new conversation. The cache goes; long-term memory is untouched.
    /// </summary>
    [ReducerMethod]
    public static ConversationState OnCleared(ConversationState s, ConversationCleared _) =>
        s with
        {
            Phase = TurnPhase.Idle,
            Level = 0,
            Heard = null,
            Reply = null,
            Detail = null,
            Recent = [],
            Recalled = [],
        };
}
