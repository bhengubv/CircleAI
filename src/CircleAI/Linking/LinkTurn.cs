// LinkTurn.cs
//
// The one turn a linked app sends across the process boundary, and what comes
// back. Deliberately flat and string-only so it marshals through an Android
// Bundle without a serializer: the device binder reads these fields off the
// Bundle, the client writes them onto it. Kept here, next to the grant types, so
// the caller's client library and the service share one definition.

namespace CircleAI.Linking;

/// <summary>A linked app asking the shared brain one turn.</summary>
/// <param name="SessionId">The caller's conversation id — its own scheme.</param>
/// <param name="Message">What the person (or the app) is asking.</param>
/// <param name="Agentic">Whether the brain may use tools for this turn.</param>
public sealed record LinkTurnRequest(string SessionId, string Message, bool Agentic = false);

/// <summary>The shared brain's answer to a <see cref="LinkTurnRequest"/>.</summary>
/// <param name="Ok">True when <see cref="Reply"/> holds an answer.</param>
/// <param name="Reply">The answer, when <see cref="Ok"/>.</param>
/// <param name="Error">Why it failed, when not <see cref="Ok"/>.</param>
public sealed record LinkTurnReply(bool Ok, string? Reply, string? Error)
{
    public static LinkTurnReply Success(string reply) => new(true, reply, null);
    public static LinkTurnReply Failure(string error) => new(false, null, error);
}
