// RagTest.kt
//
// Exercises RagContextBuilder + RagPipelineBuilder. Mirrors the verified
// TypeScript suite (tests/rag.test.ts) and C# RagContextBuilderTests plus the
// fluent-builder surface and the embedder ranking path.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.IEpisodicStore
import com.bhengubv.circleai.memory.brain.ITextEmbedder
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import com.bhengubv.circleai.memory.brain.RagContextBuilder
import com.bhengubv.circleai.memory.brain.RagPipelineBuilder
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class RagTest {

    private fun episodic(
        id: String = UUID.randomUUID().toString(),
        userText: String = "u",
        assistantText: String = "a",
        recordedAtUtc: Instant = Instant.parse("2026-06-01T12:34:00Z"),
        appContext: String? = null,
        embedding: FloatArray? = null,
    ): EpisodicEntry = EpisodicEntry(
        id = id,
        userText = userText,
        assistantText = assistantText,
        recordedAtUtc = recordedAtUtc,
        appContext = appContext,
        embedding = embedding,
    )

    private fun countOccurrences(text: String, token: String): Int {
        var count = 0
        var start = 0
        while (true) {
            val i = text.indexOf(token, start)
            if (i < 0) break
            count++
            start = i + token.length
        }
        return count
    }

    /** Store that always throws — used to test resilience. */
    private class ThrowingEpisodicStore : IEpisodicStore {
        override suspend fun addAsync(entry: EpisodicEntry): Unit = throw RuntimeException("store failure")
        override suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int): List<EpisodicEntry> =
            throw RuntimeException("store failure")
        override suspend fun getRecentAsync(count: Int): List<EpisodicEntry> = throw RuntimeException("store failure")
        override suspend fun countAsync(): Int = throw RuntimeException("store failure")
        override suspend fun pruneOlderThanAsync(cutoff: Instant): Int = throw RuntimeException("store failure")
    }

    // ══════════════════════════════════════════════════════════════════════
    // Empty / missing query
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `empty query returns empty`() = runTest {
        val b = RagContextBuilder(InMemoryEpisodicStore())
        assertEquals("", b.buildContextAsync(""))
    }

    @Test
    fun `whitespace query returns empty`() = runTest {
        val b = RagContextBuilder(InMemoryEpisodicStore())
        assertEquals("", b.buildContextAsync("   "))
    }

    // ══════════════════════════════════════════════════════════════════════
    // Empty store
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `empty store returns empty`() = runTest {
        val b = RagContextBuilder(InMemoryEpisodicStore())
        assertEquals("", b.buildContextAsync("hello"))
    }

    // ══════════════════════════════════════════════════════════════════════
    // Non-empty store — recency fallback (no embedder)
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `returns a formatted block with the header and both texts`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(
            episodic(
                userText = "What is SDPKT?",
                assistantText = "SDPKT is the TGN wallet.",
                recordedAtUtc = Instant.parse("2026-06-01T11:00:00Z"),
            ),
        )

        val b = RagContextBuilder(store, null, 3)
        val result = b.buildContextAsync("tell me about the wallet")

        assertNotEquals("", result)
        assertTrue(result.contains("What is SDPKT?"))
        assertTrue(result.contains("SDPKT is the TGN wallet."))
        assertTrue(result.contains("[Relevant past exchanges"))
    }

    @Test
    fun `formats the UTC timestamp as yyyy-MM-dd HH mm and labels User B`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(episodic(userText = "q", assistantText = "r", recordedAtUtc = Instant.parse("2026-06-01T09:05:00Z")))
        val b = RagContextBuilder(store, null, 1)
        val result = b.buildContextAsync("anything")
        assertTrue(result.contains("[2026-06-01 09:05 UTC]"))
        assertTrue(result.contains("User: q"))
        assertTrue(result.contains("B!: r"))
    }

    @Test
    fun `respects topK (counts bullet prefixes)`() = runTest {
        val store = InMemoryEpisodicStore()
        repeat(10) { i -> store.addAsync(episodic(userText = "question $i", assistantText = "answer $i")) }

        val b = RagContextBuilder(store, null, 2)
        val result = b.buildContextAsync("any question")
        assertEquals(2, countOccurrences(result, "• ["))
    }

    @Test
    fun `includes the app context when set`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(episodic(userText = "bid query", assistantText = "bid answer", appContext = "tgn.bidbaas"))
        val b = RagContextBuilder(store, null, 3)
        val result = b.buildContextAsync("bidding")
        assertTrue(result.contains("tgn.bidbaas"))
    }

    @Test
    fun `truncates long texts to half the per-entry budget with an ellipsis`() = runTest {
        val store = InMemoryEpisodicStore()
        val longText = "x".repeat(500)
        store.addAsync(episodic(userText = longText, assistantText = "a"))
        // maxCharsPerEntry 100 → half 50 → truncate to 49 chars + "…"
        val b = RagContextBuilder(store, null, 1, 100)
        val result = b.buildContextAsync("q")
        assertTrue(result.contains("x".repeat(49) + "…"))
        assertTrue(!result.contains("x".repeat(51)))
    }

    // ══════════════════════════════════════════════════════════════════════
    // Embedder ranking path
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `ranks by the embedding when an embedder is supplied`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(episodic(userText = "near", assistantText = "n", embedding = floatArrayOf(1f, 0f)))
        store.addAsync(episodic(userText = "far", assistantText = "f", embedding = floatArrayOf(0f, 1f)))

        // Embedder maps any query to the x-axis, so "near" should rank first.
        val embedder = object : ITextEmbedder {
            override suspend fun generateAsync(text: String): FloatArray = floatArrayOf(1f, 0f)
        }
        val b = RagContextBuilder(store, embedder, 1)
        val result = b.buildContextAsync("anything")
        assertTrue(result.contains("near"))
        assertTrue(!result.contains("far"))
    }

    @Test
    fun `falls back to recency when the embedder throws (still best-effort)`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(
            episodic(userText = "only", assistantText = "entry", recordedAtUtc = Instant.parse("2026-06-01T00:00:00Z")),
        )
        val embedder = object : ITextEmbedder {
            override suspend fun generateAsync(text: String): FloatArray = throw RuntimeException("embedder offline")
        }
        val b = RagContextBuilder(store, embedder, 3)
        val result = b.buildContextAsync("q")
        assertTrue(result.contains("only"))
    }

    // ══════════════════════════════════════════════════════════════════════
    // Resilience — store throws
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `returns empty when the store throws (RAG is best-effort)`() = runTest {
        val b = RagContextBuilder(ThrowingEpisodicStore())
        assertEquals("", b.buildContextAsync("query"))
    }

    // ══════════════════════════════════════════════════════════════════════
    // RagPipelineBuilder
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `builds from an in-memory store and produces a working builder`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(episodic(userText = "hi", assistantText = "hello"))
        val rag = RagPipelineBuilder.create().withStore(store).withTopK(2).withMaxCharsPerEntry(500).build()
        val ctx = rag.buildContextAsync("greeting")
        assertTrue(ctx.contains("hi"))
    }

    @Test
    fun `withInMemoryStore wires a fresh store`() = runTest {
        val rag = RagPipelineBuilder.create().withInMemoryStore().build()
        assertEquals("", rag.buildContextAsync("nothing stored"))
    }

    @Test
    fun `build without a store throws`() {
        assertFailsWith<IllegalStateException> { RagPipelineBuilder.create().build() }
    }

    @Test
    fun `withTopK rejects values below 1`() {
        assertFailsWith<IllegalArgumentException> { RagPipelineBuilder.create().withTopK(0) }
    }

    @Test
    fun `withMaxCharsPerEntry rejects values below 50`() {
        assertFailsWith<IllegalArgumentException> { RagPipelineBuilder.create().withMaxCharsPerEntry(49) }
    }

    @Test
    fun `withEmbedder wires the semantic-ranking seam`() = runTest {
        val store = InMemoryEpisodicStore()
        store.addAsync(episodic(userText = "near", assistantText = "n", embedding = floatArrayOf(1f, 0f)))
        store.addAsync(episodic(userText = "far", assistantText = "f", embedding = floatArrayOf(0f, 1f)))
        val embedder = object : ITextEmbedder {
            override suspend fun generateAsync(text: String): FloatArray = floatArrayOf(1f, 0f)
        }
        val rag = RagPipelineBuilder.create().withStore(store).withEmbedder(embedder).withTopK(1).build()
        val ctx = rag.buildContextAsync("q")
        assertTrue(ctx.contains("near"))
    }
}
