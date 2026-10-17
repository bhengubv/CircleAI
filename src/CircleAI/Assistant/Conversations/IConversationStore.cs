// IConversationStore.cs
//
// EACH APP'S CONVERSATION IS ITS OWN. A conversation is keyed by who is asking - as
// the service identified them, never as they described themselves - and by the
// caller's own session id. One app can never read another's turns, and the prompt the
// model sees for a follow-up holds only that caller's history.
//
// Memory (the person's long-term facts) is different and deliberately shared across
// the phone; that is MemoryService, not this.

using System;
using System.Collections.Generic;

namespace CircleAI.Assistant.Conversations;

/// <summary>One turn of a stored conversation.</summary>
/// <param name="Role">"user" or "assistant".</param>
/// <param name="Text">What was said.</param>
/// <param name="AskId">The ask it came from.</param>
/// <param name="At">When.</param>
public sealed record ConversationTurn(string Role, string Text, string? AskId, DateTimeOffset At);

/// <summary>Per-caller, per-session conversation history.</summary>
public interface IConversationStore : IDisposable
{
    /// <summary>Add a turn to (callerKey, sessionId). An empty session id stores nothing.</summary>
    void Append(string callerKey, string sessionId, string role, string text, string? askId, DateTimeOffset at);

    /// <summary>
    /// The most recent turns of (callerKey, sessionId), oldest first, that fit in
    /// <paramref name="maxChars"/> - whole turns only, newest kept first.
    /// </summary>
    IReadOnlyList<ConversationTurn> Window(string callerKey, string sessionId, int maxChars);

    /// <summary>Forget one conversation.</summary>
    void Forget(string callerKey, string sessionId);

    /// <summary>Forget everything a caller said.</summary>
    void ForgetCaller(string callerKey);

    /// <summary>Drop conversations idle since before <paramref name="idleBefore"/>; returns how many.</summary>
    int Purge(DateTimeOffset idleBefore);
}
