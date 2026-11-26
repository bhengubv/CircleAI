// HeadStartYieldsToAnAnswerTests.cs
//
// THE P30 CONVERSATION, 2026-11-17: every turn but the first was told the brain was busy. The brain reads ahead on the
// conversation while it waits - a head start that holds its one slot - and a brain read from flash cannot stop within
// the second the worker waits before it gives up and rejects. So the real answer that supersedes the head start was
// refused, and only a holding line ("Let me think.") ever played - "brain ?" the one tell in the turn's line.
//
// The coordinator drains the head start itself now, and waits for it to let go, before it asks the brain the real
// question. This proves it: a worker with one slot that refuses a second answer, a head start that will not stop on
// its own, and a question that still gets answered.

using System.Runtime.CompilerServices;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class HeadStartYieldsToAnAnswerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circleai-head-start-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>A brain with one slot, as the worker guards it: a second answer asked while one holds the slot is told
    /// the brain is busy. A head start (JustRead) holds the slot until it is told to let go, and will not stop sooner.</summary>
    private sealed class OneSlot : IBrainWorker
    {
        private int _busy;   // the one slot: 0 free, 1 taken
        public readonly SemaphoreSlim HeadStartHoldsTheBrain = new(0, 1);

        public BrainWorkerStatus Status { get; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "slot" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<AnsweringBrain>? AnsweringChanged { add { } remove { } }
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new BrainWorkerException(new Failure(
                    FailureKind.Rejected, Certainty.Certain, WorkPhase.Prefill, "slot",
                    "busy", "an answer is already being written", DateTimeOffset.UtcNow));
            try
            {
                if (job.JustRead)
                {
                    HeadStartHoldsTheBrain.Release();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* told to let go */ }
                }
                else
                {
                    yield return new BrainChunk("Mount Everest.");
                    yield return new BrainChunk(string.Empty, Final: true,
                        Timing: new TurnTiming(PrefillMs: 1, PromptTokens: 1, DecodeMs: 1, OutputTokens: 2, ModelId: "slot"));
                }
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_head_start_lets_the_brain_go_so_the_question_is_not_told_the_brain_is_busy()
    {
        using var asks = new SqliteAskStore(Path.Combine(_dir, "head-start.db"));
        var worker = new OneSlot();
        await using var front = new RequestCoordinator(asks, worker, ServiceLimits.Default);
        front.Start();

        // The brain reads ahead on the conversation so far: it takes the one slot and will not give it up on its own.
        front.ReadAhead(new AskSubmission("internal:assistant", "read-ahead", "assistant", AskLane.Foreground, "The tallest mountain"));
        Assert.True(await worker.HeadStartHoldsTheBrain.WaitAsync(TimeSpan.FromSeconds(5)), "the head start never reached the brain");

        // Now somebody asks. The answer must take the brain back, not be told it is busy.
        var id = front.Submit(new AskSubmission("internal:adb", "question", "adb", AskLane.Foreground, "What is the tallest mountain?")).Record!.Id;
        var done = await front.WaitAsync("internal:adb", id, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(done is { IsFinal: true }, "the ask did not finish: " + done?.State);
        Assert.Equal(AskState.Succeeded, done!.State);
        Assert.Equal("Mount Everest.", done.Text);
    }
}
