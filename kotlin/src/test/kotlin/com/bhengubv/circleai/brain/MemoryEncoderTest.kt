// MemoryEncoderTest.kt
//
// Verifies CompanionMemoryEncoder end-to-end: a turn handed to the background
// encoder fills the knowledge graph so associative recall can later reach the
// episode; attributed beliefs are formed off the hot path (a third party's fact
// never becomes the user's); the queue drops rather than blocks when full;
// closeAsync drains remaining work; and an extractor failure is captured, not
// fatal. Mirrors the TS pilot (tests/memory_encoder.test.ts) and Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.companion.brain.CompanionMemoryEncoder
import com.bhengubv.circleai.companion.brain.HeuristicBeliefExtractor
import com.bhengubv.circleai.companion.brain.SelfBeliefStore
import com.bhengubv.circleai.memory.brain.HeuristicKnowledgeGraphExtractor
import com.bhengubv.circleai.memory.brain.IKnowledgeGraphExtractor
import com.bhengubv.circleai.memory.brain.InMemoryHippoRagStore
import com.bhengubv.circleai.memory.brain.InMemoryKnowledgeGraph
import com.bhengubv.circleai.memory.brain.KnowledgeTriple
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MemoryEncoderTest {

    // ── end-to-end ──────────────────────────────────────────────────────────────

    @Test
    fun `encodes a turn so associative recall can reach the episode by a content word`() = runTest {
        val graph = InMemoryKnowledgeGraph()
        val enc = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)

        enc.enqueue("I love hiking in Drakensberg", "Sounds wonderful", "ep-hike")
        enc.closeAsync()

        assertTrue(graph.allTriples().isNotEmpty(), "graph should have filled from the turn")

        val hippo = InMemoryHippoRagStore(graph)
        val hits = hippo.multiHopRecallAsync("drakensberg", 5)
        val episode = hits.firstOrNull { it.item.id == "ep-hike" }
        assertNotNull(episode, "recall should reach the episode via the extracted edges")
        assertEquals("I love hiking in Drakensberg", episode.item.text)
    }

    @Test
    fun `forms attributed beliefs off the hot path - the mother's fact never becomes the user's`() = runTest {
        val graph = InMemoryKnowledgeGraph()
        val beliefs = SelfBeliefStore()
        val enc = CompanionMemoryEncoder(
            HeuristicKnowledgeGraphExtractor(),
            graph,
            HeuristicBeliefExtractor(),
            beliefs,
        )

        enc.enqueue("my mother is diabetic", "Noted", "ep1")
        enc.enqueue("i am vegetarian", "Got it", "ep2")
        enc.closeAsync()

        val facts = beliefs.selfFacts()
        assertFalse(facts.any { it.obj.contains("diabetic") }, "mother's condition must never be a user fact")
        assertTrue(facts.any { it.obj == "vegetarian" })
        assertTrue(beliefs.nonSelf().any { it.obj == "diabetic" }, "it is still remembered as an audit fact")
    }

    // ── queue behaviour ─────────────────────────────────────────────────────────

    @Test
    fun `drops writes beyond capacity rather than blocking`() = runTest {
        val graph = InMemoryKnowledgeGraph()
        val enc = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph, null, null, 2)

        // Enqueued synchronously before the drain resumes: the 3rd overflows a
        // capacity-2 queue and is dropped.
        enc.enqueue("alpha", "", "e1")
        enc.enqueue("bravo", "", "e2")
        enc.enqueue("charlie", "", "e3")
        enc.closeAsync()

        assertNotNull(graph.getNode("e1"))
        assertNotNull(graph.getNode("e2"))
        assertNull(graph.getNode("e3"), "the overflow write should have been dropped")
    }

    @Test
    fun `ignores an enqueue with a blank episode id`() = runTest {
        val graph = InMemoryKnowledgeGraph()
        val enc = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)
        enc.enqueue("hello", "", "")
        enc.enqueue("hello", "", "   ")
        enc.closeAsync()
        assertEquals(0, graph.allTriples().size)
    }

    @Test
    fun `captures an extractor failure without crashing the drain`() = runTest {
        val graph = InMemoryKnowledgeGraph()
        val throwing = object : IKnowledgeGraphExtractor {
            override suspend fun extractFromTurnAsync(
                userText: String,
                assistantText: String,
                sourceEpisodeId: String?,
            ): List<KnowledgeTriple> = throw RuntimeException("boom")
        }
        val enc = CompanionMemoryEncoder(throwing, graph)
        enc.enqueue("x", "", "e1")
        enc.closeAsync()

        val err = enc.lastError
        assertNotNull(err)
        assertEquals("boom", err.message)
        // The node was upserted before the extractor ran, so it survives.
        assertNotNull(graph.getNode("e1"))
    }
}
