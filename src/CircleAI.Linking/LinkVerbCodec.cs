// LinkVerbCodec.cs
//
// The marshalling for the verb channel (LinkIpc.TransactVerb), kept portable so it
// is exercised on the desktop without a phone — exactly like LinkTurnCodec does for
// the chat turn. The Android binder moves a flat string map across the boundary;
// the SEMANTICS — which keys, how a verb name and a list of rows are represented,
// what a missing verb or a missing ok means — live here.
//
// ROWS ON THE WIRE. A verb answers with zero or more rows, each a few fields. The
// map carries a row count and one entry per row, the row's fields joined by the
// ASCII unit separator (0x1F) — a control character that never occurs in a skill
// name, a capability id, or a remembered sentence, so a plain join/split is safe.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace CircleAI.Linking;

/// <summary>Encodes <see cref="LinkVerbRequest"/>/<see cref="LinkRowsReply"/> to a flat
/// string map and back, for the Android Bundle hop on <see cref="LinkIpc.TransactVerb"/>.</summary>
public static class LinkVerbCodec
{
    /// <summary>Bundle key: the verb name.</summary>
    public const string KeyVerb = "verb";
    /// <summary>Bundle key: recall/skills search text.</summary>
    public const string KeyQuery = "q";
    /// <summary>Bundle key: remember — the fact.</summary>
    public const string KeyText = "text";
    /// <summary>Bundle key: remember — the situation key.</summary>
    public const string KeySubject = "subject";
    /// <summary>Bundle key: recall — max atoms.</summary>
    public const string KeyLimit = "limit";
    /// <summary>Bundle key: "1" on a successful reply.</summary>
    public const string KeyOk = "ok";
    /// <summary>Bundle key: the failure reason, when not ok.</summary>
    public const string KeyError = "error";
    /// <summary>Bundle key: the number of rows in a reply.</summary>
    public const string KeyRowCount = "rows";
    /// <summary>Prefix for a row entry: <c>r0</c>, <c>r1</c>, …</summary>
    public const string RowPrefix = "r";

    // The verb names as they travel on the wire. Bump nothing here without bumping
    // LinkIpc.Descriptor — both sides must agree.
    private const string NameRecall = "recall";
    private const string NameRemember = "remember";
    private const string NameSkills = "skills";
    private const string NameCapabilities = "capabilities";

    private const char Unit = '';   // ASCII unit separator — the field delimiter

    private const int DefaultLimit = 5;

    // ── request ──────────────────────────────────────────────────────────────

    /// <summary>A verb request as a string map ready for a Bundle.</summary>
    public static IReadOnlyDictionary<string, string> Encode(LinkVerbRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyVerb]  = NameOf(req.Verb),
            [KeyLimit] = req.Limit.ToString(CultureInfo.InvariantCulture),
        };
        if (req.Query is not null)   map[KeyQuery]   = req.Query;
        if (req.Text is not null)    map[KeyText]    = req.Text;
        if (req.Subject is not null) map[KeySubject] = req.Subject;
        return map;
    }

    /// <summary>A request back from a map, or null when the verb is absent or unknown.</summary>
    public static LinkVerbRequest? TryDecodeRequest(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!map.TryGetValue(KeyVerb, out var name) || VerbOf(name) is not { } verb)
            return null;   // no verb, or one this version does not know

        map.TryGetValue(KeyQuery, out var query);
        map.TryGetValue(KeyText, out var text);
        map.TryGetValue(KeySubject, out var subject);
        map.TryGetValue(KeyLimit, out var limitText);
        var limit = int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l > 0
            ? l : DefaultLimit;

        return new LinkVerbRequest(verb, query, text, subject, limit);
    }

    // ── reply ────────────────────────────────────────────────────────────────

    /// <summary>A rows reply as a string map ready for a Bundle.</summary>
    public static IReadOnlyDictionary<string, string> Encode(LinkRowsReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyOk] = reply.Ok ? "1" : "0",
        };
        if (reply.Error is not null) map[KeyError] = reply.Error;

        map[KeyRowCount] = reply.Rows.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < reply.Rows.Count; i++)
            map[RowPrefix + i.ToString(CultureInfo.InvariantCulture)] = string.Join(Unit, reply.Rows[i]);
        return map;
    }

    /// <summary>A rows reply back from a map. A missing ok reads as failure, never a false success.</summary>
    public static LinkRowsReply DecodeReply(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.TryGetValue(KeyOk, out var ok);
        if (ok != "1")
        {
            map.TryGetValue(KeyError, out var error);
            return LinkRowsReply.Failure(string.IsNullOrEmpty(error) ? "unknown error" : error);
        }

        map.TryGetValue(KeyRowCount, out var countText);
        _ = int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);

        var rows = new List<IReadOnlyList<string>>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
            rows.Add(map.TryGetValue(RowPrefix + i.ToString(CultureInfo.InvariantCulture), out var row)
                ? row.Split(Unit)
                : Array.Empty<string>());
        return LinkRowsReply.Success(rows);
    }

    // ── verb <-> wire name ─────────────────────────────────────────────────────

    private static string NameOf(LinkVerb verb) => verb switch
    {
        LinkVerb.Recall       => NameRecall,
        LinkVerb.Remember     => NameRemember,
        LinkVerb.Skills       => NameSkills,
        LinkVerb.Capabilities => NameCapabilities,
        _                     => string.Empty,
    };

    private static LinkVerb? VerbOf(string name) => name switch
    {
        NameRecall       => LinkVerb.Recall,
        NameRemember     => LinkVerb.Remember,
        NameSkills       => LinkVerb.Skills,
        NameCapabilities => LinkVerb.Capabilities,
        _                => null,
    };
}
