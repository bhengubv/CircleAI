// AndroidMemoryPressure.cs
//
// Not being the process that eats the phone.
//
// IMemoryPressureSource has always documented itself as mirroring Android's
// onTrimMemory contract. Nothing on Android ever implemented it, so every head
// got NullMemoryPressureSource — Normal, forever — and the brownout path that
// exists to release models under pressure has never once fired on a phone. The
// design anticipated the signal and no one plugged in the cable.
//
// TWO DIFFERENT JOBS, and only doing the first is what earns an app its
// reputation for hogging:
//
//   REACTIVE   Android says it is running out. Release now. By the time this
//              arrives the phone is ALREADY struggling — the launcher has been
//              evicted, the keyboard is redrawing. Doing only this means the user
//              has already felt it.
//
//   PROACTIVE  Nobody has asked for anything in a while. Release anyway. This is
//              the half that makes CircleAI "feel like air": a model held for
//              twenty idle minutes is 122 MB stolen from whatever the person is
//              actually doing, and no OS signal will ever complain about it
//              because from Android's side we are a well-behaved foreground
//              service quietly sitting on a fifth of a cheap phone's RAM.
//
// The idle window is read from the device tier, not hardcoded, because the right
// answer genuinely differs: a phone with 8 GB can afford to stay warm and feel
// instant; a P30 with 1.5 GB free cannot, and pretending otherwise is how you
// throttle someone's only computer.

using Android.Content;
using CircleAI.Core;
using CircleAI.Hosting;

namespace CircleAI.Device;

/// <summary>
/// Feeds Android's <c>onTrimMemory</c> into CircleAI's brownout path, and
/// releases on idle before Android ever has to ask.
/// </summary>
public sealed class AndroidMemoryPressure : Java.Lang.Object, IMemoryPressureSource, IComponentCallbacks2
{
    private readonly List<Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask>> _handlers = new();
    private readonly object _gate = new();
    private System.Threading.Timer? _idleTimer;
    private DateTime _lastUse = DateTime.UtcNow;

    /// <inheritdoc/>
    public MemoryPressureLevel Current { get; private set; } = MemoryPressureLevel.Normal;

    /// <summary>
    /// How long the models may sit unused before they are released anyway.
    /// </summary>
    /// <remarks>
    /// Derived from the device tier rather than fixed. Holding a model is a bet
    /// that the next question comes soon enough to be worth the RAM; on a phone
    /// with headroom that bet is nearly free, and on a 3 GB phone it is being paid
    /// for by every other app the person has open.
    /// </remarks>
    public static TimeSpan IdleWindowFor(DeviceTier tier) => tier switch
    {
        DeviceTier.Workstation or DeviceTier.Desktop => TimeSpan.FromMinutes(60),
        DeviceTier.Tablet                            => TimeSpan.FromMinutes(20),
        DeviceTier.Phone                             => TimeSpan.FromMinutes(5),
        _                                            => TimeSpan.FromMinutes(2),
    };

    /// <summary>Starts watching for idleness. Call once, after the node is up.</summary>
    public void StartIdleWatch(DeviceProbe? probe = null)
    {
        var window = IdleWindowFor((probe ?? DeviceProbe.Snapshot()).Classify());
        var tick   = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond * 30, window.Ticks / 4));

        _idleTimer?.Dispose();
        _idleTimer = new System.Threading.Timer(_ =>
        {
            if (DateTime.UtcNow - _lastUse < window) return;

            // Trim, not Critical. Idle is not an emergency: the aim is to hand
            // back what is not being used, not to tear the node down and make the
            // next question pay a cold start it did not have to.
            _ = RaiseAsync(MemoryPressureLevel.Trim);
            _lastUse = DateTime.UtcNow;   // one release per idle window, not a loop
        }, null, tick, tick);
    }

    /// <summary>Call whenever the brain is actually used, to reset the idle clock.</summary>
    public void Touch() => _lastUse = DateTime.UtcNow;

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _handlers.Add(handler);
        return new Unsubscriber(this, handler);
    }

    // ── Android's side ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void OnTrimMemory(TrimMemory level) => _ = RaiseAsync(Map(level));

    /// <summary>Older devices deliver this instead; treat it as the worst case.</summary>
    public void OnLowMemory() => _ = RaiseAsync(MemoryPressureLevel.Critical);

    /// <inheritdoc/>
    public void OnConfigurationChanged(global::Android.Content.Res.Configuration? newConfig) { }

    /// <summary>Android's ladder, mapped to the three levels CircleAI acts on.</summary>
    /// <remarks>
    /// UI_HIDDEN counts as Trim even though Android does not consider it pressure.
    /// It means every screen went away — the person is doing something else, and
    /// continuing to hold a model for a UI nobody is looking at is precisely the
    /// behaviour that gets an app called a memory hog.
    /// </remarks>
    internal static MemoryPressureLevel Map(TrimMemory level) => level switch
    {
        TrimMemory.RunningCritical => MemoryPressureLevel.Critical,
        TrimMemory.Complete        => MemoryPressureLevel.Critical,
        TrimMemory.Moderate        => MemoryPressureLevel.Critical,
        TrimMemory.RunningLow      => MemoryPressureLevel.Trim,
        TrimMemory.RunningModerate => MemoryPressureLevel.Trim,
        TrimMemory.Background      => MemoryPressureLevel.Trim,
        TrimMemory.UiHidden        => MemoryPressureLevel.Trim,
        _                          => MemoryPressureLevel.Normal,
    };

    private async Task RaiseAsync(MemoryPressureLevel next)
    {
        MemoryPressureLevel previous;
        Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask>[] handlers;

        lock (_gate)
        {
            if (next == Current) return;      // only transitions are news
            previous = Current;
            Current  = next;
            handlers = _handlers.ToArray();
        }

        foreach (var h in handlers)
        {
            // One bad subscriber must not stop the others releasing. Under
            // Critical the alternative to releasing is the OS killing the process.
            try { await h(previous, next).ConfigureAwait(false); }
            catch { /* keep going — freeing memory matters more than this handler */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _idleTimer?.Dispose(); _idleTimer = null; }
        base.Dispose(disposing);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly AndroidMemoryPressure _owner;
        private readonly Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> _handler;
        public Unsubscriber(AndroidMemoryPressure owner,
                            Func<MemoryPressureLevel, MemoryPressureLevel, ValueTask> handler)
        { _owner = owner; _handler = handler; }
        public void Dispose() { lock (_owner._gate) _owner._handlers.Remove(_handler); }
    }
}
