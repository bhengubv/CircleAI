// MemoryBill.cs
//
// WHAT A MODEL ACTUALLY COSTS IN MEMORY, AS A SUM OF NAMED TERMS.
//
// THE OLD SHAPE WAS WEIGHTS x 1.18, AND THAT IS THE WRONG SHAPE.
//
//   fits  <=>  max(MinRamGb, file GB)  <=  free RAM x 0.85
//
// Rearranged, that gives everything which is NOT weights an allowance of 17.6% OF
// THE WEIGHTS. DeviceProbe.RamFitHeadroom says what the 15% is for in its own words -
// "headroom for KV-cache growth during generation + native runtime overhead" - and
// both of those are ABSOLUTE costs that have nothing to do with how big the weights
// are:
//
//   a 0.55 GB model  ->  0.10 GB allowed for a runtime that costs over a gigabyte
//   a 3.79 GB model  ->  0.67 GB allowed, against 1.41 GB measured at 4K context
//
// So the rule under-counts most on the smallest models, which is precisely where this
// product lives. It is the same error, in the same direction, that killed the P30 on
// 2026-10-09: Qwen3.5-4B declared 3.8 GB against 3.867 GB total, passed BY 67 MB, and
// the load got the whole service killed by iAwareF[LowMem].
//
// AND SOMEBODY ALREADY FOUND THE SHAPE WAS WRONG AND FIXED THE WRONG PLACE.
// ModelFit.OsReserveGb is 1.5 GB ABSOLUTE, and its remark is explicit: "Android needs
// roughly the same ABSOLUTE amount of memory on a 4 GB phone as on an 8 GB one; it
// does not need twice as much just because the phone has twice as much. A percentage
// is therefore too tight on big devices and far too loose on small ones." That
// correction was applied to the download CEILING and never to the FIT.
//
// THE RIGHT SHAPE, from a measured bill (PrismML's published figures for 1-bit Bonsai
// 27B under llama.cpp, which is the only end-to-end breakdown anybody has published):
//
//              weights   peak@4K   peak@10K   peak@100K
//   llama.cpp   3.79      5.20      5.60       11.60
//
// 10K -> 100K is +6.00 GB over 90 000 tokens = 66.7 kB/token, and 4K -> 10K is
// +0.40 GB over 6 000 = 66.7 kB/token. The same slope at both ends, so the cache is
// LINEAR IN CONTEXT. Back-solving the constant: 5.20 - 3.79 - (4096 x 66.7 kB) =
// 1.14 GB that depends on neither the weights nor the context.
//
//   bill = resident weights + runtime overhead + cache per token x context
//
// NOTHING HERE IS GUESSED FOR OUR RUNTIME, AND THAT IS THE POINT OF THE TYPE RATHER
// THAN THE NUMBERS. Those figures are llama.cpp on a 27B; ours is MNN on models a
// tenth that size, and a number lifted across runtimes is the "unmeasured discount"
// this codebase has already been burned by twice. So every term is OPTIONAL: with no
// measurement this reproduces the old rule EXACTLY, and each measurement that arrives
// replaces one guess with one fact. See Measured.

namespace CircleAI.Inference;

/// <summary>What one model costs in memory, measured on this device.</summary>
/// <param name="ResidentGb">
/// Peak resident memory attributable to the model itself - the working set while it
/// was loaded, less the floor below. For a memory-mapped model this is the part
/// actually paged in, which is the whole point: it is what the file costs in MEMORY,
/// not what it costs on disk.
/// </param>
/// <param name="FloorGb">
/// Resident memory of the same process with no model loaded: the runtime, the
/// managed heap, the natives. The absolute term the old rule had no place for.
/// </param>
/// <param name="CacheGbPerThousandTokens">
/// How fast the conversation's cache grows. Null when it was measured at one context
/// only, which cannot tell a slope from a constant.
/// </param>
/// <param name="AtContextTokens">The context the peak was seen at.</param>
/// <param name="When">When it was measured. An old number on a device whose other
/// software has changed is still better than no number, but it is worth knowing.</param>
public sealed record ModelCost(
    string ModelId,
    double ResidentGb,
    double FloorGb,
    double? CacheGbPerThousandTokens,
    int AtContextTokens,
    DateTimeOffset When);

/// <summary>The memory bill for running a model, as a sum of named terms.</summary>
public static class MemoryBill
{
    /// <summary>Floating-point slack, matching <see cref="ModelFit"/>'s.</summary>
    private const double Eps = 1e-9;

    /// <summary>
    /// The bill for running <paramref name="cost"/>'s model at a given context.
    /// </summary>
    /// <remarks>
    /// EVERY TERM IS SOMETHING SOMEBODY WATCHED. The weights term is what was resident
    /// while the model ran, so a mapped model contributes what it actually paged in
    /// rather than its size on disk - which is the whole of why a phone with 29 GB of
    /// storage and 3.9 GB of memory is not limited to 3.9 GB of model.
    /// <para>
    /// The cache term is linear in context and is zero when nobody has measured a
    /// slope. Zero is the honest default rather than an estimate: a slope invented
    /// from one data point is the same arithmetic that offered a 2.85 GB model to a
    /// 1.1 GB handset.
    /// </para>
    /// </remarks>
    public static double TotalGb(ModelCost cost, int contextTokens)
    {
        ArgumentNullException.ThrowIfNull(cost);

        var cache = cost.CacheGbPerThousandTokens is { } perThousand && perThousand > 0
            ? perThousand * Math.Max(0, contextTokens) / 1000.0
            : 0.0;

        return cost.ResidentGb + cost.FloorGb + cache;
    }

    /// <summary>
    /// Whether a measured model fits in <paramref name="freeGb"/> at this context.
    /// </summary>
    /// <remarks>
    /// AGAINST FREE RAM, NOT FREE x 0.85. The 0.85 existed to stand in for the two
    /// terms this bill now names; keeping it as well would charge for them twice, and
    /// on a 1 GB-free phone that is the difference between a brain and none.
    /// </remarks>
    public static bool Fits(ModelCost cost, double freeGb, int contextTokens)
        => TotalGb(cost, contextTokens) <= freeGb + Eps;

    /// <summary>
    /// The longest context this model can be run at in <paramref name="freeGb"/>, or
    /// 0 when it does not fit at all.
    /// </summary>
    /// <remarks>
    /// THE QUESTION A PHONE ACTUALLY FACES. "Does it fit" is not a yes or a no once
    /// the cache is in the bill - a model that fits at 2K and not at 32K fits, and the
    /// honest answer is how far. int.MaxValue means the cache is unmeasured, so
    /// nothing here can bound it.
    /// </remarks>
    public static int LongestContext(ModelCost cost, double freeGb)
    {
        ArgumentNullException.ThrowIfNull(cost);

        var fixedPart = cost.ResidentGb + cost.FloorGb;
        if (fixedPart > freeGb + Eps) return 0;

        if (cost.CacheGbPerThousandTokens is not { } perThousand || perThousand <= 0)
            return int.MaxValue;

        return (int)Math.Floor((freeGb - fixedPart) / perThousand * 1000.0);
    }
}
