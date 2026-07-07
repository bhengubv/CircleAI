// EpisodicStoreTest.kt
//
// Verifies InMemoryEpisodicStore: cosine similarity search, recency fallback,
// FIFO capacity eviction, prune, and count. Mirrors the TS pilot
// (tests/episodic_store.test.ts) and Go port (tests/episodic_store_test.go).

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class EpisodicStoreTest {

    private fun entry(
        id: String,
        userText: String = "u",
        assistantText: String = "a",
        recordedAtUtc: Instant = Instant.parse("2026-01-01T00:00:00Z"),
        embedding: FloatArray? = null,
    ): EpisodicEntry = EpisodicEntry(
        id = id,
        userText = userText,
        assistantText = assistantText,
        recordedAtUtc = recordedAtUtc,
        embedding = embedding,
    )

    // ── cosine search ──────────────────────────────────────────────────────────

    @Test
    fun `ranks the nearest embedding first`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("x", userText = "x-axis", embedding = floatArrayOf(1f, 0f)))
        store.addAsync(entry("y", userText = "y-axis", embedding = floatArrayOf(0f, 1f)))

        val hits = store.searchAsync(floatArrayOf(1f, 0f), 2)
        assertEquals(2, hits.size)
        assertEquals("x", hits[0].id)
        assertEquals("y", hits[1].id)
    }

    @Test
    fun `respects topK`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("a", embedding = floatArrayOf(1f, 0f)))
        store.addAsync(entry("b", embedding = floatArrayOf(0.9f, 0.1f)))
        store.addAsync(entry("c", embedding = floatArrayOf(0f, 1f)))

        val hits = store.searchAsync(floatArrayOf(1f, 0f), 1)
        assertEquals(1, hits.size)
        assertEquals("a", hits[0].id)
    }

    @Test
    fun `ignores entries whose embedding dimension differs from the query`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("ok", embedding = floatArrayOf(1f, 0f)))
        store.addAsync(entry("wrongdim", embedding = floatArrayOf(1f, 0f, 0f)))

        val hits = store.searchAsync(floatArrayOf(1f, 0f), 5)
        assertEquals(1, hits.size)
        assertEquals("ok", hits[0].id)
    }

    // ── recency fallback ───────────────────────────────────────────────────────

    @Test
    fun `returns newest-first when the query embedding is null`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("old", recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z")))
        store.addAsync(entry("new", recordedAtUtc = Instant.parse("2026-06-01T00:00:00Z")))

        val hits = store.searchAsync(null, 5)
        assertEquals("new", hits[0].id)
        assertEquals("old", hits[1].id)
    }

    @Test
    fun `treats an empty embedding as no embedding (recency)`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("old", recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z")))
        store.addAsync(entry("new", recordedAtUtc = Instant.parse("2026-06-01T00:00:00Z")))

        val hits = store.searchAsync(floatArrayOf(), 1)
        assertEquals("new", hits[0].id)
    }

    // ── capacity + maintenance ─────────────────────────────────────────────────

    @Test
    fun `evicts oldest entries beyond maxEntries (FIFO)`() = runTest {
        val store = InMemoryEpisodicStore(2)
        store.addAsync(entry("a"))
        store.addAsync(entry("b"))
        store.addAsync(entry("c"))

        assertEquals(2, store.countAsync())
        val recent = store.getRecentAsync(10)
        val ids = recent.map { it.id }.sorted()
        assertEquals(listOf("b", "c"), ids) // 'a' evicted
    }

    @Test
    fun `prunes entries older than the cutoff and returns the removed count`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(entry("old", recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z")))
        store.addAsync(entry("new", recordedAtUtc = Instant.parse("2026-06-01T00:00:00Z")))

        val removed = store.pruneOlderThanAsync(Instant.parse("2026-03-01T00:00:00Z"))
        assertEquals(1, removed)
        assertEquals(1, store.countAsync())
        val remaining = store.getRecentAsync(10)
        assertEquals("new", remaining[0].id)
    }

    @Test
    fun `rejects a non-positive maxEntries`() {
        assertFailsWith<IllegalArgumentException> { InMemoryEpisodicStore(0) }
    }
}
