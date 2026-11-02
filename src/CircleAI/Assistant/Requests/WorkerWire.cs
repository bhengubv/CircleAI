// WorkerWire.cs
//
// WHAT CROSSES BETWEEN THE SERVICE AND ITS INFERENCE PROCESS.
//
// The model runs in a second process (":inference") so that a native abort or the
// low-memory killer takes the model down and leaves the queue, the records and the
// link standing. The two processes talk over a hand-marshalled binder carrying flat
// string maps - the same shape the link already uses - and this is the one place that
// decides the keys, so both ends read what the other wrote. Portable, so every message
// round-trips in a desktop test.

using System;
using System.Collections.Generic;
using System.Globalization;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Inference;

namespace CircleAI.Assistant.Requests;

/// <summary>Keys and encoders for the service-to-worker protocol.</summary>
public static class WorkerWire
{
    /// <summary>The binder interface token.</summary>
    public const string Descriptor = "com.bhengubv.circleai.worker.v1";

    /// <summary>The one transaction code; the operation is a key.</summary>
    public const int TransactCall = 1;

    /// <summary>Key: the operation (hello, load, status, generate, poll, cancel, release).</summary>
    public const string Op = "op";

    /// <summary>Operation names.</summary>
    public const string OpHello = "hello", OpLoad = "load", OpStatus = "status", OpGenerate = "generate",
                        OpPoll = "poll", OpCancel = "cancel", OpRelease = "release";

    /// <summary>Keys.</summary>
    public const string Ok = "ok", Error = "error", Model = "model", Directory = "dir", SkipHash = "skiphash",
                        Job = "job", MaxTokens = "max", Spoken = "spoken", Retrieval = "rq", DeadlineMs = "deadline",
                        Since = "since", WaitMs = "wait", Text = "text", Length = "len", Final = "final",
                        Pid = "pid", StartedMs = "started", Phase = "phase", Step = "step", RetryS = "retry",
                        RssKb = "rss", MemAvailKb = "memavail", MemTotalKb = "memtotal", Trim = "trim",
                        Engine = "engine", Effort = "effort", Think = "think", Plain = "plain", Tools = "tools",
                        // THE BALANCE'S SHAPE FOR A LOAD (Step 4): the working memory's bits and the
                        // share of the context. Absent means the brain as built.
                        KvMode = "kv", ContextShare = "ctxshare";

    /// <summary>The largest image a message may carry between the two processes.</summary>
    public const int MaxImageBytes = 16 * 1024 * 1024;

    // ── a job's own settings ─────────────────────────────────────────────────

    /// <summary>
    /// Write a job's effort, thinking budget, plainness and tools - only what it sets, so an older
    /// worker reads a job exactly as it always did.
    /// </summary>
    public static void PutJobSettings(IDictionary<string, string> map, BrainJob job)
    {
        if (job.Effort is { } effort) map[Effort] = effort.ToString();
        if (job.ThinkingBudget > 0) map[Think] = I(job.ThinkingBudget);
        if (job.Plain) map[Plain] = "1";
        if (job.Tools) map[Tools] = "1";
    }

    /// <summary>Whether the job allows tools (OPEN-GAPS A16), read back.</summary>
    public static bool GetTools(IReadOnlyDictionary<string, string> map) => Flag(map, Tools);

    /// <summary>A job's effort, thinking budget and plainness, read back.</summary>
    public static (ReasoningEffort? Effort, int ThinkingBudget, bool Plain) GetJobSettings(IReadOnlyDictionary<string, string> map) =>
        (map.TryGetValue(Effort, out var named) ? ReasoningEfforts.Parse(named) : null,
         Math.Max(0, Int(map, Think)),
         Flag(map, Plain));

    // ── messages ─────────────────────────────────────────────────────────────

    /// <summary>Write a conversation into a map (m.count, m.i.role, m.i.content, m.i.imagefile).</summary>
    /// <param name="map">The map.</param>
    /// <param name="messages">The conversation.</param>
    /// <param name="spill">
    /// Where a message's image goes: written to a file it returns the path of. The two processes
    /// are one app and share its storage, and a picture through the map would be most of a binder
    /// transaction's megabyte (Step 4). Null leaves images out, as before.
    /// </param>
    public static void PutMessages(IDictionary<string, string> map, IReadOnlyList<ChatMessage> messages,
                                   Func<byte[], string>? spill = null)
    {
        map["m.count"] = I(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            map["m." + I(i) + ".role"] = messages[i].Role ?? "user";
            map["m." + I(i) + ".content"] = messages[i].Content ?? string.Empty;
            if (spill is not null && messages[i].ImageBytes is { Length: > 0 } image && image.Length <= MaxImageBytes)
                map["m." + I(i) + ".imagefile"] = spill(image);
        }
    }

    /// <summary>Read a conversation back. Missing entries are skipped, never invented.</summary>
    public static IReadOnlyList<ChatMessage> GetMessages(IReadOnlyDictionary<string, string> map)
    {
        var count = Int(map, "m.count");
        var list = new List<ChatMessage>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            if (!map.TryGetValue("m." + I(i) + ".content", out var content)) continue;
            map.TryGetValue("m." + I(i) + ".role", out var role);
            map.TryGetValue("m." + I(i) + ".imagefile", out var imageFile);
            list.Add(new ChatMessage(string.IsNullOrEmpty(role) ? "user" : role, content) { ImageBytes = Image(imageFile) });
        }
        return list;
    }

    // An image the other process left in a file: read once, never past the limit; null when absent.
    private static byte[]? Image(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists && info.Length is > 0 and <= MaxImageBytes ? System.IO.File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    // ── failures ─────────────────────────────────────────────────────────────

    /// <summary>Write a failure (f.kind, f.cert, f.phase, f.model, f.ev, f.detail, f.at).</summary>
    public static void PutFailure(IDictionary<string, string> map, Failure f)
    {
        map["f.kind"] = f.Kind.ToString();
        map["f.cert"] = f.Certainty.ToString();
        map["f.phase"] = f.Phase.ToString();
        if (f.ModelId is not null) map["f.model"] = f.ModelId;
        map["f.ev"] = f.Evidence ?? string.Empty;
        map["f.detail"] = f.Detail ?? string.Empty;
        map["f.at"] = L(f.At.ToUnixTimeMilliseconds());
    }

    /// <summary>Read a failure, or null when the map carries none.</summary>
    public static Failure? GetFailure(IReadOnlyDictionary<string, string> map)
    {
        if (!map.TryGetValue("f.kind", out var kind) || !Enum.TryParse<FailureKind>(kind, out var k)) return null;
        map.TryGetValue("f.cert", out var cert);
        map.TryGetValue("f.phase", out var phase);
        map.TryGetValue("f.model", out var model);
        map.TryGetValue("f.ev", out var ev);
        map.TryGetValue("f.detail", out var detail);
        var at = Long(map, "f.at");
        return new Failure(k,
            Enum.TryParse<Certainty>(cert, out var c) ? c : Certainty.Unknown,
            Enum.TryParse<WorkPhase>(phase, out var p) ? p : WorkPhase.None,
            model, ev ?? string.Empty, detail ?? string.Empty,
            at > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(at) : DateTimeOffset.UtcNow);
    }

    // ── timing ───────────────────────────────────────────────────────────────

    /// <summary>Write a timing (t.*).</summary>
    public static void PutTiming(IDictionary<string, string> map, TurnTiming t)
    {
        map["t.enrich"] = L(t.EnrichMs); map["t.prefill"] = L(t.PrefillMs); map["t.ptok"] = I(t.PromptTokens);
        map["t.decode"] = L(t.DecodeMs); map["t.otok"] = I(t.OutputTokens); map["t.ttft"] = L(t.TtftMs);
        if (t.ModelId is not null) map["t.model"] = t.ModelId;
        if (t.KvPath is not null) map["t.kv"] = t.KvPath;
        if (t.Finish is not null) map["t.finish"] = t.Finish;
    }

    /// <summary>Read a timing, or null when the map carries none.</summary>
    public static TurnTiming? GetTiming(IReadOnlyDictionary<string, string> map)
    {
        if (!map.ContainsKey("t.prefill")) return null;
        map.TryGetValue("t.model", out var model);
        map.TryGetValue("t.kv", out var kv);
        map.TryGetValue("t.finish", out var finish);
        return new TurnTiming(
            EnrichMs: Long(map, "t.enrich", -1), PrefillMs: Long(map, "t.prefill", -1),
            PromptTokens: Int(map, "t.ptok", -1), DecodeMs: Long(map, "t.decode", -1),
            OutputTokens: Int(map, "t.otok", -1), TtftMs: Long(map, "t.ttft", -1),
            ModelId: model, KvPath: kv, Finish: finish);
    }

    // ── status ───────────────────────────────────────────────────────────────

    /// <summary>Write a worker status.</summary>
    public static void PutStatus(IDictionary<string, string> map, BrainWorkerStatus s)
    {
        map[Phase] = s.Phase.ToString();
        map[Step] = s.Step.ToString();
        if (s.ModelId is not null) map[Model] = s.ModelId;
        if (s.RetryAfter is { } r) map[RetryS] = L((long)Math.Ceiling(r.TotalSeconds));
        if (s.ProcessId is { } pid) map[Pid] = I(pid);
        if (s.LastFailure is { } f) PutFailure(map, f);
    }

    /// <summary>Read a worker status.</summary>
    public static BrainWorkerStatus GetStatus(IReadOnlyDictionary<string, string> map, DateTimeOffset now)
    {
        map.TryGetValue(Phase, out var phase);
        map.TryGetValue(Step, out var step);
        map.TryGetValue(Model, out var model);
        var retry = Long(map, RetryS, -1);
        var pid = Int(map, Pid, -1);
        return new BrainWorkerStatus(
            Enum.TryParse<BrainPhase>(phase, out var p) ? p : BrainPhase.Unknown,
            Enum.TryParse<LoadStep>(step, out var st) ? st : LoadStep.None,
            model, GetFailure(map),
            retry >= 0 ? TimeSpan.FromSeconds(retry) : null,
            pid > 0 ? pid : null, now);
    }

    // ── scalars ──────────────────────────────────────────────────────────────

    /// <summary>An integer as the wire writes it.</summary>
    public static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A long as the wire writes it.</summary>
    public static string L(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Read an int, or a fallback.</summary>
    public static int Int(IReadOnlyDictionary<string, string> map, string key, int fallback = 0) =>
        map.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>Read a long, or a fallback.</summary>
    public static long Long(IReadOnlyDictionary<string, string> map, string key, long fallback = 0) =>
        map.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>Read a flag ("1").</summary>
    public static bool Flag(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && v == "1";
}
