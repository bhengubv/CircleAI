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

/// <summary>What a load asks of the worker, as it crosses to it.</summary>
/// <param name="Model">The model.</param>
/// <param name="Directory">The model store's root.</param>
/// <param name="SkipHash">The files were verified already; the weights are not hashed again.</param>
/// <param name="Engine">Open it with this engine rather than the one its catalogue row names; null keeps the row's.</param>
/// <param name="KvMode">The working memory's bits the balance asks for; null builds it as the engine does.</param>
/// <param name="ContextShare">The share of the context asked for; 1 is all of it.</param>
/// <param name="ForItsIndex">The brain is loaded so that its index gets built, not to answer (BrainInWaiting).</param>
/// <param name="Drafts">
/// Tokens a C# brain's own MTP head drafts ahead per pass of its trunk; null, every normal load, leaves drafting to the
/// host (BrainFactory asks for it where the trunk streams). A value - 0, to measure plain decoding, included - is a
/// developer's check and crosses as the count it is (ManagedChatGenerator.DraftsForTest).
/// </param>
/// <param name="Seed">The seed every turn of the brain samples with; null draws a new one each turn (ManagedChatGenerator.SeedForTest).</param>
public readonly record struct WorkerLoadAsk(string? Model, string? Directory, bool SkipHash, CircleAI.Core.ModelEngine? Engine,
    CircleAI.Core.KvCompressionMode? KvMode, double ContextShare, bool ForItsIndex, int? Drafts = null, int? Seed = null);

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
                        Engine = "engine", Effort = "effort", Think = "think", Plain = "plain", Tools = "tools", JustRead = "justread",
                        Conversation = "talk",
                        // THE BALANCE'S SHAPE FOR A LOAD (Step 4): the working memory's bits and the
                        // share of the context. Absent means the brain as built.
                        KvMode = "kv", ContextShare = "ctxshare",
                        // A LOAD FOR THE BRAIN'S INDEX, NOT FOR ANSWERS (BrainInWaiting): built and not warmed.
                        // Absent means a load that answers, as every load was.
                        ForIndex = "forindex",
                        // A DEVELOPER'S CHECK OF DRAFTING (SoakReceiver "engine"): how many tokens the MTP head drafts ahead,
                        // and the seed every turn samples with. Absent means plain decoding and a new seed each turn.
                        Drafts = "drafts", Seed = "seed",
                        // THE WORKER'S OWN ACCOUNT OF A TURN (OPEN-GAPS E33): the brain answering it, the
                        // process's CPU time, and how far past its deadline the job in flight is.
                        Working = "working", WorkingSlot = "slot", CpuMs = "cpu", OverdueMs = "overdue";

    /// <summary>The largest image a message may carry between the two processes.</summary>
    public const int MaxImageBytes = 16 * 1024 * 1024;

    // ── a load ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Write a load into a map: the model, its store and whether its files were verified, and - only where they differ
    /// from a brain as built - the engine, the balance's shape and that it is loaded for its index.
    /// </summary>
    public static void PutLoad(IDictionary<string, string> map, WorkerLoadAsk ask)
    {
        ArgumentNullException.ThrowIfNull(map);
        map[Op] = OpLoad;
        map[Model] = ask.Model ?? string.Empty;
        map[Directory] = ask.Directory ?? string.Empty;
        map[SkipHash] = ask.SkipHash ? "1" : "0";
        if (ask.Engine is { } engine) map[Engine] = engine.ToString();
        // THE BALANCE'S SHAPE (Step 4), sent only when it differs from the brain as built.
        if (ask.KvMode is { } kv) map[KvMode] = kv.ToString();
        if (ask.ContextShare is > 0 and < 1) map[ContextShare] = ask.ContextShare.ToString("R", CultureInfo.InvariantCulture);
        if (ask.ForItsIndex) map[ForIndex] = "1";
        // A CHECK'S COUNT CROSSES AS IT IS, 0 (plain) INCLUDED; a normal load (null) carries nothing and the worker leaves
        // drafting to the host. That is why 0 is sent here where it once was dropped - a developer measuring plain against
        // drafting needs the worker to hear "plain", not "whatever the host decides".
        if (ask.Drafts is { } drafts) map[Drafts] = drafts.ToString(CultureInfo.InvariantCulture);
        if (ask.Seed is { } seed) map[Seed] = seed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A load read back. A key that is absent, or not what it should be, reads as a brain as built.</summary>
    public static WorkerLoadAsk GetLoad(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        map.TryGetValue(Model, out var model);
        map.TryGetValue(Directory, out var directory);
        CircleAI.Core.ModelEngine? engine = map.TryGetValue(Engine, out var named)
            && Enum.TryParse<CircleAI.Core.ModelEngine>(named, ignoreCase: true, out var parsed) ? parsed : null;
        CircleAI.Core.KvCompressionMode? kv = map.TryGetValue(KvMode, out var kvName)
            && Enum.TryParse<CircleAI.Core.KvCompressionMode>(kvName, ignoreCase: true, out var kvParsed) ? kvParsed : null;
        var share = map.TryGetValue(ContextShare, out var shareText)
            && double.TryParse(shareText, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && s is > 0 and < 1 ? s : 1.0;
        int? drafts = map.TryGetValue(Drafts, out var draftsText)
            && int.TryParse(draftsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 8 ? d : null;
        int? seed = map.TryGetValue(Seed, out var seedText)
            && int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd) ? sd : null;
        return new WorkerLoadAsk(model, directory, Flag(map, SkipHash), engine, kv, share, Flag(map, ForIndex), drafts, seed);
    }

    /// <summary>
    /// Whether two loads ask for the same brain as built: the same model, engine and shape, and both to answer or both
    /// for the index. A different shape is a different brain, built again between turns - and a brain loaded for its
    /// index, built small and not warmed, is not the brain that answers.
    /// </summary>
    public static bool SameBrain(WorkerLoadAsk loaded, WorkerLoadAsk asked) =>
        string.Equals(loaded.Model, asked.Model, StringComparison.Ordinal) && loaded.Engine == asked.Engine
        && loaded.KvMode == asked.KvMode && Math.Abs(loaded.ContextShare - asked.ContextShare) < 0.001
        && loaded.ForItsIndex == asked.ForItsIndex && loaded.Drafts == asked.Drafts && loaded.Seed == asked.Seed;

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
        if (job.JustRead) map[JustRead] = "1";
        if (job.Conversation is { Length: > 0 } talk) map[Conversation] = talk;
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

    // ── the brain answering ──────────────────────────────────────────────────

    /// <summary>Write the brain answering the job in flight; nothing when it is not known yet.</summary>
    public static void PutAnswering(IDictionary<string, string> map, AnsweringBrain? brain)
    {
        if (brain is null) return;
        map[Working] = brain.ModelId;
        map[WorkingSlot] = brain.Slot;
    }

    /// <summary>The brain answering the job in flight, or null when the map names none.</summary>
    public static AnsweringBrain? GetAnswering(IReadOnlyDictionary<string, string> map) =>
        map.TryGetValue(Working, out var model) && !string.IsNullOrWhiteSpace(model)
            ? new AnsweringBrain(model, map.TryGetValue(WorkingSlot, out var slot) && !string.IsNullOrWhiteSpace(slot) ? slot : AnsweringBrain.Main)
            : null;

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
