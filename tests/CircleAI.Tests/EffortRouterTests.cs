// EffortRouterTests.cs
//
// The per-turn effort router (S8, the plan's Step 1): each question is quick (thinking off) or
// hard (thinking on, with a budget sized to the device's measured speed); a phone gives a hard
// question medium, a computer xhigh; the person cancelling or asking again moves it, per device,
// in SelfModel, and a later start remembers. Then the router in its place - the request
// coordinator, the one door every question comes through - and the plain asks that door now
// takes for a translation, kept in the store across an update.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.SelfKnowledge;
using Xunit;

namespace CircleAI.Tests;

public sealed class EffortRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "circleai-effort-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public EffortRouterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (SelfModel Self, MemoryService Memory) Self(string name = "self")
    {
        var memory = new MemoryService(Path.Combine(_dir, name));
        _owned.Add(memory);
        return (new SelfModel(memory), memory);
    }

    private static EffortRouter Router(FormFactor form, double? speed = null, SelfModel? self = null) =>
        new(form: () => form, speed: _ => speed, self: () => self);

    // ── weighing a question ─────────────────────────────────────────────

    [Theory]
    [InlineData("What time is it?")]
    [InlineData("Hello")]
    [InlineData("Set a timer for five minutes")]
    [InlineData("What is the capital of Kenya?")]
    public void A_fact_a_greeting_or_an_instruction_is_quick(string question) =>
        Assert.Equal(TurnWeight.Quick, EffortRouter.Weigh(question));

    [Theory]
    [InlineData("Why is the sky blue?")]
    [InlineData("Explain how a vaccine trains the body")]
    [InlineData("Solve 3x + 5 = 20")]                              // the concierge's own reasoning cue
    [InlineData("Compare renting with buying a house")]
    [InlineData("Is it going to rain? Should I take the bus?")]   // two questions at once
    public void A_why_a_problem_or_a_comparison_is_hard(string question) =>
        Assert.Equal(TurnWeight.Hard, EffortRouter.Weigh(question));

    [Fact]
    public void A_long_question_is_hard()
    {
        var question = string.Join(' ', Enumerable.Repeat("word", 30));
        Assert.Equal(TurnWeight.Hard, EffortRouter.Weigh(question));
        Assert.Equal(TurnWeight.Quick, EffortRouter.Weigh(string.Join(' ', Enumerable.Repeat("word", 12))));
    }

    // ── what each device gives it ───────────────────────────────────────

    [Fact]
    public void A_phone_answers_quick_asks_at_once_and_thinks_medium_about_hard_ones_within_its_speed()
    {
        // The 2B on the P30: 1.47 tokens a second, measured. Medium is about 45 s of thought.
        var router = Router(FormFactor.Phone, speed: 1.47);

        var quick = router.Choose("What time is it?", "Ternary-Bonsai-2-2B");
        Assert.Equal(ReasoningEffort.Off, quick.Effort);
        Assert.Equal(0, quick.ThinkingBudget);

        var hard = router.Choose("Why is the sky blue?", "Ternary-Bonsai-2-2B");
        Assert.Equal(ReasoningEffort.Medium, hard.Effort);
        Assert.Equal(66, hard.ThinkingBudget);
        Assert.DoesNotContain("sky", hard.Why);           // the reason names no text
        Assert.Contains("medium", hard.Why);
    }

    [Fact]
    public void A_computer_thinks_hard_questions_through_at_xhigh()
    {
        var hard = Router(FormFactor.Desktop, speed: 40).Choose("Why is the sky blue?");
        Assert.Equal(ReasoningEffort.XHigh, hard.Effort);
        Assert.Equal(12000, hard.ThinkingBudget);         // five minutes at forty a second
        Assert.Equal(ReasoningEffort.Off, Router(FormFactor.Laptop).Choose("Hello").Effort);
        Assert.Equal(ReasoningEffort.Low, Router(FormFactor.Watch).Choose("Why is the sky blue?").Effort);
        Assert.Equal(ReasoningEffort.Medium, Router(FormFactor.Unknown).Choose("Why is the sky blue?").Effort);
    }

    [Fact]
    public void The_budget_is_time_at_the_measured_speed_within_a_ceiling_and_a_floor()
    {
        Assert.Equal(0, EffortRouter.Budget(ReasoningEffort.Off, 10));
        Assert.Equal(1024, EffortRouter.Budget(ReasoningEffort.Medium, null));   // nothing measured: the ceiling
        Assert.Equal(32, EffortRouter.Budget(ReasoningEffort.Low, 0.5));         // never fewer than 32
        Assert.Equal(1024, EffortRouter.Budget(ReasoningEffort.Medium, 500));    // a fast machine meets the ceiling
        Assert.Equal("xhigh", ReasoningEffort.XHigh.TemplateName());
        Assert.Equal(ReasoningEffort.High, ReasoningEfforts.Parse("High"));
        Assert.Null(ReasoningEfforts.Parse("loud"));
    }

    // ── learning from the person ────────────────────────────────────────

    [Fact]
    public void Two_answers_cancelled_while_they_thought_take_a_step_off_hard_questions_here()
    {
        var (self, _) = Self();
        var router = Router(FormFactor.Phone, speed: 1.47, self: self);
        var hard = router.Choose("Why is the sky blue?");

        router.Learn(hard, EffortSignal.Cancelled);
        Assert.Equal(ReasoningEffort.Medium, router.Choose("Why is the sky blue?").Effort);   // one is not a lesson
        router.Learn(hard, EffortSignal.Cancelled);
        Assert.Equal(ReasoningEffort.Low, router.Choose("Why is the sky blue?").Effort);

        // Quick questions learned nothing from it.
        Assert.Equal(0, self.EffortShift(TurnWeight.Quick));
    }

    [Fact]
    public void Questions_asked_again_add_a_step_and_cancels_and_re_asks_cancel_out()
    {
        var (self, _) = Self();
        var router = Router(FormFactor.Phone, speed: 1.47, self: self);
        var quick = router.Choose("What is the capital of Kenya?");

        router.Learn(quick, EffortSignal.AskedAgain);
        router.Learn(quick, EffortSignal.AskedAgain);
        Assert.Equal(ReasoningEffort.Low, router.Choose("What is the capital of Kenya?").Effort);

        router.Learn(quick, EffortSignal.Cancelled);
        router.Learn(quick, EffortSignal.Cancelled);
        Assert.Equal(ReasoningEffort.Off, router.Choose("What is the capital of Kenya?").Effort);
    }

    [Fact]
    public void Learning_stays_inside_what_the_device_allows()
    {
        var (self, _) = Self();
        var router = Router(FormFactor.Phone, speed: 1.47, self: self);
        var hard = router.Choose("Why is the sky blue?");
        for (var i = 0; i < 8; i++) router.Learn(hard, EffortSignal.AskedAgain);

        Assert.Equal(2, self.EffortShift(TurnWeight.Hard));                                 // at most two steps
        Assert.Equal(ReasoningEffort.High, router.Choose("Why is the sky blue?").Effort);   // a phone's ceiling
        Assert.Equal(SelfModel.EffortWindow, self.EffortSignals(TurnWeight.Hard).Count);    // the latest few only
    }

    [Fact]
    public async Task A_later_start_remembers_what_this_device_learned()
    {
        var (self, memory) = Self();
        var router = Router(FormFactor.Phone, speed: 1.47, self: self);
        var hard = router.Choose("Why is the sky blue?");
        router.Learn(hard, EffortSignal.Cancelled);
        router.Learn(hard, EffortSignal.Cancelled);

        // The lessons are written in the background; wait for both to land.
        for (var i = 0; i < 200; i++)
        {
            var atoms = await memory.AllAsync(limit: 100);
            if (atoms.Count(a => SelfSubjects.EffortOf(a.Subject) is not null) >= 2) break;
            await Task.Delay(25);
        }

        var later = new SelfModel(memory);
        await later.LoadAsync();
        Assert.Equal(-1, later.EffortShift(TurnWeight.Hard));
        Assert.Equal(ReasoningEffort.Low, Router(FormFactor.Phone, speed: 1.47, self: later).Choose("Why is the sky blue?").Effort);

        // Kept by subject, never by the question's words.
        var stored = await memory.AllAsync(limit: 100);
        Assert.DoesNotContain(stored, a => a.Text.Contains("sky", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((TurnWeight.Hard, EffortSignal.Cancelled), SelfSubjects.EffortOf(SelfSubjects.Effort(TurnWeight.Hard, EffortSignal.Cancelled)));
    }

    [Theory]
    [InlineData("Why is the sky blue?", "why is the sky blue", true)]
    [InlineData("Why is the sky blue?", "But why is the sky blue?", true)]
    [InlineData("Why is the sky blue?", "What colour is the sea?", false)]
    [InlineData("Hi", "Hi there", false)]
    public void Asked_again_means_said_again(string first, string second, bool again) =>
        Assert.Equal(again, EffortRouter.Again(first, second));

    // ── in the one door ─────────────────────────────────────────────────

    /// <summary>A brain that records each job and answers when told.</summary>
    private sealed class RecordingBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; private set; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "Ternary-Bonsai-2-2B" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<AnsweringBrain>? AnsweringChanged { add { } remove { } }
        public readonly List<BrainJob> Jobs = [];
        public string Answer = "An answer.";
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            Started.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            yield return new BrainChunk(Answer);
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(PrefillMs: 10, PromptTokens: 5, DecodeMs: 10, OutputTokens: 3));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly ServiceLimits Quick = ServiceLimits.Default with { TextWriteInterval = TimeSpan.Zero };
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private SqliteAskStore Store(string name = "asks.db")
    {
        var store = new SqliteAskStore(Path.Combine(_dir, name));
        _owned.Add(store);
        return store;
    }

    private static async Task<AskRecord> Answered(RequestCoordinator front, string caller, string question, bool plain = false)
    {
        var admission = front.Submit(new AskSubmission(caller, Guid.NewGuid().ToString("N"), "", AskLane.Foreground, question, question, Plain: plain));
        Assert.True(admission.Accepted);
        var done = await front.WaitAsync(caller, admission.Record!.Id, Patience, CancellationToken.None);
        Assert.True(done!.IsFinal, "the ask did not finish: " + done.State);
        return done;
    }

    [Fact]
    public async Task The_door_gives_each_question_its_own_effort()
    {
        var brain = new RecordingBrain();
        await using var front = new RequestCoordinator(Store(), brain, Quick, effort: Router(FormFactor.Phone, speed: 1.47));
        front.Start();

        await Answered(front, "app:1", "What time is it in Nairobi?");
        await Answered(front, "app:1", "Why is the sky blue?");

        Assert.Equal(ReasoningEffort.Off, brain.Jobs[0].Effort);
        Assert.Equal(0, brain.Jobs[0].ThinkingBudget);
        Assert.Equal(ReasoningEffort.Medium, brain.Jobs[1].Effort);
        Assert.Equal(66, brain.Jobs[1].ThinkingBudget);
        Assert.All(brain.Jobs, j => Assert.False(j.Plain));
    }

    [Fact]
    public async Task A_spoken_question_never_thinks_before_its_first_word_however_hard_it_is()
    {
        var brain = new RecordingBrain();
        await using var front = new RequestCoordinator(Store(), brain, Quick, effort: Router(FormFactor.Phone, speed: 1.47));
        front.Start();

        var admission = front.Submit(new AskSubmission("internal:assistant", Guid.NewGuid().ToString("N"), "s", AskLane.Assistant,
            "Why is the sky blue?", "Why is the sky blue?", Spoken: true));
        Assert.True(admission.Accepted);
        await front.WaitAsync("internal:assistant", admission.Record!.Id, Patience, CancellationToken.None);

        var job = Assert.Single(brain.Jobs);
        Assert.True(job.Spoken);
        Assert.Equal(ReasoningEffort.Off, job.Effort);
        Assert.Equal(0, job.ThinkingBudget);
    }

    [Fact]
    public async Task Without_a_router_the_brain_keeps_its_own_default()
    {
        var brain = new RecordingBrain();
        await using var front = new RequestCoordinator(Store(), brain, Quick);
        front.Start();
        await Answered(front, "app:1", "Why is the sky blue?");
        Assert.Null(brain.Jobs[0].Effort);
    }

    [Fact]
    public async Task Answers_withdrawn_while_they_were_written_teach_less_thought()
    {
        var (self, _) = Self();
        var brain = new RecordingBrain();
        await using var front = new RequestCoordinator(Store(), brain, Quick, effort: Router(FormFactor.Phone, speed: 1.47, self: self));
        front.Start();

        for (var i = 0; i < 2; i++)
        {
            brain.Hold = new TaskCompletionSource();
            brain.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ask = front.Submit(new AskSubmission("app:1", null, "", AskLane.Foreground, "Why is the sky blue?", "Why is the sky blue?")).Record!;
            await brain.Started.Task.WaitAsync(Patience);
            Assert.True(front.Cancel("app:1", ask.Id));
        }
        brain.Hold = null;

        Assert.Equal(-1, self.EffortShift(TurnWeight.Hard));
        await Answered(front, "app:1", "Why does the moon change shape?");
        Assert.Equal(ReasoningEffort.Low, brain.Jobs[^1].Effort);
    }

    [Fact]
    public async Task A_question_withdrawn_before_its_turn_teaches_nothing()
    {
        var (self, _) = Self();
        var brain = new RecordingBrain { Hold = new TaskCompletionSource() };
        await using var front = new RequestCoordinator(Store(), brain, Quick, effort: Router(FormFactor.Phone, self: self));
        front.Start();

        var first = front.Submit(new AskSubmission("app:1", null, "", AskLane.Foreground, "Why is the sky blue?")).Record!;
        await brain.Started.Task.WaitAsync(Patience);
        var waiting = front.Submit(new AskSubmission("app:1", null, "", AskLane.Foreground, "Why is grass green?")).Record!;
        Assert.True(front.Cancel("app:1", waiting.Id));
        brain.Hold.SetResult();
        await front.WaitAsync("app:1", first.Id, Patience, CancellationToken.None);

        Assert.Empty(self.EffortSignals(TurnWeight.Hard));
    }

    [Fact]
    public async Task The_same_question_asked_again_soon_teaches_more_thought_and_an_instrument_teaches_nothing()
    {
        var (self, _) = Self();
        var brain = new RecordingBrain();
        await using var front = new RequestCoordinator(Store(), brain, Quick, effort: Router(FormFactor.Phone, speed: 1.47, self: self));
        front.Start();

        await Answered(front, "app:1", "What is the capital of Kenya?");
        await Answered(front, "app:1", "what is the capital of kenya");
        Assert.Equal([EffortSignal.AskedAgain], self.EffortSignals(TurnWeight.Quick));

        // The quality run asks the same questions on purpose; it is not a person.
        await Answered(front, "internal:quality", "What is the capital of Kenya?");
        await Answered(front, "internal:quality", "What is the capital of Kenya?");
        Assert.Single(self.EffortSignals(TurnWeight.Quick));

        // A different question is a new question.
        await Answered(front, "app:1", "How tall is Kilimanjaro?");
        Assert.Single(self.EffortSignals(TurnWeight.Quick));
    }

    [Fact]
    public async Task A_plain_ask_goes_to_the_model_as_written_and_is_never_answered_by_a_shortcut_or_rewritten()
    {
        var brain = new RecordingBrain { Answer = "No." };
        var greeting = new GreetingShortcut();
        await using var front = new RequestCoordinator(Store(), brain, Quick, shortcuts: [greeting],
            effort: Router(FormFactor.Desktop, speed: 40));
        front.Start();

        var turn = await Answered(front, "app:1", "hello");
        Assert.Equal("Hi!", turn.Text);                                    // a turn: the shortcut answers it

        var plain = await Answered(front, "app:1", "Translate the following text into English.\n\nhello", plain: true);
        Assert.True(plain.Plain);
        Assert.Equal("No.", plain.Text);                                   // the answer guard leaves a service's text alone
        var job = Assert.Single(brain.Jobs);
        Assert.True(job.Plain);
        Assert.Equal(ReasoningEffort.Off, job.Effort);
        Assert.Null(job.RetrievalQuery);
        Assert.Equal("Translate the following text into English.\n\nhello", Assert.Single(job.Messages).Content);
    }

    private sealed class GreetingShortcut : IAskShortcut
    {
        public string Name => "greeting";

        public ValueTask<string?> TryAnswerAsync(AskRecord ask, CancellationToken ct) =>
            ValueTask.FromResult<string?>(ask.Asked.Trim().Equals("hello", StringComparison.OrdinalIgnoreCase) ? "Hi!" : null);
    }

    // ── the plain flag on disk ──────────────────────────────────────────

    [Fact]
    public void A_plain_ask_is_still_plain_after_the_store_is_closed_and_opened()
    {
        string id;
        using (var store = new SqliteAskStore(Path.Combine(_dir, "plain.db")))
            id = store.Insert(AskRecord.Accept("p1", new AskSubmission("app:1", null, "", AskLane.Foreground, "x", Plain: true), DateTimeOffset.UtcNow)).Id;

        using var again = new SqliteAskStore(Path.Combine(_dir, "plain.db"));
        Assert.True(again.Get(id)!.Plain);
        var turn = again.Insert(AskRecord.Accept("t1", new AskSubmission("app:1", null, "", AskLane.Foreground, "y"), DateTimeOffset.UtcNow));
        Assert.False(again.Get(turn.Id)!.Plain);
    }

    [Fact]
    public void A_store_from_before_plain_asks_gains_the_column_and_keeps_its_asks()
    {
        // The phone's asks.db outlives an update: the table as it was, with one ask in it.
        var path = Path.Combine(_dir, "old.db");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"CREATE TABLE asks(
                 id TEXT PRIMARY KEY, caller_key TEXT NOT NULL, client_request_id TEXT, session_id TEXT NOT NULL,
                 lane INTEGER NOT NULL, message TEXT NOT NULL, question TEXT, spoken INTEGER NOT NULL,
                 agentic INTEGER NOT NULL, side_effect_free INTEGER NOT NULL, state INTEGER NOT NULL,
                 seq INTEGER NOT NULL, text TEXT NOT NULL, kind INTEGER, sentence TEXT, needs_permission TEXT,
                 model_id TEXT, attempts INTEGER NOT NULL, created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL,
                 started_ms INTEGER, finished_ms INTEGER,
                 t_queue INTEGER, t_wait INTEGER, t_enrich INTEGER, t_prefill INTEGER, t_ptok INTEGER,
                 t_decode INTEGER, t_otok INTEGER, t_ttft INTEGER, t_total INTEGER, t_model TEXT, t_kv TEXT, t_finish TEXT);
               INSERT INTO asks VALUES('old1','app:1',NULL,'',1,'q',NULL,0,0,1,6,1,'a',NULL,NULL,NULL,NULL,1,1,1,NULL,NULL,
                 NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new SqliteAskStore(path);
        var old = store.Get("old1");
        Assert.NotNull(old);
        Assert.False(old!.Plain);
        Assert.Equal("a", old.Text);
        Assert.True(store.Insert(AskRecord.Accept("new1", new AskSubmission("app:1", null, "", AskLane.Foreground, "z", Plain: true), DateTimeOffset.UtcNow)).Plain);
    }

    // ── the job's settings across the process boundary ──────────────────

    [Fact]
    public void A_jobs_effort_budget_and_plainness_cross_to_the_inference_process()
    {
        var job = new BrainJob("a1", [new("user", "q")], null, 80, false, DateTimeOffset.UtcNow, ReasoningEffort.Medium, 66, Plain: false);
        var map = new Dictionary<string, string>();
        WorkerWire.PutJobSettings(map, job);
        Assert.Equal(((ReasoningEffort?)ReasoningEffort.Medium, 66, false), WorkerWire.GetJobSettings(map));

        var plain = new Dictionary<string, string>();
        WorkerWire.PutJobSettings(plain, job with { Effort = null, ThinkingBudget = 0, Plain = true });
        Assert.Equal(((ReasoningEffort?)null, 0, true), WorkerWire.GetJobSettings(plain));

        // An older worker's job carries none of it, and reads as it always did.
        Assert.Equal(((ReasoningEffort?)null, 0, false), WorkerWire.GetJobSettings(new Dictionary<string, string>()));

        // A READ AHEAD SAYS SO ACROSS THE WIRE: a worker that lost the flag would answer half a question.
        var reading = new Dictionary<string, string>();
        WorkerWire.PutJobSettings(reading, job with { JustRead = true });
        Assert.True(WorkerWire.Flag(reading, WorkerWire.JustRead));
        Assert.False(WorkerWire.Flag(map, WorkerWire.JustRead));
    }
}
