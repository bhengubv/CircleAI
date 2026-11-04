// WorkerPoolTests.cs
//
// The engine's threads. Six claims:
//   1. Every worker runs every piece of work exactly once.
//   2. Work that arrives after the workers have stopped spinning and blocked still runs -
//      a lost wake-up would hang a token, so this one runs under a time limit.
//   3. A failure on any thread reaches the caller, and the pool carries on.
//   4. A disposed pool refuses work rather than waiting forever.
//   5. Held, the workers never sleep between pieces of work, and sleep again once let go
//      (OPEN-GAPS E34: a worker that sleeps between products looks half-idle, and the P30
//      moved the engine's to its slow cores) - and the engine holds the pool for a turn.
//   6. Holds nest, a hold let go twice counts once, and a held pool still stops.
//   7. Held but handed nothing, the workers sleep after the held spin: never spinning for ever.

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

    // Waits, up to a time limit, for something the workers do on their own threads.
    private static void WaitFor(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(done(), what);
    }

    // The sleep count once it has stopped moving: with no work and no spin, every worker asleep.
    private static long Settled(WorkerPool pool)
    {
        var last = -1L;
        for (var i = 0; i < 600 && pool.Sleeps != last; i++)
        {
            last = pool.Sleeps;
            Thread.Sleep(50);
        }
        return pool.Sleeps;
    }

    [Fact]
    public void A_held_pool_keeps_its_workers_awake_through_a_turn_and_lets_them_sleep_once_it_ends()
    {
        // No spinning of its own, and serial work between products: unheld, the workers sleep between them.
        // Held, they wait out a gap far longer than any here - a 2 ms sleep on a loaded Windows box ran past
        // the 20 ms a phone's turn is given, and they gave up and slept, as they should.
        using var pool = new WorkerPool(3, spin: TimeSpan.Zero, heldSpin: TimeSpan.FromSeconds(10));
        var work = new Count(3);
        for (var i = 0; i < 10; i++) { pool.Run(work); Thread.Sleep(2); }
        var asleep = Settled(pool);
        Assert.True(asleep >= 12, $"unheld, the two workers slept {asleep} times over ten pieces of work");

        // Held, as a turn holds it: not one sleep between its products.
        using (pool.KeepSpinning())
        {
            Assert.True(pool.Held);
            for (var i = 0; i < 50; i++) { pool.Run(work); Thread.Sleep(2); }
            Assert.Equal(asleep, pool.Sleeps);
        }

        // The turn over, they sleep again.
        Assert.False(pool.Held);
        WaitFor(() => pool.Sleeps >= asleep + 2, "let go, the workers did not sleep again");
        Assert.All(work.Hits, hits => Assert.Equal(60, hits));
    }

    [Fact]
    public void A_held_pool_handing_out_nothing_lets_its_workers_sleep_after_its_held_spin()
    {
        // Spinning without end is what stopped the engine's tests on 2026-11-04: their pools' workers took
        // 7.6 of 8 cores and starved the threads that had work.
        using var pool = new WorkerPool(3, spin: TimeSpan.Zero, heldSpin: TimeSpan.FromMilliseconds(50));
        var work = new Count(3);
        pool.Run(work);
        var asleep = Settled(pool);

        using (pool.KeepSpinning())
        {
            pool.Run(work);
            WaitFor(() => pool.Sleeps >= asleep + 2, "held and handed nothing, the workers never slept");
        }
        Assert.All(work.Hits, hits => Assert.Equal(2, hits));
    }

    [Fact]
    public void Holds_nest_a_hold_let_go_twice_counts_once_and_a_held_pool_still_stops()
    {
        var pool = new WorkerPool(3);
        var turn = pool.KeepSpinning();
        var inner = pool.KeepSpinning();
        inner.Dispose();
        inner.Dispose();
        Assert.True(pool.Held);

        pool.Run(new Count(3));
        var stopping = Task.Run(pool.Dispose);
        Assert.True(stopping.Wait(TimeSpan.FromSeconds(30)), "a held pool never stopped");
        turn.Dispose();
        Assert.False(pool.Held);
    }

    [Fact]
    public void The_engine_holds_its_pool_for_each_turn()
    {
        var generator = SlowIsNotStuckTests.Source("src", "CircleAI", "Inference", "Managed", "ManagedChatGenerator.cs");
        var turn = generator.IndexOf("private async Task<Outcome> RunAsync(", StringComparison.Ordinal);
        var hold = generator.IndexOf("using var spinning = _pool.KeepSpinning();", turn, StringComparison.Ordinal);
        var run = generator.IndexOf("return Run(messages, options, emit, ct);", hold, StringComparison.Ordinal);
        Assert.True(turn >= 0 && hold > turn && run > hold, "a turn does not hold the engine's workers");
    }
}
