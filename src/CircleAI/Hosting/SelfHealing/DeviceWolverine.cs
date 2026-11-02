// DeviceWolverine.cs
//
// WOLVERINE ON THE DEVICE, STARTED (S6).
//
// Everything below was built and never run: SelfHealer, FailureAnalyst, the healing log, the
// policy, MemoryManagerSafeFix - not one line of the service composed them, so no failure on a
// phone was ever healed by the loop. This is the composition, kept here in the desktop-buildable
// library so the whole of it is tested with fakes; the service supplies the Android parts as
// delegates (the brain's readiness, releasing it, fetching a model, saying a sentence).
//
// WHAT IT DOES WITH A FAILURE. Every HealthEvent - an exception nothing caught, the inference
// process dying, a download failing its hash, a reply that said nothing, a tool call nobody
// could read - is:
//   1. kept for WolverineAPI (the outbox), counted by fingerprint;
//   2. checked against a remedy still waiting for its proof - the same failure returning means
//      the remedy did not work, and the device remembers that (SelfModel);
//   3. handed to the loop: rules first, the brain only for a fault no rule knows (and never about
//      its own failure), autonomy AutoFixSafe by default.
// A remedy applied is pending until the next answer (or the next download that lands) proves it.
//
// ONE HEALING AT A TIME. Two failures a second apart must not both turn the same knob.

using System.Collections.Concurrent;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing.Upstream;
using CircleAI.Inference;
using CircleAI.Linking;
using CircleAI.SelfKnowledge;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>What Wolverine on the device is built from. The host supplies the platform's parts.</summary>
public sealed class WolverineOptions
{
    /// <summary>The service's state folder; Wolverine keeps its log, its outbox and the brain's knobs in "wolverine" under it.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>How autonomous the person has set it, and whether the brain is ready (DeviceSelfHealPolicy on the phone).</summary>
    public required ISelfHealPolicy Policy { get; init; }

    /// <summary>The brain, for a fault no rule covers; null asks nobody and hands such faults on.</summary>
    public IFailureAnalyst? Analyst { get; init; }

    /// <summary>The healing log; null keeps one in SQLite under the state folder.</summary>
    public IHealingLog? Log { get; init; }

    /// <summary>Fixes the host adds beside the engine's: MemoryManagerSafeFix on the phone.</summary>
    public IEnumerable<ISafeFix> ExtraFixes { get; init; } = [];

    /// <summary>Where a sentence the person should hear goes (CircleNeuronLinkService.Decided).</summary>
    public Action<string>? Announce { get; init; }

    /// <summary>The brain running now.</summary>
    public Func<string?>? RunningModel { get; init; }

    /// <summary>Whether this device holds another brain to step down to from the one named.</summary>
    public Func<string, bool>? HasAnotherBrain { get; init; }

    /// <summary>Lets a new knob take effect at the next turn - releasing the brain while nobody is waiting.</summary>
    public Func<Task>? ApplyBetweenTurns { get; init; }

    /// <summary>Starts fetching a model again; false when it cannot start.</summary>
    public Func<string, bool>? Redownload { get; init; }

    /// <summary>Resumes a model's download when the device is online; false when it cannot.</summary>
    public Func<string, bool>? ResumeDownload { get; init; }

    /// <summary>Reads the autonomy dial's stored value.</summary>
    public Func<CancellationToken, Task<string?>>? ReadAutonomy { get; init; }

    /// <summary>Stores the autonomy dial's value.</summary>
    public Func<string, CancellationToken, Task>? WriteAutonomy { get; init; }

    /// <summary>The WolverineAPI outbox; null sends nothing upstream.</summary>
    public WolverineOutbox? Outbox { get; init; }

    /// <summary>The device's class for a batch; null reads it.</summary>
    public Func<WolverineDeviceClass>? DeviceClass { get; init; }

    /// <summary>This build's version, for a batch.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Threads a brain runs at full; null reads the device (its fast cores, one left over).</summary>
    public int? FullThreads { get; init; }

    /// <summary>The context a brain holds at full.</summary>
    public int FullContext { get; init; } = 4096;

    /// <summary>How often the outbox is flushed; zero never on a timer.</summary>
    public TimeSpan FlushEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether <see cref="DeviceWolverine.Start"/> also takes the HealthEvents raised before it -
    /// the start-up's own, like the last run's crash read back by the supervisor. On by default;
    /// a test turns it off, because in a test run the ring holds other tests' events.
    /// </summary>
    public bool TakeEarlierEvents { get; init; } = true;

    /// <summary>Injected for testable time.</summary>
    public TimeProvider? Clock { get; init; }
}

/// <summary>The self-healing loop, started and fed: Wolverine on the device.</summary>
public sealed class DeviceWolverine : ILinkHealing, IDisposable
{
    /// <summary>How long a remedy waits for its proof before it is forgotten unproven.</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromDays(1);

    private static readonly HashSet<string> BrainRemedies = new(StringComparer.OrdinalIgnoreCase)
    {
        Remedies.SmallerBrain, Remedies.FewerThreads, Remedies.FewerMemoryBits, Remedies.ShorterContext,
        Remedies.TighterThinking, Remedies.ScalarKernels, Remedies.Memory, Remedies.Cache,
    };

    private static readonly HashSet<string> DownloadRemedies = new(StringComparer.OrdinalIgnoreCase)
    {
        Remedies.Redownload, Remedies.ResumeDownload,
    };

    private readonly WolverineOptions _o;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly bool _ownsLog;
    private readonly int _fixCount;
    private Timer? _flushTimer;
    private int _started;

    private sealed record Pending(string Remedy, string? Subject, DateTimeOffset At);

    /// <summary>Wolverine in this process, once the host started it; null before.</summary>
    public static DeviceWolverine? Current { get; set; }

    /// <summary>Builds Wolverine from <paramref name="options"/>. Nothing runs until <see cref="Start"/>.</summary>
    public DeviceWolverine(WolverineOptions options)
    {
        _o = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateDirectory);
        _clock = options.Clock ?? TimeProvider.System;
        Directory = Path.Combine(options.StateDirectory, "wolverine");
        System.IO.Directory.CreateDirectory(Directory);

        Log = options.Log ?? new SqliteHealingLog("Data Source=" + Path.Combine(Directory, "healing.db"));
        _ownsLog = options.Log is null;
        Knobs = new HealingKnobs(options.FullThreads ?? DeviceContext.Read().BrainThreads, options.FullContext, _clock);
        Outbox = options.Outbox;

        var fixes = EngineFixes.Standard(Knobs, options.Announce, options.ApplyBetweenTurns, options.RunningModel,
                options.HasAnotherBrain, options.Redownload, options.ResumeDownload, _clock)
            .Concat(options.ExtraFixes)
            .ToList();
        _fixCount = fixes.Count;
        Healer = new SelfHealer(options.Analyst ?? NobodyToAsk.Instance, Log, fixes, options.Policy, _clock,
            HealingRules.Standard, remembered: fingerprint => SelfModel.Current?.RemedyThatWorked(fingerprint));
        Healer.Applied += OnApplied;
    }

    /// <summary>Where Wolverine keeps its files: the log, the outbox, the knobs.</summary>
    public string Directory { get; }

    /// <summary>The loop.</summary>
    public SelfHealer Healer { get; }

    /// <summary>Every failure and what became of it.</summary>
    public IHealingLog Log { get; }

    /// <summary>The engine's knobs.</summary>
    public HealingKnobs Knobs { get; }

    /// <summary>The WolverineAPI outbox, when there is one.</summary>
    public WolverineOutbox? Outbox { get; }

    /// <summary>Remedies applied and still waiting for their proof, by fingerprint.</summary>
    public IReadOnlyDictionary<string, string> PendingRemedies =>
        _pending.ToDictionary(p => p.Key, p => p.Value.Remedy, StringComparer.Ordinal);

    /// <summary>
    /// Starts healing: the knobs earlier runs turned down go back in force once SelfModel has put
    /// them back, every HealthEvent from now on is taken - and those raised before this moment
    /// are taken too - the person is told once that reports are on, and the outbox flushes on a
    /// timer.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        BrainTuning.Directory ??= Directory;
        DownloadMirrors.PreferAlternate = id => HealingKnobs.StartsFromAlternate(id);

        // THE KNOBS AN EARLIER RUN TURNED DOWN are still down, but only once SelfModel has put
        // the switches back: projecting before that would write an empty list over them.
        if (SelfModel.Current is { } self)
            _ = self.Loaded.ContinueWith(_ => Project("after this device's memory was read back"), TaskScheduler.Default);
        else
            Project("with no memory of earlier runs");

        IReadOnlyList<HealthEvent> earlier = _o.TakeEarlierEvents ? HealthEvents.Recent : [];   // read before subscribing: none taken twice
        HealthEvents.Raised += OnHealth;
        foreach (var early in earlier.Reverse()) OnHealth(early);                                // oldest first

        // A MODEL THAT LANDED proves a re-download or a resume - whichever of the pull paths
        // fetched it, because every download in this process raises this one event.
        BundleModelLoader.ModelLanded += ModelLanded;

        if (Outbox is { Enabled: true } outbox && outbox.MarkTold())
            EngineFixes.Say(_o.Announce, WolverineSentences.ReportsOn);

        if (Outbox is not null && _o.FlushEvery > TimeSpan.Zero)
            _flushTimer = new Timer(_ => _ = FlushQuietlyAsync(), null, TimeSpan.FromMinutes(1), _o.FlushEvery);

        CircleLog.Info("Heal", $"Wolverine on the device: {_fixCount} safe fixes, rules first; reports "
            + (Outbox is null ? "not wired" : $"{(Outbox.Enabled ? "on" : "off")}, {Outbox.Queued} waiting, "
               + (Outbox.Endpoint is null ? "no endpoint yet - kept on the device" : "endpoint set")));
    }

    // ── what comes in ─────────────────────────────────────────────────────────

    /// <summary>Takes one HealthEvent: upstream, the pending check, then the loop. Returns the loop's record.</summary>
    public async Task<HealingRecord?> HandleAsync(HealthEvent health, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        Take(health);
        return await HealAsync(health, ct).ConfigureAwait(false);
    }

    private void OnHealth(HealthEvent health)
    {
        try
        {
            Take(health);
            _ = HealQuietlyAsync(health);
        }
        catch (Exception ex)
        {
            CircleLog.Warn("Heal", "Wolverine could not take a health event", ex);
        }
    }

    // Everything but the loop: synchronous, so the order of events is the order they are taken.
    private void Take(HealthEvent health)
    {
        Outbox?.Enqueue(health);

        // THE SAME FAILURE CAME BACK while a remedy for it was waiting for its proof: it did not work.
        if (health.Remedy is null && _pending.TryRemove(health.Fingerprint, out var pending))
            Learn(health.Fingerprint, pending.Remedy, worked: false);

        // A REMEDY ALREADY RUN where it happened carries its own outcome.
        if (health is { Remedy: { Length: > 0 } done, Worked: { } worked })
            Learn(health.Fingerprint, done, worked);
    }

    private async Task<HealingRecord?> HealAsync(HealthEvent health, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Healer.HealAsync(ToFailure(health), ct).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task HealQuietlyAsync(HealthEvent health)
    {
        try { await HealAsync(health, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { CircleLog.Warn("Heal", "Wolverine could not heal " + health.Fingerprint, ex); }
    }

    /// <summary>A HealthEvent as the loop takes it: the event itself rides along for the rules and the remedies.</summary>
    public static FailureContext ToFailure(HealthEvent health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return new FailureContext(
            Message: string.IsNullOrWhiteSpace(health.Template) ? health.ErrorType : $"{health.ErrorType}: {health.Template}",
            StackTrace: null,
            Source: health.Area,
            Details: $"{health.Fingerprint}; attempted {health.Attempted}" + (health.Context is { Length: > 0 } c ? "; " + c : string.Empty))
        {
            Event = health,
        };
    }

    // ── what a remedy's proof looks like ──────────────────────────────────────

    private void OnApplied(FailureContext failure, string remedy)
    {
        if (failure.Event is not { } health) return;
        _pending[health.Fingerprint] = new Pending(remedy, health.Subject, _clock.GetUtcNow());
        Outbox?.NoteRemedy(health.Fingerprint, remedy, worked: null);
    }

    /// <summary>
    /// The brain answered: every brain remedy still waiting for its proof has it, and a knob whose
    /// window ran out has just worked at full - so SelfHeal forgets its count.
    /// </summary>
    /// <param name="modelId">The brain that answered.</param>
    public void BrainAnswered(string? modelId)
    {
        try
        {
            var now = _clock.GetUtcNow();
            foreach (var (fingerprint, pending) in _pending.ToArray())
            {
                if (now - pending.At > PendingFor) { _pending.TryRemove(fingerprint, out _); continue; }
                if (BrainRemedies.Contains(pending.Remedy) && _pending.TryRemove(fingerprint, out _))
                    Learn(fingerprint, pending.Remedy, worked: true);
            }

            // THE HALF-OPEN TRY THAT WORKED: tripped before, back at full by the clock, and the
            // brain has now answered that way. Holding the count against it would put the next
            // single trip straight onto a long window.
            var changed = false;
            foreach (var name in SelfHeal.Switch.All.Where(n => HealingKnobs.KnobOf(n) is not null))
                changed |= ForgetIfBackOn(name, now);
            if (!string.IsNullOrWhiteSpace(modelId)) changed |= ForgetIfBackOn(HealingKnobs.BrainPrefix + modelId, now);
            if (changed) Project("a knob worked at full again");
        }
        catch (Exception ex)
        {
            CircleLog.Warn("Heal", "Wolverine could not take an answer as proof", ex);
        }
    }

    /// <summary>A model finished downloading: a download remedy for it has its proof, and its mirror switch is forgotten.</summary>
    public void ModelLanded(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        try
        {
            foreach (var (fingerprint, pending) in _pending.ToArray())
            {
                if (DownloadRemedies.Contains(pending.Remedy)
                    && string.Equals(pending.Subject, modelId, StringComparison.OrdinalIgnoreCase)
                    && _pending.TryRemove(fingerprint, out _))
                    Learn(fingerprint, pending.Remedy, worked: true);
            }
            SelfHeal.Worked(HealingKnobs.MirrorPrefix + modelId);
        }
        catch (Exception ex)
        {
            CircleLog.Warn("Heal", "Wolverine could not take a landed model as proof", ex);
        }
    }

    private static bool ForgetIfBackOn(string name, DateTimeOffset now)
    {
        if (HealingKnobs.Trips(name) == 0 || SelfHeal.IsOff(name, now)) return false;
        SelfHeal.Worked(name);
        return true;
    }

    private void Learn(string fingerprint, string remedy, bool worked)
    {
        SelfModel.Current?.LearnRemedy(fingerprint, remedy, worked);
        Outbox?.NoteRemedy(fingerprint, remedy, worked);
        CircleLog.Info("Heal", $"{remedy} {(worked ? "worked" : "did not work")} for {fingerprint}");
    }

    private void Project(string why)
    {
        try
        {
            var tuning = Knobs.Project();
            CircleLog.Info("Heal", $"the brain's knobs, {why}: {tuning.Describe()}");
        }
        catch (Exception ex)
        {
            CircleLog.Warn("Heal", "the brain's knobs could not be written", ex);
        }
    }

    // ── upstream ──────────────────────────────────────────────────────────────

    /// <summary>Sends what the outbox holds, when it can. Never throws.</summary>
    public async Task<OutboxFlush> FlushAsync(CancellationToken ct = default)
    {
        if (Outbox is null) return OutboxFlush.Off;
        WolverineDeviceClass? device;
        try { device = (_o.DeviceClass ?? WolverineDeviceClass.Read)(); }
        catch { device = null; }
        return await Outbox.FlushAsync(device, _o.AppVersion, ct).ConfigureAwait(false);
    }

    private async Task FlushQuietlyAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { CircleLog.Info("Heal", "Wolverine reports wait: " + ex.GetType().Name); }
    }

    // ── the link ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyList<string>>> RowsAsync(int limit, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var recent = await Log.RecentAsync(Math.Max(50, Math.Clamp(limit, 0, 200)), ct).ConfigureAwait(false);
        var since = now.AddHours(-24);
        int failures = 0, healed = 0, needs = 0;
        DateTimeOffset? lastHealed = null;
        foreach (var r in recent)
        {
            if (r.At >= since) failures++;
            if (r.Outcome == HealingOutcome.Healed) { healed++; lastHealed ??= r.At; }
            if (r.NeedsHuman && !r.Handled) needs++;
        }

        var rows = new List<IReadOnlyList<string>>
        {
            LinkHealingRows.SummaryRow(await AutonomyAsync(ct).ConfigureAwait(false), Outbox?.Enabled ?? false, Outbox?.Queued ?? 0,
                Outbox?.Endpoint is not null, Outbox?.LastSentUtc, failures, healed, needs, lastHealed),
        };

        foreach (var s in SelfHeal.Status(now).Switches)
            rows.Add(LinkHealingRows.SwitchRow(s.Name, s.Light.ToString(), s.Trips, s.Why, s.BackOnUtc));
        foreach (var h in SelfHeal.All().Where(h => !SelfHeal.Switch.All.Contains(h.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var off = h.IsOff(now);
            rows.Add(LinkHealingRows.SwitchRow(h.Name, off ? nameof(SelfHeal.HealthLight.Red) : nameof(SelfHeal.HealthLight.Amber),
                h.Trips, h.Why, off ? h.RetryAfterUtc : null));
        }

        foreach (var knob in BrainTuning.ReadKnobs().Where(k => k.UntilUtc > now))
            rows.Add(LinkHealingRows.KnobRow(knob.Name, knob.Value, knob.UntilUtc));

        foreach (var r in recent.Take(Math.Max(0, limit)))
        {
            // NO MESSAGE CROSSES THE LINK: a record goes as identifiers and the loop's own words.
            var remedy = WolverineReport.Identifier(r.Category) ?? string.Empty;
            rows.Add(LinkHealingRows.ItemRow(r.Id, r.At, WolverineReport.Identifier(r.Source) ?? string.Empty,
                WolverineReport.Identifier(r.Signature) ?? string.Empty, remedy, r.ActionTaken, r.Outcome.ToString(),
                r.NeedsHuman, r.Handled, remedy.Length > 0 ? Remedies.Describe(remedy) : string.Empty));
        }
        return rows;
    }

    /// <inheritdoc />
    public async Task<string?> SetAsync(IReadOnlyDictionary<string, string> args, bool firstParty, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var problems = new List<string>();

        if (args.TryGetValue(LinkHealingRows.ArgAutonomy, out var autonomy))
        {
            var level = autonomy.Trim().ToLowerInvariant();
            if (level is not (AutonomyLevels.Off or AutonomyLevels.SuggestOnly or AutonomyLevels.AutoFixSafe))
                problems.Add("autonomy is off, suggest-only or auto-fix-safe");
            else if (_o.WriteAutonomy is null)
                problems.Add("the autonomy dial is not kept here");
            else
            {
                await _o.WriteAutonomy(level, ct).ConfigureAwait(false);
                CircleLog.Info("Heal", "Wolverine's autonomy set to " + level);
            }
        }

        if (args.TryGetValue(LinkHealingRows.ArgReports, out var reports))
        {
            var on = reports.Trim().ToLowerInvariant() switch { "on" or "1" or "true" => (bool?)true, "off" or "0" or "false" => false, _ => null };
            if (on is null) problems.Add("reports is on or off");
            else if (Outbox is null) problems.Add("reports are not wired here");
            else if (Outbox.Enabled != on)
            {
                Outbox.SetEnabled(on.Value);
                EngineFixes.Say(_o.Announce, on.Value ? WolverineSentences.ReportsOn : WolverineSentences.ReportsOff);
            }
        }

        if (args.TryGetValue(LinkHealingRows.ArgEndpoint, out var endpoint))
        {
            if (!firstParty) problems.Add("only Circle AI's own apps may say where reports go");
            else if (Outbox is null) problems.Add("reports are not wired here");
            else if (!Outbox.SetEndpoint(endpoint)) problems.Add("the endpoint must be an https address");
        }

        if (args.TryGetValue(LinkHealingRows.ArgHandled, out var handled) && !string.IsNullOrWhiteSpace(handled))
            await Log.MarkHandledAsync(handled.Trim(), ct).ConfigureAwait(false);

        if (args.TryGetValue(LinkHealingRows.ArgSelfCheck, out var check) && check == "1")
        {
            // STRAIGHT THROUGH THE REAL LOOP, as DeviceHealing's self-check goes: a cache-shaped
            // failure a ready brain can categorise and the cache fix can cure.
            await Healer.HealAsync(new FailureContext(
                Message: "Self-check: a cached lookup failed and needs refreshing.",
                Source: "self-check",
                Details: "Injected by the Wolverine self-check to exercise the loop end to end."), ct).ConfigureAwait(false);
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>The autonomy dial's stored value; auto-fix-safe when nothing is stored.</summary>
    public async Task<string> AutonomyAsync(CancellationToken ct = default)
    {
        string? stored = null;
        if (_o.ReadAutonomy is not null)
        {
            try { stored = await _o.ReadAutonomy(ct).ConfigureAwait(false); }
            catch { /* read as the default */ }
        }
        return AutonomyLevels.ToWire(AutonomyLevels.Parse(stored));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        HealthEvents.Raised -= OnHealth;
        BundleModelLoader.ModelLanded -= ModelLanded;
        Healer.Applied -= OnApplied;
        _flushTimer?.Dispose();
        if (_ownsLog && Log is IDisposable disposable) disposable.Dispose();
        _one.Dispose();
    }

    /// <summary>The analyst for a host with no brain to ask: every unknown fault is handed on.</summary>
    private sealed class NobodyToAsk : IFailureAnalyst
    {
        public static readonly NobodyToAsk Instance = new();

        public Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default) =>
            Task.FromResult(HealingVerdict.NeedsAHuman("There is no brain here to ask about it."));
    }
}
