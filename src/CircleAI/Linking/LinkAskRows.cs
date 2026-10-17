// LinkAskRows.cs
//
// THE COLUMN ORDER FOR QUESTIONS, READINESS, HEALTH AND THE HANDSHAKE - IN ONE PLACE.
//
// Same rule as LinkSetupRows: a service that writes [state, id] and a client that reads
// [id, state] is a bug that compiles and ships. Every writer and reader of these rows
// is here, both directions, and every reader tolerates a short row - the two APKs are
// updated separately, so an older service will answer a newer client and the reverse.
// Columns are only ever APPENDED.

using System;
using System.Collections.Generic;
using System.Globalization;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;

namespace CircleAI.Linking;

/// <summary>What a client sees of one ask, read back off the wire.</summary>
/// <param name="Id">The service's id for the ask.</param>
/// <param name="State">Where it has got to; null when the service sent a state this build does not know.</param>
/// <param name="StateName">The state as sent, for logs when <paramref name="State"/> is null.</param>
/// <param name="Seq">The ask's change sequence.</param>
/// <param name="Kind">Why it failed, paused or waits.</param>
/// <param name="Sentence">What to tell the person about that.</param>
/// <param name="NeedsPermission">What it waits for, when it waits for a permission.</param>
/// <param name="Text">Answer text from <paramref name="TextOffset"/> on.</param>
/// <param name="TextOffset">Where <paramref name="Text"/> starts in the whole answer.</param>
/// <param name="TextLength">How long the whole answer is so far.</param>
/// <param name="Position">How many asks are ahead of it, or -1.</param>
/// <param name="ModelId">The model working on it.</param>
/// <param name="Lane">The lane name.</param>
/// <param name="CreatedAt">When it was accepted.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Duplicate">The submission repeated an earlier request id; this is that ask.</param>
/// <param name="Timing">Where the time went, once known.</param>
public sealed record LinkAskView(
    string Id,
    AskState? State,
    string StateName,
    long Seq,
    FailureKind? Kind,
    string? Sentence,
    string? NeedsPermission,
    string Text,
    int TextOffset,
    int TextLength,
    int Position,
    string? ModelId,
    string Lane,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Duplicate,
    TurnTiming? Timing)
{
    /// <summary>Whether nothing more will happen to it. An unknown state is treated as open.</summary>
    public bool IsFinal => State is { } s && AskStates.IsFinal(s);
}

/// <summary>Row layouts for the ask verbs, the handshake, readiness and health.</summary>
public static class LinkAskRows
{
    // ── argument keys (LinkVerbRequest.Args) ──────────────────────────────────

    /// <summary>AskSubmit: the caller's idempotency key.</summary>
    public const string ArgRequestId = "rid";
    /// <summary>AskSubmit: the caller's session id.</summary>
    public const string ArgSession = "session";
    /// <summary>AskSubmit: the message to put to the model.</summary>
    public const string ArgMessage = "message";
    /// <summary>AskSubmit: the question alone, when the message carries the caller's own context.</summary>
    public const string ArgQuestion = "question";
    /// <summary>AskSubmit: the lane name (Foreground, Background).</summary>
    public const string ArgLane = "lane";
    /// <summary>AskSubmit: "1" when the answer will be read aloud.</summary>
    public const string ArgSpoken = "spoken";
    /// <summary>AskSubmit: "1" when tools are allowed.</summary>
    public const string ArgAgentic = "agentic";
    /// <summary>AskSubmit: "0" when the ask must never be re-run after a crash.</summary>
    public const string ArgSideEffectFree = "sef";
    /// <summary>AskStatus: send answer text from this character offset.</summary>
    public const string ArgSince = "since";
    /// <summary>AskStatus/Status: the change sequence the caller already has.</summary>
    public const string ArgAfterSeq = "after";
    /// <summary>AskStatus: wait up to this many milliseconds for news (capped by the service).</summary>
    public const string ArgWaitMs = "wait";
    /// <summary>AskList: "1" for open asks only.</summary>
    public const string ArgOpenOnly = "open";

    // ── the ask row ──────────────────────────────────────────────────────────
    //
    //  0 id            1 state         2 seq           3 kind          4 sentence
    //  5 needsPerm     6 text          7 textOffset    8 textLength    9 position
    // 10 modelId      11 lane         12 createdMs    13 updatedMs    14 duplicate
    // 15 t.queue      16 t.wait       17 t.enrich     18 t.prefill    19 t.ptok
    // 20 t.decode     21 t.otok       22 t.ttft       23 t.total      24 t.kv
    // 25 t.finish     26 t.model

    /// <summary>One ask as a row.</summary>
    /// <param name="ask">The ask.</param>
    /// <param name="position">Asks ahead of it, or -1.</param>
    /// <param name="textFrom">Send answer text from this offset (clamped).</param>
    /// <param name="includeText">False for list rows, which carry no text.</param>
    /// <param name="duplicate">The submission repeated an earlier request id.</param>
    public static IReadOnlyList<string> Ask(AskRecord ask, int position, int textFrom = 0, bool includeText = true, bool duplicate = false)
    {
        ArgumentNullException.ThrowIfNull(ask);
        var text = ask.Text ?? string.Empty;
        var from = Math.Clamp(textFrom, 0, text.Length);
        var t = ask.Timing;
        return new[]
        {
            ask.Id,
            ask.State.ToString(),
            N(ask.Seq),
            ask.Kind?.ToString() ?? string.Empty,
            Clean(ask.Sentence),
            ask.NeedsPermission ?? string.Empty,
            includeText ? Clean(text[from..]) : string.Empty,
            N(includeText ? from : 0),
            N(text.Length),
            N(position),
            ask.ModelId ?? string.Empty,
            ask.Lane.ToString(),
            N(ask.CreatedAt.ToUnixTimeMilliseconds()),
            N(ask.UpdatedAt.ToUnixTimeMilliseconds()),
            duplicate ? "1" : "0",
            N(t?.QueueMs ?? -1),
            N(t?.LoadWaitMs ?? -1),
            N(t?.EnrichMs ?? -1),
            N(t?.PrefillMs ?? -1),
            N(t?.PromptTokens ?? -1),
            N(t?.DecodeMs ?? -1),
            N(t?.OutputTokens ?? -1),
            N(t?.TtftMs ?? -1),
            N(t?.TotalMs ?? -1),
            t?.KvPath ?? string.Empty,
            t?.Finish ?? string.Empty,
            t?.ModelId ?? string.Empty,
        };
    }

    /// <summary>An ask row read back, or null when the row has no id.</summary>
    public static LinkAskView? ReadAsk(IReadOnlyList<string> row)
    {
        var id = LinkSetupRows.Text(row, 0);
        if (string.IsNullOrEmpty(id)) return null;

        var stateName = LinkSetupRows.Text(row, 1);
        AskState? state = Enum.TryParse<AskState>(stateName, ignoreCase: false, out var s) ? s : null;
        var kindName = LinkSetupRows.Text(row, 3);
        FailureKind? kind = Enum.TryParse<FailureKind>(kindName, ignoreCase: false, out var k) && kindName.Length > 0 ? k : null;

        TurnTiming? timing = null;
        if (row.Count > 23 && LinkSetupRows.Number(row, 23, -1) >= 0)
        {
            timing = new TurnTiming(
                QueueMs:      LinkSetupRows.Number(row, 15, -1),
                LoadWaitMs:   LinkSetupRows.Number(row, 16, -1),
                EnrichMs:     LinkSetupRows.Number(row, 17, -1),
                PrefillMs:    LinkSetupRows.Number(row, 18, -1),
                PromptTokens: (int)LinkSetupRows.Number(row, 19, -1),
                DecodeMs:     LinkSetupRows.Number(row, 20, -1),
                OutputTokens: (int)LinkSetupRows.Number(row, 21, -1),
                TtftMs:       LinkSetupRows.Number(row, 22, -1),
                TotalMs:      LinkSetupRows.Number(row, 23, -1),
                ModelId:      Null(LinkSetupRows.Text(row, 26)),
                KvPath:       Null(LinkSetupRows.Text(row, 24)),
                Finish:       Null(LinkSetupRows.Text(row, 25)));
        }

        return new LinkAskView(
            Id: id,
            State: state,
            StateName: stateName,
            Seq: LinkSetupRows.Number(row, 2),
            Kind: kind,
            Sentence: Null(LinkSetupRows.Text(row, 4)),
            NeedsPermission: Null(LinkSetupRows.Text(row, 5)),
            Text: LinkSetupRows.Text(row, 6),
            TextOffset: (int)LinkSetupRows.Number(row, 7),
            TextLength: (int)LinkSetupRows.Number(row, 8),
            Position: (int)LinkSetupRows.Number(row, 9, -1),
            ModelId: Null(LinkSetupRows.Text(row, 10)),
            Lane: LinkSetupRows.Text(row, 11),
            CreatedAt: Ms(LinkSetupRows.Number(row, 12)),
            UpdatedAt: Ms(LinkSetupRows.Number(row, 13)),
            Duplicate: LinkSetupRows.Flag(row, 14),
            Timing: timing);
    }

    // ── the handshake ────────────────────────────────────────────────────────

    /// <summary>The handshake row: [hello, protocol, versionCode, bootId, pid, verbs].</summary>
    public static IReadOnlyList<string> Hello(int protocol, long versionCode, string bootId, int pid, IEnumerable<string> verbs)
        => new[] { "hello", N(protocol), N(versionCode), bootId ?? string.Empty, N(pid), string.Join(",", verbs) };

    /// <summary>What the handshake said: protocol revision (0 when unreadable), version, boot id, pid, verbs.</summary>
    public static (int Protocol, long VersionCode, string BootId, int Pid, IReadOnlySet<string> Verbs) ReadHello(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        foreach (var row in rows)
        {
            if (LinkSetupRows.Text(row, 0) != "hello") continue;
            var verbs = new HashSet<string>(
                LinkSetupRows.Text(row, 5).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
            return ((int)LinkSetupRows.Number(row, 1), LinkSetupRows.Number(row, 2),
                    LinkSetupRows.Text(row, 3), (int)LinkSetupRows.Number(row, 4), verbs);
        }
        return (0, 0, string.Empty, 0, new HashSet<string>(StringComparer.Ordinal));
    }

    // ── readiness (Status verb) ──────────────────────────────────────────────
    //
    //  [state, schema, seq, bootId, phase, step, kind, pause, detail, modelId, sinceMs, retryAfterSec, progress]
    //  [component, name, phase, step, kind, pause, detail, modelId, sinceMs, progress, retryAfterSec]
    //  [failure, atMs, kind, certainty, phase, modelId, evidence, detail, counted]
    //  [setting, key, value]
    //  [unchanged, seq]

    /// <summary>A snapshot as rows.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Status(ReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = new List<IReadOnlyList<string>>(4 + snapshot.Components.Count + snapshot.RecentFailures.Count + snapshot.Settings.Count);
        var b = snapshot.Brain;
        rows.Add(new[]
        {
            "state", N(snapshot.Schema), N(snapshot.Seq), snapshot.BootId,
            b.Phase.ToString(), b.Step.ToString(), b.Kind?.ToString() ?? string.Empty, b.Pause.ToString(),
            Clean(b.Detail), b.ModelId ?? string.Empty, N(b.Since.ToUnixTimeMilliseconds()),
            b.RetryAfter is { } ra ? N((long)Math.Ceiling(ra.TotalSeconds)) : string.Empty,
            b.Progress is { } p ? p.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
        });
        foreach (var c in snapshot.Components)
        {
            rows.Add(new[]
            {
                "component", c.Component, c.Phase.ToString(), c.Step.ToString(), c.Kind?.ToString() ?? string.Empty,
                c.Pause.ToString(), Clean(c.Detail), c.ModelId ?? string.Empty, N(c.Since.ToUnixTimeMilliseconds()),
                c.Progress is { } cp ? cp.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
                c.RetryAfter is { } cra ? N((long)Math.Ceiling(cra.TotalSeconds)) : string.Empty,
            });
        }
        foreach (var f in snapshot.RecentFailures)
        {
            rows.Add(new[]
            {
                "failure", N(f.At.ToUnixTimeMilliseconds()), f.Kind.ToString(), f.Certainty.ToString(), f.Phase.ToString(),
                f.ModelId ?? string.Empty, Clean(f.Evidence), Clean(f.Detail), f.CountsAgainstModel ? "1" : "0",
            });
        }
        foreach (var (key, value) in snapshot.Settings)
            rows.Add(new[] { "setting", key, Clean(value) });
        return rows;
    }

    /// <summary>The single row that answers a Status poll when nothing changed.</summary>
    public static IReadOnlyList<string> Unchanged(long seq) => new[] { "unchanged", N(seq) };

    /// <summary>
    /// Read Status rows. <c>Unchanged</c> is true (and the snapshot null) when the
    /// service said nothing changed. A snapshot with an Unknown brain comes back when
    /// the rows carry no state row at all.
    /// </summary>
    public static (bool Unchanged, long Seq, ReadinessSnapshot? Snapshot) ReadStatus(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
            if (LinkSetupRows.Text(row, 0) == "unchanged")
                return (true, LinkSetupRows.Number(row, 1), null);

        var now = DateTimeOffset.UtcNow;
        ComponentState brain = ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Unknown, string.Empty, now);
        int schema = 0; long seq = 0; string bootId = string.Empty;
        var components = new List<ComponentState>();
        var failures = new List<Failure>();
        var settings = new List<KeyValuePair<string, string>>();

        foreach (var row in rows)
        {
            switch (LinkSetupRows.Text(row, 0))
            {
                case "state":
                    schema = (int)LinkSetupRows.Number(row, 1);
                    seq = LinkSetupRows.Number(row, 2);
                    bootId = LinkSetupRows.Text(row, 3);
                    brain = new ComponentState(
                        ReadinessComponents.Brain,
                        Parse(LinkSetupRows.Text(row, 4), BrainPhase.Unknown),
                        Parse(LinkSetupRows.Text(row, 5), LoadStep.None),
                        ParseKind(LinkSetupRows.Text(row, 6)),
                        Parse(LinkSetupRows.Text(row, 7), PauseReason.None),
                        LinkSetupRows.Text(row, 8),
                        Null(LinkSetupRows.Text(row, 9)),
                        Ms(LinkSetupRows.Number(row, 10)),
                        Fraction(row, 12),
                        Seconds(row, 11));
                    break;
                case "component":
                    components.Add(new ComponentState(
                        LinkSetupRows.Text(row, 1),
                        Parse(LinkSetupRows.Text(row, 2), BrainPhase.Unknown),
                        Parse(LinkSetupRows.Text(row, 3), LoadStep.None),
                        ParseKind(LinkSetupRows.Text(row, 4)),
                        Parse(LinkSetupRows.Text(row, 5), PauseReason.None),
                        LinkSetupRows.Text(row, 6),
                        Null(LinkSetupRows.Text(row, 7)),
                        Ms(LinkSetupRows.Number(row, 8)),
                        Fraction(row, 9),
                        Seconds(row, 10)));
                    break;
                case "failure":
                    failures.Add(new Failure(
                        ParseKind(LinkSetupRows.Text(row, 2)) ?? FailureKind.Unknown,
                        Parse(LinkSetupRows.Text(row, 3), Certainty.Unknown),
                        Parse(LinkSetupRows.Text(row, 4), WorkPhase.None),
                        Null(LinkSetupRows.Text(row, 5)),
                        LinkSetupRows.Text(row, 6),
                        LinkSetupRows.Text(row, 7),
                        Ms(LinkSetupRows.Number(row, 1))));
                    break;
                case "setting":
                    var key = LinkSetupRows.Text(row, 1);
                    if (key.Length > 0) settings.Add(new KeyValuePair<string, string>(key, LinkSetupRows.Text(row, 2)));
                    break;
            }
        }

        return (false, seq, new ReadinessSnapshot(schema, seq, bootId, brain, components, failures, settings));
    }

    // ── health ───────────────────────────────────────────────────────────────

    /// <summary>One health fact: [health, key, value].</summary>
    public static IReadOnlyList<string> Health(string key, string value) => new[] { "health", key, Clean(value) };

    /// <summary>Health rows read back as key/value pairs, in order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ReadHealth(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var row in rows)
            if (LinkSetupRows.Text(row, 0) == "health" && LinkSetupRows.Text(row, 1).Length > 0)
                list.Add(new KeyValuePair<string, string>(LinkSetupRows.Text(row, 1), LinkSetupRows.Text(row, 2)));
        return list;
    }

    // ── quality run ──────────────────────────────────────────────────────────
    //
    //  [quality, id, passed, reason, answer, totalMs, prefillMs, promptTokens, outputTokens]
    //  [summary, passed, total, modelId]

    /// <summary>One quality verdict as a row.</summary>
    public static IReadOnlyList<string> Quality(string id, bool passed, string reason, string answer, TurnTiming? timing)
        => new[]
        {
            "quality", id, passed ? "1" : "0", Clean(reason), Clean(answer),
            N(timing?.TotalMs ?? -1), N(timing?.PrefillMs ?? -1), N(timing?.PromptTokens ?? -1), N(timing?.OutputTokens ?? -1),
        };

    /// <summary>The quality run's summary row.</summary>
    public static IReadOnlyList<string> QualitySummary(int passed, int total, string? modelId)
        => new[] { "summary", N(passed), N(total), modelId ?? string.Empty };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The unit separator joins fields on the wire, so it can never be inside one.</summary>
    private static string Clean(string? text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\u001F', ' ');

    private static string? Null(string text) => string.IsNullOrEmpty(text) ? null : text;

    private static DateTimeOffset Ms(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;

    private static double? Fraction(IReadOnlyList<string> row, int at) =>
        LinkSetupRows.Text(row, at).Length == 0 ? null : LinkSetupRows.Fraction(row, at);

    private static TimeSpan? Seconds(IReadOnlyList<string> row, int at) =>
        LinkSetupRows.Text(row, at).Length == 0 ? null : TimeSpan.FromSeconds(LinkSetupRows.Number(row, at));

    private static T Parse<T>(string text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: false, out var value) ? value : fallback;

    private static FailureKind? ParseKind(string text) =>
        text.Length > 0 && Enum.TryParse<FailureKind>(text, ignoreCase: false, out var k) ? k : null;
}
