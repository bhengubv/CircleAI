// BrainSpeed.cs
//
// WHETHER A BRAIN IS QUICK ENOUGH TO TALK WITH ON THIS DEVICE - KNOWN BEFORE IT IS FETCHED.
//
// The selector ranked by quality alone, so on the P30 on 2026-11-02 it wanted Bonsai 2 4B:
// "the selector wanted Ternary-Bonsai-2-4B, which is not on this phone". On that phone the
// C# engine had just answered with the 2B at 1.47 tokens a second, so the 4B - twice the
// weights to read for every token - would answer at about 0.7. An assistant that takes a
// minute to say one sentence is not the better brain, whatever its rank, and fetching a
// gigabyte to find that out is the wrong way to learn it.
//
// SPEED IS MEASURED, NOT DECLARED. SelfModel keeps the decode speed of every model that has
// answered here. A model that has never run here is estimated from one that has, on the same
// engine: producing a token means reading the weights once, so speed goes with the inverse of
// their size. No measurement on that engine at all means no estimate - unknown, never assumed.
//
// AND READING THEM IS NOT EQUALLY QUICK IN EVERY PACKING (OPEN-GAPS C8). The C# engine reads
// PQ2_0 about 3.6 times faster than PTQ1_0 on the desktop that built it and a little slower on
// the P30, so a PQ2_0 brain scaled from a PTQ1_0 one by size alone was ruled too slow on the very
// machine it ran fastest on. A brain is estimated from one in its own packing where one has run;
// across packings, by how fast this device's kernels read each (KernelThreads, measured once).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>How fast a brain answers on this device, measured or estimated.</summary>
public static class BrainSpeed
{
    /// <summary>The slowest a brain may answer and still be talked with: tokens a second.</summary>
    /// <remarks>
    /// FOUR, A DEFAULT FOR THE OWNER TO MOVE. A spoken answer is up to 80 tokens, so four a
    /// second is twenty seconds of speaking pace - the slowest that is still a conversation.
    /// Below it a brain is passed over for a quicker one, however it is ranked.
    /// </remarks>
    public const double InteractiveTokensPerSecond = 4.0;

    /// <summary>Measured speeds from somewhere other than the device's memory: a test, a host without one.</summary>
    public static Func<string, double?>? Measured { get; set; }

    /// <summary>
    /// How fast this device's kernels read each packing; null reads what the device measured
    /// (<see cref="Managed.Kernels.KernelThreads.Remembered"/>). A setting so a test decides.
    /// </summary>
    public static Func<Managed.Kernels.KernelThreads.Choice?>? Kernels { get; set; }

    /// <summary>The decode speed this device has measured for a model, or null.</summary>
    public static double? MeasuredOf(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        try
        {
            var hooked = Measured?.Invoke(modelId);
            if (hooked is > 0) return hooked;
            return CircleAI.SelfKnowledge.SelfModel.Current?.About(modelId).TokensPerSecond is > 0 and var known ? known : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The model's speed here: measured if it has run, otherwise estimated from the nearest-sized
    /// model that has run on the same engine, otherwise null.
    /// </summary>
    /// <param name="entry">The model asked about.</param>
    /// <param name="catalogue">The models it may be estimated from.</param>
    public static Estimate? Of(ModelEntry entry, IEnumerable<ModelEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(catalogue);

        if (MeasuredOf(entry.Name) is { } measured) return new Estimate(measured, entry.Name);
        if (entry.TotalBytes <= 0) return null;

        var runs = new List<(ModelEntry Model, double Speed)>();
        foreach (var other in catalogue)
        {
            if (other.Engine != entry.Engine || other.TotalBytes <= 0
                || string.Equals(other.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (MeasuredOf(other.Name) is { } speed) runs.Add((other, speed));
        }
        if (runs.Count == 0) return null;

        // ITS OWN PACKING WHERE ONE HAS RUN; ANOTHER ONLY WHEN NONE HAS, scaled by the kernels.
        var packing = PackingOf(entry);
        var alike = runs.Where(r => PackingOf(r.Model) == packing).ToList();
        var kernels = alike.Count == 0 ? KernelsHere() : null;

        Estimate? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var (other, speed) in alike.Count > 0 ? alike : runs)
        {
            var distance = Math.Abs(Math.Log((double)other.TotalBytes / entry.TotalBytes));
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            var factor = kernels?.PackingFactor(other.Quantization, entry.Quantization) ?? 1.0;
            nearest = new Estimate(speed * other.TotalBytes / entry.TotalBytes * factor, other.Name);
        }
        return nearest;
    }

    // The C# engine's packing of a row - the kernel its weights are read with - or null for any other engine.
    private static string? PackingOf(ModelEntry entry) =>
        entry.Engine == ModelEngine.Managed ? Managed.Kernels.KernelThreads.Packing(entry.Quantization) : null;

    // What this device measured of its kernels; null when nothing was, or it could not be read.
    private static Managed.Kernels.KernelThreads.Choice? KernelsHere()
    {
        try { return Kernels is { } hooked ? hooked() : Managed.Kernels.KernelThreads.Remembered(); }
        catch { return null; }
    }

    /// <summary>Whether the model is known, or estimated, to be too slow to talk with here.</summary>
    /// <remarks>Unknown is not too slow: a model nothing has measured is tried, never ruled out.</remarks>
    public static bool TooSlow(ModelEntry entry, IEnumerable<ModelEntry> catalogue)
        => Of(entry, catalogue) is { } estimate && estimate.TokensPerSecond < InteractiveTokensPerSecond;

    /// <summary>
    /// The best brain of a set this device can hold: quick enough to talk with first, then by
    /// rank; or, when every one of them is too slow, the fastest - then speed is what decides.
    /// Null for an empty set.
    /// </summary>
    /// <remarks>
    /// ONE RULE FOR EVERY PLACE THAT PICKS A BRAIN - the selector, setup's brain slot and the
    /// upgrade path - so the brain a screen names, the one setup fetches and the one the
    /// service loads cannot disagree about speed the way they once disagreed about memory.
    /// </remarks>
    public static ModelEntry? Pick(IReadOnlyList<ModelEntry> set, IReadOnlyList<ModelEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(catalogue);
        if (set.Count == 0) return null;

        var quick = set.Where(e => !TooSlow(e, catalogue)).ToList();
        return quick.Count > 0
            ? quick.OrderByDescending(e => e.QualityRank).ThenBy(e => e.MinRamGb).First()
            : set.OrderByDescending(e => Of(e, catalogue)?.TokensPerSecond ?? 0).ThenByDescending(e => e.QualityRank).First();
    }

    /// <summary>
    /// The brain to answer with while the device is under pressure, of those smaller than the one
    /// answering: the one <see cref="Pick"/> takes - unless it is too slow to talk with and the one
    /// answering is not. Null keeps the one answering, lean; null too for an empty set.
    /// </summary>
    /// <remarks>
    /// SMALLER IS NOT BETTER FOR BEING SMALLER (OPEN-GAPS E33). On the P30 the C# 2B is the smaller of
    /// its two brains - 429 MB against the 0.8B's 548 - and answers at 1.7 tokens a second, so the
    /// balance stepping down to it turned a conversation into a wait. The rung after this one lets the
    /// brain go while nobody is asking, if the pressure holds; on the P30 the quick brain kept lean until
    /// then holds only 118 MB more than the slow one would have. When neither is quick enough, memory decides.
    /// </remarks>
    /// <param name="current">The brain answering now.</param>
    /// <param name="smaller">The brains on the device smaller than it that run here.</param>
    /// <param name="catalogue">The models speeds may be estimated from.</param>
    public static ModelEntry? StepDown(ModelEntry current, IReadOnlyList<ModelEntry> smaller, IReadOnlyList<ModelEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(current);
        var pick = Pick(smaller, catalogue);
        return pick is not null && TooSlow(pick, catalogue) && !TooSlow(current, catalogue) ? null : pick;
    }

    /// <summary>One sentence on why a model is too slow here, or null when it is not.</summary>
    public static string? WhyTooSlow(ModelEntry entry, IEnumerable<ModelEntry> catalogue)
    {
        if (Of(entry, catalogue) is not { } estimate || estimate.TokensPerSecond >= InteractiveTokensPerSecond) return null;
        var how = estimate.From == entry.Name
            ? "answered"
            : $"would answer (estimated from {estimate.From})";
        return string.Create(CultureInfo.InvariantCulture,
            $"{entry.Name} {how} at {estimate.TokensPerSecond:F1} tokens a second here, below the {InteractiveTokensPerSecond:F0} a conversation needs");
    }

    /// <summary>A speed, and the model it was measured on.</summary>
    /// <param name="TokensPerSecond">Decode speed.</param>
    /// <param name="From">The model measured: the same one, or the sibling it was scaled from.</param>
    public sealed record Estimate(double TokensPerSecond, string From);
}
