// FusedRecallTest.kt
//
// Verifies FusedRecall: Reciprocal Rank Fusion order, cross-source reinforcement,
// cold-start degradation to episodic, the graph confidence gate, empty-query
// short-circuit, and dedup by normalised text. Mirrors the TS pilot
// (tests/fused_recall.test.ts) and Go port.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.FusedRecall
import com.bhengubv.circleai.memory.brain.IEpisodicStore
import com.bhengubv.circleai.memory.brain.IHippoRagStore
import com.bhengubv.circleai.memory.brain.MemoryHit
import com.bhengubv.circleai.memory.brain.MemoryItem
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class FusedRecallTest {

    // ── Test doubles ────────────────────────────────────────────────────────────

    private fun ep(id: String, userText: String): EpisodicEntry = EpisodicEntry(
        id = id,
        userText = userText,
        assistantText = "",
        recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z"),
    )

    /** Episodic store that returns a fixed, pre-ranked list from searchAsync. */
    private class FakeEpisodic(private val hits: List<EpisodicEntry>) : IEpisodicStore {
        override suspend fun addAsync(entry: EpisodicEntry) {}
        override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<EpisodicEntry> =
            hits.take(topK)
        override suspend fun getRecentAsync(count: Int): List<EpisodicEntry> = hits.take(count)
        override suspend fun countAsync(): Int = hits.size
        override suspend fun pruneOlderThanAsync(cutoff: Instant): Int = 0
    }

    /** HippoRAG store that returns a fixed, pre-ranked list from multiHopRecallAsync. */
    private class FakeHippo(private val hits: List<MemoryHit>) : IHippoRagStore {
        override val backendId: String = "fake-hippo"
        override suspend fun indexAsync(item: MemoryItem) {}
        override suspend fun multiHopRecallAsync(query: String, topK: Int): List<MemoryHit> =
            hits.take(topK)
    }

    private fun graphHit(id: String, text: String, confidence: String? = null): MemoryHit {
        val metadata = if (confidence == null) null else mapOf("confidence" to confidence)
        return MemoryHit(MemoryItem(id, text, metadata), 1f)
    }

    // ── RRF ordering ────────────────────────────────────────────────────────────

    @Test
    fun `a memory surfaced by BOTH sources outranks one surfaced by only one`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "A"), ep("b", "B"), ep("c", "C")))
        val graph = FakeHippo(listOf(graphHit("g", "B"))) // reinforces B
        val recall = FusedRecall(episodic, graph)

        val hits = recall.recallAsync("q", null, 5)
        assertEquals(listOf("B", "A", "C"), hits.map { it.item.text })
    }

    @Test
    fun `cold-start (no graph) yields the episodic order unchanged`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "A"), ep("b", "B"), ep("c", "C")))
        val recall = FusedRecall(episodic, null)

        val hits = recall.recallAsync("q", null, 5)
        assertEquals(listOf("A", "B", "C"), hits.map { it.item.text })
    }

    @Test
    fun `respects topK`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "A"), ep("b", "B"), ep("c", "C")))
        val recall = FusedRecall(episodic, null)

        val hits = recall.recallAsync("q", null, 2)
        assertEquals(2, hits.size)
        assertEquals(listOf("A", "B"), hits.map { it.item.text })
    }

    // ── integrity gates ─────────────────────────────────────────────────────────

    @Test
    fun `drops graph hits below the confidence threshold`() = runTest {
        val episodic = FakeEpisodic(emptyList())
        val graph = FakeHippo(listOf(graphHit("low", "LOW", "0.2"), graphHit("high", "HIGH", "0.9")))
        val recall = FusedRecall(episodic, graph)

        val hits = recall.recallAsync("q", null, 5)
        val texts = hits.map { it.item.text }
        assertFalse(texts.contains("LOW"), "below-threshold hit must be dropped")
        assertTrue(texts.contains("HIGH"))
    }

    @Test
    fun `keeps graph hits that carry no confidence metadata (gate is a no-op)`() = runTest {
        val episodic = FakeEpisodic(emptyList())
        val graph = FakeHippo(listOf(graphHit("g", "NOCONF")))
        val recall = FusedRecall(episodic, graph)

        val hits = recall.recallAsync("q", null, 5)
        assertEquals(listOf("NOCONF"), hits.map { it.item.text })
    }

    @Test
    fun `skips the graph entirely for an empty or whitespace query`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "A")))
        val graph = FakeHippo(listOf(graphHit("g", "GRAPH")))
        val recall = FusedRecall(episodic, graph)

        val hits = recall.recallAsync("   ", null, 5)
        val texts = hits.map { it.item.text }
        assertEquals(listOf("A"), texts)
        assertFalse(texts.contains("GRAPH"))
    }

    @Test
    fun `degrades to episodic when the graph throws`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "A")))
        val throwing = object : IHippoRagStore {
            override val backendId: String = "boom"
            override suspend fun indexAsync(item: MemoryItem) {}
            override suspend fun multiHopRecallAsync(query: String, topK: Int): List<MemoryHit> =
                throw RuntimeException("graph unavailable")
        }
        val recall = FusedRecall(episodic, throwing)

        val hits = recall.recallAsync("q", null, 5)
        assertEquals(listOf("A"), hits.map { it.item.text })
    }

    // ── dedup ───────────────────────────────────────────────────────────────────

    @Test
    fun `fuses two hits with the same normalised text into one entry`() = runTest {
        val episodic = FakeEpisodic(listOf(ep("a", "Durban  Weather")))
        val graph = FakeHippo(listOf(graphHit("g", "durban weather"))) // same key
        val recall = FusedRecall(episodic, graph)

        val hits = recall.recallAsync("q", null, 5)
        assertEquals(1, hits.size)
    }
}
