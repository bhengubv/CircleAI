// ShardKvCodecTest.kt
//
// Pins CircleAI.Core.Compression.ShardKvCodec to the C# reference. The wire
// bytes (CompressedK / CompressedV) and the seeded VQ codebook are captured
// from the real C# codec (bin/Release/net9.0/CircleAI.Core.dll) and asserted
// byte-for-byte, so a frame encoded by Kotlin decodes identically in C#.

package com.bhengubv.circleai.core

import org.junit.jupiter.api.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class ShardKvCodecTest {

    private fun hex(b: ByteArray): String =
        b.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    // ── DotNetRandom parity — pins System.Random(seed).NextDouble() ──────────

    @Test
    fun `DotNetRandom reproduces System_Random(0) sequence`() {
        val rng = DotNetRandom(0)
        val expected = doubleArrayOf(
            0.7262432699679598, 0.8173253595909687, 0.7680226893946634,
            0.5581611914365372, 0.2060331540210327, 0.5588847946184151,
        )
        for (e in expected) assertEquals(e, rng.nextDouble(), 0.0)
    }

    @Test
    fun `DotNetRandom reproduces System_Random(42) sequence`() {
        val rng = DotNetRandom(42)
        val expected = doubleArrayOf(
            0.6681064659115423, 0.14090729837348093, 0.12551828945312568, 0.5227642760252413,
        )
        for (e in expected) assertEquals(e, rng.nextDouble(), 0.0)
    }

    // ── Wire-format parity — captured from the C# codec ──────────────────────

    @Test
    fun `Encode produces the exact C# wire bytes (kDim=4 kRank=2 vDim=4 vCodewords=4 seed=0)`() {
        val codec = ShardKvCodec(kDim = 4, kRank = 2, vDim = 4, vCodewords = 4, vCodebookSeed = 0)
        val k = floatArrayOf(1f, 2f, 3f, 4f)
        val v = floatArrayOf(0.5f, -0.5f, 0.25f, -0.25f)
        val frame = codec.encode(k, v)

        assertEquals("8542a13d7fe7", hex(frame.compressedK))
        assertEquals("02", hex(frame.compressedV))
        assertContentEquals(
            floatArrayOf(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f),
            frame.kPrincipalAxes,
        )
        assertEquals(4, frame.kOriginalDim)
        assertEquals(4, frame.vOriginalDim)
    }

    @Test
    fun `Decode reconstructs the exact C# K and V (round-trip through wire bytes)`() {
        val codec = ShardKvCodec(kDim = 4, kRank = 2, vDim = 4, vCodewords = 4, vCodebookSeed = 0)
        val k = floatArrayOf(1f, 2f, 3f, 4f)
        val v = floatArrayOf(0.5f, -0.5f, 0.25f, -0.25f)
        val (dk, dv) = codec.decode(codec.encode(k, v))

        assertContentEquals(
            floatArrayOf(2.007874f, 2.992126f, 2.007874f, 2.992126f),
            dk,
        )
        // V decodes to seeded codeword index 2.
        assertContentEquals(
            floatArrayOf(0.9550995f, -0.4525911f, -0.41618744f, -0.0653706f),
            dv,
        )
    }

    @Test
    fun `Encode with observeK training matches C# (kDim=8 kRank=4 vDim=4 vCodewords=16 seed=7)`() {
        val codec = ShardKvCodec(kDim = 8, kRank = 4, vDim = 4, vCodewords = 16, vCodebookSeed = 7)
        codec.observeK(floatArrayOf(1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f))
        codec.observeK(floatArrayOf(0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f))
        assertEquals(2L, codec.samplesObserved)

        val k = floatArrayOf(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f)
        val v = floatArrayOf(1f, 2f, 3f, 4f)
        val frame = codec.encode(k, v)

        assertEquals("d2b5a73c7feca800", hex(frame.compressedK))
        assertEquals("0d", hex(frame.compressedV))
    }

    @Test
    fun `seeded codebook values match C# (dim=4 count=4 seed=0)`() {
        // Probe each codeword by decoding a hand-crafted frame with V index = idx.
        val codec = ShardKvCodec(kDim = 2, kRank = 1, vDim = 4, vCodewords = 4, vCodebookSeed = 0)
        val expected = arrayOf(
            floatArrayOf(0.45248654f, 0.6346507f, 0.5360454f, 0.11632238f),
            floatArrayOf(-0.5879337f, 0.11776959f, 0.81205416f, -0.115644254f),
            floatArrayOf(0.9550995f, -0.4525911f, -0.41618744f, -0.0653706f),
            floatArrayOf(0.26531816f, -0.060976245f, 0.9643025f, -0.939266f),
        )
        for (idx in 0 until 4) {
            val frame = ShardCompressedFrame(
                compressedK = byteArrayOf(0, 0, 0, 0, 0), // scale=0 float32 + 1 int8 (kRank=1)
                compressedV = byteArrayOf(idx.toByte()),
                kPrincipalAxes = floatArrayOf(1f, 0f),
                kOriginalDim = 2,
                vOriginalDim = 4,
            )
            val (_, dv) = codec.decode(frame)
            assertContentEquals(expected[idx], dv, "codeword $idx")
        }
    }

    // ── Behavioural coverage ─────────────────────────────────────────────────

    @Test
    fun `constructor rejects invalid arguments`() {
        assertFailsWith<IndexOutOfBoundsException> { ShardKvCodec(0, 1, 4, 4) }
        assertFailsWith<IndexOutOfBoundsException> { ShardKvCodec(4, 5, 4, 4) } // kRank > kDim
        assertFailsWith<IndexOutOfBoundsException> { ShardKvCodec(4, 2, 0, 4) }
        assertFailsWith<IndexOutOfBoundsException> { ShardKvCodec(4, 2, 4, 3) } // not power of 2
        assertFailsWith<IndexOutOfBoundsException> { ShardKvCodec(4, 2, 4, 1) } // must be > 1
    }

    @Test
    fun `encode rejects dimension mismatch`() {
        val codec = ShardKvCodec(4, 2, 4, 4)
        assertFailsWith<IllegalArgumentException> { codec.encode(floatArrayOf(1f, 2f), floatArrayOf(1f, 2f, 3f, 4f)) }
        assertFailsWith<IllegalArgumentException> { codec.encode(floatArrayOf(1f, 2f, 3f, 4f), floatArrayOf(1f)) }
    }

    @Test
    fun `observeK rejects dimension mismatch`() {
        val codec = ShardKvCodec(4, 2, 4, 4)
        assertFailsWith<IllegalArgumentException> { codec.observeK(floatArrayOf(1f, 2f)) }
    }

    @Test
    fun `decode rejects frame dim mismatch`() {
        val codec = ShardKvCodec(4, 2, 4, 4)
        val frame = codec.encode(floatArrayOf(1f, 2f, 3f, 4f), floatArrayOf(1f, 2f, 3f, 4f))
        val wrongK = ShardCompressedFrame(frame.compressedK, frame.compressedV, frame.kPrincipalAxes, 8, 4)
        assertFailsWith<IllegalArgumentException> { codec.decode(wrongK) }
    }

    @Test
    fun `setPrincipalAxes and setVCodebook validate shape`() {
        val codec = ShardKvCodec(4, 2, 4, 4)
        assertFailsWith<IllegalArgumentException> { codec.setPrincipalAxes(arrayOf(FloatArray(4))) } // wrong rank
        assertFailsWith<IllegalArgumentException> { codec.setVCodebook(arrayOf(FloatArray(4))) } // wrong count
        // Valid replacement round-trips.
        codec.setPrincipalAxes(arrayOf(floatArrayOf(1f, 0f, 0f, 0f), floatArrayOf(0f, 1f, 0f, 0f)))
        codec.setVCodebook(Array(4) { FloatArray(4) { 0f } })
        val frame = codec.encode(floatArrayOf(1f, 2f, 3f, 4f), floatArrayOf(0f, 0f, 0f, 0f))
        assertEquals("00", hex(frame.compressedV)) // all-zero codebook → index 0
    }

    @Test
    fun `larger codebook widths select the correct index byte width`() {
        // vCodewords=256 → 1 byte; 65536 → 2 bytes; 131072 → 4 bytes.
        assertEquals(1, ShardKvCodec(8, 4, 4, 256).encode(FloatArray(8), FloatArray(4)).compressedV.size)
        assertEquals(2, ShardKvCodec(8, 4, 4, 65536).encode(FloatArray(8), FloatArray(4)).compressedV.size)
        assertEquals(4, ShardKvCodec(8, 4, 4, 131072).encode(FloatArray(8), FloatArray(4)).compressedV.size)
    }

    @Test
    fun `ShardCompressedFrame equals and hashCode are content-based`() {
        val a = ShardCompressedFrame(byteArrayOf(1, 2), byteArrayOf(3), floatArrayOf(1f), 2, 4)
        val b = ShardCompressedFrame(byteArrayOf(1, 2), byteArrayOf(3), floatArrayOf(1f), 2, 4)
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertTrue(a != ShardCompressedFrame(byteArrayOf(1, 9), byteArrayOf(3), floatArrayOf(1f), 2, 4))
    }
}
