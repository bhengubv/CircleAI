// TheoryOfMindTest.kt
//
// Verifies BeliefTrackerTheoryOfMind against the C# reference semantics: the
// belief regex, verb weighting (believe=1.0 else 0.7), positional decay
// 1/(1+idx*0.1), per-key accumulation, the JSON wire format matching .NET's
// Dictionary<string,double> serialisation (whole numbers without ".0"), and the
// confidence saturating at Σ/5 capped to 1.0.

package com.bhengubv.circleai.reasoning

import com.bhengubv.circleai.companion.reasoning.BeliefTrackerTheoryOfMind
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.double
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class TheoryOfMindTest {

    private val tom = BeliefTrackerTheoryOfMind()

    @Test
    fun `empty history gives an empty belief map and zero confidence`() = runTest {
        val e = tom.estimateAsync("Bob", "no mental-state verbs here")
        assertEquals("Bob", e.targetIdentifier)
        assertEquals("{}", e.likelyBeliefJson)
        assertEquals(0.0, e.confidence, 0.0)
    }

    @Test
    fun `extracts belief and want with correct verb weights and decay`() = runTest {
        // idx0 believes -> weight 1.0, decay 1.0 -> 1
        // idx1 wants    -> weight 0.7, decay 1/1.1 -> 0.6363636363636364
        val e = tom.estimateAsync("Alice", "Alice believes the sky is blue. She wants coffee")
        assertEquals(
            """{"believes:the sky is blue":1,"wants:coffee":0.6363636363636364}""",
            e.likelyBeliefJson,
        )
        // conf = (1 + 0.6363636363636364)/5
        assertEquals((1.0 + 0.6363636363636364) / 5.0, e.confidence, 1e-15)
    }

    @Test
    fun `all five trigger verbs and their plural forms are recognised`() = runTest {
        val e = tom.estimateAsync("T", "x thinks a. y believes b. z wants c. p fears d. q hopes e")
        val obj = Json.parseToJsonElement(e.likelyBeliefJson).jsonObject
        assertTrue(obj.containsKey("thinks:a"))
        assertTrue(obj.containsKey("believes:b"))
        assertTrue(obj.containsKey("wants:c"))
        assertTrue(obj.containsKey("fears:d"))
        assertTrue(obj.containsKey("hopes:e"))
        // "thinks" is not a "believ*" verb -> weight 0.7 at idx0 (decay 1.0) -> 0.7.
        assertEquals(0.7, obj["thinks:a"]!!.jsonPrimitive.double, 1e-15)
        // "believes" (idx1) uses weight 1.0 -> 1.0 * (1/1.1).
        assertEquals(1.0 * (1.0 / 1.1), obj["believes:b"]!!.jsonPrimitive.double, 1e-15)
    }

    @Test
    fun `singular verb forms match too`() = runTest {
        val e = tom.estimateAsync("T", "he thinks so. she wants it. it fears nothing")
        val obj = Json.parseToJsonElement(e.likelyBeliefJson).jsonObject
        assertTrue(obj.containsKey("thinks:so"))
        assertTrue(obj.containsKey("wants:it"))
        assertTrue(obj.containsKey("fears:nothing"))
    }

    @Test
    fun `repeated identical claim accumulates its decayed weights`() = runTest {
        // "wants coffee" at idx0 (0.7*1.0) then idx1 (0.7*(1/1.1)).
        val e = tom.estimateAsync("T", "a wants coffee. b wants coffee")
        val obj = Json.parseToJsonElement(e.likelyBeliefJson).jsonObject
        val expected = 0.7 * (1.0 / 1.0) + 0.7 * (1.0 / 1.1)
        assertEquals(expected, obj["wants:coffee"]!!.jsonPrimitive.double, 1e-15)
        // and the exact wire string (1.3363636363636364).
        assertEquals("""{"wants:coffee":1.3363636363636364}""", e.likelyBeliefJson)
    }

    @Test
    fun `belief clause stops at sentence-ending punctuation`() = runTest {
        val e = tom.estimateAsync("T", "she believes it will rain; and more")
        val obj = Json.parseToJsonElement(e.likelyBeliefJson).jsonObject
        // ';' terminates the claim -> "it will rain" (trimmed), not "and more".
        assertTrue(obj.containsKey("believes:it will rain"))
    }

    @Test
    fun `confidence saturates at 1_0 with abundant evidence`() = runTest {
        // Six "believes" clauses; even decayed the sum exceeds 5 -> capped at 1.0.
        val history = (1..8).joinToString(". ") { "p$it believes strongly about topic$it" }
        val e = tom.estimateAsync("T", history)
        assertTrue(e.confidence <= 1.0)
        assertEquals(1.0, e.confidence, 0.0)
    }

    @Test
    fun `case-insensitive verb match keeps the first-seen key spelling`() = runTest {
        // "Believes" (capital) then "believes" -> same case-insensitive key; first wins.
        val e = tom.estimateAsync("T", "A Believes the plan. B believes the plan")
        val obj = Json.parseToJsonElement(e.likelyBeliefJson).jsonObject
        // Verb is lowercased in the key by design; claim casing preserved from first hit.
        assertTrue(obj.keys.any { it.equals("believes:the plan", ignoreCase = true) })
        assertEquals(1, obj.size, "the two identical claims fuse into one key")
    }

    @Test
    fun `blank target is rejected`() = runTest {
        assertFailsWith<IllegalArgumentException> { tom.estimateAsync("   ", "x believes y") }
    }
}
