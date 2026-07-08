// TextEmbedderTest.kt
//
// Verifies CircleAI.Embeddings.TextEmbedder: lazy, gated init that resolves +
// verifies the model via IModelManager, then embeds through the injected
// backend, plus the L2-normalisation contract.

package com.bhengubv.circleai.embeddings

import com.bhengubv.circleai.core.IModelManager
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.math.sqrt
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class TextEmbedderTest {

    /** Fake manager: returns a fixed path and reports verification per [verifies]. */
    private class FakeManager(
        private val path: String = "/models/embedding",
        private val verifies: Boolean = true,
    ) : IModelManager {
        var pathCalls = 0
        var verifyCalls = 0
        override suspend fun getModelPathAsync(modelId: String): String {
            pathCalls++
            assertEquals("embedding", modelId)
            return path
        }
        override suspend fun verifyModelAsync(modelPath: String, expectedChecksum: ByteArray): Boolean {
            verifyCalls++
            return verifies
        }
        override fun close() {}
    }

    /** Deterministic backend: hashes chars into a raw vector, L2-normalised. */
    private class HashBackend(override val dimension: Int = 8) : EmbeddingBackend {
        var closed = false
        override fun embed(text: String): FloatArray {
            val v = FloatArray(dimension)
            for ((i, c) in text.withIndex()) v[i % dimension] += (c.code % 13).toFloat()
            TextEmbedder.l2Normalize(v)
            return v
        }
        override fun close() { closed = true }
    }

    @Test
    fun `generate resolves the model once, verifies it, and returns a normalised vector`() = runTest {
        val mgr = FakeManager()
        val backendBuilds = AtomicInteger(0)
        val embedder = TextEmbedder(mgr, ByteArray(32) { 1 }) { _ ->
            backendBuilds.incrementAndGet(); HashBackend()
        }

        val a = embedder.generateAsync("hello")
        val b = embedder.generateAsync("world")

        assertEquals(8, a.size)
        // Unit length (within FP tolerance).
        var norm = 0.0
        for (x in a) norm += x.toDouble() * x
        assertEquals(1.0, sqrt(norm), 1e-5)

        // Init happened exactly once despite two calls.
        assertEquals(1, backendBuilds.get())
        assertEquals(1, mgr.pathCalls)
        assertEquals(1, mgr.verifyCalls)
        assertTrue(!a.contentEquals(b))
    }

    @Test
    fun `generate rejects blank text`() = runTest {
        val embedder = TextEmbedder(FakeManager(), ByteArray(1)) { HashBackend() }
        assertFailsWith<IllegalArgumentException> { embedder.generateAsync("   ") }
    }

    @Test
    fun `generate fails when checksum verification fails`() = runTest {
        val embedder = TextEmbedder(FakeManager(verifies = false), ByteArray(1)) { HashBackend() }
        assertFailsWith<java.io.IOException> { embedder.generateAsync("x") }
    }

    @Test
    fun `dispose closes the backend and blocks further use`() = runTest {
        val backend = HashBackend()
        val embedder = TextEmbedder(FakeManager(), ByteArray(1)) { backend }
        embedder.generateAsync("warm-up")
        embedder.close()
        assertTrue(backend.closed)
        assertFailsWith<IllegalStateException> { embedder.generateAsync("x") }
    }

    @Test
    fun `l2Normalize leaves a zero vector untouched`() {
        val z = FloatArray(4)
        TextEmbedder.l2Normalize(z)
        for (x in z) assertEquals(0f, x)
    }

    @Test
    fun `l2Normalize scales to unit length`() {
        val v = floatArrayOf(3f, 4f) // norm 5
        TextEmbedder.l2Normalize(v)
        assertEquals(0.6f, v[0], 1e-6f)
        assertEquals(0.8f, v[1], 1e-6f)
    }
}
