// AtomCandidate.cs
//
// Something worth remembering, spotted rather than written.
//
// EXTRACTION PROPOSES; IT DOES NOT DECIDE. A candidate carries what was spotted,
// which words triggered it and how sure that is, because an extractor that
// silently writes whatever it thinks it saw fills the memory with noise - and
// noise ranks. Recall then puts a misreading in front of somebody at the exact
// moment they are about to act on it, which is worse than an empty memory.
//
// So the confidence is the whole interface. Above the bar it is recorded; below
// it, it is offered. Nothing is superseded on a guess.

using System;

namespace CircleAI.Memory;

/// <summary>Something an extractor thinks is worth remembering.</summary>
/// <param name="Atom">The atom it would record.</param>
/// <param name="Confidence">
/// 0 to 1. Above <see cref="AtomCandidate.RecordAbove"/> it is safe to record
/// without being asked; below it, it wants a person.
/// </param>
/// <param name="Cue">
/// The words that triggered it - "never", "I told you", "that did not work".
/// Kept so a wrong extraction can be diagnosed instead of argued about.
/// </param>
/// <param name="Quote">The sentence it came from, verbatim.</param>
public sealed record AtomCandidate(
    MemoryAtom Atom,
    double Confidence,
    string Cue,
    string Quote)
{
    /// <summary>
    /// The bar for recording without being asked.
    /// </summary>
    /// <remarks>
    /// SET HIGH ON PURPOSE. The cost of a missed atom is that somebody has to
    /// say it again; the cost of a wrong one is that the memory hands back
    /// something untrue at the moment it is most trusted. Those are not
    /// symmetrical, so the bar is not in the middle.
    /// </remarks>
    public const double RecordAbove = 0.80;

    /// <summary>Whether this is sure enough to keep without asking.</summary>
    public bool Certain => Confidence >= RecordAbove;
}
