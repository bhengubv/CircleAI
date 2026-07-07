// KgExtractorTest.kt
//
// Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin
// triples on content words, stop-word + short-word filtering, dedup, and the
// memory-id fallback to userText when no episode id is given. Mirrors the TS
// pilot (tests/kg_extractor.test.ts) and Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.HeuristicKnowledgeGraphExtractor
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class KgExtractorTest {

    private val ex = HeuristicKnowledgeGraphExtractor()

    @Test
    fun `emits a two-way link per content word, keyed by the episode id`() = runTest {
        val triples = ex.extractFromTurnAsync("Durban weather is sunny", "", "ep1")

        // content words: durban, weather, sunny  ("is" is a short stop word)
        assertEquals(6, triples.size)

        fun has(s: String, p: String, o: String) =
            triples.any { it.subject == s && it.predicate == p && it.obj == o }
        assertTrue(has("ep1", "mentions", "durban"))
        assertTrue(has("durban", "seenin", "ep1"))
        assertTrue(has("ep1", "mentions", "weather"))
        assertTrue(has("ep1", "mentions", "sunny"))
    }

    @Test
    fun `drops stop words and words shorter than 3 chars`() = runTest {
        val triples = ex.extractFromTurnAsync("I am at the shop", "", "ep2")
        val objects = triples.filter { it.predicate == "mentions" }.map { it.obj }
        // "i","am","at","the" are all stop/short; only "shop" survives.
        assertEquals(listOf("shop"), objects)
    }

    @Test
    fun `dedupes a repeated word`() = runTest {
        val triples = ex.extractFromTurnAsync("test test test", "", "ep3")
        assertEquals(2, triples.size) // one mentions + one seenin for "test"
    }

    @Test
    fun `includes assistant-side content words`() = runTest {
        val triples = ex.extractFromTurnAsync("tell me about", "Johannesburg traffic", "ep4")
        val objects = triples.filter { it.predicate == "mentions" }.map { it.obj }.sorted()
        assertEquals(listOf("johannesburg", "tell", "traffic"), objects)
    }

    @Test
    fun `falls back to userText as the memory id when no episode id is given`() = runTest {
        val triples = ex.extractFromTurnAsync("hello world", "", null)
        assertTrue(triples.any { it.subject == "hello world" && it.predicate == "mentions" })
    }

    @Test
    fun `returns nothing for an empty turn`() = runTest {
        assertEquals(emptyList(), ex.extractFromTurnAsync("", "", null))
    }

    @Test
    fun `tags every triple with the source episode id and default confidence`() = runTest {
        val triples = ex.extractFromTurnAsync("coffee", "", "ep5")
        assertTrue(triples.isNotEmpty())
        for (t in triples) {
            assertEquals("ep5", t.source)
            assertEquals(0.6f, t.confidence)
        }
    }
}
