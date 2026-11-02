// EngineFixes.cs
//
// THE SAFE FIXES FOR THE C# ENGINE'S AND THE BRAIN PLAN'S KNOBS, AND FOR DOWNLOADS (S6).
//
// Each one is safe and reversible in the sense ISafeFix demands: it turns a knob one notch for
// SelfHeal's window and the clock turns it back; it steps down from a brain for that window; it
// fetches a damaged file again; it resumes a stalled download from its other address. None of
// them touches code, money or anything a person chose.
//
// EACH IS CHECKED AFTER IT IS APPLIED. A knob reads BrainTuning back from disk and succeeds only
// if the value the brain will be built with is the one it set - a projection that did not land
// is a fix that did not take, and the loop hands it on. Whether the knob CURED the fault is the
// second check, which only the next run can give: DeviceWolverine holds the remedy as pending,
// a completed answer (or a landed download) proves it, the same fault returning disproves it,
// and either way the device remembers (SelfModel).
//
// A KNOB AT ITS FLOOR DECLINES, so the rule's ladder moves on to the next remedy - fewer bits,
// then a shorter context, then a smaller brain - rather than tripping the same knob forever.

using CircleAI.Assistant;
using CircleAI.Core.Logging;
using CircleAI.Inference;
using CircleAI.SelfKnowledge;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>One of the engine's knobs, turned one notch further for SelfHeal's window.</summary>
public sealed class KnobFix : ISafeFix
{
    private readonly string _switch;
    private readonly HealingKnobs _knobs;
    private readonly string? _sentence;
    private readonly Action<string>? _announce;
    private readonly Func<Task>? _applyBetweenTurns;
    private readonly TimeProvider _clock;

    /// <param name="category">The remedy's name, from <see cref="Remedies"/>.</param>
    /// <param name="switchName">The SelfHeal switch whose trips are the knob's notches.</param>
    /// <param name="knobs">The ladders and the projection.</param>
    /// <param name="sentence">What the person is told, or null for a knob nobody would notice.</param>
    /// <param name="announce">Where a sentence goes (CircleNeuronLinkService.Decided on the phone).</param>
    /// <param name="applyBetweenTurns">Lets the new knob take effect at the next turn - releasing an idle brain.</param>
    /// <param name="clock">Injected for testable time.</param>
    public KnobFix(string category, string switchName, HealingKnobs knobs, string? sentence = null,
        Action<string>? announce = null, Func<Task>? applyBetweenTurns = null, TimeProvider? clock = null)
    {
        Category = category ?? throw new ArgumentNullException(nameof(category));
        _switch = switchName ?? throw new ArgumentNullException(nameof(switchName));
        _knobs = knobs ?? throw new ArgumentNullException(nameof(knobs));
        _sentence = sentence;
        _announce = announce;
        _applyBetweenTurns = applyBetweenTurns;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Category { get; }

    /// <summary>The SelfHeal switch this knob turns.</summary>
    public string Switch => _switch;

    /// <inheritdoc />
    public async Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        if (_knobs.AtFloor(_switch, now))
        {
            CircleLog.Info("Heal", $"{Category}: {_switch} is already at its limit");
            return false;
        }

        var healed = SelfHeal.Trip(_switch, EngineFixes.Why(failure, Category), now);
        _knobs.Project(now);

        // CHECKED: what the brain will be built with, read back from where it reads it.
        var wanted = _knobs.ValueAt(_switch, healed.Trips);
        var inForce = HealingKnobs.InForce(_switch, BrainTuning.Current(now));
        if (wanted is null || !string.Equals(wanted, inForce, StringComparison.Ordinal))
        {
            CircleLog.Warn("Heal", $"{Category}: {_switch} was set to {wanted ?? "nothing"} but the brain would be built with {inForce ?? "nothing"}");
            return false;
        }

        CircleLog.Info("Heal", $"{Category}: {_switch} at {wanted} until {healed.RetryAfterUtc:yyyy-MM-dd HH:mm}Z (trip {healed.Trips})");
        if (_sentence is not null) EngineFixes.Say(_announce, _sentence);
        await EngineFixes.BetweenTurns(_applyBetweenTurns).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Steps down from the brain that failed to the next one this device holds, for SelfHeal's window.</summary>
public sealed class SmallerBrainFix : ISafeFix
{
    private readonly HealingKnobs _knobs;
    private readonly Func<string?>? _runningModel;
    private readonly Func<string, bool> _hasAnother;
    private readonly Action<string>? _announce;
    private readonly Func<Task>? _applyBetweenTurns;
    private readonly TimeProvider _clock;

    /// <param name="knobs">The projection the supervisor reads.</param>
    /// <param name="runningModel">The brain running now, when the failure does not name one.</param>
    /// <param name="hasAnother">Whether this device holds another brain to step down to (never below the floor).</param>
    /// <param name="announce">Where the sentence goes.</param>
    /// <param name="applyBetweenTurns">Lets the step take effect at the next turn.</param>
    /// <param name="clock">Injected for testable time.</param>
    public SmallerBrainFix(HealingKnobs knobs, Func<string?>? runningModel, Func<string, bool> hasAnother,
        Action<string>? announce = null, Func<Task>? applyBetweenTurns = null, TimeProvider? clock = null)
    {
        _knobs = knobs ?? throw new ArgumentNullException(nameof(knobs));
        _runningModel = runningModel;
        _hasAnother = hasAnother ?? throw new ArgumentNullException(nameof(hasAnother));
        _announce = announce;
        _applyBetweenTurns = applyBetweenTurns;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Category => Remedies.SmallerBrain;

    /// <inheritdoc />
    public async Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        var model = failure.Event?.Subject ?? Ask(_runningModel);
        if (string.IsNullOrWhiteSpace(model)) return false;

        var now = _clock.GetUtcNow();
        var name = HealingKnobs.BrainPrefix + model;
        if (SelfHeal.IsOff(name, now)) return false;   // already stepped down from it
        bool another;
        try { another = _hasAnother(model); }
        catch { another = false; }
        if (!another)
        {
            CircleLog.Info("Heal", $"{Category}: {model} is the smallest brain this device holds");
            return false;
        }

        var healed = SelfHeal.Trip(name, EngineFixes.Why(failure, Category), now);
        _knobs.Project(now);

        // CHECKED: the supervisor reads this same file when it chooses the next brain.
        if (!BrainTuning.Current(now).Avoids(model))
        {
            CircleLog.Warn("Heal", $"{Category}: stepping down from {model} did not reach the brain plan");
            return false;
        }

        CircleLog.Info("Heal", $"{Category}: stepped down from {model} until {healed.RetryAfterUtc:yyyy-MM-dd HH:mm}Z (trip {healed.Trips})");
        EngineFixes.Say(_announce, WolverineSentences.SmallerBrain);
        await EngineFixes.BetweenTurns(_applyBetweenTurns).ConfigureAwait(false);
        return true;
    }

    private static string? Ask(Func<string?>? read)
    {
        try { return read?.Invoke(); }
        catch { return null; }
    }
}

/// <summary>Fetches a model again after one of its files failed its hash - once per SelfHeal window.</summary>
public sealed class RedownloadFix : ISafeFix
{
    private readonly Func<string, bool> _fetchAgain;
    private readonly Action<string>? _announce;
    private readonly TimeProvider _clock;

    /// <param name="fetchAgain">Starts the fetch of a model; false when it cannot start.</param>
    /// <param name="announce">Where the sentence goes.</param>
    /// <param name="clock">Injected for testable time.</param>
    public RedownloadFix(Func<string, bool> fetchAgain, Action<string>? announce = null, TimeProvider? clock = null)
    {
        _fetchAgain = fetchAgain ?? throw new ArgumentNullException(nameof(fetchAgain));
        _announce = announce;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Category => Remedies.Redownload;

    /// <inheritdoc />
    public Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        var model = failure.Event?.Subject;
        if (string.IsNullOrWhiteSpace(model)) return Task.FromResult(false);

        // ONE RE-DOWNLOAD PER WINDOW, so a mirror serving a bad file is not fetched in a loop at
        // somebody's expense: a second mismatch inside the window is handed on instead.
        var now = _clock.GetUtcNow();
        var name = HealingKnobs.RedownloadPrefix + model;
        if (SelfHeal.IsOff(name, now)) return Task.FromResult(false);

        bool started;
        try { started = _fetchAgain(model); }
        catch { started = false; }
        if (!started) return Task.FromResult(false);

        SelfHeal.Trip(name, EngineFixes.Why(failure, Category), now);
        CircleLog.Info("Heal", $"{Category}: fetching {model} again; its hash is checked as it lands");
        EngineFixes.Say(_announce, WolverineSentences.Redownloading);
        return Task.FromResult(true);
    }
}

/// <summary>Resumes a stalled download, starting from its other address.</summary>
public sealed class ResumeDownloadFix : ISafeFix
{
    private readonly Func<string, bool> _resume;
    private readonly TimeProvider _clock;

    /// <param name="resume">Resumes a model's download when the device is online; false when it cannot.</param>
    /// <param name="clock">Injected for testable time.</param>
    public ResumeDownloadFix(Func<string, bool> resume, TimeProvider? clock = null)
    {
        _resume = resume ?? throw new ArgumentNullException(nameof(resume));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Category => Remedies.ResumeDownload;

    /// <inheritdoc />
    public Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
    {
        var model = failure.Event?.Subject;
        if (string.IsNullOrWhiteSpace(model)) return Task.FromResult(false);

        var now = _clock.GetUtcNow();

        // THE OTHER ADDRESS FIRST: an odd trip starts from the fallback, an even one from the
        // usual address, so each resume tries the one that did not just stall.
        var healed = SelfHeal.Trip(HealingKnobs.MirrorPrefix + model, EngineFixes.Why(failure, Category), now);
        bool resumed;
        try { resumed = _resume(model); }
        catch { resumed = false; }

        CircleLog.Info("Heal", resumed
            ? $"{Category}: resuming {model} from its {(healed.Trips % 2 == 1 ? "other" : "usual")} address; the bytes on disk are kept"
            : $"{Category}: {model} could not be resumed now");
        return Task.FromResult(resumed);
    }
}

/// <summary>What the fixes share.</summary>
public static class EngineFixes
{
    /// <summary>The reason a switch records: the failure's fingerprint, which names it without a word anybody said.</summary>
    internal static string Why(FailureContext failure, string remedy) =>
        $"{remedy} for {failure.Event?.Fingerprint ?? failure.Source ?? "a failure"}";

    internal static void Say(Action<string>? announce, string sentence)
    {
        try { announce?.Invoke(sentence); }
        catch (Exception ex) { CircleLog.Warn("Heal", "could not say what Wolverine changed: " + ex.GetType().Name); }
    }

    internal static async Task BetweenTurns(Func<Task>? apply)
    {
        if (apply is null) return;
        try { await apply().ConfigureAwait(false); }
        catch (Exception ex) { CircleLog.Warn("Heal", "the change waits for the next start: " + ex.GetType().Name); }
    }

    /// <summary>
    /// Every engine and brain-plan fix, in the order the rules name them: the knobs, the smaller
    /// brain, and the download fixes when the host can fetch.
    /// </summary>
    public static IReadOnlyList<ISafeFix> Standard(
        HealingKnobs knobs,
        Action<string>? announce = null,
        Func<Task>? applyBetweenTurns = null,
        Func<string?>? runningModel = null,
        Func<string, bool>? hasAnotherBrain = null,
        Func<string, bool>? fetchAgain = null,
        Func<string, bool>? resumeDownload = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(knobs);
        var fixes = new List<ISafeFix>
        {
            new KnobFix(Remedies.FewerMemoryBits, SelfHeal.Switch.WorkingMemoryBits, knobs, null, announce, applyBetweenTurns, clock),
            new KnobFix(Remedies.ShorterContext, SelfHeal.Switch.LongContext, knobs, WolverineSentences.ShorterContext, announce, applyBetweenTurns, clock),
            new KnobFix(Remedies.FewerThreads, SelfHeal.Switch.BrainThreads, knobs, WolverineSentences.FewerThreads, announce, applyBetweenTurns, clock),
            new KnobFix(Remedies.TighterThinking, SelfHeal.Switch.ThinkingBudget, knobs, WolverineSentences.TighterThinking, announce, applyBetweenTurns, clock),
            new KnobFix(Remedies.ScalarKernels, SelfHeal.Switch.VectorKernels, knobs, WolverineSentences.ScalarKernels, announce, applyBetweenTurns, clock),
        };
        if (hasAnotherBrain is not null)
            fixes.Add(new SmallerBrainFix(knobs, runningModel, hasAnotherBrain, announce, applyBetweenTurns, clock));
        if (fetchAgain is not null) fixes.Add(new RedownloadFix(fetchAgain, announce, clock));
        if (resumeDownload is not null) fixes.Add(new ResumeDownloadFix(resumeDownload, clock));
        return fixes;
    }
}
