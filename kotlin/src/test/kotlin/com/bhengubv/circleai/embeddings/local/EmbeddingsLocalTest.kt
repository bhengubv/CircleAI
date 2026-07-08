// EmbeddingsLocalTest.kt
//
// Verifies CircleAI.Embeddings.Local: the InMemoryEmbeddingStore (TurboQuant-
// compressed brute-force store) + InMemoryEmbeddingIndex, and pins the store's
// persistence header to the C# BinaryWriter byte layout.

package com.bhengubv.circleai.embeddings.local

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import java.io.ByteArrayOutputStream
import java.nio.file.Path
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class EmbeddingsLocalTest {

    /** Deterministic encoder: maps text length + chars into a fixed-dim vector. */
    private class FakeEncoder(override val dimension: Int = 16) : IEmbeddingEncoder {
        override suspend fun encodeAsync(text: String): FloatArray {
            val v = FloatArray(dimension)
            for ((i, c) in text.withIndex()) v[i % dimension] += ((c.code % 17) - 8).toFloat()
            if (v.all { it == 0f }) v[0] = 1f
            return v
        }
    }

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    // ── CsBinary byte-format parity (captured from C# BinaryWriter) ───────────

    @Test
    fun `CsBinary reproduces the C# BinaryWriter store header layout`() {
        val out = ByteArrayOutputStream()
        CsBinary.writeInt32(out, 0x4C455143)
        CsBinary.writeUInt16(out, 1)
        CsBinary.writeUInt16(out, 4)
        CsBinary.writeInt32(out, 1536)
        CsBinary.writeInt32(out, 2)
        CsBinary.writeString(out, "doc-1")
        CsBinary.writeString(out, "héllo wörld")
        CsBinary.writeInt32(out, 1)
        CsBinary.writeString(out, "k")
        CsBinary.writeString(out, "v")
        CsBinary.writeFloat(out, 1.4282857179641724f)
        CsBinary.writeInt32(out, 3)
        CsBinary.writeBytes(out, byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte()))

        assertEquals(
            "4351454c01000400000600000200000005646f632d310d68c3a96c6c6f2077c3b6726c6401000000016b017611d2b63f03000000aabbcc",
            hex(out.toByteArray()),
        )
    }

    @Test
    fun `CsBinary round-trips primitives and strings`() {
        val out = ByteArrayOutputStream()
        CsBinary.writeInt32(out, -12345)
        CsBinary.writeUInt16(out, 60000)
        CsBinary.writeFloat(out, 3.14159f)
        CsBinary.writeString(out, "a".repeat(200)) // forces 2-byte 7-bit length
        val inp = java.io.ByteArrayInputStream(out.toByteArray())
        assertEquals(-12345, CsBinary.readInt32(inp))
        assertEquals(60000, CsBinary.readUInt16(inp))
        assertEquals(3.14159f, CsBinary.readFloat(inp))
        assertEquals("a".repeat(200), CsBinary.readString(inp))
    }

    // ── InMemoryEmbeddingStore ───────────────────────────────────────────────

    @Test
    fun `store add via encoder then search ranks the closest document first`() = runTest {
        InMemoryEmbeddingStore(FakeEncoder(), bitsPerDim = 4).use { store ->
            store.addAsync(EmbeddingDocument("near", "the quick brown fox"))
            store.addAsync(EmbeddingDocument("far", "zzzzzzzzzz different text"))
            assertEquals(2, store.count)
            assertEquals(16, store.dimension)

            val hits = store.searchAsync("the quick brown fox", topK = 2)
            assertEquals(2, hits.size)
            assertEquals("near", hits[0].document.id)
            assertTrue(hits[0].score >= hits[1].score)
        }
    }

    @Test
    fun `store add with explicit vector and vector search`() = runTest {
        InMemoryEmbeddingStore(FakeEncoder(4), bitsPerDim = 4).use { store ->
            store.addAsync(EmbeddingDocument("a", "a"), floatArrayOf(1f, 0f, 0f, 0f))
            store.addAsync(EmbeddingDocument("b", "b"), floatArrayOf(0f, 1f, 0f, 0f))
            val hits = store.searchAsync(floatArrayOf(0.9f, 0.1f, 0f, 0f), topK = 1)
            assertEquals(1, hits.size)
            assertEquals("a", hits[0].document.id)
        }
    }

    @Test
    fun `store remove deletes by id`() = runTest {
        InMemoryEmbeddingStore(FakeEncoder(4)).use { store ->
            store.addAsync(EmbeddingDocument("x", "x"), floatArrayOf(1f, 2f, 3f, 4f))
            assertTrue(store.removeAsync("x"))
            assertFalse(store.removeAsync("x"))
            assertEquals(0, store.count)
        }
    }

    @Test
    fun `store rejects a dimension mismatch and an invalid topK`() = runTest {
        InMemoryEmbeddingStore(FakeEncoder(4)).use { store ->
            assertFailsWith<IllegalArgumentException> {
                store.addAsync(EmbeddingDocument("x", "x"), floatArrayOf(1f, 2f))
            }
            assertFailsWith<IllegalArgumentException> { store.searchAsync(floatArrayOf(1f, 2f), 1) }
            assertFailsWith<IndexOutOfBoundsException> { store.searchAsync(floatArrayOf(1f, 2f, 3f, 4f), 0) }
        }
    }

    @Test
    fun `store rejects an invalid bitsPerDim`() {
        assertFailsWith<IndexOutOfBoundsException> { InMemoryEmbeddingStore(FakeEncoder(4), bitsPerDim = 0) }
        assertFailsWith<IndexOutOfBoundsException> { InMemoryEmbeddingStore(FakeEncoder(4), bitsPerDim = 9) }
    }

    @Test
    fun `store save then load restores documents, metadata, and search`(@TempDir dir: Path) = runTest {
        val path = dir.resolve("store.celq").toString()
        val original = InMemoryEmbeddingStore(FakeEncoder(16), bitsPerDim = 4)
        original.addAsync(
            EmbeddingDocument("d1", "first document", mapOf("lang" to "en", "src" to "test")),
        )
        original.addAsync(EmbeddingDocument("d2", "second document"))
        original.saveAsync(path)
        original.close()

        InMemoryEmbeddingStore(FakeEncoder(16), bitsPerDim = 4).use { loaded ->
            loaded.loadAsync(path)
            assertEquals(2, loaded.count)
            val hits = loaded.searchAsync("first document", topK = 2)
            assertEquals("d1", hits[0].document.id)
            assertEquals(mapOf("lang" to "en", "src" to "test"), hits[0].document.metadata)
        }
    }

    @Test
    fun `store load rejects a mismatched bits-per-dim file`(@TempDir dir: Path) = runTest {
        val path = dir.resolve("s.celq").toString()
        InMemoryEmbeddingStore(FakeEncoder(16), bitsPerDim = 4).use { s ->
            s.addAsync(EmbeddingDocument("d", "text"))
            s.saveAsync(path)
        }
        InMemoryEmbeddingStore(FakeEncoder(16), bitsPerDim = 2).use { s ->
            assertFailsWith<java.io.IOException> { s.loadAsync(path) }
        }
    }

    @Test
    fun `store load rejects a missing file`(@TempDir dir: Path) = runTest {
        InMemoryEmbeddingStore(FakeEncoder(4)).use { s ->
            assertFailsWith<java.io.FileNotFoundException> { s.loadAsync(dir.resolve("nope.celq").toString()) }
        }
    }

    @Test
    fun `store persistence header begins with the CELQ magic`(@TempDir dir: Path) = runTest {
        val path = dir.resolve("magic.celq").toString()
        InMemoryEmbeddingStore(FakeEncoder(16), bitsPerDim = 4).use { s ->
            s.addAsync(EmbeddingDocument("d", "x"))
            s.saveAsync(path)
        }
        val head = java.io.File(path).readBytes().copyOfRange(0, 8)
        // int32 magic 0x4C455143 LE, uint16 version 1, uint16 bits 4.
        assertEquals("4351454c01000400", hex(head))
    }

    // ── InMemoryEmbeddingIndex ───────────────────────────────────────────────

    @Test
    fun `index add assigns sequential ids and search returns nearest`() = runTest {
        InMemoryEmbeddingIndex(4).use { idx ->
            assertEquals(0L, idx.addAsync(floatArrayOf(1f, 0f, 0f, 0f)))
            assertEquals(1L, idx.addAsync(floatArrayOf(0f, 1f, 0f, 0f)))
            assertEquals(2L, idx.addAsync(floatArrayOf(0f, 0f, 1f, 0f)))
            assertEquals(3L, idx.count)

            val hits = idx.searchAsync(floatArrayOf(0.9f, 0.1f, 0f, 0f), topK = 2)
            assertEquals(2, hits.size)
            assertEquals(0L, hits[0].internalId)
            assertTrue(hits[0].score >= hits[1].score)
        }
    }

    @Test
    fun `index search on an empty index returns nothing`() = runTest {
        InMemoryEmbeddingIndex(4).use { idx ->
            assertTrue(idx.searchAsync(floatArrayOf(1f, 0f, 0f, 0f), 3).isEmpty())
        }
    }

    @Test
    fun `index rejects dimension mismatches`() = runTest {
        InMemoryEmbeddingIndex(4).use { idx ->
            assertFailsWith<IllegalArgumentException> { idx.addAsync(floatArrayOf(1f, 2f)) }
            assertFailsWith<IllegalArgumentException> { idx.searchAsync(floatArrayOf(1f, 2f), 1) }
        }
        assertFailsWith<IndexOutOfBoundsException> { InMemoryEmbeddingIndex(0) }
    }

    @Test
    fun `index save then load restores vectors and search`(@TempDir dir: Path) = runTest {
        val path = dir.resolve("idx.ceiq").toString()
        InMemoryEmbeddingIndex(4).use { idx ->
            idx.addAsync(floatArrayOf(1f, 0f, 0f, 0f))
            idx.addAsync(floatArrayOf(0f, 0f, 1f, 0f))
            idx.saveAsync(path)
        }
        InMemoryEmbeddingIndex(4).use { idx ->
            idx.loadAsync(path)
            assertEquals(2L, idx.count)
            val hits = idx.searchAsync(floatArrayOf(0f, 0f, 0.8f, 0f), 1)
            assertEquals(1L, hits[0].internalId)
        }
    }

    @Test
    fun `index load rejects a dimension-mismatched file`(@TempDir dir: Path) = runTest {
        val path = dir.resolve("dim.ceiq").toString()
        InMemoryEmbeddingIndex(4).use { idx ->
            idx.addAsync(floatArrayOf(1f, 2f, 3f, 4f))
            idx.saveAsync(path)
        }
        InMemoryEmbeddingIndex(8).use { idx ->
            assertFailsWith<java.io.IOException> { idx.loadAsync(path) }
        }
    }

    // ── record types ─────────────────────────────────────────────────────────

    @Test
    fun `EmbeddingDocument and hits are value types`() {
        assertEquals(
            EmbeddingDocument("a", "t", mapOf("k" to "v")),
            EmbeddingDocument("a", "t", mapOf("k" to "v")),
        )
        assertEquals(EmbeddingSearchHit(EmbeddingDocument("a", "t"), 0.5f), EmbeddingSearchHit(EmbeddingDocument("a", "t"), 0.5f))
        assertEquals(EmbeddingIndexHit(3L, 0.9f), EmbeddingIndexHit(3L, 0.9f))
    }
}
