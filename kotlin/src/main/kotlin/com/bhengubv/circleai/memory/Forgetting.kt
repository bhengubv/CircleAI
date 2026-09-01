// Forgetting.kt
//
// Why a memory has to let go of things, and how.
//
// A STORE THAT KEEPS EVERYTHING AT FULL VOLUME FOREVER IS A FILING CABINET.
// Ask it about deploying after a year and it hands back the same fifty things
// with the same confidence, and the one that matters is somewhere among them.
// Forgetting is not a defect of human memory being worked around; it is the
// mechanism that makes recall useful, and a memory on a phone needs it more
// than one on a server because the working set has to stay small.
//
// TWO STRENGTHS, NOT ONE, following Bjork - because a single score that goes up
// when used and down when not gets the important case backwards.
//
//   STABILITY      - how deeply the thing is learned. It ONLY EVER GROWS.
//   RETRIEVABILITY - how reachable it is right now. Decays with time, restored
//                    by being retrieved.
//
// THE PART THAT MAKES IT FEEL LIKE MEMORY: retrieving something nearly
// forgotten strengthens it far MORE than retrieving something fresh. That is
// the spacing effect, and here it falls out of the arithmetic rather than being
// bolted on - the gain is scaled by (1 - retrievability), so an atom recalled
// at the edge of fading gains most and one recalled twice in a minute gains
// almost nothing.
//
// NOTHING IS EVER DELETED. Fading means dropping out of what recall OFFERS; the
// log still has every line and the atom is still there by id. That is the
// difference between "I cannot bring it to mind" and "it never happened", and
// only the first one is memory.

package com.bhengubv.circleai.memory

import java.time.Duration
import java.time.Instant
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min

/** How worn the path to one atom is, on this machine. */
data class MemoryTrace(
    val retrievals: Int,
    val lastRetrievedUtc: Instant,
    val stabilityDays: Double,
)

object Forgetting {

    /**
     * Three months. Long enough that nothing said this quarter fades, short
     * enough that a year-old aside is not still being volunteered.
     */
    const val INITIAL_STABILITY_DAYS = 90.0

    /** Below this an atom stops being offered. */
    const val THRESHOLD = 0.05

    /** How much a retrieval at the edge of fading is worth over a fresh one. */
    const val SPACING_GAIN = 2.0

    /** How much each correction deepens the initial learning. */
    const val CORRECTION_GAIN = 0.9

    fun retrievability(stabilityDays: Double, elapsed: Duration): Double {
        if (stabilityDays <= 0) return 0.0
        val days = max(elapsed.toMillis() / 86_400_000.0, 0.0)
        return exp(-days / stabilityDays)
    }

    /**
     * The spacing effect, as arithmetic.
     *
     * Stability never falls: max() against the floor means a strengthening pass
     * cannot make something LESS learned than a brand new atom.
     */
    fun strengthened(stabilityDays: Double, retrievability: Double): Double {
        val current = max(stabilityDays, INITIAL_STABILITY_DAYS)
        val wasNearlyGone = 1.0 - retrievability.coerceIn(0.0, 1.0)
        return current * (1.0 + SPACING_GAIN * wasNearlyGone)
    }

    /**
     * A repeatedly-corrected atom starts out more deeply learned, capped at six:
     * past that the point is made and an uncapped multiplier would make one
     * much-corrected atom immortal.
     */
    fun initialStability(atom: MemoryAtom): Double =
        INITIAL_STABILITY_DAYS * (1.0 + CORRECTION_GAIN * min(atom.corrections, 6))

    /**
     * What refuses to fade.
     *
     * A RULE does not stop applying because nobody has mentioned it lately -
     * that is exactly when it gets broken. Neither does how somebody wants to
     * be spoken to. A decision or a fact can fade; a standing instruction
     * cannot.
     */
    fun floorFor(kind: AtomKind): Double = when (kind) {
        AtomKind.RULING -> 0.40
        AtomKind.RELATIONSHIP -> 0.40
        AtomKind.PREFERENCE -> 0.20
        else -> 0.00
    }

    fun reach(atom: MemoryAtom, trace: MemoryTrace?, now: Instant): Double {
        val stability = trace?.stabilityDays ?: initialStability(atom)
        // Never retrieved here: the clock starts at the last CORRECTION if there
        // was one, because being corrected is a stronger event than being filed.
        val since = trace?.lastRetrievedUtc ?: atom.lastCorrectedUtc ?: atom.recordedAtUtc
        return max(retrievability(stability, Duration.between(since, now)), floorFor(atom.kind))
    }

    fun faded(atom: MemoryAtom, trace: MemoryTrace?, now: Instant): Boolean =
        reach(atom, trace, now) < THRESHOLD
}

/**
 * How worn the path to each memory is, on THIS machine.
 *
 * WEAR IS LOCAL AND IT IS NOT MEMORY. What was decided is shared - it goes in
 * the log and travels by git, and all three machines see it. How often somebody
 * reached for it HERE is a different thing entirely: my use of a memory
 * strengthens my access to it, not yours. Syncing wear would mean one machine
 * habits deciding what another finds easy to bring to mind, which is not how
 * anything works.
 *
 * Losing it costs FAMILIARITY, not knowledge. Everything still recalls; it just
 * recalls the way it did the first week.
 *
 * It is BUFFERED, because recall is the hot path: marking a retrieval touches
 * memory and nothing else, and a crash costs the last few retrievals, which is
 * usage data rather than anything anybody said.
 */
class MemoryWear() {

    private val traces = HashMap<java.util.UUID, MemoryTrace>()
    private var dirty = false

    val count: Int get() = traces.size

    val isDirty: Boolean get() = dirty

    fun forAtom(atom: java.util.UUID): MemoryTrace? = traces[atom]

    fun reach(atom: MemoryAtom, now: Instant): Double =
        Forgetting.reach(atom, forAtom(atom.id), now)

    fun faded(atom: MemoryAtom, now: Instant): Boolean =
        Forgetting.faded(atom, forAtom(atom.id), now)

    fun retrieved(atom: MemoryAtom, now: Instant) {
        val existing = forAtom(atom.id)
        // The reach is measured BEFORE the trace is updated - that is what makes
        // the spacing effect work. Measure it after and every retrieval looks
        // fresh, and nothing ever gains anything.
        val reach = Forgetting.reach(atom, existing, now)
        traces[atom.id] = MemoryTrace(
            retrievals = (existing?.retrievals ?: 0) + 1,
            lastRetrievedUtc = now,
            stabilityDays = Forgetting.strengthened(
                existing?.stabilityDays ?: Forgetting.initialStability(atom),
                reach,
            ),
        )
        dirty = true
    }

    fun retrieved(atoms: Iterable<MemoryAtom>, now: Instant) {
        for (atom in atoms) retrieved(atom, now)
    }

    fun clear() {
        if (traces.isEmpty()) return
        traces.clear()
        dirty = true
    }

    /** The rows as they go to disk, keyed by atom id. */
    fun snapshot(): Map<java.util.UUID, MemoryTrace> = traces.toMap()

    fun restore(rows: Map<java.util.UUID, MemoryTrace>) {
        traces.clear()
        traces.putAll(rows)
        dirty = false
    }

    fun markClean() { dirty = false }
}
