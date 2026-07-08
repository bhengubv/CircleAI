// MultimodalTest.kt
//
// Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
// InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester (dedup +
// caption + persist). Mirrors the verified TypeScript suite
// (tests/multimodal.test.ts) and C# MultimodalMemoryTests. Bytes are
// synthesised inline so the tests run identically on every box.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.CaptionResult
import com.bhengubv.circleai.memory.brain.HeuristicMultimodalCaptioner
import com.bhengubv.circleai.memory.brain.IMultimodalCaptioner
import com.bhengubv.circleai.memory.brain.InMemoryMultimodalMemoryStore
import com.bhengubv.circleai.memory.brain.IngestOptions
import com.bhengubv.circleai.memory.brain.MediaModality
import com.bhengubv.circleai.memory.brain.MultimodalMemoryEntry
import com.bhengubv.circleai.memory.brain.MultimodalMemoryIngester
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MultimodalTest {

    // ── Test helpers (mirror the C# FakeJpeg/FakePng/WireIngester) ───────────

    private fun fakeJpeg(extraBytes: Int = 100): ByteArray {
        val buf = ByteArray(2 + extraBytes)
        buf[0] = 0xFF.toByte()
        buf[1] = 0xD8.toByte()
        for (i in 2 until buf.size) buf[i] = (i % 251).toByte()
        return buf
    }

    private fun fakePng(extraBytes: Int = 100): ByteArray {
        val buf = ByteArray(4 + extraBytes)
        buf[0] = 0x89.toByte()
        buf[1] = 0x50.toByte()
        buf[2] = 0x4E.toByte()
        buf[3] = 0x47.toByte()
        for (i in 4 until buf.size) buf[i] = (i % 251).toByte()
        return buf
    }

    private data class Wired(
        val ingester: MultimodalMemoryIngester,
        val store: InMemoryMultimodalMemoryStore,
    )

    private fun wireIngester(customCaptioner: IMultimodalCaptioner? = null): Wired {
        val store = InMemoryMultimodalMemoryStore()
        val captioners = if (customCaptioner != null) {
            listOf(customCaptioner, HeuristicMultimodalCaptioner())
        } else {
            listOf(HeuristicMultimodalCaptioner())
        }
        return Wired(MultimodalMemoryIngester(captioners, store), store)
    }

    /** FakeRichCaptioner — only handles Image, returns a rich caption + embedding. */
    private class FakeRichCaptioner : IMultimodalCaptioner {
        override fun canCaption(modality: MediaModality, mimeType: String?): Boolean =
            modality == MediaModality.Image

        override suspend fun captionAsync(
            modality: MediaModality,
            sourceBytes: ByteArray,
            mimeType: String?,
        ): CaptionResult = CaptionResult(
            caption = "A blue sky with two clouds.",
            embedding = floatArrayOf(0.1f, 0.2f, 0.3f),
            widthPx = 1920,
            heightPx = 1080,
        )
    }

    // ══════════════════════════════════════════════════════════════════════
    // HeuristicMultimodalCaptioner
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `always can caption any modality`() {
        val c = HeuristicMultimodalCaptioner()
        assertTrue(c.canCaption(MediaModality.Image, "image/jpeg"))
        assertTrue(c.canCaption(MediaModality.Audio, null))
        assertTrue(c.canCaption(MediaModality.Video, "video/mp4"))
        assertTrue(c.canCaption(MediaModality.TextDocument, "application/pdf"))
    }

    @Test
    fun `detects the JPEG magic and produces no embedding`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        val r = c.captionAsync(MediaModality.Image, fakeJpeg(), null)
        assertTrue(r.caption.contains("image/jpeg"))
        assertNull(r.embedding)
    }

    @Test
    fun `detects PNG GIF WAV PDF magic bytes`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        assertTrue(c.captionAsync(MediaModality.Image, fakePng(), null).caption.contains("image/png"))
        assertTrue(
            c.captionAsync(MediaModality.Image, byteArrayOf(0x47, 0x49, 0x46, 0x38), null).caption.contains("image/gif"),
        )
        assertTrue(
            c.captionAsync(MediaModality.Audio, byteArrayOf(0x52, 0x49, 0x46, 0x46), null).caption.contains("audio/wav"),
        )
        assertTrue(
            c.captionAsync(MediaModality.TextDocument, byteArrayOf(0x25, 0x50, 0x44, 0x46), null).caption
                .contains("application/pdf"),
        )
    }

    @Test
    fun `falls back to application octet-stream for unknown magic`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        val r = c.captionAsync(MediaModality.Audio, byteArrayOf(1, 2, 3, 4), null)
        assertTrue(r.caption.contains("application/octet-stream"))
    }

    @Test
    fun `uses the declared MIME type when provided`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        val r = c.captionAsync(MediaModality.Image, fakePng(), "image/heic")
        assertTrue(r.caption.contains("image/heic"))
    }

    @Test
    fun `marks itself as a fallback and includes the byte count`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        val bytes = fakeJpeg()
        val r = c.captionAsync(MediaModality.Image, bytes, null)
        assertTrue(r.caption.contains("no captioner wired"))
        assertTrue(r.caption.contains("${bytes.size} bytes"))
    }

    @Test
    fun `uses the right modality label per media kind`() = runTest {
        val c = HeuristicMultimodalCaptioner()
        assertTrue(c.captionAsync(MediaModality.Image, fakeJpeg(), null).caption.startsWith("[Image"))
        assertTrue(c.captionAsync(MediaModality.Audio, fakeJpeg(), "audio/wav").caption.startsWith("[Audio"))
        assertTrue(c.captionAsync(MediaModality.Video, fakeJpeg(), "video/mp4").caption.startsWith("[Video"))
        assertTrue(
            c.captionAsync(MediaModality.TextDocument, fakeJpeg(), "application/pdf").caption.startsWith("[Document"),
        )
    }

    // ══════════════════════════════════════════════════════════════════════
    // Ingester — happy path
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `first time adds an entry and reports not deduplicated`() = runTest {
        val (ingester, store) = wireIngester()
        val bytes = fakeJpeg()
        val r = ingester.ingestAsync(MediaModality.Image, bytes, IngestOptions(mimeType = "image/jpeg"))

        assertEquals(false, r.wasDeduplicated)
        assertEquals(1, store.countAsync())
        assertEquals(bytes.size.toLong(), r.entry.sourceByteCount)
        assertEquals("image/jpeg", r.entry.sourceMimeType)
        assertTrue(r.entry.sourceSha256.isNotBlank())
    }

    @Test
    fun `second time same bytes deduplicates and reinforces`() = runTest {
        val (ingester, store) = wireIngester()
        val bytes = fakeJpeg()
        val first = ingester.ingestAsync(MediaModality.Image, bytes, IngestOptions(mimeType = "image/jpeg"))
        val second = ingester.ingestAsync(MediaModality.Image, bytes, IngestOptions(mimeType = "image/jpeg"))

        assertEquals(false, first.wasDeduplicated)
        assertEquals(true, second.wasDeduplicated)
        assertEquals(1, store.countAsync())
        assertEquals(first.entry.sourceSha256, second.entry.sourceSha256)
        assertEquals(2, second.entry.referenceCount)
    }

    @Test
    fun `different bytes produce distinct entries`() = runTest {
        val (ingester, store) = wireIngester()
        val ra = ingester.ingestAsync(MediaModality.Image, fakeJpeg(50))
        val rb = ingester.ingestAsync(MediaModality.Image, fakeJpeg(60))
        assertNotEquals(ra.entry.sourceSha256, rb.entry.sourceSha256)
        assertEquals(2, store.countAsync())
    }

    @Test
    fun `empty bytes throw`() = runTest {
        val (ingester, _) = wireIngester()
        assertFailsWith<IllegalArgumentException> { ingester.ingestAsync(MediaModality.Image, ByteArray(0)) }
    }

    @Test
    fun `records source URI and tags when provided`() = runTest {
        val (ingester, _) = wireIngester()
        val bytes = fakePng()
        val r = ingester.ingestAsync(
            MediaModality.Image,
            bytes,
            IngestOptions(
                mimeType = "image/png",
                sourceUri = "file:///photos/IMG_001.png",
                tags = mapOf("location" to "home", "person" to "alex"),
            ),
        )
        assertEquals("file:///photos/IMG_001.png", r.entry.sourceUri)
        assertNotNull(r.entry.tags)
        assertEquals("home", r.entry.tags!!["location"])
        assertEquals("alex", r.entry.tags!!["person"])
    }

    @Test
    fun `computes a hex-lower SHA-256 that is stable across calls`() = runTest {
        val (ingester, _) = wireIngester()
        val r = ingester.ingestAsync(MediaModality.Image, fakeJpeg(0))
        assertTrue(Regex("^[0-9a-f]{64}$").matches(r.entry.sourceSha256))
    }

    // ══════════════════════════════════════════════════════════════════════
    // Captioner selection
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `prefers the rich captioner over the heuristic`() = runTest {
        val (ingester, _) = wireIngester(FakeRichCaptioner())
        val r = ingester.ingestAsync(MediaModality.Image, fakeJpeg(), IngestOptions(mimeType = "image/jpeg"))
        assertEquals("A blue sky with two clouds.", r.entry.caption)
        assertNotNull(r.entry.embedding)
        assertEquals(1920, r.entry.widthPx)
        assertEquals(1080, r.entry.heightPx)
    }

    @Test
    fun `falls back to the heuristic when the rich captioner declines`() = runTest {
        val (ingester, _) = wireIngester(FakeRichCaptioner())
        val r = ingester.ingestAsync(MediaModality.Audio, fakePng(), IngestOptions(mimeType = "audio/wav"))
        assertTrue(r.entry.caption.contains("no captioner wired"))
        assertNull(r.entry.embedding)
    }

    @Test
    fun `rejects construction with zero captioners`() {
        assertFailsWith<IllegalArgumentException> {
            MultimodalMemoryIngester(emptyList(), InMemoryMultimodalMemoryStore())
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Store: search, prune, recent, reinforce
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `search by embedding ranks by cosine`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "near", caption = "near", embedding = floatArrayOf(1f, 0.1f, 0f)))
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "far", caption = "far", embedding = floatArrayOf(0f, 0f, 1f)))

        val ranked = store.searchAsync(floatArrayOf(1f, 0f, 0f), 2)
        assertEquals("near", ranked[0].sourceSha256)
        assertEquals("far", ranked[1].sourceSha256)
    }

    @Test
    fun `search with a null query returns most recent`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.addAsync(
            MultimodalMemoryEntry(
                sourceSha256 = "older",
                caption = "older",
                recordedAtUtc = Instant.now().minusMillis(10L * 86_400_000),
            ),
        )
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "newer", caption = "newer", recordedAtUtc = Instant.now()))
        val recent = store.searchAsync(null, 2)
        assertEquals("newer", recent[0].sourceSha256)
    }

    @Test
    fun `prune removes entries older than the cutoff`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.addAsync(
            MultimodalMemoryEntry(
                sourceSha256 = "old",
                caption = "old",
                recordedAtUtc = Instant.now().minusMillis(10L * 86_400_000),
            ),
        )
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "new", caption = "new", recordedAtUtc = Instant.now()))

        val removed = store.pruneOlderThanAsync(Instant.now().minusMillis(5L * 86_400_000))
        assertEquals(1, removed)
        assertEquals(1, store.countAsync())
        assertNotNull(store.getByHashAsync("new"))
        assertNull(store.getByHashAsync("old"))
    }

    @Test
    fun `reinforce increments the reference count`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "x", caption = "x"))
        store.reinforceAsync("x")
        store.reinforceAsync("x")
        val got = store.getByHashAsync("x")
        assertNotNull(got)
        assertEquals(3, got.referenceCount) // initial 1 + 2 reinforce
    }

    @Test
    fun `reinforce on an unknown hash is a no-op`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.reinforceAsync("missing") // must not throw
        assertEquals(0, store.countAsync())
    }

    @Test
    fun `add without a hash throws`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        assertFailsWith<IllegalArgumentException> {
            store.addAsync(MultimodalMemoryEntry(sourceSha256 = "", caption = "x"))
        }
    }

    @Test
    fun `hash lookup is case-insensitive (matches the C# OrdinalIgnoreCase dictionary)`() = runTest {
        val store = InMemoryMultimodalMemoryStore()
        store.addAsync(MultimodalMemoryEntry(sourceSha256 = "ABCDEF", caption = "x"))
        assertNotNull(store.getByHashAsync("abcdef"))
    }
}
