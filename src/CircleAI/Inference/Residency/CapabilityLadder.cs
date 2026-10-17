// CapabilityLadder.cs
//
// THE PHONE FINDS ITS OWN CEILING, INSTEAD OF BEING TOLD A LOWER ONE.
//
// On 2026-10-10 a 3.87 GB handset ended the day running a 0.6B model. Nothing
// was broken. Every individual decision was defensible: a model killed the
// service, so the fit rule tightened; the tighter rule refused the next model
// down, so it tightened again. Each step was a correct response to a real
// crash, and the sum of them was a product that does less than the hardware
// can.
//
// THE DEFECT IS THAT THE SYSTEM COULD ONLY LEARN "NO". CrashVerdict records
// what killed the device and nothing records what worked, so every piece of
// evidence pushed in one direction. A loop with only a brake coasts to a stop.
//
// This is the accelerator. Given what this device has actually tried, it
// answers one question - what should it attempt next - with three rules:
//
//   * a model that WORKED here is the floor, and the floor never drops;
//   * reach one step above the floor, not to the top, so a failure costs one
//     attempt rather than a crash loop;
//   * a model that was killed is retried only when the phone is materially
//     roomier than it was that day, and never after it has killed twice.
//
// It converges. On a device with headroom it climbs until something fails and
// then stops; on a device without, it settles on the first rung and stays
// there. Either way the answer comes from attempts on THIS phone rather than
// from a constant somebody picked.
//
// IT PROPOSES; IT DOES NOT LOAD. Everything here is pure - a list in, an id
// out - so the whole policy is testable on both framework legs with no device,
// which is the lesson ReadinessWait and DecisionLog were moved out for.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference.Residency;

/// <summary>What this device should try next, from what it has already tried.</summary>
public static class CapabilityLadder
{
    /// <summary>
    /// How much roomier the phone must be before a killed model is retried.
    /// </summary>
    /// <remarks>
    /// MEASURED, NOT CHOSEN. The P30 killed Qwen3.5-2B with 982 MB free and ran
    /// it earlier the same day with more headroom, so the retry threshold has
    /// to sit above ordinary minute-to-minute drift and below a genuine change
    /// of circumstance. Half a gigabyte is both: it is far more than the noise
    /// in MemAvailable on that device (which moved between 0.62 and 1.28 GB
    /// while idle) and it is a real difference in what can be held.
    /// </remarks>
    public const double RetryNeedsExtraGb = 0.5;

    /// <summary>Two kills is a verdict; one is a bad moment.</summary>
    public const int KillsBeforeGivingUp = 2;

    /// <summary>A rate below this is not worth having, whatever the quality.</summary>
    /// <remarks>
    /// Natural speech is about 4-5 tokens a second. One is well under
    /// conversational but still usable for text; below that a person is waiting
    /// on a machine rather than talking to it, and a better model is not a
    /// better experience.
    /// </remarks>
    public const double UselesslySlowTokensPerSecond = 1.0;

    /// <summary>
    /// The best model this device has actually run, or null if it never has.
    /// </summary>
    /// <remarks>
    /// THE FLOOR, AND IT ONLY EVER RISES. Evidence that something worked cannot
    /// be undone by a later bad moment - a phone that ran a 2B once can run it
    /// again when it is next quiet, and dropping the floor on one kill is
    /// precisely the ratchet-downwards this type exists to stop.
    /// </remarks>
    public static string? BestKnownGood(
        IReadOnlyList<ModelEntry> candidates, IReadOnlyList<ModelTrial> trials)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(trials);

        var worked = trials
            .Where(t => t.Worked && t.TokensPerSecond >= UselesslySlowTokensPerSecond)
            .Select(t => t.ModelId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(m => worked.Contains(m.Name))
            .OrderByDescending(m => m.QualityRank)
            .FirstOrDefault()?.Name;
    }

    /// <summary>
    /// The next model worth attempting, or null when this device has settled.
    /// </summary>
    /// <param name="candidates">
    /// What could be run at all - already filtered for whether the bytes are
    /// here and whether the device could ever hold it. This type decides
    /// AMBITION, not eligibility.
    /// </param>
    /// <param name="trials">Everything this device has attempted.</param>
    /// <param name="probe">The device as it is right now.</param>
    /// <remarks>
    /// ONE STEP, NEVER A LEAP. Returning the best candidate outright would put
    /// the heaviest model on a phone that has never run anything, which is the
    /// 22.8 GB MoE on a 7.6 GB handset that took a dozen system apps down with
    /// it. Climbing one rung means a wrong guess costs a single restart.
    /// <para>
    /// NULL IS A REAL ANSWER and means "stay where you are": everything above
    /// the floor has been tried and failed, or nothing is eligible. A caller
    /// gets the floor from <see cref="BestKnownGood"/> and runs that.
    /// </para>
    /// </remarks>
    public static string? NextToTry(
        IReadOnlyList<ModelEntry> candidates,
        IReadOnlyList<ModelTrial> trials,
        DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(probe);

        var floor = BestKnownGood(candidates, trials);
        var floorRank = floor is null
            ? int.MinValue
            : candidates.First(m => m.Name.Equals(floor, StringComparison.OrdinalIgnoreCase)).QualityRank;

        // Only ever upward. A candidate at or below something already proven is
        // not an experiment, it is a step backwards.
        var above = candidates
            .Where(m => m.QualityRank > floorRank)
            .OrderBy(m => m.QualityRank)        // the SMALLEST step up, not the biggest
            .ToList();

        foreach (var m in above)
        {
            if (IsWorthAttempting(m.Name, trials, probe)) return m.Name;
        }

        return null;
    }

    /// <summary>Whether this device should spend an attempt on this model now.</summary>
    public static bool IsWorthAttempting(
        string modelId, IReadOnlyList<ModelTrial> trials, DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(probe);
        if (string.IsNullOrWhiteSpace(modelId)) return false;

        var mine = trials
            .Where(t => t.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mine.Count == 0) return true;             // never tried: find out

        // Already works here. Nothing to learn by trying it again.
        if (mine.Any(t => t.Worked)) return false;

        // Too slow, or refused outright, is a property of this pairing and
        // waiting will not change it. Memory is the only failure that might.
        if (mine.Any(t => t.Outcome is TrialOutcome.TooSlow or TrialOutcome.Refused)) return false;

        var kills = mine.Count(t => t.FailedForNow);
        if (kills >= KillsBeforeGivingUp) return false;

        // Killed once. Retry only if the phone is genuinely roomier than it was
        // then - otherwise this is the same attempt with the same answer, and
        // repeating it costs a person their assistant for another restart.
        //
        // RAW FREE MEMORY ON BOTH SIDES. The first version compared
        // probe.UsableRamGb - which is MemAvailable already discounted by
        // RamFitHeadroom - against a trial's raw FreeRamGbAtStart, so "is the
        // phone roomier than when it died" was being asked in two different
        // units and answered wrongly by the size of the discount. A trial
        // records what was actually free; so must this.
        var freeNowGb = probe.RamAvailableBytes / DeviceProbe.BytesPerGb;
        var worstCase = mine.Where(t => t.FailedForNow)
                            .Max(t => t.FreeRamGbAtStart);

        return freeNowGb >= worstCase + RetryNeedsExtraGb;
    }
}
