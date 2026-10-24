// ModelEvidence.cs
//
// WHAT THIS DEVICE HAS LEARNED ABOUT ONE MODEL, AS OPPOSED TO WHAT WE SHIPPED.
//
// ModelEntry.QualityRank is an editorial opinion, written into the registry when the
// entry was written, identical on every phone on earth and unchanged by anything that
// has happened since. It is the right thing to start from and the wrong thing to
// finish on. The selector that reads it alone answers "which was the best model when
// we released it", which is not the question anybody is asking.
//
// WHAT A DEVICE KNOWS THAT A CATALOGUE CANNOT:
//
//   what it cost      the P30 was offered Qwen3.5-4B because 3.8 declared fits in
//                     3.867 total - by 67 MB - and the load got the service killed.
//                     The rank did not know. The phone found out.
//   how fast it was   a model that answers in forty seconds on this handset is not
//                     "better" than one that answers in eight, whatever its rank.
//   whether it ran    two strikes and a model is refused here. That is evidence,
//                     and it is the only kind that outranks a declaration.
//
// THREE NUMBERS AND A COUNT, NOT A SCORE. A single blended "quality" figure would be
// a new opinion sitting where the old one was, and the first time it disagreed with
// somebody's experience nobody could say which term was wrong. These stay separate so
// the selector's reasoning can be read back.

namespace CircleAI.SelfKnowledge;

/// <summary>What this device has observed about one model, by running it.</summary>
/// <param name="ModelId">The catalogue name.</param>
/// <param name="ResidentGb">
/// Peak memory the model itself held, over the runtime's floor. Null until something
/// watched it. For a memory-mapped model this is the part actually paged in, which is
/// why a 22.8 GB file on a phone with 29 GB of storage is not a 22.8 GB question.
/// </param>
/// <param name="FloorGb">The runtime's own cost with no model loaded.</param>
/// <param name="CacheGbPerThousandTokens">
/// How fast the conversation's cache grows here. Null until two readings at different
/// lengths have been taken - one reading cannot tell a slope from a constant.
/// </param>
/// <param name="TokensPerSecond">Measured decode speed on this device. Null until measured.</param>
/// <param name="Loads">How many times it has loaded here.</param>
/// <param name="Failures">
/// How many times loading or answering ended badly. The number that outranks a
/// declaration: a model this phone has already been killed by is not a candidate,
/// however it is ranked.
/// </param>
/// <param name="LastSeenUtc">When any of this was last true.</param>
public sealed record ModelEvidence(
    string ModelId,
    double? ResidentGb = null,
    double? FloorGb = null,
    double? CacheGbPerThousandTokens = null,
    double? TokensPerSecond = null,
    int Loads = 0,
    int Failures = 0,
    DateTimeOffset? LastSeenUtc = null)
{
    /// <summary>Whether anything here was actually observed, as opposed to defaulted.</summary>
    public bool IsEmpty => ResidentGb is null && TokensPerSecond is null && Loads == 0 && Failures == 0;

    /// <summary>Whether this device has had this model work at least once.</summary>
    /// <remarks>
    /// PROVEN IS A STRONGER WORD THAN COMPATIBLE. Compatible is a prediction from
    /// declared metadata; this is a model that ran here. ModelChoice already prefers a
    /// proven model when the free-memory reading of the moment is too tight to trust -
    /// this is where that proof comes from, instead of being passed in by whichever
    /// caller happened to know.
    /// </remarks>
    public bool Proven => Loads > 0 && Failures < Loads;

    /// <summary>
    /// Whether this device has refused it on evidence: tried, and it went badly more
    /// often than it went well.
    /// </summary>
    /// <remarks>
    /// TWO STRIKES, matching CrashVerdict's existing rule rather than inventing a
    /// second one. A single bad load is a bad day - the phone was busy, another app
    /// was leaving - and refusing a model for ever on one reading is how a device ends
    /// up with no brain at all.
    /// </remarks>
    public bool Refused => Failures >= 2 && Failures > Loads;

    /// <summary>What this model cost here, in the shape MemoryBill uses, or null.</summary>
    public CircleAI.Inference.ModelCost? AsCost() =>
        ResidentGb is { } resident && FloorGb is { } floor
            ? new CircleAI.Inference.ModelCost(
                ModelId, resident, floor, CacheGbPerThousandTokens,
                AtContextTokens: 0, When: LastSeenUtc ?? DateTimeOffset.UtcNow)
            : null;
}
