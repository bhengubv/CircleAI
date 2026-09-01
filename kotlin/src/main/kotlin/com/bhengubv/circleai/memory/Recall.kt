// Recall.kt
//
// What to put in front of the agent, at the moment it is about to act.
//
// THE STORE FINDS CANDIDATES; THIS DECIDES WHAT IS WORTH THE SPACE. Keeping the
// ranking out of the store is what lets the same policy run over SQLite on a
// phone and PostgreSQL on a server without either engine SQL encoding the
// judgement.
//
// THE KINDS ARE WEIGHTS, NOT GATES. Nothing here blocks anything. A ruling
// outranks a preference when both match; a fact that failed its last check is
// still returned, carrying the doubt. The agent is being told, not handcuffed.
//
// SMALL ON PURPOSE. Five atoms and six hundred characters by default. This sits
// in front of every action on a phone, and a memory that floods the context
// window defeats the thing it exists to protect.

package com.bhengubv.circleai.memory

import java.time.Instant
import kotlin.math.max
import kotlin.math.min

interface IRecall {
    suspend fun forSituation(situation: Situation, budget: RecallBudget? = null): RecallResult
}

class Recall(
    private val atoms: IAtomStore,
    val wear: MemoryWear? = null,
) : IRecall {

    override suspend fun forSituation(situation: Situation, budget: RecallBudget?): RecallResult {
        if (situation.isEmpty) return RecallResult.empty

        val cap = budget ?: RecallBudget.default

        // Ask for MORE than the budget: ranking only means something if there
        // was a choice, and the store ordering is by subject match rather than
        // by what matters here.
        val candidates = atoms.match(situation, max(cap.maxAtoms * 4, 20))
        val now = Instant.now()

        // TONE IS NOT SITUATIONAL, and fetching it from the situation match was
        // wrong. "Blunt, hates being asked twice" applies to answering about
        // deploying exactly as much as to anything else - it describes the
        // PERSON, not the subject. Filed under its own topic it simply never
        // matched, so the manner vanished the moment the work got specific,
        // which is precisely when it matters most.
        val tone = atoms.byKind(AtomKind.RELATIONSHIP, 8)
            .sortedWith(compareByDescending<MemoryAtom> { it.corrections }.thenByDescending { it.recordedAtUtc })
            .take(3)

        if (candidates.isEmpty()) {
            return if (tone.isEmpty()) RecallResult.empty else RecallResult(emptyList(), tone, 0)
        }

        // WHAT HAS FADED IS NOT OFFERED. It is not gone - the log still has
        // every line and the atom is still there by id - it simply stops being
        // volunteered.
        val ranked = candidates
            .withIndex()
            .filter { it.value.kind != AtomKind.RELATIONSHIP }
            .filter { wear == null || !wear.faded(it.value, now) }
            .map { (position, a) -> a to (score(a, situation, now) + found(position, candidates.size)) }
            .sortedWith(
                compareByDescending<Pair<MemoryAtom, Double>> { it.second }
                    .thenByDescending { it.first.recordedAtUtc },
            )
            .map { it.first }

        val chosen = mutableListOf<MemoryAtom>()
        var characters = 0
        for (atom in ranked) {
            if (chosen.size >= cap.maxAtoms) break
            // A single long atom must not eat the whole budget and starve three
            // short ones that would have been more use together. SKIPPED, not
            // stopped at - the next one may well fit.
            val cost = atom.text.length
            if (characters + cost > cap.maxCharacters && chosen.isNotEmpty()) continue
            chosen.add(atom)
            characters += cost
        }

        // BRINGING SOMETHING TO MIND IS WHAT MAKES IT STICK. Only what was
        // actually handed back counts: an atom that matched and lost on ranking
        // was not remembered, it was passed over.
        wear?.retrieved(chosen, now)

        return RecallResult(chosen, tone, candidates.size)
    }

    /**
     * A small nudge for what the store put first. It is a tiebreak, not a
     * ranking: the store ordering knows about subject match and nothing about
     * what kind of thing this is or how badly it went last time.
     */
    private fun found(position: Int, total: Int): Double =
        if (total <= 1) 0.0 else 0.12 * (1.0 - position.toDouble() / (total - 1).toDouble())

    internal fun score(atom: MemoryAtom, situation: Situation, now: Instant): Double {
        var score = when (atom.kind) {
            AtomKind.RULING -> 1.00
            AtomKind.DECISION -> 0.90
            AtomKind.FACT -> 0.80
            AtomKind.PREFERENCE -> 0.55
            else -> 0.00
        }

        // A ROAD ALREADY TRIED AND FOUND CLOSED goes near the top. Knowing what
        // failed is worth as much as knowing what worked, and it arrives too
        // late by default: the whole cost of a repeated mistake is paid before
        // anybody remembers making it the first time.
        if (atom.failed) score += 0.25

        // CAPPED at four: after that the point is made, and without a cap one
        // much-corrected atom would crowd out everything else forever.
        score += min(atom.corrections, 4) * 0.18

        val subject = atom.subject
        if (!subject.isNullOrEmpty()) {
            val keys = situation.keys
            val depth = keys.indexOfFirst { it.equals(subject, ignoreCase = true) }
            // Exact key first, then the broader ones it rolls up to.
            if (depth == 0) score += 0.50 else if (depth > 0) score += 0.30
        }

        // HOW REACHABLE IT IS, which replaced a plain recency term. Recency said
        // "newer is better" and nothing else; this says "what you have been
        // using is easier to bring to mind, and what you have not is fading" -
        // and it is the same arithmetic that decides what has faded out
        // altogether, rather than a second opinion about the same thing.
        score += 0.30 * (wear?.reach(atom, now) ?: Forgetting.reach(atom, null, now))

        // A fact that failed its own check is still returned, carrying the doubt.
        if (atom.isStale) score -= 0.35

        return score
    }
}
