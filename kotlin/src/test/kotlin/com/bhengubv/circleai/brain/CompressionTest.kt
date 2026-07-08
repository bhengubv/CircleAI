// CompressionTest.kt
//
// Exercises the TurboQuant codec + the compressed store decorators. Mirrors the
// verified TypeScript suite (tests/compression.test.ts) and the C#
// TurboQuantCodecTests + CompressedStoreTests, and PINS the cross-language wire
// format against ground-truth captured from the C# codec. The encoded payload —
// the thing that is persisted and shared across devices/languages — must be
// BYTE-IDENTICAL with C#.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.BetaLloydMaxCodebook
import com.bhengubv.circleai.memory.brain.BitPacker
import com.bhengubv.circleai.memory.brain.COMPRESSED_TAG_KEY
import com.bhengubv.circleai.memory.brain.CompressedEpisodicMemoryStore
import com.bhengubv.circleai.memory.brain.CompressedMultimodalMemoryStore
import com.bhengubv.circleai.memory.brain.EmbeddingPayloadCodec
import com.bhengubv.circleai.memory.brain.EpisodicEntry
import com.bhengubv.circleai.memory.brain.InMemoryEpisodicStore
import com.bhengubv.circleai.memory.brain.InMemoryMultimodalMemoryStore
import com.bhengubv.circleai.memory.brain.MediaModality
import com.bhengubv.circleai.memory.brain.MultimodalMemoryEntry
import com.bhengubv.circleai.memory.brain.OrthogonalRotation
import com.bhengubv.circleai.memory.brain.TurboQuantCodec
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CompressionTest {

    // ── Helpers (mirror the C# / TS test helpers) ────────────────────────────

    /** Deterministic Mulberry32 PRNG so vectors are reproducible across runs. */
    private fun mulberry32(seed: Int): () -> Double {
        var a = seed
        return {
            a = a + 0x6D2B79F5
            var t = a
            // Math.imul semantics via Int overflow multiplication.
            t = (t xor (t ushr 15)) * (1 or t)
            t = ((t + ((t xor (t ushr 7)) * (61 or t))) xor t)
            ((t xor (t ushr 14)).toLong() and 0xFFFFFFFFL).toDouble() / 4294967296.0
        }
    }

    private fun randomUnit(dim: Int, seed: Int): FloatArray {
        val rng = mulberry32(seed)
        val v = DoubleArray(dim)
        var sumSq = 0.0
        for (i in 0 until dim) {
            v[i] = rng() * 2 - 1
            sumSq += v[i] * v[i]
        }
        val inv = 1 / Math.sqrt(sumSq)
        val out = FloatArray(dim)
        for (i in 0 until dim) out[i] = (v[i] * inv).toFloat()
        return out
    }

    private fun cosine(a: FloatArray, b: FloatArray): Double {
        var dot = 0.0
        var magA = 0.0
        var magB = 0.0
        for (i in a.indices) {
            dot += a[i].toDouble() * b[i]
            magA += a[i].toDouble() * a[i]
            magB += b[i].toDouble() * b[i]
        }
        val denom = Math.sqrt(magA) * Math.sqrt(magB)
        return if (denom < 1e-30) 0.0 else dot / denom
    }

    private fun hex(b: ByteArray): String {
        val sb = StringBuilder(b.size * 2)
        for (x in b) {
            val v = x.toInt() and 0xFF
            sb.append("0123456789abcdef"[v ushr 4])
            sb.append("0123456789abcdef"[v and 0x0F])
        }
        return sb.toString()
    }

    private fun episodic(
        id: String = UUID.randomUUID().toString(),
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

    private fun mm(
        sourceSha256: String,
        modality: MediaModality = MediaModality.Image,
        caption: String = "",
        embedding: FloatArray? = null,
        recordedAtUtc: Instant = Instant.now(),
        widthPx: Int? = null,
        heightPx: Int? = null,
    ): MultimodalMemoryEntry = MultimodalMemoryEntry(
        sourceSha256 = sourceSha256,
        modality = modality,
        caption = caption,
        embedding = embedding,
        recordedAtUtc = recordedAtUtc,
        widthPx = widthPx,
        heightPx = heightPx,
    )

    // ══════════════════════════════════════════════════════════════════════
    // Cross-language parity — ground truth captured from the C# codec.
    // If these break, the wire format has diverged from every other SDK lang.
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `BitPacker pack matches C# for 2-3-4-bit index arrays`() {
        assertEquals("9c63", hex(BitPacker.pack(intArrayOf(0, 3, 1, 2, 3, 0, 2, 1), 2)))
        assertEquals("f81a8b", hex(BitPacker.pack(intArrayOf(0, 7, 3, 5, 1, 6, 2, 4), 3)))
        assertEquals("0f78e169", hex(BitPacker.pack(intArrayOf(15, 0, 8, 7, 1, 14, 9, 6), 4)))
    }

    @Test
    fun `BetaLloydMaxCodebook centroids match C# (FP32-exact)`() {
        val cb = BetaLloydMaxCodebook.get(2, 8)
        assertEquals(
            listOf(-0.5048246383666992f, -0.15792210400104523f, 0.15792210400104523f, 0.5048246383666992f),
            cb.centroids.toList(),
        )
        val cb4 = BetaLloydMaxCodebook.get(4, 16)
        assertEquals(
            listOf(
                -0.6039019227027893f, -0.4742901921272278f, -0.37855634093284607f, -0.2978082597255707f,
                -0.2253989577293396f, -0.1580331176519394f, -0.09372113645076752f, -0.031065061688423157f,
                0.031065061688423157f, 0.09372113645076752f, 0.1580331176519394f, 0.2253989577293396f,
                0.2978082597255707f, 0.37855634093284607f, 0.4742901921272278f, 0.6039019227027893f,
            ),
            cb4.centroids.toList(),
        )
    }

    @Test
    fun `encodes an 8-dim vector to the exact C# base64 payload (2-bit and 4-bit)`() {
        val v8 = floatArrayOf(0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f)
        // Byte-identical to what CircleAI.Memory.Compression emits.
        assertEquals("VFEzAQIAAAAIAAAAEdK2P9B5", EmbeddingPayloadCodec.encodeBase64(v8, 2))
        assertEquals("VFEzAQQAAAAIAAAAEdK2PzPHpV4=", EmbeddingPayloadCodec.encodeBase64(v8, 4))
        assertEquals("54513301020000000800000011d2b63fd079", hex(EmbeddingPayloadCodec.encode(v8, 2)))
        assertEquals("54513301040000000800000011d2b63f33c7a55e", hex(EmbeddingPayloadCodec.encode(v8, 4)))
    }

    @Test
    fun `stores the exact C# norm in the payload`() {
        val v8 = floatArrayOf(0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f)
        assertEquals(1.4282857179641724f, TurboQuantCodec.encode(v8, 2).norm)
    }

    @Test
    fun `encodes a tiny 4-dim vector to the exact C# byte layout`() {
        val v4 = floatArrayOf(1f, 2f, 3f, 4f)
        assertEquals("5451330102000000040000006f45af409c", hex(EmbeddingPayloadCodec.encode(v4, 2)))
        assertEquals("VFEzAQIAAAAEAAAAb0WvQJw=", EmbeddingPayloadCodec.encodeBase64(v4, 2))
        assertEquals(5.4772257804870605f, TurboQuantCodec.encode(v4, 2).norm)
    }

    @Test
    fun `rotation matrix row 0 (dim=8) matches C# (FP32-exact)`() {
        val row0 = OrthogonalRotation.getMatrix(8).copyOfRange(0, 8).toList()
        assertEquals(
            listOf(
                0.32915404438972473f, -0.15729351341724396f, -0.6576523184776306f, 0.4990078806877136f,
                -0.2985365092754364f, -0.17185114324092865f, 0.024059195071458817f, 0.2572260797023773f,
            ),
            row0,
        )
    }

    // ══════════════════════════════════════════════════════════════════════
    // BitPacker
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `BitPacker round-trips indices at 1-2-3-4-8 bits`() {
        for (bits in intArrayOf(1, 2, 3, 4, 8)) {
            val max = (1 shl bits) - 1
            val rng = mulberry32(123 + bits)
            val indices = IntArray(256)
            for (i in indices.indices) indices[i] = Math.floor(rng() * (max + 1)).toInt()

            val packed = BitPacker.pack(indices, bits)
            val unpacked = BitPacker.unpack(packed, indices.size, bits)

            assertEquals(indices.size, unpacked.size)
            for (i in indices.indices) assertEquals(indices[i], unpacked[i], "bits=$bits index=$i")
        }
    }

    @Test
    fun `BitPacker byte count matches spec (1536 indices at 2 bits = 384 bytes)`() {
        assertEquals(384, BitPacker.pack(IntArray(1536), 2).size)
    }

    @Test
    fun `BitPacker rejects an overflowing index (value 4 at 2 bits)`() {
        assertFailsWith<IllegalArgumentException> { BitPacker.pack(intArrayOf(4), 2) }
    }

    @Test
    fun `BitPacker rejects an out-of-range width`() {
        assertFailsWith<IndexOutOfBoundsException> { BitPacker.pack(intArrayOf(0), 0) }
        assertFailsWith<IndexOutOfBoundsException> { BitPacker.pack(intArrayOf(0), 17) }
    }

    // ══════════════════════════════════════════════════════════════════════
    // OrthogonalRotation
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `OrthogonalRotation preserves L2 norm`() {
        val dim = 64
        val v = randomUnit(dim, 42)
        val r = FloatArray(dim)
        OrthogonalRotation.rotate(dim, v, r)
        var sqA = 0.0
        var sqR = 0.0
        for (i in 0 until dim) {
            sqA += v[i].toDouble() * v[i]
            sqR += r[i].toDouble() * r[i]
        }
        assertTrue(Math.abs(Math.sqrt(sqR) - Math.sqrt(sqA)) < 1e-3)
    }

    @Test
    fun `OrthogonalRotation rotate then unrotate recovers the input`() {
        val dim = 64
        val v = randomUnit(dim, 7)
        val r = FloatArray(dim)
        val v2 = FloatArray(dim)
        OrthogonalRotation.rotate(dim, v, r)
        OrthogonalRotation.unrotate(dim, r, v2)
        for (i in 0 until dim) assertTrue(Math.abs(v2[i] - v[i]) < 1e-3)
    }

    @Test
    fun `OrthogonalRotation is deterministic - cached across calls (same reference)`() {
        val a = OrthogonalRotation.getMatrix(32)
        val b = OrthogonalRotation.getMatrix(32)
        assertTrue(a === b) // cached: identical reference
    }

    // ══════════════════════════════════════════════════════════════════════
    // BetaLloydMaxCodebook
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `BetaLloydMaxCodebook has correct sizes`() {
        for ((bits, dim) in listOf(1 to 16, 2 to 64, 3 to 128, 4 to 256)) {
            val cb = BetaLloydMaxCodebook.get(bits, dim)
            val n = 1 shl bits
            assertEquals(n, cb.centroids.size)
            assertEquals(n - 1, cb.boundaries.size)
        }
    }

    @Test
    fun `BetaLloydMaxCodebook centroids are strictly monotonic`() {
        val cb = BetaLloydMaxCodebook.get(4, 128)
        for (i in 1 until cb.centroids.size) assertTrue(cb.centroids[i] > cb.centroids[i - 1])
    }

    @Test
    fun `BetaLloydMaxCodebook binFor round-trips through the boundaries`() {
        val cb = BetaLloydMaxCodebook.get(2, 64)
        for (i in cb.boundaries.indices) {
            val justBefore = cb.boundaries[i] - 1e-6f
            val justAfter = cb.boundaries[i] + 1e-6f
            assertEquals(i, BetaLloydMaxCodebook.binFor(justBefore, cb.boundaries))
            assertEquals(i + 1, BetaLloydMaxCodebook.binFor(justAfter, cb.boundaries))
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // TurboQuantCodec end-to-end
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `TurboQuantCodec round-trip preserves geometry`() {
        for ((dim, bits, floor) in listOf(
            Triple(64, 4, 0.99),
            Triple(128, 4, 0.99),
            Triple(256, 3, 0.96),
            Triple(512, 2, 0.85),
        )) {
            val v = randomUnit(dim, 42)
            val reconstructed = TurboQuantCodec.roundTrip(v, bits)
            assertEquals(dim, reconstructed.size)
            val cos = cosine(v, reconstructed)
            assertTrue(cos >= floor, "dim=$dim bits=$bits: cos $cos below floor $floor")
        }
    }

    @Test
    fun `TurboQuantCodec zero vector round-trips to zeros`() {
        val z = FloatArray(64)
        val r = TurboQuantCodec.roundTrip(z, 2)
        for (x in r) assertEquals(0f, x)
    }

    @Test
    fun `TurboQuantCodec payload size matches spec (1536-dim at 2 bits = 384 bytes)`() {
        assertEquals(384, TurboQuantCodec.payloadByteCount(1536, 2))
    }

    @Test
    fun `TurboQuantCodec compression ratio at 1536-dim 2-bit exceeds 15x`() {
        val ratio = TurboQuantCodec.compressionRatio(1536, 2)
        assertTrue(ratio > 15.0, "got $ratio")
        assertEquals(15.835051546391753, ratio)
    }

    @Test
    fun `TurboQuantCodec rejects invalid bit widths`() {
        val v = FloatArray(32)
        v[0] = 1f
        assertFailsWith<IndexOutOfBoundsException> { TurboQuantCodec.encode(v, 0) }
        assertFailsWith<IndexOutOfBoundsException> { TurboQuantCodec.encode(v, 9) }
    }

    @Test
    fun `TurboQuantCodec rejects a length-1 vector`() {
        assertFailsWith<IllegalArgumentException> { TurboQuantCodec.encode(floatArrayOf(1f), 2) }
    }

    @Test
    fun `TurboQuantCodec encode is deterministic across runs`() {
        val v = randomUnit(128, 7)
        val a = TurboQuantCodec.encode(v, 3)
        val b = TurboQuantCodec.encode(v, 3)
        assertEquals(a.norm, b.norm)
        assertEquals(a.packedIndices.toList(), b.packedIndices.toList())
    }

    @Test
    fun `TurboQuantCodec preserves inner product between correlated compressed vectors`() {
        val dim = 128
        val a = randomUnit(dim, 1)
        val b = randomUnit(dim, 2)
        val blended = FloatArray(dim)
        for (i in 0 until dim) blended[i] = 0.7f * a[i] + 0.3f * b[i]
        var bn = 0.0
        for (i in 0 until dim) bn += blended[i].toDouble() * blended[i]
        val invN = 1 / Math.sqrt(bn)
        for (i in 0 until dim) blended[i] = (blended[i] * invN).toFloat()

        val trueCos = cosine(a, blended)
        val aHat = TurboQuantCodec.roundTrip(a, 4)
        val blendHat = TurboQuantCodec.roundTrip(blended, 4)
        val reconCos = cosine(aHat, blendHat)
        assertTrue(Math.abs(reconCos - trueCos) <= 0.05, "true=$trueCos recon=$reconCos")
    }

    // ══════════════════════════════════════════════════════════════════════
    // EmbeddingPayloadCodec
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `EmbeddingPayloadCodec round-trip preserves cosine (4-bit at least 0_99)`() {
        val v = randomUnit(128, 42)
        val encoded = EmbeddingPayloadCodec.encode(v, 4)
        val decoded = EmbeddingPayloadCodec.decode(encoded)
        assertTrue(cosine(v, decoded) >= 0.99)
    }

    @Test
    fun `EmbeddingPayloadCodec detects its own header`() {
        val encoded = EmbeddingPayloadCodec.encode(randomUnit(64, 1), 2)
        assertTrue(EmbeddingPayloadCodec.isEncoded(encoded))
        assertTrue(!EmbeddingPayloadCodec.isEncoded(byteArrayOf(0, 1, 2)))
    }

    @Test
    fun `EmbeddingPayloadCodec rejects a too-short payload`() {
        assertFailsWith<IllegalArgumentException> { EmbeddingPayloadCodec.decode(byteArrayOf(1, 2, 3)) }
    }

    @Test
    fun `EmbeddingPayloadCodec rejects a payload without the magic header`() {
        val bad = ByteArray(20) // right length, wrong magic
        assertFailsWith<IllegalArgumentException> { EmbeddingPayloadCodec.decode(bad) }
    }

    @Test
    fun `EmbeddingPayloadCodec base64 round-trip preserves cosine (3-bit at least 0_96)`() {
        val v = randomUnit(64, 7)
        val b64 = EmbeddingPayloadCodec.encodeBase64(v, 3)
        val back = EmbeddingPayloadCodec.decodeBase64(b64)
        assertTrue(cosine(v, back) >= 0.96)
    }

    @Test
    fun `EmbeddingPayloadCodec payload at 2 bits is over 12x smaller than FP32 at 1536-dim`() {
        val v = randomUnit(1536, 42)
        val encoded = EmbeddingPayloadCodec.encode(v, 2)
        val ratio = (v.size * 4).toDouble() / encoded.size
        assertTrue(ratio > 12.0, "got $ratio")
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedEpisodicMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `CompressedEpisodic stores the embedding as a compressed tag, not a float array`() = runTest {
        val inner = InMemoryEpisodicStore()
        val outer = CompressedEpisodicMemoryStore(inner, 2)
        outer.addAsync(episodic(userText = "hello", assistantText = "hi", embedding = randomUnit(128, 1)))

        val rawRecent = inner.getRecentAsync(1)
        assertEquals(1, rawRecent.size)
        assertNull(rawRecent[0].embedding)
        assertNotNull(rawRecent[0].tags)
        assertTrue(COMPRESSED_TAG_KEY in rawRecent[0].tags!!)
    }

    @Test
    fun `CompressedEpisodic getRecent rehydrates the embedding (cosine at least 0_99 at 4-bit)`() = runTest {
        val inner = InMemoryEpisodicStore()
        val outer = CompressedEpisodicMemoryStore(inner, 4)
        val original = randomUnit(64, 1)
        outer.addAsync(episodic(embedding = original))

        val got = outer.getRecentAsync(1)
        assertEquals(1, got.size)
        assertNotNull(got[0].embedding)
        assertTrue(cosine(original, got[0].embedding!!) >= 0.99)
    }

    @Test
    fun `CompressedEpisodic search ranks by cosine through compression`() = runTest {
        val inner = InMemoryEpisodicStore()
        val outer = CompressedEpisodicMemoryStore(inner, 4)
        val v1 = randomUnit(64, 1)
        val v2 = randomUnit(64, 2)
        outer.addAsync(episodic(userText = "near", embedding = v1))
        outer.addAsync(episodic(userText = "far", embedding = v2))

        val results = outer.searchAsync(v1, 2)
        assertEquals(2, results.size)
        assertEquals("near", results[0].userText)
    }

    @Test
    fun `CompressedEpisodic search with a null query returns recency (topK respected)`() = runTest {
        val inner = InMemoryEpisodicStore()
        val outer = CompressedEpisodicMemoryStore(inner, 4)
        outer.addAsync(
            episodic(userText = "old", recordedAtUtc = Instant.parse("2026-01-01T00:00:00Z"), embedding = randomUnit(32, 1)),
        )
        outer.addAsync(
            episodic(userText = "new", recordedAtUtc = Instant.parse("2026-06-01T00:00:00Z"), embedding = randomUnit(32, 2)),
        )
        val results = outer.searchAsync(null, 1)
        assertEquals(1, results.size)
        assertEquals("new", results[0].userText)
    }

    @Test
    fun `CompressedEpisodic an entry without an embedding passes through unchanged`() = runTest {
        val inner = InMemoryEpisodicStore()
        val outer = CompressedEpisodicMemoryStore(inner)
        outer.addAsync(episodic(userText = "u", assistantText = "a"))
        val raw = inner.getRecentAsync(1)
        assertEquals(1, raw.size)
        assertNull(raw[0].embedding)
        assertTrue(raw[0].tags == null || COMPRESSED_TAG_KEY !in raw[0].tags!!)
    }

    @Test
    fun `CompressedEpisodic rejects an invalid bit width`() {
        assertFailsWith<IndexOutOfBoundsException> { CompressedEpisodicMemoryStore(InMemoryEpisodicStore(), 9) }
    }

    @Test
    fun `CompressedEpisodic exposes the compressed-tag key constant`() {
        assertEquals("x-tq-embedding", CompressedEpisodicMemoryStore.CompressedTagKey)
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedMultimodalMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `CompressedMultimodal round-trips the embedding and metadata (cosine at least 0_99 at 4-bit)`() = runTest {
        val inner = InMemoryMultimodalMemoryStore()
        val outer = CompressedMultimodalMemoryStore(inner, 4)
        val emb = randomUnit(128, 42)
        outer.addAsync(
            mm(
                sourceSha256 = "deadbeef",
                modality = MediaModality.Image,
                caption = "a sunny beach",
                embedding = emb,
                widthPx = 1920,
                heightPx = 1080,
            ),
        )

        val got = outer.getByHashAsync("deadbeef")
        assertNotNull(got)
        assertEquals("a sunny beach", got.caption)
        assertEquals(1920, got.widthPx)
        assertEquals(1080, got.heightPx)
        assertNotNull(got.embedding)
        assertTrue(cosine(emb, got.embedding!!) >= 0.99)
    }

    @Test
    fun `CompressedMultimodal inner store sees a null embedding and a compressed tag`() = runTest {
        val inner = InMemoryMultimodalMemoryStore()
        val outer = CompressedMultimodalMemoryStore(inner)
        outer.addAsync(mm(sourceSha256 = "abc", caption = "x", embedding = randomUnit(64, 1)))

        val raw = inner.getByHashAsync("abc")
        assertNotNull(raw)
        assertNull(raw.embedding)
        assertTrue(raw.tags != null && COMPRESSED_TAG_KEY in raw.tags!!)
    }

    @Test
    fun `CompressedMultimodal search ranks by cosine through compression`() = runTest {
        val inner = InMemoryMultimodalMemoryStore()
        val outer = CompressedMultimodalMemoryStore(inner, 4)
        val v1 = randomUnit(64, 1)
        val v2 = randomUnit(64, 2)
        outer.addAsync(mm(sourceSha256 = "a", caption = "near", embedding = v1))
        outer.addAsync(mm(sourceSha256 = "b", caption = "far", embedding = v2))

        val results = outer.searchAsync(v1, 2)
        assertEquals(2, results.size)
        assertEquals("near", results[0].caption)
    }

    @Test
    fun `CompressedMultimodal reinforce and prune delegate to the inner store through the decorator`() = runTest {
        val inner = InMemoryMultimodalMemoryStore()
        val outer = CompressedMultimodalMemoryStore(inner, 4)
        outer.addAsync(mm(sourceSha256 = "x", caption = "x", embedding = randomUnit(32, 1)))
        outer.reinforceAsync("x")
        val got = outer.getByHashAsync("x")
        assertEquals(2, got!!.referenceCount)
        assertEquals(1, outer.countAsync())
    }
}
