// WorkerPool.cs
//
// The engine's own threads. One token runs about 150 matrix products back to back, each a
// millisecond or two on a phone, so the threads that share them must already be awake when
// the next one arrives: the thread pool's queueing, and a blocked thread's wake-up (tens of
// microseconds on Android), would be paid 150 times a token. So a worker spins for a short
// while after each piece of work, and only then blocks until the next.
//
// The caller of Run is worker 0, so a pool of N threads starts N - 1. Work is handed over as
// an IWork object the caller keeps and reuses: nothing is allocated per product.

using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace CircleAI.Inference.Managed.Runtime;

/// <summary>A fixed set of threads that run one piece of work together, then wait for the next.</summary>
public sealed class WorkerPool : IDisposable
{
    /// <summary>One piece of work, run once on every worker of the pool.</summary>
    public interface IWork
    {
        /// <summary>Runs this worker's share; <paramref name="worker"/> is 0 to <paramref name="workers"/> - 1.</summary>
        void Execute(int worker, int workers);
    }

    private readonly Thread[] _threads;
    private readonly object _gate = new();
    private readonly long _spinTicks;
    private IWork? _work;
    private Exception? _error;
    private int _generation;
    private int _pending;
    private bool _stopping;

    /// <summary>
    /// A pool of <paramref name="threads"/> workers, the caller of <see cref="Run"/> counted as
    /// one of them. An idle worker spins for <paramref name="spin"/> (200 µs unless given)
    /// before it blocks.
    /// </summary>
    public WorkerPool(int threads, TimeSpan? spin = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        Threads = threads;
        _spinTicks = (long)((spin ?? TimeSpan.FromMicroseconds(200)).TotalSeconds * Stopwatch.Frequency);
        _threads = new Thread[threads - 1];
        for (var i = 0; i < _threads.Length; i++)
        {
            var worker = i + 1;
            _threads[i] = new Thread(() => Loop(worker)) { IsBackground = true, Name = $"Engine {worker}" };
            _threads[i].Start();
        }
    }

    /// <summary>Workers, the caller of <see cref="Run"/> included.</summary>
    public int Threads { get; }

    /// <summary>
    /// Runs <paramref name="work"/> on every worker, this thread as worker 0, and returns when
    /// all have finished. An exception from any worker is rethrown here once the rest are done,
    /// and the pool stays usable. One Run at a time.
    /// </summary>
    public void Run(IWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping), this);
        if (Threads == 1)
        {
            work.Execute(0, 1);
            return;
        }

        _work = work;
        _error = null;
        Volatile.Write(ref _pending, Threads - 1);
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            Monitor.PulseAll(_gate);
        }

        Exception? mine = null;
        try
        {
            work.Execute(0, Threads);
        }
        catch (Exception e)
        {
            mine = e;
        }

        // The others are mid-product, so spin for them rather than block.
        var spinner = new SpinWait();
        while (Volatile.Read(ref _pending) != 0) spinner.SpinOnce(sleep1Threshold: -1);
        _work = null;

        var error = mine ?? _error;
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Stops the workers and waits for them to exit.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
        foreach (var thread in _threads) thread.Join();
    }

    private void Loop(int worker)
    {
        var seen = 0;
        while (WaitForWork(ref seen))
        {
            try
            {
                _work!.Execute(worker, Threads);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref _error, e, null);
            }
            Interlocked.Decrement(ref _pending);
        }
    }

    // Spins for a while, then blocks, until there is work newer than seen. False when stopping.
    private bool WaitForWork(ref int seen)
    {
        var spinUntil = Stopwatch.GetTimestamp() + _spinTicks;
        while (true)
        {
            var generation = Volatile.Read(ref _generation);
            if (generation != seen)
            {
                seen = generation;
                return true;
            }
            if (Volatile.Read(ref _stopping)) return false;
            if (Stopwatch.GetTimestamp() < spinUntil)
            {
                Thread.SpinWait(16);
                continue;
            }
            lock (_gate)
            {
                while (_generation == seen && !_stopping) Monitor.Wait(_gate);
            }
        }
    }
}
