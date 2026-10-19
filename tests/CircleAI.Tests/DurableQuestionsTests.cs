// DurableQuestionsTests.cs
//
// THE FIVE-STAGE UPGRADE'S PORTABLE HALF, PINNED: questions are saved before they are
// accepted, a retry is the same question, a question asked while the model loads waits
// in line, a crash puts a safe question back in line, failures are classified so that
// only a crash counts against the model, the wire rows round-trip, the answers that
// must not come from the model do not, and the generator never hands MNN a template
// inside its template.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Quality;
using CircleAI.Assistant.Requests;
using CircleAI.Inference;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class DurableQuestionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "circleai-asks-" + Guid.NewGuid().ToString("N"));

    public DurableQuestionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SqliteAskStore Store() => new(Path.Combine(_dir, "asks.db"));

    private static AskSubmission Ask(string message, string caller = "app:1", string? rid = null,
                                     AskLane lane = AskLane.Foreground, string session = "") =>
        new(caller, rid, session, lane, message, message);

    // ── a scripted brain ─────────────────────────────────────────────────────

    private sealed class FakeBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; private set; } = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow);
        public event EventHandler<BrainWorkerStatus>? StatusChanged;
        public TaskCompletionSource? Loading;
        public Func<BrainJob, int, Exception?>? FailOnAttempt;
        public readonly List<BrainJob> Jobs = new();
        public string Answer = "Nairobi.";
        public CancellationToken LastToken;
        public TaskCompletionSource? HoldAnswer;

        public async Task EnsureReadyAsync(CancellationToken ct)
        {
            if (Status.Phase == BrainPhase.Ready) return;
            if (Loading is { } loading) await loading.Task.WaitAsync(ct);
            Set(BrainPhase.Ready);
        }

        public void Set(BrainPhase phase)
        {
            Status = Status with { Phase = phase, ModelId = "Qwen3.5-0.8B-MNN" };
            StatusChanged?.Invoke(this, Status);
        }

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            Jobs.Add(job);
            LastToken = ct;
            if (FailOnAttempt?.Invoke(job, Jobs.Count) is { } failure) throw failure;
            if (HoldAnswer is { } hold) await hold.Task.WaitAsync(ct);
            foreach (var word in Answer.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return new BrainChunk(word + " ");
                await Task.Yield();
            }
            yield return new BrainChunk(string.Empty, Final: true,
                Timing: new TurnTiming(PrefillMs: 100, PromptTokens: 40, DecodeMs: 50, OutputTokens: 2, ModelId: "Qwen3.5-0.8B-MNN"));
        }

        public Task ReleaseAsync(CancellationToken ct) { Set(BrainPhase.Installed); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Failure Died(FailureKind kind) =>
        new(kind, Certainty.Inferred, WorkPhase.Decode, "Qwen3.5-0.8B-MNN", "test", "test", DateTimeOffset.UtcNow);

    private static ServiceLimits Quick => ServiceLimits.Default with
    {
        TextWriteInterval = TimeSpan.Zero, LoadWaitBudget = TimeSpan.FromSeconds(10),
    };

    // THIRTY SECONDS, NOT TEN. Every ask here is answered in milliseconds by a fake brain,
    // but the store is SQLite with synchronous=FULL, and on 2026-10-17 the Windows box's
    // disk sat at 0% idle for hours: three of these waits ran out ("did not finish:
    // Running", "...: Queued") and the same tests passed 46 of 46 the moment it was quiet.
    // A wait that cannot tell a slow disk from a hang is not testing the queue.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static async Task<AskRecord> Final(RequestCoordinator front, string caller, string id)
    {
        var done = await front.WaitAsync(caller, id, Patience, CancellationToken.None);
        Assert.NotNull(done);
        Assert.True(done!.IsFinal, "the ask did not finish: " + done.State);
        return done;
    }

    // ── the store ────────────────────────────────────────────────────────────

    [Fact]
    public void An_accepted_question_survives_the_store_closing_and_seq_keeps_rising()
    {
        string id;
        long seq;
        using (var store = Store())
        {
            var saved = store.Insert(AskRecord.Accept("a1", Ask("What is the capital of Kenya?", rid: "r1"), DateTimeOffset.UtcNow));
            id = saved.Id;
            seq = saved.Seq;
        }

        using var again = Store();
        var back = again.Get(id);
        Assert.NotNull(back);
        Assert.Equal("What is the capital of Kenya?", back!.Message);
        Assert.Equal(AskState.Queued, back.State);
        var next = again.Insert(AskRecord.Accept("a2", Ask("again"), DateTimeOffset.UtcNow));
        Assert.True(next.Seq > seq);
    }

    [Fact]
    public void The_same_request_id_from_the_same_caller_is_the_same_ask()
    {
        using var store = Store();
        var first = store.Insert(AskRecord.Accept("a1", Ask("q", rid: "same"), DateTimeOffset.UtcNow));
        var second = store.Insert(AskRecord.Accept("a2", Ask("q", rid: "same"), DateTimeOffset.UtcNow));
        var other = store.Insert(AskRecord.Accept("a3", Ask("q", caller: "app:2", rid: "same"), DateTimeOffset.UtcNow));

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, other.Id);
    }

    [Fact]
    public void A_final_ask_is_never_changed_back()
    {
        using var store = Store();
        var a = store.Insert(AskRecord.Accept("a1", Ask("q"), DateTimeOffset.UtcNow));
        store.Update(a.Id, r => r with { State = AskState.Succeeded, Text = "done" });
        var after = store.Update(a.Id, r => r with { State = AskState.Running });
        Assert.Equal(AskState.Succeeded, after!.State);
        Assert.Equal("done", after.Text);
    }

    // ── the coordinator ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_question_is_stored_before_submit_returns_and_answered()
    {
        using var store = Store();
        var brain = new FakeBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);

        var admission = front.Submit(Ask("What is the capital of Kenya?"));
        Assert.True(admission.Accepted);
        Assert.NotNull(store.Get(admission.Record!.Id));   // on disk before anything ran

        front.Start();
        var done = await Final(front, "app:1", admission.Record.Id);
        Assert.Equal(AskState.Succeeded, done.State);
        Assert.Contains("Nairobi", done.Text);
        Assert.NotNull(done.Timing);
        Assert.Equal(40, done.Timing!.PromptTokens);
    }

    [Fact]
    public async Task A_retry_with_the_same_request_id_does_no_second_generation()
    {
        using var store = Store();
        var brain = new FakeBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var first = front.Submit(Ask("q", rid: "once"));
        await Final(front, "app:1", first.Record!.Id);
        var again = front.Submit(Ask("q", rid: "once"));

        Assert.True(again.Duplicate);
        Assert.Equal(first.Record.Id, again.Record!.Id);
        Assert.Single(brain.Jobs);
    }

    [Fact]
    public async Task A_question_asked_while_the_model_loads_waits_in_line_then_is_answered()
    {
        using var store = Store();
        var brain = new FakeBrain { Loading = new TaskCompletionSource() };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var ask = front.Submit(Ask("q")).Record!;
        var waiting = await front.WaitForChangeAsync("app:1", ask.Id, ask.Seq, Patience, CancellationToken.None);
        Assert.Equal(AskState.WaitingForModel, waiting!.State);

        brain.Loading.SetResult();
        var done = await Final(front, "app:1", ask.Id);
        Assert.Equal(AskState.Succeeded, done.State);
    }

    [Fact]
    public async Task A_crash_mid_answer_puts_a_safe_question_back_in_line_and_it_is_answered()
    {
        using var store = Store();
        var brain = new FakeBrain
        {
            FailOnAttempt = (_, n) => n == 1 ? new BrainWorkerException(Died(FailureKind.NativeWorkerCrashed)) : null,
        };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var done = await Final(front, "app:1", front.Submit(Ask("q")).Record!.Id);
        Assert.Equal(AskState.Succeeded, done.State);
        Assert.Equal(2, done.Attempts);
    }

    [Fact]
    public async Task A_fault_injected_mid_answer_is_put_back_in_line_and_answered()
    {
        // THE SOAK'S FAULT, 2026-10-17 HOUR 8: classified as the watchdog's TimedOut, it was
        // never retried and the question it interrupted was failed. A test kill is retried.
        using var store = Store();
        var brain = new FakeBrain
        {
            FailOnAttempt = (_, n) => n == 1 ? new BrainWorkerException(Died(FailureKind.FaultInjected)) : null,
        };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var done = await Final(front, "app:1", front.Submit(Ask("q")).Record!.Id);
        Assert.Equal(AskState.Succeeded, done.State);
        Assert.Equal(2, done.Attempts);
    }

    [Fact]
    public void A_test_kill_is_retried_and_never_held_against_the_model_a_watchdog_kill_is_a_timeout()
    {
        var at = DateTimeOffset.UtcNow;
        var test = FailureClassifier.Classify(new ExitFacts(29, WorkPhase.Decode, "Qwen3.5-0.8B-MNN", at, KilledForTest: true));
        Assert.Equal(FailureKind.FaultInjected, test.Kind);
        Assert.True(test.Retryable);
        Assert.False(test.CountsAgainstModel);

        var watchdog = FailureClassifier.Classify(new ExitFacts(29, WorkPhase.Decode, "Qwen3.5-0.8B-MNN", at, KilledByUs: true));
        Assert.Equal(FailureKind.TimedOut, watchdog.Kind);
        Assert.False(watchdog.Retryable);
    }

    [Fact]
    public async Task A_question_that_keeps_crashing_the_engine_fails_with_its_kind()
    {
        using var store = Store();
        var brain = new FakeBrain { FailOnAttempt = (_, _) => new BrainWorkerException(Died(FailureKind.NativeWorkerCrashed)) };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var done = await Final(front, "app:1", front.Submit(Ask("q")).Record!.Id);
        Assert.Equal(AskState.Failed, done.State);
        Assert.Equal(FailureKind.NativeWorkerCrashed, done.Kind);
        Assert.Equal(AskSentences.WorkerCrashed, done.Sentence);
    }

    [Fact]
    public async Task An_interrupted_question_is_picked_up_by_the_next_process()
    {
        using var store = Store();
        var interrupted = store.Insert(AskRecord.Accept("dead", Ask("q"), DateTimeOffset.UtcNow));
        store.Update(interrupted.Id, r => r with { State = AskState.Running, Attempts = 1 });

        var brain = new FakeBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var done = await Final(front, "app:1", interrupted.Id);
        Assert.Equal(AskState.Succeeded, done.State);
    }

    [Fact]
    public async Task Something_that_changes_the_world_is_not_run_again_after_a_crash()
    {
        using var store = Store();
        var risky = store.Insert(AskRecord.Accept("risky", Ask("clear it") with { SideEffectFree = false }, DateTimeOffset.UtcNow));
        store.Update(risky.Id, r => r with { State = AskState.Running, Attempts = 1 });

        var brain = new FakeBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var done = await Final(front, "app:1", risky.Id);
        Assert.Equal(AskState.Failed, done.State);
        Assert.Empty(brain.Jobs);
    }

    [Fact]
    public async Task One_app_cannot_see_or_cancel_another_apps_question()
    {
        using var store = Store();
        var brain = new FakeBrain { HoldAnswer = new TaskCompletionSource() };
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var mine = front.Submit(Ask("q", caller: "app:1")).Record!;
        Assert.Null(front.Status("app:2", mine.Id));
        Assert.False(front.Cancel("app:2", mine.Id));
        Assert.True(front.Cancel("app:1", mine.Id));
        var done = await Final(front, "app:1", mine.Id);
        Assert.Equal(AskState.Cancelled, done.State);
    }

    [Fact]
    public async Task A_follow_up_carries_its_own_conversation_and_never_another_callers()
    {
        using var store = Store();
        var brain = new FakeBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        await Final(front, "app:1", front.Submit(Ask("What is the capital of Kenya?", "app:1", session: "s")).Record!.Id);
        await Final(front, "app:2", front.Submit(Ask("secret question", "app:2", session: "s")).Record!.Id);
        await Final(front, "app:1", front.Submit(Ask("And its population?", "app:1", session: "s")).Record!.Id);

        var followUp = brain.Jobs.Last();
        Assert.Equal(3, followUp.Messages.Count);
        Assert.Equal("What is the capital of Kenya?", followUp.Messages[0].Content);
        Assert.DoesNotContain(followUp.Messages, m => m.Content.Contains("secret"));
    }

    [Fact]
    public async Task A_spoken_question_goes_first_and_gets_a_short_answer_budget()
    {
        using var store = Store();
        var brain = new FakeBrain { Loading = new TaskCompletionSource() };
        await using var front = new RequestCoordinator(store, brain, Quick);
        var typed = front.Submit(Ask("typed", "app:1")).Record!;
        var spoken = front.Submit(new AskSubmission("internal:assistant", null, "", AskLane.Assistant, "spoken", "spoken", Spoken: true)).Record!;
        front.Start();
        brain.Loading.SetResult();

        await Final(front, "app:1", typed.Id);
        await Final(front, "internal:assistant", spoken.Id);
        Assert.Equal("spoken", brain.Jobs[0].Messages.Last().Content);
        Assert.Equal(ServiceLimits.Default.MaxTokensSpoken, brain.Jobs[0].MaxTokens);
        Assert.Equal(ServiceLimits.Default.MaxTokensTyped, brain.Jobs[1].MaxTokens);
    }

    [Fact]
    public async Task No_model_on_the_phone_waits_for_setup_rather_than_failing()
    {
        using var store = Store();
        var brain = new NoModelBrain();
        await using var front = new RequestCoordinator(store, brain, Quick);
        front.Start();

        var ask = front.Submit(Ask("q")).Record!;
        AskRecord? now = ask;
        for (var i = 0; i < 50 && now!.State != AskState.WaitingForSetup; i++)
            now = await front.WaitForChangeAsync("app:1", ask.Id, now.Seq, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(AskState.WaitingForSetup, now!.State);
        Assert.Equal(FailureKind.NotInstalled, now.Kind);
    }

    private sealed class NoModelBrain : IBrainWorker
    {
        public BrainWorkerStatus Status { get; } = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.NoModel };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public Task EnsureReadyAsync(CancellationToken ct) => Task.FromException(new BrainWorkerException(
            new Failure(FailureKind.NotInstalled, Certainty.Certain, WorkPhase.Load, null, "none", "none", DateTimeOffset.UtcNow)));
        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── failure classification ───────────────────────────────────────────────

    [Theory]
    [InlineData(3, null, FailureKind.OutOfMemory)]
    [InlineData(5, null, FailureKind.NativeWorkerCrashed)]
    [InlineData(10, null, FailureKind.AndroidStopped)]
    [InlineData(16, null, FailureKind.AndroidStopped)]
    [InlineData(6, null, FailureKind.AndroidTimeout)]
    [InlineData(2, 11, FailureKind.NativeWorkerCrashed)]
    public void Android_exit_reasons_are_read_as_kinds(int reason, int? signal, FailureKind expected)
    {
        var f = FailureClassifier.Classify(new ExitFacts(34, WorkPhase.Load, "m", DateTimeOffset.UtcNow,
                                                         ExitReason: reason, ExitStatus: signal));
        Assert.Equal(expected, f.Kind);
    }

    [Fact]
    public void On_api_29_a_worker_dying_alone_with_no_memory_left_is_memory_not_the_model()
    {
        var f = FailureClassifier.Classify(new ExitFacts(29, WorkPhase.Load, "m", DateTimeOffset.UtcNow,
                                                         WorkerDiedAlone: true, MemAvailableFractionAtDeath: 0.04));
        Assert.Equal(FailureKind.OutOfMemory, f.Kind);
        Assert.False(f.CountsAgainstModel);
    }

    [Fact]
    public void An_update_or_a_reboot_during_a_load_is_never_held_against_the_model()
    {
        foreach (var facts in new[]
                 {
                     new ExitFacts(29, WorkPhase.Load, "m", DateTimeOffset.UtcNow, PackageUpdated: true),
                     new ExitFacts(29, WorkPhase.Load, "m", DateTimeOffset.UtcNow, BootChanged: true),
                 })
        {
            var f = FailureClassifier.Classify(facts);
            Assert.Equal(FailureKind.AndroidStopped, f.Kind);
            Assert.False(f.CountsAgainstModel);
        }
    }

    [Theory]
    [InlineData(FailureKind.AndroidStopped)]
    [InlineData(FailureKind.AndroidTimeout)]
    [InlineData(FailureKind.OutOfMemory)]
    [InlineData(FailureKind.ModelFilesDamaged)]
    [InlineData(FailureKind.PermissionMissing)]
    [InlineData(FailureKind.TimedOut)]
    public void Only_a_crash_counts_against_a_model(FailureKind kind)
    {
        foreach (var phase in Enum.GetValues<WorkPhase>())
            Assert.False(FailurePolicy.CountsAgainstModel(kind, phase));
        Assert.True(FailurePolicy.CountsAgainstModel(FailureKind.NativeWorkerCrashed, WorkPhase.Load));
    }

    // ── readiness ────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_seq_moves_only_on_a_real_change_and_survives_a_restart()
    {
        var path = Path.Combine(_dir, "readiness.json");
        var tracker = new ReadinessTracker(path);
        var start = tracker.Seq;
        var ready = ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, "Ready to answer.", DateTimeOffset.UtcNow, "m");
        tracker.Report(ready);
        var once = tracker.Seq;
        tracker.Report(ready with { Since = DateTimeOffset.UtcNow.AddSeconds(5) });
        Assert.Equal(once, tracker.Seq);
        Assert.True(once > start);

        tracker.Failed(new Failure(FailureKind.OutOfMemory, Certainty.Inferred, WorkPhase.Load, "m", "e", "d", DateTimeOffset.UtcNow));
        var next = new ReadinessTracker(path);
        Assert.NotNull(next.Previous);
        Assert.Equal(FailureKind.OutOfMemory, next.Current.RecentFailures[0].Kind);
        Assert.True(next.Seq > once);
    }

    // ── the wire ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_ask_row_round_trips_with_its_timing_and_text_offset()
    {
        var ask = AskRecord.Accept("id1", Ask("q"), DateTimeOffset.UtcNow) with
        {
            State = AskState.Running, Seq = 7, Text = "The capital is Nairobi.",
            Timing = new TurnTiming(QueueMs: 5, PrefillMs: 900, PromptTokens: 120, DecodeMs: 300, OutputTokens: 9, TotalMs: 1300),
        };
        var view = LinkAskRows.ReadAsk(LinkAskRows.Ask(ask, position: 0, textFrom: 4));
        Assert.NotNull(view);
        Assert.Equal(AskState.Running, view!.State);
        Assert.Equal("capital is Nairobi.", view.Text);
        Assert.Equal(4, view.TextOffset);
        Assert.Equal(ask.Text.Length, view.TextLength);
        Assert.Equal(120, view.Timing!.PromptTokens);
    }

    [Fact]
    public void A_short_row_from_an_older_service_reads_without_throwing()
    {
        var view = LinkAskRows.ReadAsk(new[] { "id1", "SomeStateFromTheFuture" });
        Assert.NotNull(view);
        Assert.Null(view!.State);
        Assert.False(view.IsFinal);
        Assert.Null(view.Timing);
    }

    [Fact]
    public void Status_rows_round_trip_and_unchanged_is_one_cheap_row()
    {
        var tracker = new ReadinessTracker();
        tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Loading, "Getting its brain ready.", DateTimeOffset.UtcNow, "m"));
        tracker.Setting(ReadinessSettings.MaxTokensSpoken, "160");
        var (unchanged, _, snapshot) = LinkAskRows.ReadStatus(LinkAskRows.Status(tracker.Current));
        Assert.False(unchanged);
        Assert.Equal(BrainPhase.Loading, snapshot!.Brain.Phase);
        Assert.Contains(snapshot.Settings, s => s.Key == ReadinessSettings.MaxTokensSpoken && s.Value == "160");
        Assert.True(LinkAskRows.ReadStatus(new[] { LinkAskRows.Unchanged(9) }).Unchanged);
    }

    [Fact]
    public void Verb_arguments_travel_as_extra_keys_an_older_side_ignores()
    {
        var req = new LinkVerbRequest(LinkVerb.AskSubmit, Args: new Dictionary<string, string> { ["rid"] = "r1", ["message"] = "hi" });
        var map = LinkVerbCodec.Encode(req);
        Assert.Equal("r1", map["a.rid"]);
        var back = LinkVerbCodec.TryDecodeRequest(map);
        Assert.Equal("hi", back!.Arg("message"));
        Assert.Null(LinkVerbCodec.TryDecodeRequest(LinkVerbCodec.Encode(new LinkVerbRequest(LinkVerb.Recall)))!.Args);
    }

    [Fact]
    public void Worker_messages_round_trip()
    {
        var map = new Dictionary<string, string>();
        WorkerWire.PutMessages(map, new[] { new ChatMessage("user", "a\nb"), new ChatMessage("assistant", "c") });
        WorkerWire.PutFailure(map, Died(FailureKind.OutOfMemory));
        WorkerWire.PutTiming(map, new TurnTiming(PrefillMs: 10, PromptTokens: 3, DecodeMs: 4, OutputTokens: 2));
        var messages = WorkerWire.GetMessages(map);
        Assert.Equal("a\nb", messages[0].Content);
        Assert.Equal(FailureKind.OutOfMemory, WorkerWire.GetFailure(map)!.Kind);
        Assert.Equal(3, WorkerWire.GetTiming(map)!.PromptTokens);
    }

    // ── answers that do not come from the model ──────────────────────────────

    private static AskRecord Asked(string q) => AskRecord.Accept("x", Ask(q), DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_date_comes_from_the_clock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 16, 21, 41, 0, TimeSpan.Zero));
        var said = await new DeviceClockShortcut(clock, () => TimeZoneInfo.Utc).TryAnswerAsync(Asked("Hey, B. What date is it today?"), default);
        Assert.Equal("It's Friday, 16 October 2026.", said);
    }

    [Fact]
    public async Task The_time_somewhere_named_comes_from_its_time_zone()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 16, 12, 41, 0, TimeSpan.Zero));
        var said = await new DeviceClockShortcut(clock).TryAnswerAsync(Asked("What time is it in Tokushima Japan?"), default);
        Assert.NotNull(said);
        Assert.Contains("9:41", said);   // Japan is UTC+9
        Assert.Contains("Japan", said);
    }

    [Theory]
    [InlineData("What is the temperature in Tokushima Japan today?", true)]
    [InlineData("what's the weather like", true)]
    [InlineData("What is the boiling temperature of water?", false)]
    public async Task Live_weather_is_never_invented_and_science_is_not_mistaken_for_it(string q, bool captured)
    {
        var said = await new NoLiveDataShortcut().TryAnswerAsync(Asked(q), default);
        Assert.Equal(captured, said is not null);
    }

    [Theory]
    [InlineData("(Bell)", true)]
    [InlineData("[Music]", true)]
    [InlineData("Placeculeeai.", false)]
    public async Task A_sound_is_not_a_question(string heard, bool captured)
    {
        var said = await new NonSpeechShortcut().TryAnswerAsync(Asked(heard), default);
        Assert.Equal(captured, said is not null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── the quality gate ─────────────────────────────────────────────────────

    [Fact]
    public void The_recorded_questions_catch_what_went_wrong()
    {
        var q = RecordedQuestions.Load().ToDictionary(r => r.Id);
        Assert.False(AnswerExpectation.Check(q["weather-tokushima"], "It is twenty five degrees in Tokushima.").Passed);
        Assert.True(AnswerExpectation.Check(q["weather-tokushima"], "I can't see live weather from this phone.").Passed);
        Assert.False(AnswerExpectation.Check(q["capital-ke"], "IT! > Nairobi.").Passed);
        Assert.False(AnswerExpectation.Check(q["capital-za"], "Thinking Process: 1. Analyze").Passed);
        Assert.True(AnswerExpectation.Check(q["capital-za"], "Pretoria is the administrative capital.").Passed);
        Assert.False(AnswerExpectation.Check(q["can-do"], "I'm Qwen3.5, the latest version of Alibaba's model.").Passed);
    }

    // ── one template owner ───────────────────────────────────────────────────

    [Fact]
    public void Mnn_is_handed_the_users_content_and_never_a_chat_template()
    {
        var content = QwenTextGenerator.MnnContent(new[]
        {
            new ChatMessage("system", "You are B!."),
            new ChatMessage("user", "What is the capital of Kenya?"),
            new ChatMessage("assistant", "Nairobi."),
            new ChatMessage("user", "And its population?"),
        }, loadPersona: "You are B!.");

        Assert.DoesNotContain("<|im_", content);
        Assert.DoesNotContain("You are B!.", content);          // MNN holds the persona
        Assert.Contains("User: What is the capital of Kenya?", content);
        Assert.EndsWith("And its population?", content);
    }

    [Fact]
    public void A_persona_mnn_does_not_hold_is_still_read_as_plain_words()
    {
        var content = QwenTextGenerator.MnnContent(
            new[] { new ChatMessage("system", "Answer in French."), new ChatMessage("user", "Hi") }, loadPersona: null);
        Assert.StartsWith("Answer in French.", content);
        Assert.DoesNotContain("<|im_", content);
    }

    [Fact]
    public void The_persona_is_escaped_for_mnns_config()
    {
        var json = QwenTextGenerator.JsonEscape("Say \"hi\"\nthen stop");
        Assert.Equal("\"Say \\u0022hi\\u0022\\nthen stop\"", json);
    }
}
