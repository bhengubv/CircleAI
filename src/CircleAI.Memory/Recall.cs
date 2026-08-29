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
    private readonly MemoryWear? _wear;

    /// <param name="atoms">What is known.</param>
    /// <param name="wear">
    /// How worn the paths are on this machine, or null for a memory with no
    /// sense of use - everything equally reachable, nothing ever fading.
    /// </param>
    public Recall(IAtomStore atoms, MemoryWear? wear = null)
    {
        _atoms = atoms ?? throw new ArgumentNullException(nameof(atoms));
        _wear = wear;
    }

    /// <summary>What this recall has strengthened, if it is keeping track.</summary>
    public MemoryWear? Wear => _wear;

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

        // WHAT HAS FADED IS NOT OFFERED. It is not gone - the log still has
        // every line and the atom is still there by id - it simply stops being
        // volunteered, which is the difference between "I cannot bring it to
        // mind" and "it never happened".
        var ranked = candidates
            .Where(a => a.Kind != AtomKind.Relationship)
            .Where(a => _wear is null || !_wear.Faded(a, now))
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

        // BRINGING SOMETHING TO MIND IS WHAT MAKES IT STICK. Only what was
        // actually handed back counts: an atom that matched and lost on
        // ranking was not remembered, it was passed over.
        _wear?.Retrieved(chosen, now);

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
    private double Score(MemoryAtom atom, Situation situation, DateTimeOffset now)
    {
        var score = atom.Kind switch
        {
            AtomKind.Ruling     => 1.00,
            AtomKind.Decision   => 0.90,
            AtomKind.Fact       => 0.80,
            AtomKind.Preference => 0.55,
            _                   => 0.00,
        };

        // A ROAD ALREADY TRIED AND FOUND CLOSED goes near the top. Knowing what
        // failed is worth as much as knowing what worked, and it arrives too
        // late by default: the whole cost of a repeated mistake is paid before
        // anybody remembers making it the first time.
        if (atom.Failed) score += 0.25;

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

        // HOW REACHABLE IT IS, which replaced a plain recency term. Recency
        // said "newer is better" and nothing else; this says "what you have
        // been using is easier to bring to mind, and what you have not is
        // fading" - and it is the same arithmetic that decides what has faded
        // out altogether, rather than a second opinion about the same thing.
        score += 0.30 * (_wear is not null
            ? _wear.Reach(atom, now)
            : Forgetting.Reach(atom, null, now));

        if (atom.IsStale) score -= 0.35;

        return score;
    }
}
