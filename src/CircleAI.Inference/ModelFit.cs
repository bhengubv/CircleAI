// ModelFit.cs
//
// ONE ANSWER TO "DOES THIS MODEL FIT THIS DEVICE".
//
// There were six. DeviceAwareModelSelector asked it twice, SpeechModelSelector
// three times, and DeviceModelAssessor once — and the assessor's was the only one
// that knew about mmap. So the catalogue said Qwen2.5-VL-3B runs on a P30 (2.74 GB
// of weights paged, 1.16 GB resident) while the selector that actually picks a
// vision model said SmolVLM-256M, because it compared a declared 3.9 GB against
// ~1.19 GB usable and stopped there. Same phone, same catalogue, two answers.
//
// CLAUDE.md names this exactly: "One fact with two owners always ends up with two
// answers... When adding something a screen displays, find who else displays it and
// make them read the same source." This is that source.
//
// WHY THE MMAP TERM MATTERS RATHER THAN BEING A DETAIL: without it the gate refuses
// models this repo has PROVEN on the reference phone. Qwen2.5-3B declares 3.1 GB and
// weighs 2.37 GB; eagerly that is 3.1 GB and it is refused, but it has been measured
// loading and generating on a P30 Lite at ~800 MB resident. A fit rule that cannot
// see paging is not conservative, it is wrong.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Inference;

/// <summary>Whether a catalogued model can run on a device, and what it costs there.</summary>
public static class ModelFit
{
    /// <summary>
    /// Floating-point slack, so a model whose requirement equals what is available to
    /// the last bit still counts as fitting.
    /// </summary>
    public const double Eps = 0.0001;

    /// <summary>
    /// Resident floor for a memory-mapped model: <b>0.6 GB</b>.
    /// </summary>
    /// <remarks>
    /// Grounded in measurement, not chosen: Qwen2.5-3B, 2.4 GB of weights, ran on a
    /// P30 Lite at roughly 800 MB resident once weight-mmap was on (2026-09-22).
    /// </remarks>
    public const double MmapResidentFloorGb = 0.6;

    /// <summary>Resident memory when the weights are loaded eagerly: the whole file.</summary>
    public static double EagerGb(ModelEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var weightGb = e.TotalBytes > 0 ? e.TotalBytes / DeviceProbe.BytesPerGb : 0.0;
        return Math.Max(e.MinRamGb, weightGb);
    }

    /// <summary>Resident memory when the weights are memory-mapped and paged from disk.</summary>
    /// <remarks>
    /// For an MoE whose <see cref="ModelEntry.MinRamGb"/> is already just the active
    /// experts — below its own weight — that figure is the truthful floor and is kept
    /// rather than reduced further.
    /// </remarks>
    public static double MmappedGb(ModelEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var weightGb = e.TotalBytes > 0 ? e.TotalBytes / DeviceProbe.BytesPerGb : 0.0;
        return e.MinRamGb <= weightGb
            ? e.MinRamGb
            : Math.Max(MmapResidentFloorGb, e.MinRamGb - weightGb);
    }

    /// <summary>Whether this model will be memory-mapped on this device.</summary>
    /// <remarks>
    /// MMAP IS A RESPONSE TO A CONSTRAINT, NOT A PROPERTY OF A MODEL. An earlier
    /// attempt made the rule "declares more RAM than it weighs", which is true of
    /// nearly every small model — Qwen3-0.6B declares 0.6 GB and weighs 0.45 — so a
    /// 0.45 GB model was treated as a slow loader and a giant outranked it.
    /// <para>
    /// The honest question is whether it fits EAGERLY here. If it does, load it
    /// eagerly. If it does not, paging is the only way in. That makes the answer
    /// depend on the device, which is correct: one 3B is an eager load on a tablet
    /// and a paged one on a P30. The huge-model threshold stays independent, because
    /// no phone holds a 30B-class MoE eagerly at all.
    /// </para>
    /// </remarks>
    public static bool WillMmap(ModelEntry e, double usableRamGb, bool? mmapAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var allowed = mmapAllowed ?? QwenTextGenerator.MmapIsAllowed;
        return allowed
            && (e.TotalBytes > QwenTextGenerator.MmapWeightThresholdBytes
                || EagerGb(e) > usableRamGb + Eps);
    }

    /// <summary>The RAM this model actually needs resident on this device.</summary>
    public static double EffectiveMinRamGb(ModelEntry e, double usableRamGb, bool? mmapAllowed = null)
        => WillMmap(e, usableRamGb, mmapAllowed) ? MmappedGb(e) : EagerGb(e);

    /// <summary>Whether the device has the memory for it.</summary>
    public static bool FitsRam(ModelEntry e, double usableRamGb, bool? mmapAllowed = null)
        => EffectiveMinRamGb(e, usableRamGb, mmapAllowed) <= usableRamGb + Eps;

    /// <summary>Whether the device has the disk for it. <c>0</c> free = unknown, so skip.</summary>
    public static bool FitsStorage(ModelEntry e, double storageFreeGb)
    {
        ArgumentNullException.ThrowIfNull(e);
        return storageFreeGb <= 0 || e.MinStorageGb <= storageFreeGb + Eps;
    }

    /// <summary>Whether this model can run on this device at all.</summary>
    public static bool Fits(ModelEntry e, double usableRamGb, double storageFreeGb,
                            bool? mmapAllowed = null)
        => FitsRam(e, usableRamGb, mmapAllowed) && FitsStorage(e, storageFreeGb);

    /// <summary>Whether it fits, given a probe.</summary>
    public static bool Fits(ModelEntry e, DeviceProbe probe, bool? mmapAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return Fits(e, probe.UsableRamGb, probe.StorageFreeGb, mmapAllowed);
    }
}
