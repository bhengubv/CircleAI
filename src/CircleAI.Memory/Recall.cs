// Recall.cs
//
// What to put in front of the agent, at the moment it is about to act.
//
// THE STORE FINDS CANDIDATES; THIS DECIDES WHAT IS WORTH THE SPACE. Keeping
// the ranking out of the store is what lets the same policy run over SQLite on
// a phone and PostgreSQL on a server without either engine's SQL encoding the
// judgement.
//
// THE KINDS ARE WEIGHTS, NOT GATES. Nothing here blocks anything. A ruling
// outranks a preference when both match; a fact that failed its last check is
// still returned, carrying the doubt. The agent is being told, not handcuffed.
//
// SMALL ON PURPOSE. Five atoms and six hundred characters by default. This is
// meant to sit in front of every action on a phone, and a memory that floods
// the context window defeats the thing it exists to protect.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>Answers "what do we know that bears on what I am about to do".</summary>
public interface IRecall
{
    /// <summary>What is worth knowing before this action, inside the budget.</summary>
    Task<RecallResult> ForAsync(
        Situation situation,
        RecallBudget? budget = null,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class Recall : IRecall
{
    private readonly IAtomStore _atoms;

    public Recall(IAtomStore atoms) =>
        _atoms = atoms ?? throw new ArgumentNullException(nameof(atoms));

    /// <inheritdoc />
    public async Task<RecallResult> ForAsync(
        Situation situation,
        RecallBudget? budget = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(situation);
        if (situation.IsEmpty) return RecallResult.Empty;

        var cap = budget ?? RecallBudget.Default;

        // Ask for more than the budget: ranking only means something if there
        // was a choice, and the store's ordering is by subject match, not by
        // what matters here.
        var candidates = await _atoms
            .MatchAsync(situation, Math.Max(cap.MaxAtoms * 4, 20), ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // TONE IS NOT SITUATIONAL, and fetching it from the situation match was
        // wrong. "Blunt, hates being asked twice" applies to answering about
        // deploying exactly as much as to answering about anything else - it
        // describes the person, not the subject. Filed under its own topic it
        // simply never matched, so the manner vanished the moment the work got
        // specific, which is precisely when it matters most.
        //
        // So it is loaded by kind, independent of what is about to happen -
        // the same reasoning that keeps a persona in context for a whole
        // session rather than looking it up per turn.
        var tone = (await _atoms.ByKindAsync(AtomKind.Relationship, 8, ct).ConfigureAwait(false))
            .OrderByDescending(a => a.Corrections)
            .ThenByDescending(a => a.RecordedAtUtc)
            .Take(3)
            .ToList();

        if (candidates.Count == 0)
            return tone.Count == 0
                ? RecallResult.Empty
                : new RecallResult(Array.Empty<MemoryAtom>(), tone, 0);

        var ranked = candidates
            .Where(a => a.Kind != AtomKind.Relationship)
            .Select(a => (Atom: a, Score: Score(a, situation, now)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Atom.RecordedAtUtc)
            .Select(x => x.Atom);

        var chosen = new List<MemoryAtom>();
        var characters = 0;

        foreach (var atom in ranked)
        {
            if (chosen.Count >= cap.MaxAtoms) break;

            // A single long atom must not eat the whole budget and starve three
            // short ones that would have been more use together.
            var cost = atom.Text.Length;
            if (characters + cost > cap.MaxCharacters && chosen.Count > 0) continue;

            chosen.Add(atom);
            characters += cost;
        }

        return new RecallResult(chosen, tone, candidates.Count);
    }

    // ------------------------------------------------------------------
    // Ranking
    // ------------------------------------------------------------------

    /// <summary>How much this atom deserves the space, for this situation.</summary>
    /// <remarks>
    /// Four contributions, in the order they matter:
    /// <list type="bullet">
    /// <item>KIND. A ruling is a decision somebody made; a preference is a
    /// leaning. When both match the same moment the decision goes first.</item>
    /// <item>CORRECTIONS. The strongest signal in the store. Something that had
    /// to be put right three times is what most needs to arrive before the
    /// action - and it is the one thing the agent could never have judged for
    /// itself, because it did not see the corrections coming.</item>
    /// <item>SUBJECT MATCH. Filed under the exact situation beats sharing
    /// vocabulary with it.</item>
    /// <item>RECENCY, faintly. Enough to break ties in favour of the newer
    /// thing, never enough to bury a ruling under this morning's trivia.</item>
    /// </list>
    /// A stale fact is penalised but not removed: it still knows more than
    /// nothing, and it arrives labelled.
    /// </remarks>
    private static double Score(MemoryAtom atom, Situation situation, DateTimeOffset now)
    {
        var score = atom.Kind switch
        {
            AtomKind.Ruling     => 1.00,
            AtomKind.Fact       => 0.80,
            AtomKind.Preference => 0.55,
            _                   => 0.00,
        };

        // Capped: after about four corrections the point is made, and without a
        // cap one much-corrected atom would crowd out everything else forever.
        score += Math.Min(atom.Corrections, 4) * 0.18;

        if (!string.IsNullOrEmpty(atom.Subject) &&
            situation.Keys.Contains(atom.Subject, StringComparer.OrdinalIgnoreCase))
        {
            // Exact key first, then the broader ones it rolls up to.
            var depth = situation.Keys.ToList().IndexOf(atom.Subject!);
            score += depth == 0 ? 0.50 : 0.30;
        }

        var days = Math.Max((now - atom.RecordedAtUtc).TotalDays, 0);
        score += 0.15 / (1 + days / 30);

        if (atom.IsStale) score -= 0.35;

        return score;
    }
}
