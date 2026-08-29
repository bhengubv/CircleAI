// Forgetting.cs
//
// Why a memory has to let go of things, and how.
//
// A STORE THAT KEEPS EVERYTHING AT FULL VOLUME FOREVER IS A FILING CABINET.
// Ask it about deploying after a year and it hands back the same fifty things
// with the same confidence, and the one that matters is somewhere among them.
// Forgetting is not a defect of human memory that we are working around; it is
// the mechanism that makes recall useful, and a memory on a phone needs it more
// than one on a server because the working set has to stay small.
//
// TWO STRENGTHS, NOT ONE. This follows Bjork's two-factor account, because a
// single "score that goes up when used and down when not" gets the important
// case backwards.
//
//   STABILITY  - how deeply the thing is learned. It ONLY EVER GROWS. Every
//                retrieval, every correction, every restatement adds to it.
//   RETRIEVABILITY - how reachable it is right now. It decays with time and is
//                restored by being retrieved.
//
// THE PART THAT MAKES IT FEEL LIKE MEMORY: retrieving something you had nearly
// forgotten strengthens it far MORE than retrieving something fresh. That is
// the spacing effect, and here it falls out of the arithmetic rather than being
// bolted on - the gain is scaled by (1 - retrievability), so an atom recalled
// at the edge of fading gains most and one recalled twice in a minute gains
// almost nothing.
//
// NOTHING IS EVER DELETED. Fading means dropping out of what recall offers; the
// log still has every line and the atom is still there by id. That is the
// difference between "I cannot bring it to mind" and "it never happened", and
// only the first one is memory.

using System;

namespace CircleAI.Memory;

/// <summary>The curve: what fades, how fast, and what never does.</summary>
public static class Forgetting
{
    // ------------------------------------------------------------------
    // Constants, and why each one is what it is
    // ------------------------------------------------------------------

    /// <summary>
    /// How long a newly recorded atom stays reachable without being touched.
    /// </summary>
    /// <remarks>
    /// Fourteen days, so a decision recorded once and never returned to is
    /// still there a fortnight later and has faded by about six weeks. A single
    /// human exposure decays far faster than this; a memory somebody is relying
    /// on to not repeat a mistake should not.
    /// </remarks>
    public const double InitialStabilityDays = 14.0;

    /// <summary>Below this it has faded out of what recall offers.</summary>
    /// <remarks>
    /// Not deleted. Still in the log, still there by id, still findable by
    /// anybody who goes looking - just no longer volunteered.
    /// </remarks>
    public const double Threshold = 0.05;

    /// <summary>How much a retrieval at the edge of fading is worth.</summary>
    /// <remarks>
    /// A retrieval at retrievability 0 would multiply stability by 1 + this;
    /// one at retrievability 1 would not move it at all. Two is a doubling at
    /// the edge, which puts an atom rescued at the last moment about six weeks
    /// further out.
    /// </remarks>
    public const double SpacingGain = 2.0;

    /// <summary>What a correction is worth to how deeply a thing is learned.</summary>
    /// <remarks>
    /// Being told the same thing again is the strongest encoding there is - it
    /// carries the weight of having got it wrong. Four corrections put an atom
    /// roughly a year out on its own.
    /// </remarks>
    public const double CorrectionGain = 0.9;

    // ------------------------------------------------------------------
    // The curve
    // ------------------------------------------------------------------

    /// <summary>
    /// How reachable something is now, given how deeply it is learned.
    /// </summary>
    /// <remarks>
    /// The exponential forgetting curve: r = e^(-t/S). At t = S it is 0.37; at
    /// three times S it has faded. Elapsed time in the future - a clock that
    /// went backwards, a log line stamped ahead - is treated as no time at all
    /// rather than as strengthening.
    /// </remarks>
    public static double Retrievability(double stabilityDays, TimeSpan elapsed)
    {
        if (stabilityDays <= 0) return 0;

        var days = Math.Max(elapsed.TotalDays, 0);
        return Math.Exp(-days / stabilityDays);
    }

    /// <summary>
    /// What a successful retrieval does to how deeply a thing is learned.
    /// </summary>
    /// <remarks>
    /// SCALED BY HOW NEARLY IT WAS FORGOTTEN. This is the whole difference
    /// between this and a counter: recalling something at the edge of fading is
    /// worth a great deal, and recalling the same thing again ten seconds later
    /// is worth almost nothing. Without it, anything asked about often enough
    /// would become permanent regardless of whether it was ever in doubt.
    /// </remarks>
    public static double Strengthened(double stabilityDays, double retrievability)
    {
        var current = Math.Max(stabilityDays, InitialStabilityDays);
        var wasNearlyGone = 1.0 - Math.Clamp(retrievability, 0, 1);

        return current * (1.0 + SpacingGain * wasNearlyGone);
    }

    /// <summary>
    /// Where an atom starts, before anybody has used it.
    /// </summary>
    /// <remarks>
    /// Corrections count from the beginning: an atom that arrived already
    /// having been said four times is not a new memory, it is an old one that
    /// finally got written down.
    /// </remarks>
    public static double InitialStability(MemoryAtom atom)
    {
        ArgumentNullException.ThrowIfNull(atom);
        return InitialStabilityDays * (1.0 + CorrectionGain * Math.Min(atom.Corrections, 6));
    }

    // ------------------------------------------------------------------
    // What refuses to fade
    // ------------------------------------------------------------------

    /// <summary>
    /// The lowest this kind of thing ever falls to.
    /// </summary>
    /// <remarks>
    /// SOME THINGS DO NOT FADE, and pretending otherwise would be the worst
    /// possible reading of "make it like human memory". A standing rule is not
    /// an episode - it stops being a thing you remember happening and becomes a
    /// thing you know, and people do not forget how they work because a month
    /// went by. "Never restart a device" going quiet because nobody deployed in
    /// August is exactly the failure this whole store exists to prevent.
    ///
    /// The same goes for how somebody wants to be worked with: that is who they
    /// are, not something that came up once.
    ///
    /// A decision about one challenge, and a fact that can go stale, are
    /// episodes. Those fade.
    /// </remarks>
    public static double FloorFor(AtomKind kind) => kind switch
    {
        AtomKind.Ruling       => 0.40,
        AtomKind.Relationship => 0.40,
        AtomKind.Preference   => 0.20,
        _                     => 0.00,
    };

    /// <summary>
    /// How reachable this atom is, floor included.
    /// </summary>
    /// <param name="atom">The thing.</param>
    /// <param name="trace">What use it has had here, or null if none.</param>
    /// <param name="now">When it is being asked for.</param>
    public static double Reach(MemoryAtom atom, MemoryTrace? trace, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atom);

        var stability = trace?.StabilityDays ?? InitialStability(atom);
        var since = trace?.LastRetrievedUtc ?? atom.LastCorrectedUtc ?? atom.RecordedAtUtc;

        return Math.Max(Retrievability(stability, now - since), FloorFor(atom.Kind));
    }

    /// <summary>Whether this has faded out of what recall offers.</summary>
    public static bool Faded(MemoryAtom atom, MemoryTrace? trace, DateTimeOffset now) =>
        Reach(atom, trace, now) < Threshold;
}
