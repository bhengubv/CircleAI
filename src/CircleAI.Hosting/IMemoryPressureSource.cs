// IMemoryPressureSource.cs
//
// (RT-04) Platform-published memory-pressure signal. Hosting wires the
// platform-specific source (Android `onTrimMemory`, iOS memory warning,
// Microsoft.Extensions.Caching.Memory.MemoryCacheOptions in .NET) into the
// pipeline. `AIService` listens and triggers the fallback-chain swap when
// the level reaches Critical.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting;

/// <summary>
/// Coarse memory-pressure level. Mirrors Android's onTrimMemory contract
/// (ComponentCallbacks2.TRIM_MEMORY_RUNNING_*) and iOS's
/// UIApplicationDidReceiveMemoryWarningNotification.
/// </summary>
public enum MemoryPressureLevel
{
    /// <summary>Plenty of headroom; no action.</summary>
    Normal   = 0,
    /// <summary>OS asked apps to release optional caches. Drop prefix cache.</summary>
    Trim     = 1,
    /// <summary>OS is about to kill the process. Drop everything; consider downshifting model.</summary>
    Critical = 2,
}

/// <summary>
/// (RT-04) A platform-published memory-pressure signal. Implementations
/// notify subscribers (registered via <see cref="Subscribe"/>) on a worker
/// thread; subscribers must be thread-safe.
/// </summary>
public interface IMemoryPressureSource
{
    /// <summary>Current pressure level as last observed.</summary>
    MemoryPressureLevel Current { get; }

    /// <summary>
    /// Subscribe to pressure-level transitions. The handler receives
    /// (oldLevel, newLevel). Returns an unsubscribe handle.
    /// </summary>
    IDisposable Subscribe(Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler);
}

/// <summary>
/// Default <see cref="IMemoryPressureSource"/> that always reports Normal
/// pressure and never raises events. Used when no platform-specific source
/// is registered — CircleAI keeps working, brownout simply never fires.
/// </summary>
public sealed class NullMemoryPressureSource : IMemoryPressureSource
{
    public static readonly NullMemoryPressureSource Instance = new();
    public MemoryPressureLevel Current => MemoryPressureLevel.Normal;
    public IDisposable Subscribe(Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler)
        => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Manually-driven <see cref="IMemoryPressureSource"/>. Hosting layers (or
/// tests) can construct one and call <see cref="Raise"/> when the platform
/// publishes a pressure event. Thread-safe.
/// </summary>
public sealed class ManualMemoryPressureSource : IMemoryPressureSource
{
    private readonly object _gate = new();
    private MemoryPressureLevel _current = MemoryPressureLevel.Normal;
    private readonly System.Collections.Generic.List<Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask>> _handlers = new();

    public MemoryPressureLevel Current
    {
        get { lock (_gate) return _current; }
    }

    public IDisposable Subscribe(Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <summary>
    /// Publish a new pressure level. Idempotent for the same level — only
    /// transitions fire handlers.
    /// </summary>
    public async ValueTask Raise(MemoryPressureLevel level, CancellationToken ct = default)
    {
        MemoryPressureLevel previous;
        Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask>[] snapshot;
        lock (_gate)
        {
            if (_current == level) return;
            previous = _current;
            _current = level;
            snapshot = _handlers.ToArray();
        }
        foreach (var h in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            try { await h(previous, level).ConfigureAwait(false); }
            catch { /* error-isolated; pressure handlers must not break the source */ }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ManualMemoryPressureSource _owner;
        private readonly Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> _handler;
        public Subscription(ManualMemoryPressureSource owner, Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler)
        { _owner = owner; _handler = handler; }
        public void Dispose() { lock (_owner._gate) _owner._handlers.Remove(_handler); }
    }
}
