// MemoryBudget.cs
//
// The Memory Manager's brain: what the phone can take, before it takes it.
//
// THE PRINCIPLE, IN ONE SENTENCE: prevent, do not cure, and never surprise.
// Circle AI must not be the reason a phone runs out of room, so the decision to
// download something the device cannot hold is made BEFORE the download, not
// after the disk is full. This file makes that decision from real device
// figures (DeviceResources) and returns it in words a person can act on.
//
// THE FLOOR IS THE DEVICE'S, NOT OURS. Android degrades badly below roughly a
// tenth free - it starts killing apps and storage slows - so the budget is not
// "how much may Circle AI use" but "never let free space drop below what the
// PHONE needs to stay healthy because of us". Everything is measured against
// that floor.
//
// Pure arithmetic over bytes: no I/O, no device calls, no phone needed to test
// it. The head reads the numbers (DeviceResources); this decides.

namespace CircleAI.Assistant;

/// <summary>What the budget concluded about a proposed download.</summary>
public enum FootprintVerdict
{
    /// <summary>The device could not be read, so no honest decision is possible.</summary>
    Unknown,

    /// <summary>It fits with room to spare above the floor. Proceed.</summary>
    Fits,

    /// <summary>It fits only after reclaiming space Circle AI no longer needs.</summary>
    ReclaimFirst,

    /// <summary>It will not fit even after reclaiming. Refuse and say so.</summary>
    WontFit,
}

/// <summary>A budget decision, and the sentence to show for it.</summary>
/// <param name="Verdict">What to do.</param>
/// <param name="ShortfallBytes">How much more room is needed, when it will not fit.</param>
/// <param name="Message">Plain words for the person, or empty when nothing needs saying.</param>
public readonly record struct BudgetDecision(
    FootprintVerdict Verdict, long ShortfallBytes, string Message);

/// <summary>Deciding what a device can hold before it is asked to hold it.</summary>
public static class MemoryBudget
{
    /// <summary>
    /// The device-health floor: never let free storage drop below this fraction
    /// of total because of Circle AI.
    /// </summary>
    /// <remarks>
    /// Not a Circle AI limit - the DEVICE's. Below about a tenth free, Android
    /// itself starts to struggle, which is the bad UX this whole module exists
    /// to prevent.
    /// </remarks>
    public const double FreeFloorFraction = 0.10;

    /// <summary>
    /// The cap on a swap / disk-backed paging file, when the Manager creates one.
    /// </summary>
    /// <remarks>
    /// The user's number: never more than a tenth of TOTAL disk. Of total, not
    /// free, so the ceiling is stable and predictable rather than growing with
    /// whatever room happens to be spare. Defined here; used in the paging phase,
    /// which forks by platform (mmap on stock Android, a real swap only on Circle
    /// OS, where the kernel is ours).
    /// </remarks>
    public const double SwapCapFraction = 0.10;

    /// <summary>The floor in bytes for this device, or 0 when unknown.</summary>
    public static long FreeFloorBytes(DeviceResources device)
        => device.Known ? (long)(device.TotalDiskBytes * FreeFloorFraction) : 0;

    /// <summary>The largest swap/paging file allowed on this device, in bytes.</summary>
    public static long SwapCapBytes(DeviceResources device)
        => device.Known ? (long)(device.TotalDiskBytes * SwapCapFraction) : 0;

    /// <summary>
    /// Whether <paramref name="downloadBytes"/> can be taken now, given what is
    /// free and what could be reclaimed first.
    /// </summary>
    /// <param name="device">The real device figures.</param>
    /// <param name="downloadBytes">How large the thing to fetch is.</param>
    /// <param name="reclaimableBytes">
    /// Space Circle AI could free at no cost to the person - duplicate models,
    /// generated audio, prefix caches. NOT downloaded models, which cost data to
    /// get back and are never reclaimed without a say-so.
    /// </param>
    public static BudgetDecision ForDownload(
        DeviceResources device, long downloadBytes, long reclaimableBytes = 0)
    {
        if (!device.Known)
            return new(FootprintVerdict.Unknown, 0,
                "Cannot tell how much room this phone has.");

        if (downloadBytes <= 0)
            return new(FootprintVerdict.Fits, 0, string.Empty);

        var floor = FreeFloorBytes(device);
        var freeAfter = device.FreeDiskBytes - downloadBytes;

        // Room to spare above the floor: just take it.
        if (freeAfter >= floor)
            return new(FootprintVerdict.Fits, 0, string.Empty);

        // Not by itself, but reclaiming what we no longer need makes room.
        var reclaim = reclaimableBytes < 0 ? 0 : reclaimableBytes;
        if (freeAfter + reclaim >= floor)
            return new(FootprintVerdict.ReclaimFirst, 0,
                "Freeing up space Circle AI no longer needs, then getting this.");

        // Not even then. Refuse, and say exactly how much room to make - the
        // person can act on a number, never on "storage full".
        var shortfall = floor - (freeAfter + reclaim);
        return new(FootprintVerdict.WontFit, shortfall,
            $"Not enough room. This needs {Human(downloadBytes)}, and the phone would be "
            + $"too full to run well. Free about {Human(shortfall)} and try again.");
    }

    /// <summary>Bytes as a person reads them - "540 MB", "1.3 GB".</summary>
    /// <remarks>
    /// INVARIANT CULTURE, DELIBERATELY. This box - and the P30 - format a decimal
    /// with a comma ("1,3"), so a plain interpolation gives "1,3 GB" and a test
    /// pinning "1.3 GB" fails for a reason that has nothing to do with the sum.
    /// The point is fixed here so the number reads the same everywhere.
    /// </remarks>
    public static string Human(long bytes)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 0 => "0 MB",
            < 1024L * 1024 => string.Format(c, "{0:0} KB", bytes / 1024.0),
            < 1024L * 1024 * 1024 => string.Format(c, "{0:0} MB", bytes / (1024.0 * 1024)),
            _ => string.Format(c, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024)),
        };
    }
}
