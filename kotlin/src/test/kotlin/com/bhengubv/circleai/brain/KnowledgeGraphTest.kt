// KnowledgeGraphTest.kt
//
// Verifies InMemoryKnowledgeGraph (triples + nodes) and InMemoryHippoRagStore
// (Personalised PageRank multi-hop recall) — including the three precision
// guarantees: no-seed→empty, seeds excluded from results, confidence-weighting.
// Mirrors the TS pilot (tests/knowledge_graph.test.ts) and Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.InMemoryHippoRagStore
import com.bhengubv.circleai.memory.brain.InMemoryKnowledgeGraph
import com.bhengubv.circleai.memory.brain.KnowledgeNode
import com.bhengubv.circleai.memory.brain.MemoryItem
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class KnowledgeGraphTest {

    // ── InMemoryKnowledgeGraph ──────────────────────────────────────────────────

    @Test
    fun `stores and returns triples`() {
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("a", "rel", "b", "ep1", 1.0f)
        val all = kg.allTriples()
        assertEquals(1, all.size)
        assertEquals("a", all[0].subject)
        assertEquals("b", all[0].obj)
        assertEquals(1.0f, all[0].confidence)
    }

    @Test
    fun `replaces a triple with the same (subject, predicate, object)`() {
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("a", "rel", "b", "ep1", 0.5f)
        kg.addTriple("a", "rel", "b", "ep2", 0.9f)
        val all = kg.allTriples()
        assertEquals(1, all.size)
        assertEquals(0.9f, all[0].confidence)
        assertEquals("ep2", all[0].source)
    }

    @Test
    fun `upserts and fetches nodes`() {
        val kg = InMemoryKnowledgeGraph()
        kg.upsertNode(KnowledgeNode("heart", "organ", "the heart"))
        assertEquals("the heart", kg.getNode("heart")?.name)
        assertNull(kg.getNode("missing"))
    }

    @Test
    fun `rejects out-of-range confidence`() {
        val kg = InMemoryKnowledgeGraph()
        assertFailsWith<IllegalArgumentException> { kg.addTriple("a", "r", "b", null, 1.5f) }
    }

    // ── InMemoryHippoRagStore — multi-hop recall ───────────────────────────────

    @Test
    fun `reaches associated nodes across hops and excludes the seed`() = runTest {
        // chest → heart → father_cardiac_event
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("chest", "relates", "heart", "ep1", 1.0f)
        kg.addTriple("heart", "relates", "father_cardiac_event", "ep2", 1.0f)
        val hippo = InMemoryHippoRagStore(kg)

        val hits = hippo.multiHopRecallAsync("chest tightness", 5)
        val ids = hits.map { it.item.id }

        assertFalse(ids.contains("chest"), "seed node must be excluded")
        assertTrue(ids.contains("heart"), "one-hop node should be recalled")
        assertTrue(ids.contains("father_cardiac_event"), "two-hop node should be recalled")

        // One hop carries more PPR mass than two hops.
        val heart = hits.first { it.item.id == "heart" }
        val father = hits.first { it.item.id == "father_cardiac_event" }
        assertTrue(heart.score >= father.score)
    }

    @Test
    fun `returns empty when no query term touches the graph (no fabricated association)`() = runTest {
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("chest", "relates", "heart", "ep1", 1.0f)
        val hippo = InMemoryHippoRagStore(kg)

        val hits = hippo.multiHopRecallAsync("banana apple", 5)
        assertEquals(0, hits.size)
    }

    @Test
    fun `returns empty on an empty graph`() = runTest {
        val hippo = InMemoryHippoRagStore(InMemoryKnowledgeGraph())
        val hits = hippo.multiHopRecallAsync("anything", 5)
        assertEquals(0, hits.size)
    }

    @Test
    fun `confidence-weights edge spread - a stated fact outranks a guess`() = runTest {
        // root → alpha (stated, 1.0) and root → beta (guessed, 0.1)
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("root", "r", "alpha", "ep1", 1.0f)
        kg.addTriple("root", "r", "beta", "ep2", 0.1f)
        val hippo = InMemoryHippoRagStore(kg)

        val hits = hippo.multiHopRecallAsync("root", 5)
        val ids = hits.map { it.item.id }
        assertFalse(ids.contains("root"), "seed excluded")
        assertEquals("alpha", hits[0].item.id)
        assertEquals("beta", hits[1].item.id)
        assertTrue(hits[0].score > hits[1].score)
    }

    @Test
    fun `uses the node name as recall text when a node is present`() = runTest {
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("chest", "relates", "heart", "ep1", 1.0f)
        kg.upsertNode(KnowledgeNode("heart", "organ", "the heart"))
        val hippo = InMemoryHippoRagStore(kg)

        val hits = hippo.multiHopRecallAsync("chest", 5)
        val heart = hits.first { it.item.id == "heart" }
        assertEquals("the heart", heart.item.text)
    }

    @Test
    fun `indexAsync registers the item plus its metadata as graph triples`() = runTest {
        val kg = InMemoryKnowledgeGraph()
        val hippo = InMemoryHippoRagStore(kg)
        hippo.indexAsync(MemoryItem("note1", "durban weather", mapOf("topic" to "durban")))

        val preds = kg.readTriples("note1").map { it.predicate }.sorted()
        assertEquals(listOf("memory_text", "topic"), preds)
    }

    @Test
    fun `recalls a memory node reached from a query-term seed (reverse edge)`() = runTest {
        // Extractor-style reverse edge: the term "durban" points to the memory that
        // mentions it, so a forward walk from the seed reaches the memory node.
        val kg = InMemoryKnowledgeGraph()
        kg.addTriple("durban", "seenin", "note1", "ep1", 1.0f)
        kg.upsertNode(KnowledgeNode("note1", "memory", "durban weather"))
        val hippo = InMemoryHippoRagStore(kg)

        val hits = hippo.multiHopRecallAsync("durban", 5)
        val ids = hits.map { it.item.id }
        assertFalse(ids.contains("durban"), "seed excluded")
        assertTrue(ids.contains("note1"))
        assertEquals("durban weather", hits.first { it.item.id == "note1" }.item.text)
    }
}
