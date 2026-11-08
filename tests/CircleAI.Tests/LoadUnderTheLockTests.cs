// LoadUnderTheLockTests.cs
//
// S9 ON THE P30, 2026-11-09: THE LOAD WATCHDOG WENT BLIND FOR THE WHOLE LOAD, THEN ENDED A LOAD THAT HAD
// JUST FINISHED. The in-process worker started its load inside its own lock, and the managed engine's load does
// its work before its first await - the hash, the split into stores on flash, the build. Every Status read
// waited on that lock, so the supervisor's three-minute watchdog could not see a 10-minute load at all, and saw
// its limit the moment the lock let go: "managed engine ready: Ternary-Bonsai-2-27B" and, in the same second,
// "TimedOut, killed-by-watchdog". The status is readable while a load works, whatever the load does first.

using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Hosting;
using Xunit;

namespace CircleAI.Tests;

public sealed class LoadUnderTheLockTests
{
    [Fact]
    public async Task The_status_can_be_read_while_a_load_works_before_its_first_await()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var worker = new InProcessBrainWorker(_ =>
        {
            // The managed engine's way: the work first, synchronously - no await gives the thread back.
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("the load ends");
        });

        var ensuring = Task.Run(() => worker.EnsureReadyAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the load never started");

        var reading = Task.Run(() => worker.Status);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(5)), "a Status read waited on the load");
        Assert.Equal(BrainPhase.Loading, reading.Result.Phase);

        release.Set();
        await Assert.ThrowsAsync<BrainWorkerException>(() => ensuring);
    }

    // A load that fails before it ever waits must not be kept: within its retry time the failure is answered, and
    // after it the next caller loads again - the desktop service once answered "not installed" for good.
    [Fact]
    public async Task A_load_that_fails_before_its_first_await_leaves_no_load_behind()
    {
        var clock = new HandClock();
        var calls = 0;
        var worker = new InProcessBrainWorker(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("no model here");
        }, clock: clock);

        await Assert.ThrowsAsync<BrainWorkerException>(() => worker.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(BrainPhase.Failed, worker.Status.Phase);
        await Assert.ThrowsAsync<BrainWorkerException>(() => worker.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(1, calls);

        clock.Now += TimeSpan.FromMinutes(10);
        await Assert.ThrowsAsync<BrainWorkerException>(() => worker.EnsureReadyAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    // A clock moved by hand.
    private sealed class HandClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 4, 6, 57, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
