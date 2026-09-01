// MemoryAtom.kt
//
// The atom: one thing worth remembering, and the situation that finds it.
//
// Port of MemoryAtom.cs, Situation.cs, IAtomStore.cs, AtomCandidate.cs and
// IAtomExtractor.cs.

package com.bhengubv.circleai.memory

import java.time.Instant
import java.util.UUID

enum class AtomKind { DECISION, RULING, FACT, PREFERENCE, RELATIONSHIP }

enum class DecisionOutcome { OPEN, RESOLVED, FAILED }

data class MemoryAtom(
    val id: UUID = UUID.randomUUID(),
    val kind: AtomKind = AtomKind.DECISION,
    val text: String = "",
    val subject: String? = null,
    val sourceEpisode: UUID? = null,
    val recordedAtUtc: Instant = Instant.EPOCH,
    val machine: String? = null,
    val corrections: Int = 0,
    val lastCorrectedUtc: Instant? = null,
    val supersededBy: UUID? = null,
    val challenge: String? = null,
    val outcome: DecisionOutcome? = null,
    val verify: String? = null,
    val verifiedAtUtc: Instant? = null,
    val verifiedOk: Boolean? = null,
) {
    val isCurrent: Boolean get() = supersededBy == null

    /** A FACT that failed its own check. Still readable, no longer an answer. */
    val isStale: Boolean get() = kind == AtomKind.FACT && verifiedOk == false

    val failed: Boolean get() = outcome == DecisionOutcome.FAILED
}

/**
 * What is about to happen, described well enough to look it up.
 *
 * THIS IS THE WHOLE DIFFERENCE between a memory that helps and one that does
 * not. Loading everything at the start of a conversation puts the rules
 * furthest from the moment they apply; an hour and forty tool calls later,
 * nothing read at the greeting is meaningfully present, and no amount of
 * emphasis in the file changes that.
 *
 * So recall is keyed on the ACTION rather than the session. Before a deploy,
 * ask what is known about deploying. The subject of the action is matched
 * against the subject of the atom, which is a lookup rather than a guess.
 */
data class Situation(
    val verb: String? = null,
    val target: String? = null,
    val tool: String? = null,
    val text: String? = null,
) {
    val key: String
        get() = listOfNotNull(verb, target)
            .filter { it.isNotBlank() }
            .joinToString(":") { it.trim().lowercase() }

    /**
     * Most specific first, then broader.
     *
     * A slash-delimited target is walked UP - android/p30 also matches android -
     * because a rule filed against the general case has to be found by the
     * specific one. Without that, a rule about deploying to Android is invisible
     * the moment somebody names the phone.
     */
    val keys: List<String>
        get() {
            val out = mutableListOf<String>()
            val v = verb?.trim()?.lowercase()
            var t = target?.trim()?.lowercase()

            if (!v.isNullOrEmpty() && !t.isNullOrEmpty()) {
                out.add(v + ":" + t)
                var cut = t.lastIndexOf('/')
                while (cut > 0) {
                    t = t!!.substring(0, cut)
                    out.add(v + ":" + t)
                    cut = t!!.lastIndexOf('/')
                }
            }

            if (!v.isNullOrEmpty()) out.add(v)
            return out
        }

    val query: String
        get() = listOfNotNull(verb, target, tool, text)
            .filter { it.isNotBlank() }
            .joinToString(" ") { it.trim() }

    val isEmpty: Boolean get() = keys.isEmpty() && text.isNullOrBlank()
}

data class RecallResult(
    val atoms: List<MemoryAtom>,
    val tone: List<MemoryAtom>,
    val considered: Int,
) {
    val any: Boolean get() = atoms.isNotEmpty()

    companion object { val empty = RecallResult(emptyList(), emptyList(), 0) }
}

/**
 * How much recall is allowed to say.
 *
 * A budget rather than a limit: what fits is chosen by rank, so the cap costs
 * the least useful atoms rather than truncating the most useful one mid-word.
 */
data class RecallBudget(val maxAtoms: Int = 5, val maxCharacters: Int = 600) {
    companion object { val default = RecallBudget() }
}

/**
 * Reading and writing the layer between raw turns and a persona.
 *
 * NOTHING HERE REQUIRES AN EMBEDDING. Vector search improves recall; it must
 * never be what ENABLES it. A store that stops working without a 100 MB model
 * is a store that does not work on the phone this is for.
 */
interface IAtomStore {
    suspend fun add(atom: MemoryAtom)
    suspend fun supersede(oldAtomId: UUID, replacement: MemoryAtom): MemoryAtom
    suspend fun match(situation: Situation, limit: Int = 20): List<MemoryAtom>
    suspend fun byKind(kind: AtomKind, limit: Int = 50): List<MemoryAtom>
    suspend fun all(includeSuperseded: Boolean = false, limit: Int = 500): List<MemoryAtom>
    suspend fun knows(text: String): Boolean
    suspend fun get(id: UUID): MemoryAtom?
    suspend fun markVerified(id: UUID, ok: Boolean, whenUtc: Instant)
    suspend fun count(): Int
}

/**
 * Something worth remembering, SPOTTED rather than written.
 *
 * Extraction PROPOSES; it does not decide. A candidate carries what was
 * spotted, which words triggered it and how sure that is, because an extractor
 * that silently writes whatever it thinks it saw fills the memory with noise -
 * and noise ranks. Recall then puts a misreading in front of somebody at the
 * exact moment they are about to act on it, which is worse than an empty
 * memory.
 */
data class AtomCandidate(
    val atom: MemoryAtom,
    val confidence: Double,
    val cue: String,
    val quote: String,
) {
    val certain: Boolean get() = confidence >= RECORD_ABOVE

    companion object {
        /** Above this it is recorded; below it, it is offered. Nothing is superseded on a guess. */
        const val RECORD_ABOVE = 0.80
    }
}

/**
 * The seam between what was said and what is remembered.
 *
 * ONE SEAM, TWO MECHANISMS. CueExtractor needs no model and therefore works on
 * a phone with the radios off, which makes it the FLOOR rather than the
 * fallback. A model reads a conversation better than any list of phrases will,
 * and when one is loaded it should do this job - but it must never be what
 * makes the memory work at all.
 */
interface IAtomExtractor {
    val name: String
    fun extract(episode: EpisodicMemoryEntry, subject: String? = null): List<AtomCandidate>
}
