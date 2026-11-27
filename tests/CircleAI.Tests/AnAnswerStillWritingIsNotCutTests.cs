// AnAnswerStillWritingIsNotCutTests.cs
//
// THE 27B ON THE P30, 2026-11-09: two minutes was the most one answer could take, and the 27B streamed from flash
// writes a word every 30 s or so - so every answer it began was cut after its second or third word, and an answer
// cut before its first whole sentence is no answer at all. The deadline is a word's now: two minutes to the first
// word, and two minutes from each word to the next. An answer that keeps writing is not cut; one that stops is.
//
// THE DEADLINE IS DRIVEN, NOT TIMED. RequestCoordinator arms the word's deadline on its injected TimeProvider, not
// on CancellationTokenSource.CancelAfter, so this test moves a hand clock and the deadline falls only when the test
// says - never on how busy the box is. The earlier wall-clock version proved the same thing by letting the whole
// answer outlast a two-second deadline with a word every 400 ms, and under a full test run the box stretched one of
// those gaps past two seconds and cut a still-writing answer. A clock moved by hand cannot be stretched.

using System.Runtime.CompilerServices;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class AnAnswerStillWritingIsNotCutTests : IDisposable
{
    private static readonly string[] Words = ["Rivers ", "carry ", "salt ", "into ", "the ", "sea, ", "and ", "it ", "stays."];
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-word-deadline-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task An_answer_that_keeps_writing_outlasts_the_deadline_and_one_that_stops_writing_does_not()
    {
        var deadline = TimeSpan.FromSeconds(2);
        var gap = TimeSpan.FromSeconds(1);   // half the deadline: no single silence between two words ever trips it
        var limits = ServiceLimits.Default with { TextWriteInterval = TimeSpan.FromHours(1), GenerationDeadline = deadline };

        // KEEPS WRITING: a word every half-deadline. The whole answer runs far longer than one deadline, yet is never
        // cut, because each word gives the answer as long again.
        {
            var clock = new HandClock();
            var writer = new SteppedWriter();
            using var asks = new SqliteAskStore(Path.Combine(_dir, "writing.db"));
            await using var front = new RequestCoordinator(asks, writer, limits, clock: clock);
            front.Start();
            var id = Submit(front, "writing");
            for (var i = 0; i < Words.Length; i++)
            {
                await writer.Parked(i);   // the writer is about to wait for word i - the word before it has re-armed the deadline
                clock.Advance(gap);        // a silence shorter than the deadline
                writer.Produce(i);
            }
            var done = await Final(front, id);
            Assert.Equal(AskState.Succeeded, done.State);
            Assert.Equal("Rivers carry salt into the sea, and it stays.", done.Text);
            Assert.NotEqual("deadline", done.Timing?.Finish);
        }

        // STOPS WRITING: two words, then silence. One whole deadline passes with nothing written, and the answer is cut.
        {
            var clock = new HandClock();
            var writer = new SteppedWriter(stopAfter: 2);
            using var asks = new SqliteAskStore(Path.Combine(_dir, "stopped.db"));
            await using var front = new RequestCoordinator(asks, writer, limits, clock: clock);
            front.Start();
            var id = Submit(front, "stopped");
            await writer.Parked(0); clock.Advance(gap); writer.Produce(0);
            await writer.Parked(1); clock.Advance(gap); writer.Produce(1);
            await writer.Parked(2);        // the writer has stopped for good
            clock.Advance(deadline);        // a silence as long as the deadline
            var cut = await Final(front, id);
            Assert.True(cut.State == AskState.Failed ? cut.Kind == FailureKind.TimedOut : cut.Timing?.Finish == "deadline",
                $"ended {cut.State}, {cut.Kind}, {cut.Timing?.Finish}");
            Assert.DoesNotContain("stays", cut.Text ?? string.Empty);
        }
    }

    private static string Submit(RequestCoordinator front, string requestId) =>
        front.Submit(new AskSubmission("internal:adb", requestId, "adb", AskLane.Foreground, "Why is the sea salty?")).Record!.Id;

    private static async Task<AskRecord> Final(RequestCoordinator front, string id)
    {
        var done = await front.WaitAsync("internal:adb", id, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(done!.IsFinal, "the ask did not finish: " + done.State);
        return done;
    }

    /// <summary>A brain that writes a word only when the test lets it, and parks before each one so the test can move the clock.</summary>
    private sealed class SteppedWriter(int stopAfter = int.MaxValue) : IBrainWorker
    {
        private readonly TaskCompletionSource[] _go =
            [.. Enumerable.Range(0, Words.Length).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        private readonly TaskCompletionSource[] _parked =
            [.. Enumerable.Range(0, Words.Length).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];

        public Task Parked(int i) => _parked[i].Task;       // completes when the writer is about to wait for word i
        public void Produce(int i) => _go[i].TrySetResult(); // lets word i out

        public BrainWorkerStatus Status { get; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "slow" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<AnsweringBrain>? AnsweringChanged { add { } remove { } }
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < Words.Length; i++)
            {
                _parked[i].TrySetResult();
                if (i >= stopAfter) await Task.Delay(Timeout.Infinite, ct);   // stops for good: only the deadline ends it
                await _go[i].Task.WaitAsync(ct);
                yield return new BrainChunk(Words[i]);
            }
            yield return new BrainChunk(string.Empty, Final: true,
                Timing: new TurnTiming(PrefillMs: 1, PromptTokens: 1, DecodeMs: 1, OutputTokens: Words.Length, ModelId: "slow"));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A clock moved by hand whose timers fall when it is advanced, so a deadline on TimeProvider is driven, not timed.</summary>
    private sealed class HandClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 11, 9, 4, 6, 57, TimeSpan.Zero);
        private readonly List<HandTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }

        public void Advance(TimeSpan by)
        {
            HandTimer[] due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due is { } d && d <= _now)];
                foreach (var t in due) t.Due = null;   // one-shot; re-armed by Change
            }
            foreach (var t in due) t.Fire();            // outside the lock: the callback may dispose its own timer
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new HandTimer(this, callback, state);
            lock (_gate) _timers.Add(t);
            t.Change(dueTime, period);
            return t;
        }

        private sealed class HandTimer(HandClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
