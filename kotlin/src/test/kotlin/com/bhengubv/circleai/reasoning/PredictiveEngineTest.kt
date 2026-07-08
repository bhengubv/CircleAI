// PredictiveEngineTest.kt
//
// Verifies HistogramPredictiveEngine and SequencePredictiveEngine against the
// C# reference semantics: 24x7 time-of-day histogram slotting, upcoming/total
// probability, Markov n-gram back-off with 2^k weighting, mean inter-arrival
// forecasting and the horizon filter, plus the empty / invalid-argument paths.

package com.bhengubv.circleai.reasoning

import com.bhengubv.circleai.companion.reasoning.HistogramPredictiveEngine
import com.bhengubv.circleai.companion.reasoning.SequencePredictiveEngine
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class PredictiveEngineTest {

    // ── HistogramPredictiveEngine ─────────────────────────────────────────────────

    @Test
    fun `histogram surfaces an event that recurs in the current slot at probability 1`() = runTest {
        val eng = HistogramPredictiveEngine()
        val now = Instant.now()
        repeat(3) { eng.observe("coffee", now) }

        // horizon=1 => the scan runs only m=0, i.e. the current weekday+hour slot.
        val needs = eng.anticipateAsync(1)
        val coffee = needs.single { it.description == "coffee" }
        assertEquals(1.0, coffee.probability, 0.0)
    }

    @Test
    fun `histogram probability is upcoming over total across slots`() = runTest {
        val eng = HistogramPredictiveEngine()
        val now = Instant.now()
        repeat(3) { eng.observe("A", now) }         // current slot
        eng.observe("A", now.plusSeconds(12 * 3600)) // a far slot, 12h away

        val needs = eng.anticipateAsync(1)
        val a = needs.single { it.description == "A" }
        // total = 4, upcoming (current slot only) = 3.
        assertEquals(3.0 / 4.0, a.probability, 1e-12)
    }

    @Test
    fun `histogram omits events with no hits in the horizon window`() = runTest {
        val eng = HistogramPredictiveEngine()
        val now = Instant.now()
        // Only ever seen 12 hours from now -> not in a 1-minute window.
        eng.observe("far", now.plusSeconds(12 * 3600))
        eng.observe("coffee", now)

        val needs = eng.anticipateAsync(1)
        assertTrue(needs.any { it.description == "coffee" })
        assertFalse(needs.any { it.description == "far" })
    }

    @Test
    fun `histogram orders results by descending probability`() = runTest {
        val eng = HistogramPredictiveEngine()
        val now = Instant.now()
        // "sure" always in current slot (prob 1); "maybe" half in / half out (prob 0.5).
        repeat(2) { eng.observe("sure", now) }
        eng.observe("maybe", now)
        eng.observe("maybe", now.plusSeconds(12 * 3600))

        val needs = eng.anticipateAsync(1)
        val probs = needs.map { it.probability }
        assertEquals(probs.sortedDescending(), probs)
        assertEquals("sure", needs.first().description)
    }

    @Test
    fun `histogram expected-by is now plus half the horizon`() = runTest {
        val eng = HistogramPredictiveEngine()
        val now = Instant.now()
        repeat(1) { eng.observe("x", now) }
        val before = Instant.now()
        val needs = eng.anticipateAsync(60) // half = 30 minutes
        val after = Instant.now()

        val x = needs.single { it.description == "x" }
        // expectedBy ≈ callTime + 30min; allow the small window between before/after.
        assertTrue(x.expectedByUtc >= before.plusSeconds(30 * 60))
        assertTrue(x.expectedByUtc <= after.plusSeconds(30 * 60 + 1))
    }

    @Test
    fun `histogram empty engine yields nothing and rejects bad horizon`() = runTest {
        val eng = HistogramPredictiveEngine()
        assertTrue(eng.anticipateAsync(30).isEmpty())
        assertFailsWith<IllegalArgumentException> { eng.anticipateAsync(0) }
        assertFailsWith<IllegalArgumentException> { eng.observe("  ", Instant.now()) }
    }

    // ── SequencePredictiveEngine ──────────────────────────────────────────────────

    @Test
    fun `sequence predicts the deterministic next event of a repeating chain`() = runTest {
        val eng = SequencePredictiveEngine(order = 3)
        val t0 = Instant.parse("2026-01-01T08:00:00Z")
        // wake -> coffee -> email, repeated; last context ends on wake so coffee is next.
        val seq = listOf("wake", "coffee", "email", "wake", "coffee", "email", "wake")
        seq.forEachIndexed { i, ev -> eng.observe(ev, t0.plusSeconds(i * 60L)) }

        val needs = eng.anticipateAsync(horizonMinutes = 24 * 60)
        assertTrue(needs.isNotEmpty())
        // After "…email, wake", the only observed successor of wake is coffee.
        assertEquals("coffee", needs.first().description)
    }

    @Test
    fun `sequence back-off weights probabilities so a strong short-context winner leads`() = runTest {
        val eng = SequencePredictiveEngine(order = 3)
        val t0 = Instant.parse("2026-01-01T00:00:00Z")
        // "a" is almost always followed by "b".
        val seq = listOf("a", "b", "a", "b", "a", "b", "a")
        seq.forEachIndexed { i, ev -> eng.observe(ev, t0.plusSeconds(i * 120L)) }

        val needs = eng.anticipateAsync(horizonMinutes = 24 * 60)
        assertEquals("b", needs.first().description)
        // Probabilities are a normalised distribution -> sum to ~1.
        assertEquals(1.0, needs.sumOf { it.probability }, 1e-9)
    }

    @Test
    fun `sequence drops candidates whose mean inter-arrival exceeds the horizon`() = runTest {
        val eng = SequencePredictiveEngine(order = 2)
        val t0 = Instant.parse("2026-01-01T00:00:00Z")
        // "daily" recurs once per day (86400s). With a 60-minute horizon it is
        // too far out and must be filtered from the anticipated set.
        eng.observe("daily", t0)
        eng.observe("daily", t0.plusSeconds(86_400))
        eng.observe("daily", t0.plusSeconds(2 * 86_400))

        val needs = eng.anticipateAsync(horizonMinutes = 60)
        assertFalse(needs.any { it.description == "daily" })
    }

    @Test
    fun `sequence with no history yields nothing and validates arguments`() = runTest {
        val eng = SequencePredictiveEngine()
        assertTrue(eng.anticipateAsync(30).isEmpty())
        assertFailsWith<IllegalArgumentException> { eng.anticipateAsync(0) }
        assertFailsWith<IllegalArgumentException> { eng.observe("", Instant.now()) }
        assertFailsWith<IllegalArgumentException> { SequencePredictiveEngine(order = 0) }
        assertFailsWith<IllegalArgumentException> { SequencePredictiveEngine(order = 7) }
    }
}
