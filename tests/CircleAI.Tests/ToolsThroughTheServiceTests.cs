// ToolsThroughTheServiceTests.cs
//
// OPEN-GAPS A16: AN APP'S TOOL TURN THROUGH THE SERVICE. The queue stored "tools allowed" with
// every ask and nothing read it, and the inference process had no tool bridge, so a turn an app
// asked with tools - because its streamed answer had begun a call - came back as the plain one:
// the call it had already seen, written again. These pin the whole path. The door carries the
// flag to the job, the wire carries it across the two processes, the worker answers such a job
// through ToolTurn, and ToolTurn runs the engine's own tool for a plain intent and the agentic
// loop for the rest. The service's own wiring - the bridge in the inference process, TurnAgentic -
// is Android code the test legs do not compile, so it is pinned by reading the source.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class ToolsThroughTheServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-tools-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static readonly ServiceLimits Quick = ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero };

    /// <summary>A brain worker that records each job and answers it.</summary>
    private sealed class RecordingBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "Qwen3.5-0.8B-MNN" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public readonly List<BrainJob> Jobs = [];

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            await Task.Yield();
            yield return new BrainChunk("An answer.");
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(PrefillMs: 10, PromptTokens: 5, DecodeMs: 10, OutputTokens: 3));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A brain that says which path a turn took: streamed, the agentic loop, or a tool run by the engine.</summary>
    private sealed class ToolBrain : IAIService
    {
        public readonly List<string> Agentic = [];
        public readonly List<string> Tools = [];
        public int Streams;
        public string AgenticAnswer = "Twelve times eight is 96.";

        public bool IsReady => true;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> AskAsync(string question, CancellationToken ct = default) => Task.FromResult("asked");
        public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
            => Task.FromResult("chatted");

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
                                                         [EnumeratorCancellation] CancellationToken ct = default)
        {
            Streams++;
            await Task.Yield();
            yield return "<tool_call>";
        }

        public Task<ToolResult> InvokeToolAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            Tools.Add(invocation.ToolName);
            return Task.FromResult(ToolResult.Ok(invocation.ToolName, 64));
        }

        public Task<string> AgenticChatAsync(string prompt, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Agentic.Add(prompt);
            return Task.FromResult(AgenticAnswer);
        }

        public Task SubmitFeedbackAsync(FeedbackSignal signal, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<AskRecord> Answered(RequestCoordinator front, string question, bool tools)
    {
        var admission = front.Submit(new AskSubmission("app:1", Guid.NewGuid().ToString("N"), "", AskLane.Foreground,
                                                       question, question, Agentic: tools));
        Assert.True(admission.Accepted);
        var done = await front.WaitAsync("app:1", admission.Record!.Id, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(done!.IsFinal, "the ask did not finish: " + done.State);
        return done;
    }

    [Fact]
    public async Task An_ask_that_allows_tools_reaches_the_brain_as_a_tool_turn()
    {
        var brain = new RecordingBrain();
        var store = new SqliteAskStore(Path.Combine(_dir, "asks.db"));
        _owned.Add(store);
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        await Answered(front, "What is 6789 times 3?", tools: true);
        await Answered(front, "Tell me about the sea.", tools: false);

        Assert.Equal(2, brain.Jobs.Count);
        Assert.True(brain.Jobs[0].Tools, "the door dropped the tools the caller allowed");
        Assert.False(brain.Jobs[1].Tools);
    }

    [Fact]
    public void The_wire_carries_tools_and_a_job_without_them_writes_nothing()
    {
        var job = new BrainJob("a1", [new("user", "q")], "q", 80, false, DateTimeOffset.UtcNow, Tools: true);
        var map = new Dictionary<string, string>();
        WorkerWire.PutJobSettings(map, job);
        Assert.True(WorkerWire.GetTools(map));

        // AN OLDER WORKER READS A PLAIN JOB EXACTLY AS IT ALWAYS DID: no key it does not know.
        var plain = new Dictionary<string, string>();
        WorkerWire.PutJobSettings(plain, job with { Tools = false });
        Assert.False(plain.ContainsKey(WorkerWire.Tools));
        Assert.False(WorkerWire.GetTools(plain));
    }

    [Fact]
    public async Task A_plain_intent_runs_the_engines_own_tool_and_the_rest_go_to_the_agentic_loop()
    {
        var brain = new ToolBrain();

        // A battery question: the engine runs the tool and words the answer - the model is not asked.
        var battery = await ToolTurn.RunAsync(brain, "composed: what is my battery", "What's my battery level?");
        Assert.Equal(new[] { "get_battery_level" }, brain.Tools);
        Assert.Empty(brain.Agentic);
        Assert.Equal("Your battery is at 64%.", battery);

        // Anything else: the agentic loop, with the turn as composed.
        var sum = await ToolTurn.RunAsync(brain, "composed: 12 times 8", "What is 12 times 8?");
        Assert.Equal(new[] { "composed: 12 times 8" }, brain.Agentic);
        Assert.Equal(brain.AgenticAnswer, sum);
    }

    [Fact]
    public async Task The_worker_answers_a_tool_job_whole_through_the_tool_turn_and_streams_the_rest()
    {
        var service = new ToolBrain();
        await using var worker = new InProcessBrainWorker(_ => Task.FromResult(new InProcessBrain(service, "brain")));

        // THE TURN IS THE LAST THE PERSON SAID, as the coordinator's conversation ends on it.
        var job = new BrainJob("a1",
            [new("user", "earlier"), new("assistant", "ok"), new("user", "composed: 12 times 8")],
            "What is 12 times 8?", 80, false, DateTimeOffset.UtcNow.AddMinutes(1), Tools: true);
        var chunks = new List<BrainChunk>();
        await foreach (var chunk in worker.GenerateAsync(job, CancellationToken.None)) chunks.Add(chunk);

        Assert.Equal(0, service.Streams);
        Assert.Equal(new[] { "composed: 12 times 8" }, service.Agentic);
        Assert.Equal(service.AgenticAnswer, string.Concat(chunks.Select(c => c.Text)));
        Assert.True(chunks[^1].Final);
        Assert.Equal("brain", chunks[^1].Timing!.ModelId);

        // A job that allows no tools streams, as every job did before.
        chunks.Clear();
        await foreach (var chunk in worker.GenerateAsync(job with { Tools = false }, CancellationToken.None)) chunks.Add(chunk);
        Assert.Equal(1, service.Streams);
        Assert.Single(service.Agentic);
        Assert.Equal("<tool_call>", string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task Each_tool_that_runs_is_logged_by_name_and_never_by_its_arguments_or_result()
    {
        var lines = new List<string>();
        var bridge = new LoggedToolBridge(new CircleAIToolBridge(() => 41), lines.Add);
        Assert.Equal(new CircleAIToolBridge().AvailableTools.Select(t => t.Name), bridge.AvailableTools.Select(t => t.Name));

        var result = await bridge.InvokeAsync(new ToolInvocation
        {
            ToolName = "calculate",
            Arguments = new Dictionary<string, object?> { ["expression"] = "6789 * 3" },
        });
        Assert.True(result.Success);
        var line = Assert.Single(lines);
        Assert.StartsWith("tool ran: calculate (ok)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("6789", line, StringComparison.Ordinal);
        Assert.DoesNotContain("20367", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_gives_its_inference_process_the_tools_and_says_turns_carry_them()
    {
        // THE CALLERS: Android code the test legs do not compile, so read, not run - the bridge is
        // built where the inference process's brain is, and the readiness tells apps tools are carried.
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAIService", "ServiceApplication.cs"));
        Assert.Contains("ToolBridge = new CircleAI.Tools.LoggedToolBridge(", service, StringComparison.Ordinal);
        Assert.Contains("new CircleAI.Assistant.CircleAIToolBridge(BatteryPercent)", service, StringComparison.Ordinal);
        Assert.Contains("ReadinessSettings.TurnAgentic, \"1\"", service, StringComparison.Ordinal);

        var worker = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAIService", "Droid", "Inference", "InferenceWorkerService.cs"));
        Assert.Contains("Tools: WorkerWire.GetTools(req)", worker, StringComparison.Ordinal);

        var desktop = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAIService", "Host", "Program.cs"));
        Assert.Contains("new CircleAI.Assistant.CircleAIToolBridge()", desktop, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "CircleAIService")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
