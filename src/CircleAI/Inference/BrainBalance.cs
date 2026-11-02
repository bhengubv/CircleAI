// BrainBalance.cs
//
// SHRINK BEFORE YOU DROP, GROW BACK WHEN CALM, AND NEVER FLAP (Step 4, "balanced").
//
// Battery and heat already reach the service (PowerBudgetPolicy since A13, DeviceContext since S4),
// and what they could do was cap one answer's length. Under real pressure a phone did nothing until
// Android released the brain for it - all of it, at once - and on the P30 that is fifteen seconds of
// reload the moment the pressure lifts, again and again as a battery or a heat reading crossed its
// line. The owner's framework asks for the opposite: give ground a step at a time, cheapest first,
// and take it back once, when things are calm.
//
// THE LADDER, cheapest first:
//
//   Full          the brain as built: working memory at 4 bits, the whole context, drafter and eyes on
//   LeanMemory    working memory at 3 bits
//   ShortContext  ... and half the context
//   NoExtras      ... working memory at 2 bits, drafter and eyes off
//   SmallerBrain  ... and the next smaller brain on the device
//   Released      ... and the brain let go while nobody is asking
//
// THREE GUARDS AGAINST A SWING RELOADING GIGABYTES OVER AND OVER:
//   bands         a pressure starts below one line and ends above another (battery 20% / 30%), so a
//                 reading wobbling on one line does not switch it on and off;
//   a minimum gap no two changes closer than ten minutes, up or down;
//   between turns the loop applies a change only while the queue is idle, never mid-answer.
//
// AND IT REMEMBERS PER DEVICE WHAT WORKED: the rung a kind of pressure needed last time is where it
// starts next time, instead of walking down the ladder again while the phone overheats. The memory
// and the current rung are kept in a small file, so a restart does not forget a hot phone.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>How far the brain has given ground to the device. Each rung keeps everything above it.</summary>
public enum BalanceRung
{
    /// <summary>The brain as built.</summary>
    Full = 0,

    /// <summary>Working memory at 3 bits.</summary>
    LeanMemory = 1,

    /// <summary>And half the context.</summary>
    ShortContext = 2,

    /// <summary>Working memory at 2 bits, and the drafter and the eyes off.</summary>
    NoExtras = 3,

    /// <summary>And the next smaller brain on the device.</summary>
    SmallerBrain = 4,

    /// <summary>And the brain let go while nobody is asking.</summary>
    Released = 5,
}

/// <summary>What pressed the brain to give ground.</summary>
public enum PressureKind
{
    /// <summary>A battery running down, not on charge.</summary>
    LowBattery = 0,

    /// <summary>A hot device.</summary>
    Heat = 1,

    /// <summary>A device that is out of memory.</summary>
    Memory = 2,
}

/// <summary>The knobs a rung sets.</summary>
/// <param name="Rung">The rung.</param>
/// <param name="KvMode">The working memory's bits; null leaves the engine's own (4 bits on the C# engine).</param>
/// <param name="ContextShare">The share of the context the brain is built with (1 is all of it).</param>
/// <param name="Drafter">Whether a drafter may speed answers.</param>
/// <param name="Vision">Whether the eyes may run.</param>
/// <param name="SmallerBrain">Whether to choose the next smaller brain on the device.</param>
/// <param name="Released">Whether to let the brain go while nobody is asking.</param>
public sealed record BalanceShape(
    BalanceRung Rung, KvCompressionMode? KvMode, double ContextShare, bool Drafter, bool Vision, bool SmallerBrain, bool Released)
{
    /// <summary>The brain as built.</summary>
    public static BalanceShape Full { get; } = Of(BalanceRung.Full);

    /// <summary>The knobs for <paramref name="rung"/>.</summary>
    public static BalanceShape Of(BalanceRung rung) => rung switch
    {
        BalanceRung.Full         => new(rung, null, 1.0, true, true, false, false),
        BalanceRung.LeanMemory   => new(rung, KvCompressionMode.TurboQuant3Bit, 1.0, true, true, false, false),
        BalanceRung.ShortContext => new(rung, KvCompressionMode.TurboQuant3Bit, 0.5, true, true, false, false),
        BalanceRung.NoExtras     => new(rung, KvCompressionMode.TurboQuant2Bit, 0.5, false, false, false, false),
        BalanceRung.SmallerBrain => new(rung, KvCompressionMode.TurboQuant2Bit, 0.5, false, false, true, false),
        _                        => new(BalanceRung.Released, KvCompressionMode.TurboQuant2Bit, 0.5, false, false, true, true),
    };

    /// <summary>Whether going from <paramref name="from"/> to this needs the brain built again.</summary>
    /// <param name="from">The shape the brain has now.</param>
    /// <param name="workingMemoryCounts">
    /// Whether the brain's engine reads the working memory's bits. MNN's stays as A11 left it, so a
    /// change of bits alone would reload an MNN brain for nothing - fifteen seconds on the P30.
    /// </param>
    public bool Rebuilds(BalanceShape from, bool workingMemoryCounts = true)
    {
        ArgumentNullException.ThrowIfNull(from);
        return (workingMemoryCounts && from.KvMode != KvMode)
               || Math.Abs(from.ContextShare - ContextShare) > 0.001
               || from.SmallerBrain != SmallerBrain;
    }

    /// <summary>One line for a log.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Rung}: working memory {(KvMode is { } kv ? kv.ToString() : "as built")}, context x{ContextShare:0.##}, drafter {(Drafter ? "on" : "off")}, eyes {(Vision ? "on" : "off")}{(SmallerBrain ? ", a smaller brain" : string.Empty)}{(Released ? ", released while idle" : string.Empty)}");
}

/// <summary>What the device is going through, as read now.</summary>
/// <param name="BatteryPercent">0..100, or null when unknown.</param>
/// <param name="Charging">Whether it is on charge, or null when unknown.</param>
/// <param name="Heat">0 normal, 1 hot (throttling), 2 critical.</param>
/// <param name="MemoryCritical">Whether the platform says memory is critically short (a trim at "complete").</param>
public sealed record BalanceReading(int? BatteryPercent, bool? Charging, int Heat, bool MemoryCritical)
{
    /// <summary>Nothing pressing.</summary>
    public static BalanceReading Calm { get; } = new(null, null, 0, false);
}

/// <summary>A change the balance wants, and why.</summary>
/// <param name="To">The rung to go to.</param>
/// <param name="Shape">Its knobs.</param>
/// <param name="Reason">One plain sentence.</param>
/// <param name="Kind">The pressure behind it; null when it is the calm coming back.</param>
public sealed record BalanceDecision(BalanceRung To, BalanceShape Shape, string Reason, PressureKind? Kind);

/// <summary>The lines and the gaps the balance keeps.</summary>
public sealed record BalanceOptions
{
    /// <summary>Below this battery, not on charge, the brain starts to give ground.</summary>
    public int LowBattery { get; init; } = 20;

    /// <summary>Above this, the low battery is over (the band's other line).</summary>
    public int LowBatteryClear { get; init; } = 30;

    /// <summary>Below this, the battery is severe.</summary>
    public int VeryLowBattery { get; init; } = 10;

    /// <summary>Above this, the severe battery is over.</summary>
    public int VeryLowBatteryClear { get; init; } = 15;

    /// <summary>No two changes closer than this, up or down.</summary>
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long nothing must press before the brain grows back.</summary>
    public TimeSpan CalmFor { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Decides how far the brain gives ground, with bands, a minimum gap and memory per device.</summary>
public sealed class BrainBalance
{
    private readonly object _gate = new();
    private readonly BalanceOptions _options;
    private readonly string? _file;
    private readonly Dictionary<PressureKind, BalanceRung> _worked = new();

    private BalanceRung _rung;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    private DateTimeOffset? _calmSince;
    private bool _low, _veryLow;
    private PressureKind? _episode;
    private BalanceRung _deepest;

    /// <summary>A balance remembered in <paramref name="file"/> (null: this run only).</summary>
    public BrainBalance(BalanceOptions? options = null, string? file = null)
    {
        _options = options ?? new BalanceOptions();
        _file = file;
        Load();
    }

    /// <summary>The rung the brain is on now.</summary>
    public BalanceRung Current { get { lock (_gate) return _rung; } }

    /// <summary>The knobs for the rung it is on now.</summary>
    public BalanceShape Shape => BalanceShape.Of(Current);

    /// <summary>The rung each kind of pressure needed last time, as remembered for this device.</summary>
    public IReadOnlyDictionary<PressureKind, BalanceRung> Worked
    {
        get { lock (_gate) return new Dictionary<PressureKind, BalanceRung>(_worked); }
    }

    /// <summary>
    /// What to do about <paramref name="reading"/> at <paramref name="now"/>: a change, or null to stay.
    /// Nothing changes until <see cref="Applied"/> says it took effect, so a change refused for being
    /// mid-answer is simply asked for again.
    /// </summary>
    public BalanceDecision? Decide(BalanceReading reading, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        lock (_gate)
        {
            var (severity, kind) = Press(reading);
            var gapOver = now - _lastChange >= _options.MinimumGap;

            if (severity == 0)
            {
                _calmSince ??= now;
                if (_rung == BalanceRung.Full) { CloseEpisode(); return null; }
                if (now - _calmSince.Value < _options.CalmFor || !gapOver) return null;
                return new(BalanceRung.Full, BalanceShape.Full,
                           $"calm again for {_options.CalmFor.TotalMinutes:0} minutes: the brain grows back", null);
            }

            _calmSince = null;
            var floor = severity >= 2 ? BalanceRung.Released : BalanceRung.NoExtras;
            if (_rung >= floor || !gapOver) return null;

            // WHERE THIS KIND OF PRESSURE ENDED UP LAST TIME, when that is further than one step:
            // walking the ladder again while the phone overheats is the slow way to the same place.
            var next = (BalanceRung)Math.Min((int)floor, (int)_rung + 1);
            if (_episode != kind && _worked.TryGetValue(kind, out var remembered) && remembered > next)
                next = (BalanceRung)Math.Min((int)floor, (int)remembered);

            return new(next, BalanceShape.Of(next), Why(kind, severity, next), kind);
        }
    }

    /// <summary>The change took effect: the rung moves, the gap starts, and what worked is remembered.</summary>
    public void Applied(BalanceDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (_gate)
        {
            _rung = decision.To;
            _lastChange = now;
            if (decision.Kind is { } kind)
            {
                if (_episode != kind) { CloseEpisode(); _episode = kind; _deepest = decision.To; }
                if (decision.To > _deepest) _deepest = decision.To;
            }
            else
            {
                CloseEpisode();
            }
            Save();
        }
    }

    // A pressure's severity (0 none, 1 give some ground, 2 give all of it) and the kind that leads.
    private (int Severity, PressureKind Kind) Press(BalanceReading r)
    {
        // THE BAND: on below one line, off above another, and charging ends it.
        if (r.Charging == true || r.BatteryPercent is null) { _low = false; _veryLow = false; }
        else
        {
            var pct = r.BatteryPercent.Value;
            if (pct <= _options.LowBattery) _low = true;
            else if (pct >= _options.LowBatteryClear) _low = false;
            if (pct <= _options.VeryLowBattery) _veryLow = true;
            else if (pct >= _options.VeryLowBatteryClear) _veryLow = false;
        }

        var battery = _veryLow ? 2 : _low ? 1 : 0;
        var heat = Math.Clamp(r.Heat, 0, 2);
        var memory = r.MemoryCritical ? 2 : 0;

        // The heaviest leads; on a tie memory, then heat, then the battery.
        if (memory >= heat && memory >= battery && memory > 0) return (memory, PressureKind.Memory);
        if (heat >= battery && heat > 0) return (heat, PressureKind.Heat);
        return (battery, PressureKind.LowBattery);
    }

    private void CloseEpisode()
    {
        if (_episode is { } kind) _worked[kind] = _deepest;
        _episode = null;
        _deepest = BalanceRung.Full;
    }

    private static string Why(PressureKind kind, int severity, BalanceRung to)
    {
        var what = kind switch
        {
            PressureKind.LowBattery => severity >= 2 ? "the battery is nearly flat" : "the battery is low",
            PressureKind.Heat       => severity >= 2 ? "the phone is very hot" : "the phone is hot",
            _                       => "the phone is out of memory",
        };
        var step = to switch
        {
            BalanceRung.LeanMemory   => "its working memory is packed tighter",
            BalanceRung.ShortContext => "it keeps a shorter conversation in mind",
            BalanceRung.NoExtras     => "faster answers and seeing rest",
            BalanceRung.SmallerBrain => "a smaller brain answers",
            _                        => "the brain is let go while nobody is asking",
        };
        return $"{what}, so {step}";
    }

    // ── what survives a restart ─────────────────────────────────────────────

    private sealed class Stored
    {
        public int Rung { get; set; }
        public long LastChangeMs { get; set; }
        public Dictionary<string, int> Worked { get; set; } = new();
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_file) || !File.Exists(_file)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file));
            if (stored is null) return;
            if (Enum.IsDefined(typeof(BalanceRung), stored.Rung)) _rung = (BalanceRung)stored.Rung;
            if (stored.LastChangeMs > 0) _lastChange = DateTimeOffset.FromUnixTimeMilliseconds(stored.LastChangeMs);
            foreach (var (name, rung) in stored.Worked)
                if (Enum.TryParse<PressureKind>(name, out var kind) && Enum.IsDefined(typeof(BalanceRung), rung))
                    _worked[kind] = (BalanceRung)rung;
        }
        catch
        {
            // A memory that will not read starts the device afresh at Full: the cost is walking the
            // ladder once more, never a brain stuck small.
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_file)) return;
        try
        {
            var stored = new Stored
            {
                Rung = (int)_rung,
                LastChangeMs = _lastChange == DateTimeOffset.MinValue ? 0 : _lastChange.ToUnixTimeMilliseconds(),
                Worked = _worked.ToDictionary(p => p.Key.ToString(), p => (int)p.Value),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var writing = _file + ".writing";
            File.WriteAllText(writing, JsonSerializer.Serialize(stored));
            File.Move(writing, _file, overwrite: true);
        }
        catch
        {
            // Remembered for this run; the next change tries the file again.
        }
    }
}

/// <summary>
/// Runs the balance: reads the device, asks <see cref="BrainBalance"/>, and applies a change only
/// between turns - then primes the brain again so the next question does not pay for it.
/// </summary>
public sealed class BalanceLoop : IDisposable
{
    private readonly BrainBalance _balance;
    private readonly Func<BalanceReading> _read;
    private readonly Func<bool> _idle;
    private readonly Func<BalanceShape, CancellationToken, Task> _apply;
    private readonly Func<CancellationToken, Task>? _prime;
    private readonly Func<CancellationToken, Task>? _rest;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private Timer? _timer;

    /// <param name="balance">The decisions.</param>
    /// <param name="read">The device now.</param>
    /// <param name="idle">Whether nothing is being answered or waiting - a change waits for this.</param>
    /// <param name="apply">Gives the brain a shape (rebuilding it between turns when it must).</param>
    /// <param name="prime">Loads and warms the brain again after a change that keeps one.</param>
    /// <param name="rest">On the released rung, lets go of a brain loaded since for a question.</param>
    /// <param name="enabled">The person's "balance with the phone" preference; null is on.</param>
    /// <param name="log">Where each change is said.</param>
    /// <param name="clock">The clock; a test sets its own.</param>
    public BalanceLoop(
        BrainBalance balance, Func<BalanceReading> read, Func<bool> idle,
        Func<BalanceShape, CancellationToken, Task> apply, Func<CancellationToken, Task>? prime = null,
        Func<CancellationToken, Task>? rest = null, Func<bool>? enabled = null, Action<string>? log = null,
        TimeProvider? clock = null)
    {
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _idle = idle ?? throw new ArgumentNullException(nameof(idle));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _prime = prime;
        _rest = rest;
        _enabled = enabled ?? (() => true);
        _log = log ?? (_ => { });
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The last change made, for a status line; null before the first.</summary>
    public BalanceDecision? Last { get; private set; }

    /// <summary>Ticks every <paramref name="period"/> until disposed.</summary>
    public void Start(TimeSpan period)
    {
        _timer ??= new Timer(_ => _ = TickAsync(CancellationToken.None), null, period, period);
    }

    /// <summary>
    /// One look at the device: a change when one is due and the queue is idle; on the released rung,
    /// a brain loaded since for a question is let go again. Never throws.
    /// </summary>
    /// <returns>The change made, or null.</returns>
    public async Task<BalanceDecision?> TickAsync(CancellationToken ct)
    {
        if (!await _one.WaitAsync(0, ct).ConfigureAwait(false)) return null; // one tick at a time
        try
        {
            var reading = SafeRead();
            if (!SafeEnabled())
            {
                // SWITCHED OFF BY THE PERSON: grow back to the brain as built and stay there.
                reading = BalanceReading.Calm;
            }

            var decision = _balance.Decide(reading, _clock.GetUtcNow());
            if (decision is null)
            {
                if (_balance.Current == BalanceRung.Released && _rest is not null && SafeIdle())
                    await _rest(ct).ConfigureAwait(false);
                return null;
            }

            // BETWEEN TURNS, NEVER MID-ANSWER: a change refused now is decided again next tick.
            if (!SafeIdle()) return null;

            var from = _balance.Shape;
            await _apply(decision.Shape, ct).ConfigureAwait(false);
            _balance.Applied(decision, _clock.GetUtcNow());
            Last = decision;
            _log($"balance: {from.Rung} -> {decision.To} - {decision.Reason} ({decision.Shape.Describe()})");

            // PRIMED AGAIN WHEN THE CHANGE KEEPS A BRAIN, so the next question finds it warm.
            if (!decision.Shape.Released && _prime is not null)
            {
                try { await _prime(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log("balance: the brain did not prime after the change (" + ex.GetType().Name + "); the next question loads it");
                }
            }
            return decision;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log("balance: could not apply a change: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private BalanceReading SafeRead()
    {
        try { return _read() ?? BalanceReading.Calm; }
        catch { return BalanceReading.Calm; }
    }

    private bool SafeIdle()
    {
        try { return _idle(); }
        catch { return false; }
    }

    private bool SafeEnabled()
    {
        try { return _enabled(); }
        catch { return true; }
    }
}
