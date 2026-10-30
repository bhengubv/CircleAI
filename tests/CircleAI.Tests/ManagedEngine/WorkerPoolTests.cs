// WorkerPoolTests.cs
//
// The engine's threads. Four claims:
//   1. Every worker runs every piece of work exactly once.
//   2. Work that arrives after the workers have stopped spinning and blocked still runs -
//      a lost wake-up would hang a token, so this one runs under a time limit.
//   3. A failure on any thread reaches the caller, and the pool carries on.
//   4. A disposed pool refuses work rather than waiting forever.

using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class WorkerPoolTests
{
    private sealed class Count(int threads) : WorkerPool.IWork
    {
        public int[] Hits { get; } = new int[threads];

        public void Execute(int worker, int workers) => Interlocked.Increment(ref Hits[worker]);
    }

    private sealed class Fail : WorkerPool.IWork
    {
        public int FailOn { get; set; } = -1;

        public void Execute(int worker, int workers)
        {
            if (worker == FailOn) throw new InvalidOperationException($"worker {worker}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Every_worker_runs_every_piece_of_work_once(int threads)
    {
        using var pool = new WorkerPool(threads);
        var work = new Count(threads);
        for (var i = 0; i < 500; i++) pool.Run(work);
        Assert.All(work.Hits, hits => Assert.Equal(500, hits));
    }

    [Fact]
    public async Task Work_that_arrives_after_the_workers_blocked_still_runs()
    {
        // No spinning at all, and a pause before every run: each one has to wake blocked threads.
        using var pool = new WorkerPool(3, spin: TimeSpan.Zero);
        var work = new Count(3);
        var runs = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                Thread.Sleep(2);
                pool.Run(work);
            }
        });

        var finished = await Task.WhenAny(runs, Task.Delay(TimeSpan.FromSeconds(30))) == runs;
        Assert.True(finished, "a run never finished: a blocked worker missed its wake-up");
        Assert.All(work.Hits, hits => Assert.Equal(20, hits));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_failure_on_any_thread_reaches_the_caller_and_the_pool_carries_on(int failingWorker)
    {
        using var pool = new WorkerPool(3);
        var fail = new Fail { FailOn = failingWorker };
        var error = Assert.Throws<InvalidOperationException>(() => pool.Run(fail));
        Assert.Equal($"worker {failingWorker}", error.Message);

        var work = new Count(3);
        pool.Run(work);
        Assert.All(work.Hits, hits => Assert.Equal(1, hits));
    }

    [Fact]
    public void A_disposed_pool_refuses_work()
    {
        var pool = new WorkerPool(2);
        pool.Dispose();
        pool.Dispose(); // twice is harmless
        Assert.Throws<ObjectDisposedException>(() => pool.Run(new Count(2)));
    }
}
