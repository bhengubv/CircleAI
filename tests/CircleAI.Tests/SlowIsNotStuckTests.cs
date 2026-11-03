// SlowIsNotStuckTests.cs
//
// S9 ON THE P30, 2026-11-02 (OPEN-GAPS E33): THE WATCHDOG ENDED A WORKING PREFILL, AND NAMED THE
// WRONG BRAIN FOR IT. A picture went through the queue to the C# 2B and its tower; they read the turn
// - persona, the session's history and the question - at about two tokens a second, wrote nothing
// for 90 s, and the supervisor ended the inference process twice. The health event and the death line
// named Qwen3.5-0.8B-MNN, idle in the main slot, "while ... loading"; Wolverine shortened every
// brain's context for seven hours. These pin the fix: the worker's CPU time counts as progress, a
// job a minute past its own deadline is ended however busy it is, every report names the brain that
// was working, and a picture turn carries no history.

using System.Runtime.CompilerServices;
using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using CircleAI.Linking;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

public sealed class SlowIsNotStuckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 18, 35, 15, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

    [Fact]
    public void A_turn_that_writes_nothing_while_its_process_works_is_not_stalled()
    {
        // The E33 turn: the tower, then the prefill - several cores busy for two and a half minutes, no text.
        var watch = new ProgressWatch(T0, Limit);
        var cpu = 50_000L;
        for (var s = 0; s <= 150; s += 10)
        {
            watch.Worked(9116, cpu, T0.AddSeconds(s));
            cpu += 38_000;   // 3.8 cores for ten seconds
        }

        Assert.False(watch.Stalled(T0.AddSeconds(150)));
        Assert.True(watch.Silent(T0.AddSeconds(150)));   // the old rule would have ended it at 90 s
        Assert.Equal(3.8, watch.Cores!.Value, 3);
    }

    [Fact]
    public void A_turn_that_writes_nothing_while_its_process_spends_nothing_is_stalled_at_the_limit()
    {
        // A hung native call: the binder threads answer polls, a few milliseconds in ten seconds.
        var watch = new ProgressWatch(T0, Limit);
        for (var s = 0; s <= 90; s += 10) watch.Worked(9116, 50_000 + s * 2L, T0.AddSeconds(s));

        Assert.False(watch.Stalled(T0.AddSeconds(90)));
        Assert.True(watch.Stalled(T0.AddSeconds(91)));

        // And with no CPU time to read at all, the rule is the old one.
        var blind = new ProgressWatch(T0, Limit);
        Assert.False(blind.Stalled(T0.AddSeconds(90)));
        Assert.True(blind.Stalled(T0.AddSeconds(91)));
    }

    [Fact]
    public void Work_that_stops_counts_until_the_limit_after_it_and_a_piece_of_the_answer_always_counts()
    {
        // Busy for a minute, then hung: ended 90 s after the last work seen, not 90 s after the start.
        var watch = new ProgressWatch(T0, Limit);
        for (var s = 0; s <= 60; s += 10) watch.Worked(9116, s * 3_000L, T0.AddSeconds(s));
        for (var s = 70; s <= 160; s += 10) watch.Worked(9116, 180_000, T0.AddSeconds(s));
        Assert.False(watch.Stalled(T0.AddSeconds(150)));
        Assert.True(watch.Stalled(T0.AddSeconds(151)));

        var writing = new ProgressWatch(T0, Limit);
        writing.Wrote(T0.AddSeconds(80));
        Assert.False(writing.Stalled(T0.AddSeconds(170)));
        Assert.False(writing.Silent(T0.AddSeconds(170)));
        Assert.True(writing.Stalled(T0.AddSeconds(171)));
    }

    [Fact]
    public void A_new_process_a_sample_seen_before_or_a_time_that_could_not_be_read_is_no_evidence()
    {
        var watch = new ProgressWatch(T0, Limit);
        Assert.False(watch.Worked(100, 1_000, T0));

        // The worker died and came back: its count starts from nought, so the jump is not work.
        Assert.False(watch.Worked(200, 900_000, T0.AddSeconds(10)));
        Assert.Null(watch.Cores);

        Assert.True(watch.Worked(200, 940_000, T0.AddSeconds(20)));
        Assert.False(watch.Worked(200, 940_000, T0.AddSeconds(20)));   // the same heartbeat, read twice
        Assert.False(watch.Worked(200, -1, T0.AddSeconds(30)));        // the worker could not read its time
        Assert.False(watch.Worked(0, 999_999, T0.AddSeconds(40)));
    }

    [Fact]
    public void The_cpu_time_is_read_from_a_proc_stat_line()
    {
        // utime 2310 and stime 145 ticks, a hundred to the second, in a line shaped as the kernel writes it.
        const string worker = "9116 (rvice:inference) R 548 548 0 0 -1 4211008 91283 0 1203 0 2310 145 0 0 20 0 41 0 6243911 "
                              + "2312478720 98213 18446744073709551615 1 1 0 0 0 0 4612 1 1073775864 0 0 0 17 5 0 0 0 0 0";
        Assert.Equal(24_550L, ProcessCpu.FromStat(worker));

        // A command may hold spaces and parentheses of its own: the fields count from the last ')'.
        Assert.Equal(1_230L, ProcessCpu.FromStat("77 (a (b) c) S 1 1 0 0 -1 0 0 0 0 0 100 23 0 0 20 0 1 0 1 1 1"));

        Assert.Null(ProcessCpu.FromStat(null));
        Assert.Null(ProcessCpu.FromStat("not a stat line"));
        Assert.Null(ProcessCpu.FromStat("1 (x) S 1 2 3"));
    }

    [Fact]
    public void A_job_a_minute_past_its_deadline_is_a_call_that_will_not_stop()
    {
        Assert.False(ProgressWatch.Overran(TimeSpan.Zero));
        Assert.False(ProgressWatch.Overran(TimeSpan.FromSeconds(59)));   // the tower, or MNN's prefill, ending after a cancel
        Assert.True(ProgressWatch.Overran(TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void The_worker_says_which_brain_is_answering_across_the_process_boundary()
    {
        var map = new Dictionary<string, string>();
        WorkerWire.PutAnswering(map, new AnsweringBrain("Ternary-Bonsai-2-2B", "Vision specialist"));
        var back = WorkerWire.GetAnswering(map);
        Assert.Equal(new AnsweringBrain("Ternary-Bonsai-2-2B", "Vision specialist"), back);
        Assert.Equal("Ternary-Bonsai-2-2B (Vision specialist)", back!.ToString());

        var none = new Dictionary<string, string>();
        WorkerWire.PutAnswering(none, null);
        Assert.Empty(none);
        Assert.Null(WorkerWire.GetAnswering(none));
        Assert.Equal(AnsweringBrain.Main, WorkerWire.GetAnswering(new Dictionary<string, string> { [WorkerWire.Working] = "m" })!.Slot);
    }

    [Fact]
    public void The_supervisor_counts_the_workers_cpu_and_ends_a_job_past_its_ceiling()
    {
        // THE ANDROID HALF, which the test legs do not compile, read rather than run.
        var supervisor = Source("src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs");
        var generate = supervisor.IndexOf("public async IAsyncEnumerable<BrainChunk> GenerateAsync(", StringComparison.Ordinal);
        var watch = supervisor.IndexOf("new ProgressWatch(DateTimeOffset.UtcNow, NoProgress)", generate, StringComparison.Ordinal);
        var worked = supervisor.IndexOf("progress.Worked(beat.Pid, beat.CpuMs, beat.At)", watch, StringComparison.Ordinal);
        var stalled = supervisor.IndexOf("if (progress.Stalled(now))", worked, StringComparison.Ordinal);
        var wrote = supervisor.IndexOf("progress.Wrote(DateTimeOffset.UtcNow);", stalled, StringComparison.Ordinal);
        Assert.True(generate >= 0 && watch > generate && worked > watch && stalled > worked && wrote > stalled,
            "the watchdog does not count the worker's CPU time as progress");

        var heartbeat = supervisor.IndexOf("private void StartHeartbeat()", StringComparison.Ordinal);
        Assert.True(heartbeat >= 0 && supervisor.IndexOf("ProgressWatch.Overran(TimeSpan.FromMilliseconds(beat.OverdueMs))",
            heartbeat, StringComparison.Ordinal) > heartbeat, "nothing ends a job that runs past its deadline");

        // The worker reads its own time, and says how late its ask is and who is answering it.
        var worker = Source("src", "CircleAIService", "Droid", "Inference", "InferenceWorkerService.cs");
        Assert.Contains("res[WorkerWire.CpuMs] = WorkerWire.L(ProcessCpu.OfThisProcess());", worker, StringComparison.Ordinal);
        Assert.Contains("res[WorkerWire.OverdueMs] = WorkerWire.L(", worker, StringComparison.Ordinal);
        Assert.Contains("Deadline = deadline,", worker, StringComparison.Ordinal);
        var connection = Source("src", "CircleAIService", "Droid", "Inference", "WorkerConnection.cs");
        Assert.Contains("CpuMs: WorkerWire.Long(map, WorkerWire.CpuMs, -1)", connection, StringComparison.Ordinal);
        Assert.Contains("OverdueMs: WorkerWire.Long(map, WorkerWire.OverdueMs)", connection, StringComparison.Ordinal);
    }

    internal static string Source(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "CircleAIService")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("the repository root was not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir, Path.Combine(parts)));
    }
}

[Collection(nameof(WolverineCollection))]
public sealed class TheBrainThatWasWorkingTests : IDisposable
{
    private const string Main = "Qwen3.5-0.8B-MNN";
    private const string Eyes = "Ternary-Bonsai-2-2B";
    private readonly string _dir = TempDir("e33");
    private readonly SqliteModelCatalog _cat = new("Data Source=:memory:");

    public TheBrainThatWasWorkingTests() => Reset();

    public void Dispose()
    {
        Reset();
        _cat.Dispose();
        Delete(_dir);
    }

    private string Weights(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "weights");
        return path;
    }

    /// <summary>A router the test turns: a picture to the Vision specialist, then text to the brain.</summary>
    private sealed class TurnRouter(RouteDecision first) : INeuronRouter
    {
        public RouteDecision Next = first;
        public RouteDecision Route(RouteContext context) => Next;
    }

    /// <summary>A brain that answers in one piece, or fails as MNN's vision call did; it can look around mid-turn.</summary>
    private sealed class Speaker(string text) : IChatGenerator
    {
        public bool Broken;
        public Action? During;

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(text);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
                                                         [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            During?.Invoke();
            if (Broken) throw new InvalidOperationException("MNN vision generation failed with code -6");
            yield return text;
        }

        public void Dispose() { }
    }

    private static async Task<List<BrainChunk>> All(IAsyncEnumerable<BrainChunk> chunks)
    {
        var all = new List<BrainChunk>();
        await foreach (var chunk in chunks) all.Add(chunk);
        return all;
    }

    [Fact]
    public async Task The_service_names_the_specialist_before_its_load_and_the_main_brain_when_it_answers()
    {
        var mainPath = Weights("main.bin");
        var eyesPath = Weights("eyes.bin");
        var said = new List<AnsweringBrain>();
        var saidAtItsLoad = new List<AnsweringBrain>();
        var router = new TurnRouter(RouteDecision.Specialist(ChatCapability.Vision, "a picture"));
        var options = new AIOptions { ModelPath = mainPath, ModelId = Main, WarmOnStart = false, Router = router, Answering = said.Add };
        await using var service = new AIService(options, new FakeModelLoader(new Dictionary<string, string> { [Eyes] = eyesPath }),
            path =>
            {
                if (path != eyesPath) return new Speaker("GEN");
                saidAtItsLoad.AddRange(said);
                return new Speaker("SPEC");
            },
            new FakeModelSelector(new ModelSelection(Eyes, false, 10_000, DeviceTier.Desktop)));
        await service.StartAsync();

        Assert.Equal("SPEC", await service.AskAsync("What is in this picture?"));

        // NAMED BEFORE ITS LOAD: a second brain loading beside the first is where a phone runs out of memory.
        Assert.Equal(new[] { new AnsweringBrain(Eyes, "Vision specialist") }, saidAtItsLoad);
        Assert.Equal(new AnsweringBrain(Eyes, "Vision specialist"), said.Single());

        router.Next = RouteDecision.Generalist("text");
        Assert.Equal("GEN", await service.AskAsync("Hello?"));
        Assert.Equal(new AnsweringBrain(Main, AnsweringBrain.Main), said[^1]);
    }

    [Fact]
    public async Task The_worker_names_the_brain_answering_in_its_marker_its_timing_and_its_failure()
    {
        var mainPath = Weights("main.bin");
        var eyesPath = Weights("eyes.bin");
        var eyes = new Speaker("A red circle beside a blue square.");
        var router = new TurnRouter(RouteDecision.Specialist(ChatCapability.Vision, "a picture"));
        var options = new AIOptions { ModelPath = mainPath, ModelId = Main, WarmOnStart = false, Router = router };
        var service = new AIService(options, new FakeModelLoader(new Dictionary<string, string> { [Eyes] = eyesPath }),
            path => path == eyesPath ? eyes : new Speaker("A main answer."),
            new FakeModelSelector(new ModelSelection(Eyes, false, 10_000, DeviceTier.Desktop)));
        await using var worker = new InProcessBrainWorker(async ct =>
        {
            await service.StartAsync(ct);
            return new InProcessBrain(service, Main);
        });
        options.Answering = worker.ReportAnswering;   // as both hosts wire it

        RiskySteps.Directory = RiskySteps.For(_dir, "inference");
        string? marker = null;
        eyes.During = () => marker = RiskySteps.Leftover(RiskySteps.Directory!);

        // A picture goes to the C# 2B as the Vision specialist: the marker says so while it works - a
        // process that dies there is read by it - and the timing a speed is learned from names it.
        var picture = new BrainJob("p1", [new ChatMessage("user", "What is in this picture?")], null, 64, false,
                                   DateTimeOffset.UtcNow.AddMinutes(1));
        var chunks = await All(worker.GenerateAsync(picture, CancellationToken.None));
        Assert.Equal("A red circle beside a blue square.", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(Eyes, chunks[^1].Timing!.ModelId);
        Assert.StartsWith($"generate {Eyes} | Vision specialist | ", marker, StringComparison.Ordinal);
        Assert.Null(RiskySteps.Leftover(RiskySteps.Directory!));
        Assert.Null(worker.Answering);

        // Its failure - SmolVLM's -6, on the P30 - is the specialist's, not the main brain's.
        eyes.Broken = true;
        var failed = await Assert.ThrowsAsync<BrainWorkerException>(() => All(worker.GenerateAsync(picture with { AskId = "p2" }, CancellationToken.None)));
        Assert.Equal(Eyes, failed.Failure.ModelId);

        // A turn the main brain answers names it.
        router.Next = RouteDecision.Generalist("text");
        chunks = await All(worker.GenerateAsync(picture with { AskId = "t1" }, CancellationToken.None));
        Assert.Equal("A main answer.", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(Main, chunks[^1].Timing!.ModelId);

        // Outside a turn, a report is nobody's.
        worker.ReportAnswering(new AnsweringBrain("stray", "Vision specialist"));
        Assert.Null(worker.Answering);
    }

    [Fact]
    public void The_death_line_says_which_brain_was_doing_what_and_why()
    {
        var said = new List<string>();
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb(Eyes), said.Add, killedByTheSystem: true,
                            doing: "reading its turn as the Vision specialist", why: "TimedOut, killed-by-watchdog");
        Assert.Equal($"{Eyes} stopped while reading its turn as the Vision specialist (TimedOut, killed-by-watchdog); "
                     + "not held against the model", said[^1]);

        // A crash while answering is a strike against the brain that was answering, said as one.
        _cat.Upsert(new ModelEntry(Eyes, "1.0", "PTQ1_0")
        {
            Modality = ModelModality.Chat, Engine = ModelEngine.Managed, TotalBytes = 430_000_000,
            MinRamGb = 1, MinStorageGb = 1, QualityRank = 10, Capabilities = ["Default"],
        });
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb(Eyes), said.Add, doing: "answering as the Vision specialist");
        Assert.Equal($"{Eyes} stopped while answering as the Vision specialist (1 of 2); trying it again before refusing it", said[^1]);

        // A load's words are as they were.
        CrashVerdict.Record(_cat, CrashVerdict.Breadcrumb(Main), said.Add, killedByTheSystem: true);
        Assert.Equal($"the system stopped this service while {Main} was loading; not held against the model", said[^1]);
    }

    [Fact]
    public void The_supervisor_sets_a_death_a_failure_and_a_speed_against_the_brain_the_worker_named()
    {
        var supervisor = SlowIsNotStuckTests.Source("src", "CircleAIService", "Droid", "Inference", "InferenceSupervisor.cs");
        Assert.Contains("Fail(failure, model, died.Death.Working);", supervisor, StringComparison.Ordinal);
        Assert.Contains("var subject = working?.ModelId ?? model;", supervisor, StringComparison.Ordinal);
        Assert.Contains("RaiseHealth(failure, subject, marker, working?.Slot);", supervisor, StringComparison.Ordinal);
        Assert.Contains("CrashVerdict.Record(_catalogue(), CrashVerdict.Breadcrumb(subject)", supervisor, StringComparison.Ordinal);
        Assert.Contains("doing: Doing(failure.Phase, working)", supervisor, StringComparison.Ordinal);
        Assert.Contains("var subject = refused.Failure.ModelId ?? working?.ModelId ?? model;", supervisor, StringComparison.Ordinal);
        Assert.Contains("var answered = chunk.Timing?.ModelId ?? model;", supervisor, StringComparison.Ordinal);
        Assert.Contains("LearnSpeed(answered, chunk.Timing);", supervisor, StringComparison.Ordinal);

        // The connection keeps what the worker said, and a death inside an ask carries it.
        var connection = SlowIsNotStuckTests.Source("src", "CircleAIService", "Droid", "Inference", "WorkerConnection.cs");
        Assert.Contains("if (WorkerWire.GetAnswering(poll) is { } working) lock (_gate) _working = working;", connection, StringComparison.Ordinal);
        Assert.Contains("_ask is null ? null : _working", connection, StringComparison.Ordinal);

        // Both hosts wire the service's word to their worker.
        var worker = SlowIsNotStuckTests.Source("src", "CircleAIService", "Droid", "Inference", "InferenceWorkerService.cs");
        Assert.Contains("answering => created?.ReportAnswering(answering)", worker, StringComparison.Ordinal);
        Assert.Contains("options.Answering = answering;", worker, StringComparison.Ordinal);
        Assert.Contains("WorkerWire.PutAnswering(res, brain?.Answering);", worker, StringComparison.Ordinal);
        var desktop = SlowIsNotStuckTests.Source("src", "CircleAIService", "Host", "Program.cs");
        Assert.Contains("options.Answering = answering => reportAnswering?.Invoke(answering);", desktop, StringComparison.Ordinal);
        Assert.Contains("reportAnswering = brain.ReportAnswering;", desktop, StringComparison.Ordinal);
    }
}

public sealed class APictureTurnCarriesNoHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-e33-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private sealed class Brain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; private set; } = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow);
        public event EventHandler<BrainWorkerStatus>? StatusChanged;
        public readonly List<BrainJob> Jobs = new();

        public Task EnsureReadyAsync(CancellationToken ct)
        {
            Status = Status with { Phase = BrainPhase.Ready, ModelId = "brain" };
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            await Task.Yield();
            yield return new BrainChunk("Here is what I can tell you.");
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(PrefillMs: 1, PromptTokens: 1, DecodeMs: 1, OutputTokens: 2));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_picture_turn_is_the_picture_and_its_question_and_the_next_text_turn_has_its_history()
    {
        using var asks = new SqliteAskStore(Path.Combine(_dir, "asks.db"));
        var brain = new Brain();
        var store = new LinkImageStore(Path.Combine(_dir, "pictures"));
        await using var front = new RequestCoordinator(asks, brain, ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero })
        {
            Images = ask => store.For(ask.CallerKey, ask.ClientRequestId),
        };
        front.Start();

        async Task<AskRecord> Ask(string requestId, string question)
        {
            var admission = front.Submit(new AskSubmission("internal:adb", requestId, "adb", AskLane.Foreground, question));
            var done = await front.WaitAsync("internal:adb", admission.Record!.Id, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(AskState.Succeeded, done!.State);
            return done;
        }

        // The session as S9's soak left it: questions already answered in it.
        await Ask("r1", "What is 17 times 4?");
        await Ask("r2", "What is the capital of Kenya?");
        Assert.Equal(3, brain.Jobs[^1].Messages.Count);   // the first turn, its answer, the question

        // A picture asked in that session is the picture and its question, and nothing else.
        var picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
        store.Hold(picture, "internal:adb", "pic");
        await Ask("pic", "What is in this picture?");
        var turn = Assert.Single(brain.Jobs[^1].Messages);
        Assert.Equal("What is in this picture?", turn.Content);
        Assert.Equal(picture, turn.ImageBytes);

        // And the session goes on: the next text turn carries its history again.
        await Ask("r3", "Thank you.");
        var after = brain.Jobs[^1].Messages;
        Assert.True(after.Count > 3, "the history did not come back after a picture");
        Assert.Contains(after, m => m.Content == "What is 17 times 4?");
        Assert.All(after, m => Assert.Null(m.ImageBytes));
    }
}
