// HealingRules.cs
//
// RULES FIRST (S6).
//
// The plan's order: for a fault Wolverine knows, a rule decides - deterministically, with no
// brain - which remedies to try and in what order. Only a fault no rule covers is offered to the
// brain, and the brain may only choose from the list of remedies that exist (Remedies); it never
// invents an action. When the brain ITSELF failed - the inference process died, it ran out of
// memory, it answered nothing - it is not asked about its own failure: rules alone decide, and a
// fault no rule covers is recorded and reported rather than guessed at.
//
// A RULE IS A LADDER. "Out of memory while answering" is not one remedy but an order to try them
// in: fewer working-memory bits first (nothing a person notices), then a shorter context, then a
// smaller brain. Each remedy is a knob with a floor; one that is already at its floor declines,
// and the next on the ladder is tried. What worked here before for the same fingerprint is tried
// first (SelfModel), which is how this device's own experience outranks the shipped order.

using System.Globalization;
using System.Text.RegularExpressions;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core.Logging;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>One rule: which faults it covers, and the remedies to try for them, in order.</summary>
/// <param name="Name">What the rule is about, in a line a person reads in the healing log.</param>
/// <param name="Matches">Whether a fault is one this rule covers.</param>
/// <param name="Ladder">The remedies to try, first to last (names from <see cref="Remedies"/>).</param>
public sealed record HealingRule(string Name, Func<HealthEvent, bool> Matches, IReadOnlyList<string> Ladder);

/// <summary>Wolverine's rules: fault to remedies, with no brain involved.</summary>
public static class HealingRules
{
    /// <summary>The area every failure of the brain itself is raised under.</summary>
    public const string EngineArea = "Engine";

    /// <summary>The area model downloads raise under.</summary>
    public const string ModelsArea = "Models";

    /// <summary>The area tool calls raise under.</summary>
    public const string ToolsArea = "Tools";

    // The ways the inference process dies by a signal while working, by the name the
    // supervisor gives them (EngineFaults.ErrorType).
    private static readonly HashSet<string> Crashes = new(StringComparer.Ordinal)
    {
        nameof(FailureKind.NativeWorkerCrashed), "SegmentationFault", "Abort", "BusError", "FloatingPointError", "Trap",
    };

    private static readonly HashSet<string> Stalls = new(StringComparer.Ordinal)
    {
        nameof(CircleAI.Inference.ModelDownloadException), nameof(IOException), "HttpRequestException", nameof(TimeoutException),
    };

    /// <summary>The rules, in the order they are asked; the first that matches decides.</summary>
    public static IReadOnlyList<HealingRule> Standard { get; } =
    [
        new("an illegal instruction in the engine",
            e => e.ErrorType == "IllegalInstruction",
            [Remedies.ScalarKernels]),

        new("out of memory while the brain was loading",
            e => IsEngine(e) && e.ErrorType == nameof(FailureKind.OutOfMemory) && WhileLoading(e),
            [Remedies.SmallerBrain]),

        // ONLY WHILE IT WAS WORKING. An idle brain the phone took back for memory was the phone
        // needing room for something else; nothing about the brain's knobs caused it.
        new("out of memory while the brain was answering",
            e => IsEngine(e) && e.ErrorType == nameof(FailureKind.OutOfMemory) && WhileAnswering(e),
            [Remedies.FewerMemoryBits, Remedies.ShorterContext, Remedies.SmallerBrain]),

        new("the engine crashed while it worked",
            e => IsEngine(e) && Crashes.Contains(e.ErrorType) && (WhileLoading(e) || WhileAnswering(e)),
            [Remedies.FewerThreads, Remedies.ScalarKernels, Remedies.SmallerBrain]),

        new("the brain stopped making progress",
            e => IsEngine(e) && e.ErrorType == nameof(FailureKind.TimedOut) && WhileAnswering(e),
            [Remedies.ShorterContext, Remedies.FewerThreads, Remedies.SmallerBrain]),

        new("a reply thought its whole length away",
            e => IsEngine(e) && e.ErrorType == "NoAnswer",
            [Remedies.TighterThinking]),

        new("a model file failed its check",
            e => e.ErrorType is nameof(CircleAI.Inference.ModelIntegrityException) or nameof(FailureKind.ModelFilesDamaged),
            [Remedies.Redownload]),

        new("a download stalled past every retry",
            e => e.Area == ModelsArea && Stalls.Contains(e.ErrorType),
            [Remedies.ResumeDownload]),

        new("a tool call could not be read",
            e => e.ErrorType == "MalformedToolCall",
            [Remedies.ToolCallRetry]),

        new("the process ran short of memory",
            e => e.ErrorType == nameof(OutOfMemoryException),
            [Remedies.Memory]),
    ];

    /// <summary>The first rule that covers <paramref name="health"/>, or null.</summary>
    public static HealingRule? For(HealthEvent? health, IReadOnlyList<HealingRule>? rules = null)
    {
        if (health is null) return null;
        foreach (var rule in rules ?? Standard)
        {
            try { if (rule.Matches(health)) return rule; }
            catch { /* a rule that throws covers nothing */ }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="health"/> is a failure of the brain itself - which the brain is
    /// never asked to diagnose.
    /// </summary>
    public static bool IsTheBrainsOwn(HealthEvent? health) => health is not null && IsEngine(health);

    private static bool IsEngine(HealthEvent e) => string.Equals(e.Area, EngineArea, StringComparison.Ordinal);

    private static bool WhileAnswering(HealthEvent e) =>
        e.Site.EndsWith('.' + nameof(WorkPhase.Prefill), StringComparison.Ordinal)
        || e.Site.EndsWith('.' + nameof(WorkPhase.Decode), StringComparison.Ordinal);

    private static bool WhileLoading(HealthEvent e) =>
        e.Site.EndsWith('.' + nameof(WorkPhase.Load), StringComparison.Ordinal)
        || e.Site.EndsWith('.' + nameof(WorkPhase.Verify), StringComparison.Ordinal)
        || e.Site.EndsWith('.' + nameof(WorkPhase.Warm), StringComparison.Ordinal)
        || e.Site.EndsWith('.' + nameof(WorkPhase.Startup), StringComparison.Ordinal);
}

/// <summary>Names for the ways the inference process ends, finer than <see cref="FailureKind"/> where the evidence allows.</summary>
public static partial class EngineFaults
{
    /// <summary>
    /// The error type a worker's failure is reported under: the signal's own name when the evidence
    /// carries one ("signal=4" is an illegal instruction), else the failure's kind.
    /// </summary>
    /// <remarks>
    /// THE KIND IS TOO COARSE FOR A REMEDY. NativeWorkerCrashed covers an illegal instruction - which
    /// scalar kernels cure - and a segmentation fault - which they may not - and the two need
    /// different rules and different fingerprints at WolverineAPI.
    /// </remarks>
    public static string ErrorType(FailureKind kind, string? evidence)
    {
        if (kind == FailureKind.NativeWorkerCrashed && SignalOf(evidence) is { } signal)
        {
            return signal switch
            {
                4 => "IllegalInstruction",
                5 => "Trap",
                6 => "Abort",
                7 => "BusError",
                8 => "FloatingPointError",
                11 => "SegmentationFault",
                _ => kind.ToString(),
            };
        }
        return kind.ToString();
    }

    /// <summary>The signal number in a failure's evidence ("signal=11"), or null.</summary>
    public static int? SignalOf(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence)) return null;
        var match = Signal().Match(evidence);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>The site a worker's failure happened at: the inference worker, in the phase it was in.</summary>
    public static string Site(WorkPhase phase) => "InferenceWorker." + phase;

    [GeneratedRegex(@"signal=(\d+)")]
    private static partial Regex Signal();
}
