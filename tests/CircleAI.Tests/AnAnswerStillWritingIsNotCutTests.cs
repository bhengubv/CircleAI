// AnAnswerStillWritingIsNotCutTests.cs
//
// THE 27B ON THE P30, 2026-11-09: two minutes was the most one answer could take, and the 27B streamed from flash
// writes a word every 30 s or so - so every answer it began was cut after its second or third word, and an answer
// cut before its first whole sentence is no answer at all. The deadline is a word's now: two minutes to the first
// word, and two minutes from each word to the next. An answer that keeps writing is not cut; one that stops is.

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

    /// <summary>A brain that writes its answer a word at a time, and stops for good after <paramref name="stopAfter"/> words.</summary>
    private sealed class SlowWriter(TimeSpan between, int stopAfter = int.MaxValue) : IBrainWorker
    {
        public BrainWorkerStatus Status { get; } =
            BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow) with { Phase = BrainPhase.Ready, ModelId = "slow" };
        public event EventHandler<BrainWorkerStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<AnsweringBrain>? AnsweringChanged { add { } remove { } }

        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < Words.Length; i++)
            {
                await Task.Delay(i >= stopAfter ? Timeout.InfiniteTimeSpan : between, ct);
                yield return new BrainChunk(Words[i]);
            }
            yield return new BrainChunk(string.Empty, Final: true,
                Timing: new TurnTiming(PrefillMs: 1, PromptTokens: 1, DecodeMs: 1, OutputTokens: Words.Length, ModelId: "slow"));
        }

        public Task ReleaseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_answer_that_keeps_writing_outlasts_the_deadline_and_one_that_stops_writing_does_not()
    {
        // THE DEADLINE, NOT THE DISK: no text is written through while the answer runs, so no store write sits between two
        // words. Written after every word, one write on this PC's hard drive under a full test run outlasted the deadline,
        // twice after the fourth word, and cut the answer there. Two seconds a word, less than the whole answer takes.
        var limits = ServiceLimits.Default with { TextWriteInterval = TimeSpan.FromHours(1), GenerationDeadline = TimeSpan.FromSeconds(2) };

        // Nine words 400 ms apart: 3.6 s in all, longer than the deadline, never 2 s without a word.
        using (var asks = new SqliteAskStore(Path.Combine(_dir, "writing.db")))
        await using (var front = new RequestCoordinator(asks, new SlowWriter(TimeSpan.FromMilliseconds(400)), limits))
        {
            front.Start();
            var done = await Final(front, Submit(front, "writing"));
            Assert.Equal(AskState.Succeeded, done.State);
            Assert.Equal("Rivers carry salt into the sea, and it stays.", done.Text);
            Assert.NotEqual("deadline", done.Timing?.Finish);
        }

        // Two words, then nothing: ended 2 s after the second, the rest never written.
        using (var asks = new SqliteAskStore(Path.Combine(_dir, "stopped.db")))
        await using (var front = new RequestCoordinator(asks, new SlowWriter(TimeSpan.FromMilliseconds(100), stopAfter: 2), limits))
        {
            front.Start();
            var cut = await Final(front, Submit(front, "stopped"));
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
}
