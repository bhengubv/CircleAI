// WolverineOutboxTests.cs
//
// The WolverineAPI outbox (S6): fingerprinted reports counted per fingerprint, kept on the device
// until an endpoint exists, sent in batches when online with every JSON name pinned, left in the
// queue until the server accepts them, capped, on by default, switchable off - and carrying no
// personal content, which the last test proves end to end: a question and an answer go through
// the real queue, the real agentic loop and an exception message, and not a word of either
// reaches the queue on disk or the body that is posted.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core.Logging;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Hosting.SelfHealing.Upstream;
using CircleAI.Inference;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

[Collection(nameof(WolverineCollection))]
public sealed class WolverineOutboxTests : IDisposable
{
    private const string Endpoint = "https://wolverine.example/api/device-reports";
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TempDir("outbox");
    private readonly Capture _http = new();
    private bool _online = true;

    public WolverineOutboxTests() => Reset();

    public void Dispose()
    {
        Reset();
        Delete(_dir);
    }

    /// <summary>A server that keeps every body it was sent and answers what it is told to.</summary>
    private sealed class Capture : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        public HttpStatusCode Status = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(Status);
        }
    }

    private WolverineOutbox Outbox(Func<string, bool>? known = null) =>
        new(Path.Combine(_dir, "wolverine"), new HttpClient(_http), () => _online, new WolverineClock(Noon), known);

    [Fact]
    public void One_failure_many_times_is_one_report_with_a_count()
    {
        using var outbox = Outbox();
        for (var i = 0; i < 3; i++) Assert.True(outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode)));

        var report = Assert.Single(outbox.Pending());
        Assert.Equal(3, report.Occurrences);
        Assert.Equal("CircleAI:IllegalInstruction:InferenceWorker.Decode", report.Fingerprint);
    }

    [Fact]
    public async Task With_no_endpoint_the_reports_stay_on_the_device_across_a_restart()
    {
        using (var outbox = Outbox())
        {
            outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode));
            Assert.Equal(OutboxFlush.NoEndpoint, await outbox.FlushAsync());
        }

        using var again = Outbox();
        Assert.Single(again.Pending());
        Assert.Empty(_http.Bodies);
    }

    [Fact]
    public async Task Offline_they_wait()
    {
        using var outbox = Outbox();
        outbox.SetEndpoint(Endpoint);
        outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode));
        _online = false;

        Assert.Equal(OutboxFlush.Offline, await outbox.FlushAsync());
        Assert.Single(outbox.Pending());
    }

    [Fact]
    public async Task A_batch_goes_with_its_names_pinned_and_leaves_the_queue_only_once_accepted()
    {
        using var outbox = Outbox(known: id => id == Model);
        Assert.True(outbox.SetEndpoint(Endpoint));
        outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode));

        _http.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(OutboxFlush.Failed, await outbox.FlushAsync());
        Assert.Single(outbox.Pending());

        _http.Status = HttpStatusCode.Accepted;
        Assert.Equal(OutboxFlush.Sent, await outbox.FlushAsync(new WolverineDeviceClass { FormFactor = "Phone" }, "3.9.0+42"));
        Assert.Empty(outbox.Pending());
        Assert.NotNull(outbox.LastSentUtc);

        using var json = JsonDocument.Parse(_http.Bodies[^1]);
        var root = json.RootElement;
        Assert.Equal("CircleAI", root.GetProperty("apiName").GetString());
        Assert.Equal("3.9.0+42", root.GetProperty("appVersion").GetString());
        Assert.Equal("Phone", root.GetProperty("device").GetProperty("formFactor").GetString());
        var report = root.GetProperty("reports")[0];
        Assert.Equal("CircleAI:IllegalInstruction:InferenceWorker.Decode", report.GetProperty("fingerprint").GetString());
        Assert.Equal("IllegalInstruction", report.GetProperty("errorType").GetString());
        Assert.Equal("InferenceWorker.Decode", report.GetProperty("endpoint").GetString());
        Assert.Equal("Engine", report.GetProperty("area").GetString());
        Assert.Equal(Model, report.GetProperty("subject").GetString());
        Assert.Equal(1, report.GetProperty("occurrences").GetInt32());
        Assert.Equal(16, report.GetProperty("messageHash").GetString()!.Length);
        Assert.False(report.TryGetProperty("message", out _));   // there is no message to carry
    }

    [Fact]
    public void A_subject_travels_only_when_it_is_a_model_this_device_knows()
    {
        using var outbox = Outbox(known: id => id == Model);
        outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode, model: "somebody's-name"));

        Assert.Null(Assert.Single(outbox.Pending()).Subject);
    }

    [Fact]
    public async Task Switched_off_it_keeps_nothing_sends_nothing_and_remembers_it_is_off()
    {
        using (var outbox = Outbox())
        {
            outbox.SetEndpoint(Endpoint);
            outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode));
            outbox.SetEnabled(false);

            Assert.Empty(outbox.Pending());
            Assert.False(outbox.Enqueue(Engine("IllegalInstruction", WorkPhase.Decode)));
            Assert.Equal(OutboxFlush.Off, await outbox.FlushAsync());
        }

        using var again = Outbox();
        Assert.False(again.Enabled);
        Assert.Empty(_http.Bodies);
    }

    [Fact]
    public void Only_an_https_address_is_an_endpoint()
    {
        using var outbox = Outbox();
        Assert.False(outbox.SetEndpoint("http://wolverine.example/"));
        Assert.False(outbox.SetEndpoint("not an address"));
        Assert.Null(outbox.Endpoint);
        Assert.True(outbox.SetEndpoint(Endpoint));
        Assert.True(outbox.SetEndpoint(""));
        Assert.Null(outbox.Endpoint);
    }

    [Fact]
    public void The_queue_is_capped_so_a_device_with_nowhere_to_send_cannot_fill_up()
    {
        using var outbox = Outbox();
        for (var i = 0; i < WolverineOutbox.MaxQueued + 10; i++)
            outbox.Enqueue(HealthEvents.Of("Voice", "speak", "VoiceFault" + i, "Speaker.Say"));

        Assert.Equal(WolverineOutbox.MaxQueued, outbox.Queued);
    }

    [Fact]
    public async Task A_remedys_outcome_that_comes_after_the_report_left_travels_on_its_own()
    {
        using var outbox = Outbox();
        outbox.SetEndpoint(Endpoint);
        var e = Engine("IllegalInstruction", WorkPhase.Decode);
        outbox.Enqueue(e);
        outbox.NoteRemedy(e.Fingerprint, Remedies.ScalarKernels, worked: null);
        Assert.Equal(OutboxFlush.Sent, await outbox.FlushAsync());

        outbox.NoteRemedy(e.Fingerprint, Remedies.ScalarKernels, worked: true);

        var update = Assert.Single(outbox.Pending());
        Assert.Equal(0, update.Occurrences);
        Assert.Equal(Remedies.ScalarKernels, update.Remedy);
        Assert.True(update.RemedyWorked);
    }

    // ── the privacy proof ────────────────────────────────────────────────────

    private const string Question = "Remind me that my bank pin is 4321 and that Thandiwe has the spare key";
    private const string Answer = "Your secret word is aubergine and Thandiwe has it";
    private static readonly string[] Words = ["bank", "4321", "Thandiwe", "spare key", "aubergine", "secret word", "Remind me"];

    /// <summary>A brain that answers nothing but whitespace, to its length limit - a reply that thought it all away.</summary>
    private sealed class SilentBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; private set; } = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow);
        public event EventHandler<BrainWorkerStatus>? StatusChanged;
        public event EventHandler<AnsweringBrain>? AnsweringChanged { add { } remove { } }

        public Task EnsureReadyAsync(CancellationToken ct)
        {
            Status = Status with { Phase = BrainPhase.Ready, ModelId = Model };
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new BrainChunk("   ");
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(OutputTokens: 64, ModelId: Model, Finish: "length"));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A generator whose replies are scripted: a tool call nobody can read, then the answer.</summary>
    private sealed class ScriptedGenerator(params string[] replies) : IChatGenerator
    {
        private int _next;

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return replies[Math.Min(_next++, replies.Length - 1)];
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task No_prompt_or_answer_text_reaches_the_outbox()
    {
        using var outbox = Outbox();
        outbox.SetEndpoint(Endpoint);
        var log = new WolverineLog();
        using var wolverine = new DeviceWolverine(new WolverineOptions
        {
            StateDirectory = _dir,
            Policy = new WolverinePolicy(),
            Log = log,
            FullThreads = 3,
            Outbox = outbox,
            FlushEvery = TimeSpan.Zero,
            TakeEarlierEvents = false,
        });
        BrainTuning.Directory = Path.Combine(_dir, "wolverine");
        wolverine.Start();

        // 1. AN EXCEPTION THAT CARRIES THE QUESTION, quoted and bare - the worst a careless catch can do.
        try { throw new InvalidOperationException($"could not parse {Question} or '{Question}' after: {Answer}"); }
        catch (InvalidOperationException ex) { HealthEvents.Raise(HealthEvents.From("Crash", "unhandled", ex)); }

        // 2. THE REAL QUEUE, asked the question by a person, its reply thinking itself away.
        var asks = Path.Combine(_dir, "asks");
        Directory.CreateDirectory(asks);
        using (var store = new SqliteAskStore(Path.Combine(asks, "asks.db")))
        {
            await using var front = new RequestCoordinator(store, new SilentBrain(),
                ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero, LoadWaitBudget = TimeSpan.FromSeconds(10) });
            front.Start();
            var admission = front.Submit(new AskSubmission("app:1", null, "s", AskLane.Foreground, Question, Question));
            var done = await front.WaitAsync("app:1", admission.Record!.Id, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(AskState.Failed, done!.State);
        }

        // 3. THE REAL AGENTIC LOOP, its tool call holding the answer and readable by nobody.
        var model = Path.GetTempFileName();
        try
        {
            await using var service = new AIService(new AIOptions { ModelPath = model, WarmOnStart = false, AgenticMaxIterations = 3 },
                generatorFactory: _ => new ScriptedGenerator($"<tool_call>{{{Answer}}}</tool_call>", Answer));
            Assert.Equal(Answer, await service.AgenticChatAsync(Question));
        }
        finally
        {
            File.Delete(model);
        }

        // Every path reached the outbox - the test is not vacuous...
        await Eventually.TrueAsync(() => outbox.Pending().Count >= 3, "the three failures to reach the outbox");
        var fingerprints = outbox.Pending().Select(r => r.Fingerprint).ToList();
        Assert.Contains(fingerprints, f => f.StartsWith("CircleAI:InvalidOperationException:", StringComparison.Ordinal));
        Assert.Contains("CircleAI:NoAnswer:Reply.Length", fingerprints);
        Assert.Contains("CircleAI:MalformedToolCall:AIService.AgenticChatAsync", fingerprints);

        // ...and not a word of the question or the answer is in what is kept, or in what leaves.
        Assert.Equal(OutboxFlush.Sent, await wolverine.FlushAsync());
        var kept = string.Concat(Directory.GetFiles(Path.Combine(_dir, "wolverine"), "outbox-*.json").Select(File.ReadAllText));
        var sent = Assert.Single(_http.Bodies);
        foreach (var word in Words)
        {
            Assert.DoesNotContain(word, kept, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, sent, StringComparison.OrdinalIgnoreCase);
        }
    }
}
