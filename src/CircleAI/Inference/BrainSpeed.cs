// BrainSpeed.cs
//
// HOW FAST A BRAIN ANSWERS ON THIS DEVICE - MEASURED, OR ESTIMATED, AND SAID. NEVER A GATE.
//
// This used to decide brains too: below four tokens a second a brain was "too slow to talk with"
// and passed over, whatever its rank. On the P30, where the C# 2B reads at 1.7, that handed every
// pick back to MNN - the selector, setup, the upgrade path, the stand-in, the brain kept while a
// candidate waits, the balance, the validation and the picture route. The owner ruled it out on
// 2026-11-04: "no rule is valid - if these are cars then a mini cooper must do the best it can do
// with what it has as resources and an F1 car must do the best it can do with what it has as
// resources." Brains are chosen by ModelChoice.Best (the Bonsai 2 fleet first, then rank); how
// fast one answers here is the engine's to make the most of, and this says what it is.
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

    /// <summary>A speed, and the model it was measured on.</summary>
    /// <param name="TokensPerSecond">Decode speed.</param>
    /// <param name="From">The model measured: the same one, or the sibling it was scaled from.</param>
    public sealed record Estimate(double TokensPerSecond, string From);
}
