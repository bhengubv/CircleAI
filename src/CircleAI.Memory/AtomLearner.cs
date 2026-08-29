// AtomLearner.cs
//
// What gets kept, out of what was spotted.
//
// THIS IS THE HALF THAT DECIDES, and it is separate from the half that spots so
// that "what did you see" and "what did you keep" are two questions with two
// answers. An extractor that also committed would make a wrong reading
// unfalsifiable: there would be nothing to look at but the atom it produced.
//
// TWICE MUST NOT MEAN TWO. Running this over the same conversation again - after
// a crash, a pull, or simply a second pass - has to be the same as running it
// once. Everything below follows from that, and it is why a near-duplicate is
// dropped rather than counted.
//
// IT NEVER SUPERSEDES ON A GUESS. Correcting is how an atom's history is
// rewritten and how its rank climbs; doing that because two sentences looked
// similar would let a misreading quietly replace something a person actually
// said. Only a person, or an explicit call, supersedes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>What one pass of learning did.</summary>
/// <param name="Considered">Candidates the extractor proposed.</param>
/// <param name="Recorded">Candidates that were sure enough and new.</param>
/// <param name="AlreadyKnown">Candidates dropped as things already remembered.</param>
/// <param name="Offered">
/// Candidates that were not sure enough to keep without being asked. They are
/// returned rather than discarded: an uncertain reading is still a question
/// worth putting to somebody.
/// </param>
public sealed record LearnReport(
    int Considered,
    IReadOnlyList<AtomCandidate> Recorded,
    IReadOnlyList<AtomCandidate> AlreadyKnown,
    IReadOnlyList<AtomCandidate> Offered);

/// <summary>Turns what was said into what is remembered.</summary>
public sealed class AtomLearner
{
    private readonly IAtomExtractor _extractor;

    public AtomLearner(IAtomExtractor? extractor = null) =>
        _extractor = extractor ?? new CueExtractor();

    /// <summary>Which extractor is doing the reading.</summary>
    public string Extractor => _extractor.Name;

    /// <summary>
    /// Read a conversation and keep what is worth keeping.
    /// </summary>
    /// <param name="episodes">The exchanges, in any order.</param>
    /// <param name="record">
    /// Where a kept atom goes. A delegate rather than a store, because
    /// recording has to go through the log when there is one - and the log
    /// lives a layer up, in MemorySync.
    /// </param>
    /// <param name="known">
    /// What is already remembered, to check against. Pass the current atoms;
    /// superseded ones are deliberately not consulted, so a thing that was
    /// corrected away can be learned again if it comes back.
    /// </param>
    /// <param name="subject">The situation key to file under, if the caller has one.</param>
    public Task<LearnReport> LearnAsync(
        IEnumerable<EpisodicMemoryEntry> episodes,
        Func<MemoryAtom, CancellationToken, Task> record,
        IReadOnlyList<MemoryAtom> known,
        string? subject = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(known);

        var already = new HashSet<string>(
            known.Select(a => CueExtractor.Normalise(a.Text)),
            StringComparer.OrdinalIgnoreCase);

        return LearnAsync(episodes, record,
            (text, _) => Task.FromResult(already.Contains(CueExtractor.Normalise(text))),
            subject, ct);
    }

    /// <summary>
    /// Read a conversation and keep what is worth keeping.
    /// </summary>
    /// <param name="episodes">The exchanges, in any order.</param>
    /// <param name="record">Where a kept atom goes.</param>
    /// <param name="knows">
    /// Whether something is already remembered.
    /// </param>
    /// <param name="subject">The situation key to file under, if the caller has one.</param>
    /// <remarks>
    /// ASKED, NOT HANDED THE WHOLE MEMORY. Learning runs on every turn of a
    /// conversation and asks this of every sentence it spots. Being given a
    /// list means loading everything first, which cost 229 ms at 247 atoms on a
    /// P30 and grows from there - on the one path that must never make anybody
    /// wait. A store answers it with an index.
    /// </remarks>
    public async Task<LearnReport> LearnAsync(
        IEnumerable<EpisodicMemoryEntry> episodes,
        Func<MemoryAtom, CancellationToken, Task> record,
        Func<string, CancellationToken, Task<bool>> knows,
        string? subject = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(knows);

        // Still a set as well, because two identical sentences in ONE pass are
        // not yet in any store and would otherwise both be kept.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var considered = 0;
        var recorded = new List<AtomCandidate>();
        var alreadyKnown = new List<AtomCandidate>();
        var offered = new List<AtomCandidate>();

        // Oldest first, so that when two passes over the same conversation
        // produce the same sentence twice, the one that is kept is the one that
        // was said first - and a rebuild lands on the same atom either way.
        foreach (var episode in episodes.OrderBy(e => e.RecordedAtUtc))
        {
            ct.ThrowIfCancellationRequested();

            foreach (var candidate in _extractor.Extract(episode, subject))
            {
                considered++;

                // ALREADY KNOWN BEATS NOT SURE ENOUGH. A sentence that is
                // already remembered is not a question for anybody, however
                // faintly it was spotted, and offering it would ask somebody to
                // confirm what they already told us.
                var normalised = CueExtractor.Normalise(candidate.Atom.Text);
                if (!seen.Add(normalised) ||
                    await knows(candidate.Atom.Text, ct).ConfigureAwait(false))
                {
                    alreadyKnown.Add(candidate);
                    continue;
                }

                if (!candidate.Certain)
                {
                    offered.Add(candidate);
                    continue;
                }

                await record(candidate.Atom, ct).ConfigureAwait(false);
                recorded.Add(candidate);
            }
        }

        return new LearnReport(considered, recorded, alreadyKnown, offered);
    }

    /// <summary>
    /// Read one exchange without keeping anything.
    /// </summary>
    /// <remarks>
    /// For showing somebody what would be learned before it is. The same reading
    /// either way - this is not a second, gentler extractor.
    /// </remarks>
    public IReadOnlyList<AtomCandidate> Read(EpisodicMemoryEntry episode, string? subject = null) =>
        _extractor.Extract(episode, subject);
}
