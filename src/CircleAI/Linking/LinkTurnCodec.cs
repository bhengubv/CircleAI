// LinkTurnCodec.cs
//
// The marshalling, kept portable so it can be tested without a phone.
//
// The Android binder moves a turn across the process boundary through a Bundle,
// which carries strings. The SEMANTICS of that — which keys, how a bool and an
// absent field are represented, what a turn with no message means — live here,
// as a flat string map, so they are exercised in a desktop unit test. The Android
// side is then only transport: one PutString per entry on the way out, one
// GetString per key on the way back.

namespace CircleAI.Linking;

/// <summary>Encodes the <see cref="LinkTurnRequest"/>/<see cref="LinkTurnReply"/>
/// DTOs to a flat string map and back, for the Android Bundle hop.</summary>
public static class LinkTurnCodec
{
    /// <summary>Bundle key: the caller's session id.</summary>
    public const string KeySession = "session";
    /// <summary>Bundle key: the message to ask.</summary>
    public const string KeyMessage = "message";
    /// <summary>Bundle key: "1" when the turn may use tools.</summary>
    public const string KeyAgentic = "agentic";
    /// <summary>Bundle key: "1" on a successful reply.</summary>
    public const string KeyOk = "ok";
    /// <summary>Bundle key: the reply text, when ok.</summary>
    public const string KeyReply = "reply";
    /// <summary>Bundle key: the failure reason, when not ok.</summary>
    public const string KeyError = "error";

    /// <summary>A request as a string map ready for a Bundle.</summary>
    public static IReadOnlyDictionary<string, string> Encode(LinkTurnRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeySession] = req.SessionId ?? string.Empty,
            [KeyMessage] = req.Message ?? string.Empty,
            [KeyAgentic] = req.Agentic ? "1" : "0",
        };
    }

    /// <summary>A request back from a map, or null when there is no message to ask.</summary>
    public static LinkTurnRequest? TryDecodeRequest(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!map.TryGetValue(KeyMessage, out var message) || string.IsNullOrEmpty(message))
            return null;   // a turn with no message is not a turn
        map.TryGetValue(KeySession, out var session);
        map.TryGetValue(KeyAgentic, out var agentic);
        return new LinkTurnRequest(session ?? string.Empty, message, agentic == "1");
    }

    /// <summary>A reply as a string map ready for a Bundle. Absent fields stay absent.</summary>
    public static IReadOnlyDictionary<string, string> Encode(LinkTurnReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyOk] = reply.Ok ? "1" : "0",
        };
        if (reply.Reply is not null) map[KeyReply] = reply.Reply;
        if (reply.Error is not null) map[KeyError] = reply.Error;
        return map;
    }

    /// <summary>A reply back from a map. A missing ok reads as failure, never a false success.</summary>
    public static LinkTurnReply DecodeReply(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.TryGetValue(KeyOk, out var ok);
        map.TryGetValue(KeyReply, out var reply);
        map.TryGetValue(KeyError, out var error);
        return ok == "1"
            ? LinkTurnReply.Success(reply ?? string.Empty)
            : LinkTurnReply.Failure(string.IsNullOrEmpty(error) ? "unknown error" : error);
    }
}
