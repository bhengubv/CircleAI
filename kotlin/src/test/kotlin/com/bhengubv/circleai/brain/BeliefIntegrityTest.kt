// BeliefIntegrityTest.kt
//
// Verifies the memory-integrity core: attribution discipline (self/other/world),
// and SelfBeliefStore filtering, revision (supersede), correction (retract), and
// provenance. The headline guarantee: "my mother is diabetic" never becomes a
// fact about the user. Mirrors the TS pilot (tests/belief_integrity.test.ts) and
// Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.companion.brain.Attribution
import com.bhengubv.circleai.companion.brain.HeuristicBeliefExtractor
import com.bhengubv.circleai.companion.brain.PersonalBelief
import com.bhengubv.circleai.companion.brain.SelfBeliefStore
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class BeliefIntegrityTest {

    private val ex = HeuristicBeliefExtractor()

    private suspend fun one(text: String): PersonalBelief {
        val beliefs = ex.extractAsync(text, "turn")
        assertEquals(1, beliefs.size, "expected one belief from \"$text\"")
        return beliefs[0]
    }

    // ── attribution ─────────────────────────────────────────────────────────────

    @Test
    fun `my mother is diabetic yields Other about the mother`() = runTest {
        val b = one("my mother is diabetic")
        assertEquals(Attribution.Other, b.attribution)
        assertEquals("mother", b.subject)
        assertEquals("diabetic", b.obj)
    }

    @Test
    fun `i am vegetarian yields Self about the user`() = runTest {
        val b = one("i am vegetarian")
        assertEquals(Attribution.Self, b.attribution)
        assertEquals("user", b.subject)
        assertEquals("vegetarian", b.obj)
    }

    @Test
    fun `my car is fast (my plus non-relation) yields Self`() = runTest {
        val b = one("my car is fast")
        assertEquals(Attribution.Self, b.attribution)
        assertEquals("user", b.subject)
    }

    @Test
    fun `a bare relation as subject yields Other`() = runTest {
        val b = one("brother lives in Cape Town")
        assertEquals(Attribution.Other, b.attribution)
        assertEquals("brother", b.subject)
    }

    @Test
    fun `a general statement yields World`() = runTest {
        val b = one("paris is beautiful")
        assertEquals(Attribution.World, b.attribution)
        assertEquals("paris", b.subject)
    }

    // ── SelfBeliefStore — filtering, revision, correction ──────────────────────

    @Test
    fun `only Self beliefs become user facts - Other or World are audited`() = runTest {
        val store = SelfBeliefStore()
        for (b in ex.extractAsync("my mother is diabetic", "t1")) store.record(b)
        for (b in ex.extractAsync("i am vegetarian", "t2")) store.record(b)

        val facts = store.selfFacts()
        assertEquals(1, facts.size)
        assertEquals("vegetarian", facts[0].obj)

        // The mother's fact is remembered, but never as a user fact.
        assertFalse(facts.any { it.obj.contains("diabetic") })
        assertTrue(store.nonSelf().any { it.obj == "diabetic" })
    }

    @Test
    fun `a newer self-belief supersedes the older one on the same predicate`() {
        val store = SelfBeliefStore()
        fun mk(obj: String) = PersonalBelief(
            attribution = Attribution.Self,
            subject = "user",
            predicate = "isAbout",
            obj = obj,
            confidence = 0.6f,
            source = "t",
            recordedAtUtc = Instant.now(),
        )
        store.record(mk("vegetarian"))
        store.record(mk("vegan"))

        val facts = store.selfFacts()
        assertEquals(1, facts.size)
        assertEquals("vegan", facts[0].obj)
    }

    @Test
    fun `retract removes user facts mentioning the text`() = runTest {
        val store = SelfBeliefStore()
        for (b in ex.extractAsync("i am vegetarian", "t1")) store.record(b)
        val removed = store.retract("vegetarian")
        assertEquals(1, removed)
        assertEquals(0, store.selfFacts().size)
    }

    @Test
    fun `provenance returns the distinct source turns behind user facts`() {
        // Distinct predicates so both survive — the heuristic extractor always uses
        // "isAbout", which would (correctly) supersede one self-fact with the next.
        val store = SelfBeliefStore()
        fun mk(obj: String, predicate: String, source: String) = PersonalBelief(
            attribution = Attribution.Self,
            subject = "user",
            predicate = predicate,
            obj = obj,
            confidence = 0.6f,
            source = source,
            recordedAtUtc = Instant.now(),
        )
        store.record(mk("vegetarian", "diet", "t1"))
        store.record(mk("hiking", "hobby", "t2"))
        val prov = store.provenance().sorted()
        assertEquals(listOf("t1", "t2"), prov)
    }
}
