// WorkerPool.cs
//
// The engine's own threads. One token runs about 150 matrix products back to back, each a
// millisecond or two on a phone, so the threads that share them must already be awake when
// the next one arrives: the thread pool's queueing, and a blocked thread's wake-up (tens of
// microseconds on Android), would be paid 150 times a token. So a worker spins for a short
// while after each piece of work, and only then blocks until the next.
//
// AND THROUGH A WHOLE TURN IT DOES NOT BLOCK AT ALL, when the caller holds the pool (OPEN-GAPS
// E34). A worker that sleeps between products looks half-idle to a phone's energy-aware
// scheduler, which moves it to a slow core: on the P30 on 2026-11-04 the three workers were on
// an A53 in a quarter of the samples taken during a turn, and a product waits for its slowest.
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

    /// <summary>How long a held worker waits for its next piece of work, spinning, before it sleeps.</summary>
    /// <remarks>
    /// TWENTY MILLISECONDS, meant to outlast the serial work between two products of a token - norms,
    /// attention, the delta rule, sampling - so a turn does not put its workers to sleep, and short enough
    /// that a held pool handing out nothing soon stops spending a core. <see cref="Sleeps"/> says whether it did.
    /// </remarks>
    public static readonly TimeSpan HeldSpin = TimeSpan.FromMilliseconds(20);

    private readonly Thread[] _threads;
    private readonly object _gate = new();
    private readonly long _spinTicks;
    private readonly long _heldSpinTicks;
    private IWork? _work;
    private Exception? _error;
    private int _generation;
    private int _pending;
    private int _held;
    private long _sleeps;
    private bool _stopping;

    /// <summary>
    /// A pool of <paramref name="threads"/> workers, the caller of <see cref="Run"/> counted as
    /// one of them. An idle worker spins for <paramref name="spin"/> (200 µs unless given)
    /// before it blocks - or, while the pool is held, for <paramref name="heldSpin"/>
    /// (<see cref="HeldSpin"/> unless given).
    /// </summary>
    public WorkerPool(int threads, TimeSpan? spin = null, TimeSpan? heldSpin = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        Threads = threads;
        _spinTicks = (long)((spin ?? TimeSpan.FromMicroseconds(200)).TotalSeconds * Stopwatch.Frequency);
        _heldSpinTicks = (long)((heldSpin ?? HeldSpin).TotalSeconds * Stopwatch.Frequency);
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

    /// <summary>Whether the workers are being kept spinning between pieces of work.</summary>
    public bool Held => Volatile.Read(ref _held) > 0;

    /// <summary>How many times a worker has gone to sleep waiting for work: what a hold saves, counted.</summary>
    public long Sleeps => Interlocked.Read(ref _sleeps);

    /// <summary>
    /// Keeps the workers spinning between pieces of work until the returned hold is disposed: for a
    /// stretch of products with serial work between them, such as a turn. Holds nest; once the last
    /// is let go a worker spins its short while again and then sleeps.
    /// </summary>
    public IDisposable KeepSpinning()
    {
        Interlocked.Increment(ref _held);
        return new Hold(this);
    }

    // One hold: let go once, however often it is disposed.
    private sealed class Hold(WorkerPool pool) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref pool._held);
        }
    }

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

    // Spins for a while - or, while the pool is held, until work has not come for HeldSpin - then
    // blocks, until there is work newer than seen. False when stopping.
    //
    // A HELD WORKER GIVES WAY, AND GIVES UP. Spinning without either, the engine's test classes ran
    // their turns side by side on an 8-core PC on 2026-11-04 and their workers took 7.6 cores between
    // them: each product waits for every worker of its pool, so the spinners starved the threads that
    // had work and the run stopped moving. So a held worker yields its core to any other thread ready
    // on it every so often - it stays runnable, so it still looks busy - and a hold that is not handing
    // out work, like a turn waiting on something else, lets it sleep after HeldSpin.
    private bool WaitForWork(ref int seen)
    {
        var started = Stopwatch.GetTimestamp();
        var spinUntil = started + _spinTicks;
        var heldUntil = started + _heldSpinTicks;
        var spins = 0;
        while (true)
        {
            var generation = Volatile.Read(ref _generation);
            if (generation != seen)
            {
                seen = generation;
                return true;
            }
            if (Volatile.Read(ref _stopping)) return false;
            var now = Stopwatch.GetTimestamp();
            if (now < spinUntil || (Volatile.Read(ref _held) > 0 && now < heldUntil))
            {
                Thread.SpinWait(16);
                if ((++spins & 63) == 0) Thread.Yield();
                continue;
            }
            lock (_gate)
            {
                if (_generation == seen && !_stopping) Interlocked.Increment(ref _sleeps);
                while (_generation == seen && !_stopping) Monitor.Wait(_gate);
            }
        }
    }
}
