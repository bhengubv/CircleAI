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

    /// <summary>
    /// Resident memory when the weights are memory-mapped, or <c>null</c> when that
    /// is not known for this model.
    /// </summary>
    /// <remarks>
    /// TWO SOURCES, AND ONLY ONE OF THEM IS TRUSTED FOR AN ARBITRARY MODEL.
    /// <list type="number">
    /// <item><description>
    /// <see cref="ModelEntry.MmapResidentGb"/> — somebody ran the model on a device
    /// and watched the number. Used whenever it is present.
    /// </description></item>
    /// </list>
    /// <para>
    /// THERE USED TO BE A SECOND SOURCE AND IT LET A 35B THROUGH. The rule was: an
    /// MoE whose <see cref="ModelEntry.MinRamGb"/> is already below its own weight is
    /// declaring its active experts, so believe it — "a statement about the
    /// architecture rather than an estimate". The architecture part is probably true;
    /// an A3B does keep roughly 3B active, which at Q4 is about the 2.5 GB claimed.
    /// <para>
    /// It is still a claim about how a RUNTIME will page 18.7 GB of inactive experts
    /// on a PHONE, and that is not architecture — it is the page cache, the router
    /// changing experts every token, and whatever else on the device wants memory.
    /// Nobody has watched it. On a 7.6 GB handset the rule offered 21.2 GB of weights
    /// and the download was 10.6 hours; the same row would have been offered to the
    /// 3.6 GB P30 this product is built for.
    /// </para>
    /// <para>
    /// The paragraph below already says paging counts only when we know what it
    /// costs, and this branch manufactured exactly the figure that sentence refuses.
    /// A model that genuinely holds 2.5 GB resident should carry MmapResidentGb,
    /// which somebody sets by running it.
    /// </para>
    /// </para>
    /// <para>
    /// ANYTHING ELSE RETURNS NULL, and the caller falls back to the eager figure. The
    /// tempting third source is <c>MinRamGb - weight</c>: assume the declared
    /// requirement splits cleanly into weights plus runtime and subtract the paged
    /// part. It was in this file and it was wrong — Qwen3.5-4B declares 3.8 GB against
    /// 2.85 GB of weights, so it produced 0.95 GB and offered a 2.85 GB model to a
    /// 1.1 GB handset. The guards that caught it exist because the app was OOM-killed
    /// on a P30. One measurement (Qwen2.5-3B at ~0.8 GB) does not license the same
    /// arithmetic for every other model, and a crash mid-answer is worse than a model
    /// nobody was offered.
    /// </para>
    /// </remarks>
    public static double? MmappedGb(ModelEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.MmapResidentGb is > 0 and var measured)
            return Math.Max(MmapResidentFloorGb, measured);

        return null;                    // unmeasured: no discount
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
        if (!allowed) return false;

        // Paging only counts when we KNOW what it costs. Without a resident figure
        // the honest answer is that this model loads eagerly, whatever the loader
        // might do — claiming otherwise is how an unmeasured discount reaches a phone.
        if (MmappedGb(e) is null) return false;

        return e.TotalBytes > QwenTextGenerator.MmapWeightThresholdBytes
            || EagerGb(e) > usableRamGb + Eps;
    }

    /// <summary>The RAM this model actually needs resident on this device.</summary>
    public static double EffectiveMinRamGb(ModelEntry e, double usableRamGb, bool? mmapAllowed = null)
        => WillMmap(e, usableRamGb, mmapAllowed) ? MmappedGb(e)!.Value : EagerGb(e);

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

    /// <summary>
    /// Could this device EVER hold this model, whatever happens to be free now?
    /// </summary>
    /// <remarks>
    /// A DURABLE QUESTION NEEDS A DURABLE INPUT, AND Fits DOES NOT HAVE ONE.
    /// <see cref="DeviceProbe.UsableRamGb"/> is derived from FREE RAM, which is
    /// whatever is spare this second - correct for "can I load it right now", wrong
    /// for "is this model a candidate on this phone at all". DeviceProbe's own remark
    /// already draws that distinction for storage: free space answers whether a
    /// download can land now, and TOTAL answers how much of the device may ever be
    /// claimed, "and only the second is stable". Nobody had applied it to RAM.
    /// <para>
    /// MEASURED, AS A REGRESSION I CAUSED. Putting Fits on the installed-model
    /// fallback stopped a 22.8 GB MoE being loaded on a 7.6 GB phone - and then, on
    /// that same phone at versionCode 53 with 1.8 GB free, it refused the complete
    /// 1.4 GB Qwen3.5-2B as well and reported "no model on this device yet" while the
    /// 2B sat on disk. 1 798 668 kB available x 0.85 = 1.565 GB usable against the
    /// 2B's declared 1.9. The phone had not changed; the moment had.
    /// </para>
    /// <para>
    /// So the fallback asks THIS instead. It is deliberately crude and therefore
    /// non-volatile: weights larger than the whole device's memory can never run
    /// here, and no amount of waiting changes that. It refuses the 35 B (22.8 GB of
    /// weights, 30.3 GB declared, against 7.64 GB of RAM) and admits the 2 B. The
    /// finer judgement stays with the selector, and when the finer judgement is
    /// wrong, <see cref="CrashVerdict"/> is what catches it now - a durable gate for
    /// the absurd case, a remembered crash for the marginal one.
    /// </para>
    /// </remarks>
    public static bool CouldEverHold(ModelEntry e, DeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(probe);

        // RamTotalBytes is supplied by a platform head and falls back to the
        // available figure on desktop, where the two are close. Zero means the
        // device could not be measured, and refusing everything on that basis is
        // how the smallest phones get told they can do nothing.
        var totalBytes = probe.RamTotalBytes > 0 ? probe.RamTotalBytes : probe.RamAvailableBytes;
        if (totalBytes <= 0) return true;

        var totalGb = totalBytes / DeviceProbe.BytesPerGb;
        return EagerGb(e) <= ResidentCeilingGb(totalGb) + Eps;
    }

    /// <summary>
    /// The most of a device's TOTAL memory one model's weights may ever claim.
    /// </summary>
    /// <remarks>
    /// THIS CONSTANT EXISTS BECAUSE THE RULE WITHOUT IT KILLED A PHONE, and the
    /// rule without it was mine.
    ///
    /// CouldEverHold compared the weights against the whole of total RAM, so
    /// anything up to 100% of the device passed. Measured on the P30 (3.6 GB)
    /// on 2026-10-09, on a true cold first run:
    ///
    ///     22:07:03  fetching Qwen3.5-4B-MNN (2845944094 bytes)
    ///     22:13:14  Qwen3.5-4B-MNN complete
    ///     22:25:40  Killing 7743:...circleai.service (adj 100):
    ///               iAwareF[LowMem](service)
    ///
    /// 2.85 GB of weights is 79% of that device. It downloaded for six minutes,
    /// was selected, and the load got the whole service killed - the "the offer
    /// was the defect" failure this file's own remarks describe, reintroduced by
    /// the fix for the opposite problem. Before that fix the gate was Fits, on
    /// FREE ram, which would have refused it; trading a volatile rule for a
    /// durable one was right, and making the durable one 100% was not.
    ///
    /// AN OPERATING SYSTEM NEEDS ITS SHARE. On this handset MemAvailable sat
    /// between 1.0 and 1.3 GB of 3.6 GB with no model loaded at all - 28 to 35% -
    /// so a model may never assume anything close to the whole device.
    ///
    /// THE REAL NUMBERS, because they are closer than they look. EagerGb already
    /// uses the DECLARED MinRamGb, not the file size, so the comparison was never
    /// naive:
    ///
    ///   Qwen3.5-4B   weights 2.85 GB, MinRamGb 3.8  -> EagerGb 3.8
    ///   the P30      MemTotal 3 776 516 kB          -> 3.867 GB
    ///
    /// 3.8 <= 3.867, so it passed - by 67 MB, on a declaration that it needs
    /// essentially the entire phone. That is what 100% permits, and the load got
    /// the service killed by iAwareF[LowMem].
    ///
    /// A RESERVE, NOT A PERCENTAGE, AND THAT CORRECTION MATTERS. The first fix
    /// was a share of total memory, set to 0.90 because Qwen3-8B declares 7.2 GB
    /// and a test asserted it stays selectable on an 8 GB handset. That reasoning
    /// was circular - the ceiling was set by a catalogue claim nobody had
    /// measured - and it is also the wrong SHAPE. Android needs roughly the same
    /// ABSOLUTE amount of memory on a 4 GB phone as on an 8 GB one; it does not
    /// need twice as much just because the phone has twice as much. A percentage
    /// is therefore too tight on big devices and far too loose on small ones,
    /// which is exactly where this product lives.
    ///
    /// 1.5 GB is what the P30 actually keeps busy with no model loaded at all:
    /// MemAvailable sat between 0.85 and 1.28 GB of 3.867 GB across a whole day
    /// of measurements, so 2.6-3.0 GB was never ours to claim.
    ///
    /// What the reserve decides, against what was measured:
    ///
    ///   P30   3.867 GB -> ceiling 2.37 GB   refuses the 4B (3.8, killed it),
    ///                                       admits the 2B (1.9, which ran)
    ///   8 GB  8.0 GB   -> ceiling 6.5 GB    refuses Qwen3-8B (7.2) - and that
    ///                                       is the honest answer: 7.2 GB
    ///                                       declared on an 8 GB phone leaves
    ///                                       Android 800 MB, which is the P30's
    ///                                       failure scaled up.
    ///
    /// THE PROPORTIONAL FLOOR stops a reserve bigger than the device telling the
    /// smallest phones they can run nothing - the failure DeviceProbe's
    /// MeasurementWarning exists to shout about. Below about 2.5 GB total the
    /// floor takes over and the finer gates do the judging.
    ///
    /// AND IT IS NOT THE LOAD-TIME RULE. ModelFit.Fits, on free RAM, is what
    /// decides whether a model can be loaded right now; this decides only
    /// whether it is worth OWNING. A correction to an earlier comment of mine:
    /// the two do NOT differ by mmap - not one model in the catalogue carries a
    /// measured MmapResidentGb, so WillMmap is false everywhere and both rules
    /// read EagerGb. They differ in free versus total, and nothing else.
    /// </remarks>
    public const double OsReserveGb = 1.5;

    /// <summary>The least of a device's memory a model may ever be offered.</summary>
    /// <remarks>
    /// The floor under <see cref="OsReserveGb"/>, so a small phone is not told it
    /// can run nothing at all.
    /// </remarks>
    public const double LeastShare = 0.40;

    /// <summary>The most memory one model's weights may ever claim here.</summary>
    public static double ResidentCeilingGb(double totalGb)
        => Math.Max(totalGb - OsReserveGb, totalGb * LeastShare);

    /// <summary>Whether it fits, given a probe.</summary>
    public static bool Fits(ModelEntry e, DeviceProbe probe, bool? mmapAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return Fits(e, probe.UsableRamGb, probe.StorageFreeGb, mmapAllowed);
    }
}
