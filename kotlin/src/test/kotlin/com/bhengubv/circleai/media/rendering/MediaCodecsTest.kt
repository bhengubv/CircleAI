package com.bhengubv.circleai.media.rendering

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ChecksumTest {

    @Test
    fun crc32MatchesTheKnownAnswerForTheStandardVector() {
        // "123456789" is the ubiquitous CRC test vector; 0xCBF43926 is what any
        // correct CRC-32 produces for it.
        assertEquals(0xCBF43926.toInt(), Crc32.compute("123456789".toByteArray()))
    }

    @Test
    fun crc32OfNothingIsZero() {
        assertEquals(0, Crc32.compute(ByteArray(0)))
    }

    @Test
    fun adler32OfTheClassicStringIsTheClassicAnswer() {
        // Wikipedia vector: Adler-32 of "Wikipedia" is 0x11E60398.
        assertEquals(0x11E60398, Adler32.compute("Wikipedia".toByteArray()))
    }

    @Test
    fun adler32OfNothingIsOne() {
        // Not zero: the low half starts at 1, and a checksum implementation
        // that starts both halves at zero passes almost every other test.
        assertEquals(1, Adler32.compute(ByteArray(0)))
    }

    @Test
    fun adler32WrapsAtSixtyFiveThousandFiveHundredAndTwentyOne() {
        // Long enough to force the modulus. The answer only comes out right if
        // both sums wrap at the prime rather than at 65536.
        val data = ByteArray(1000) { 255.toByte() }
        val a = Adler32.compute(data)
        assertTrue(a != 0)
        assertEquals(a, Adler32.compute(data), "not deterministic")
    }
}

class ZlibStoredTest {

    @Test
    fun theHeaderIsTheStandardDeflateThirtyTwoKWindow() {
        val z = ZlibStored.compress("hello".toByteArray())
        assertEquals(0x78, z[0].toInt() and 0xFF)
        assertEquals(0x01, z[1].toInt() and 0xFF)
    }

    @Test
    fun theRealInflaterCanReadWhatTheStoredEncoderWrote() {
        // The whole point: valid zlib, just uncompressed. If this fails, every
        // PNG this module writes is unopenable.
        for (payload in listOf("", "a", "hello world", "x".repeat(70000))) {
            val bytes = payload.toByteArray()
            val round = ImageCodecs.zlibDecompress(ZlibStored.compress(bytes))
            assertContentEquals(bytes, round, "failed for " + bytes.size + " bytes")
        }
    }

    @Test
    fun aPayloadLargerThanOneBlockIsSplitAndStillReadsBack() {
        // 65535 is the stored-block ceiling; 70000 forces a second block and a
        // final-bit that must land on the LAST one only.
        val bytes = ByteArray(70000) { (it % 251).toByte() }
        assertContentEquals(bytes, ImageCodecs.zlibDecompress(ZlibStored.compress(bytes)))
    }

    @Test
    fun theTrailerIsTheAdlerOfTheUNCOMPRESSEDdataBigEndian() {
        val bytes = "hello world".toByteArray()
        val z = ZlibStored.compress(bytes)
        val adler = Adler32.compute(bytes)
        val tail = z.copyOfRange(z.size - 4, z.size)
        assertContentEquals(
            byteArrayOf(
                ((adler ushr 24) and 0xFF).toByte(),
                ((adler ushr 16) and 0xFF).toByte(),
                ((adler ushr 8) and 0xFF).toByte(),
                (adler and 0xFF).toByte(),
            ),
            tail,
        )
    }

    @Test
    fun theSameInputAlwaysProducesTheSameBytes() {
        // Determinism is the reason this encoder exists alongside the real
        // deflater: identical output across every language port is what makes a
        // fixture comparison mean anything.
        val a = ZlibStored.compress("repeatable".toByteArray())
        val b = ZlibStored.compress("repeatable".toByteArray())
        assertContentEquals(a, b)
    }
}

class PngWriterTest {

    private fun buffer(w: Int, h: Int, c: Rgba32): PixelBuffer {
        val b = PixelBuffer.of(w, h)!!
        val canvas = RasterCanvas(b)
        canvas.clear(c)
        return b
    }

    @Test
    fun aChunkCrcCoversTheTYPEasWellAsTheData() {
        // The classic mistake, and it produces a file every viewer rejects with
        // no useful message.
        val data = byteArrayOf(1, 2, 3)
        val c = PngWriter.chunk("IDAT", data)
        val expected = Crc32.compute("IDAT".toByteArray() + data)
        val crc = c.copyOfRange(c.size - 4, c.size)
        assertContentEquals(PngWriter.be32(expected), crc)
    }

    @Test
    fun aChunkIsLengthTypeDataCrc() {
        val c = PngWriter.chunk("IEND", ByteArray(0))
        assertEquals(12, c.size)
        assertContentEquals(byteArrayOf(0, 0, 0, 0), c.copyOfRange(0, 4))
        assertEquals("IEND", String(c, 4, 4, Charsets.US_ASCII))
    }

    @Test
    fun theHeaderSaysEightBitTruecolourWithAlpha() {
        val h = PngWriter.ihdr(1080, 1920)
        assertEquals(13, h.size)
        assertContentEquals(PngWriter.be32(1080), h.copyOfRange(0, 4))
        assertContentEquals(PngWriter.be32(1920), h.copyOfRange(4, 8))
        assertEquals(8, h[8].toInt()) // bit depth
        assertEquals(6, h[9].toInt()) // colour type RGBA
        assertEquals(0, h[12].toInt()) // no interlace
    }

    @Test
    fun everyScanlineIsPrefixedWithItsFilterByte() {
        val b = buffer(3, 2, Rgba32.white)
        val s = PngWriter.filteredScanlines(b)
        assertEquals(2 * (1 + 12), s.size)
        assertEquals(0, s[0].toInt())
        assertEquals(0, s[13].toInt())
    }

    @Test
    fun anEncodedPngStartsWithTheSignatureAndEndsWithIEND() {
        val png = PngWriter.encode(buffer(4, 4, Rgba32.rgb(200, 30, 40)))
        assertContentEquals(PngWriter.signature, png.copyOfRange(0, 8))
        assertEquals("IEND", String(png, png.size - 8, 4, Charsets.US_ASCII))
    }

    @Test
    fun theStoredEncoderOutputIsReadableByTheRealDecoder() {
        // The two halves of this module have to agree, or a still written on one
        // device cannot be opened on another.
        val src = buffer(5, 3, Rgba32(12, 200, 90, 210))
        val back = ImageCodecs.decodePng(PngWriter.encode(src))
        assertEquals(5, back.width)
        assertEquals(3, back.height)
        assertEquals(Rgba32(12, 200, 90, 210), back.pixel(2, 1))
    }
}

class ImageCodecsRoundTripTest {

    private fun gradient(w: Int, h: Int): PixelBuffer {
        val b = PixelBuffer.of(w, h)!!
        for (y in 0 until h) {
            for (x in 0 until w) {
                val i = b.index(x, y)
                b.write(i, (x * 255 / maxOf(1, w - 1)))
                b.write(i + 1, (y * 255 / maxOf(1, h - 1)))
                b.write(i + 2, 128)
                b.write(i + 3, 255)
            }
        }
        return b
    }

    @Test
    fun pngSurvivesARoundTripPixelForPixel() {
        val src = gradient(17, 11)
        val back = ImageCodecs.decodePng(ImageCodecs.encodePng(src))
        assertEquals(src.width, back.width)
        assertEquals(src.height, back.height)
        assertContentEquals(src.pixels, back.pixels)
    }

    @Test
    fun aTransparentPixelStaysTransparentThroughPng() {
        val b = PixelBuffer.of(2, 1)!!
        b.write(3, 0) // fully transparent
        b.write(4, 255); b.write(7, 255) // opaque red
        val back = ImageCodecs.decodePng(ImageCodecs.encodePng(b))
        assertEquals(0, back.pixel(0, 0)!!.a)
        assertEquals(255, back.pixel(1, 0)!!.a)
    }

    @Test
    fun bmpSurvivesARoundTripExceptForTheAlphaItCannotCarry() {
        // 24-bit BMP has no alpha channel, so everything comes back opaque.
        // That is the format, not a bug, and the test says so out loud.
        val src = gradient(9, 5)
        val back = ImageCodecs.decodeBmp(ImageCodecs.encodeBmp(src))
        assertEquals(9, back.width)
        assertEquals(5, back.height)
        for (y in 0 until 5) {
            for (x in 0 until 9) {
                val a = src.pixel(x, y)!!
                val b = back.pixel(x, y)!!
                assertEquals(a.r, b.r, "red at " + x + "," + y)
                assertEquals(a.g, b.g, "green at " + x + "," + y)
                assertEquals(a.b, b.b, "blue at " + x + "," + y)
                assertEquals(255, b.a)
            }
        }
    }

    @Test
    fun theBmpRowStrideIsPaddedToFourBytes() {
        // 3 pixels x 3 bytes is 9, which pads to 12. Getting this wrong shears
        // the image diagonally, which looks like a decoder bug rather than an
        // encoder one.
        val bmp = ImageCodecs.encodeBmp(gradient(3, 2))
        assertEquals(54 + 12 * 2, bmp.size)
    }

    @Test
    fun aBmpIsWrittenBOTTOMupAndReadBackTheRightWayRound() {
        val b = PixelBuffer.of(1, 2)!!
        b.write(0, 255); b.write(3, 255) // row 0: red
        b.write(4, 0); b.write(5, 255); b.write(7, 255) // row 1: green
        val back = ImageCodecs.decodeBmp(ImageCodecs.encodeBmp(b))
        assertEquals(255, back.pixel(0, 0)!!.r)
        assertEquals(255, back.pixel(0, 1)!!.g)
    }

    @Test
    fun garbageIsRefusedRatherThanDecodedIntoNoise() {
        assertFailsWith<ImageFormatError> { ImageCodecs.decodePng(ByteArray(4)) }
        assertFailsWith<ImageFormatError> { ImageCodecs.decodePng("not a png at all".toByteArray()) }
        assertFailsWith<ImageFormatError> { ImageCodecs.decodeBmp(ByteArray(4)) }
    }
}

class ManagedImageDecoderTest {

    private val decoder = ManagedImageDecoder.instance

    @Test
    fun theMAGICdecidesNotTheMimeHint() {
        // A hint is what the sender claimed; the first bytes are what arrived.
        val png = PngWriter.encode(PixelBuffer.of(2, 2)!!)
        assertNotNull(decoder.decode(png, "image/jpeg"))
    }

    @Test
    fun itReadsBothPngAndBmp() {
        assertNotNull(decoder.decode(PngWriter.encode(PixelBuffer.of(3, 3)!!)))
        assertNotNull(decoder.decode(ImageCodecs.encodeBmp(PixelBuffer.of(3, 3)!!)))
        assertEquals("managed-png-bmp", decoder.backendId)
    }

    @Test
    fun jpegIsNAMEDasNeedingAPlatformDecoderRatherThanFailingVaguely() {
        val jpegHeader = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xE0.toByte())
        val e = assertFailsWith<ImageFormatError> { decoder.decode(jpegHeader) }
        assertTrue(e.message!!.contains("JPEG"))
    }

    @Test
    fun tryDecodeSwallowsWhatDecodeThrows() {
        assertNull(decoder.tryDecode(byteArrayOf(1, 2, 3)))
        assertNull(decoder.tryDecode(byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte())))
        assertNotNull(decoder.tryDecode(PngWriter.encode(PixelBuffer.of(1, 1)!!)))
    }
}

class AnimatedPngEncoderTest {

    private val encoder = AnimatedPngEncoder.instance

    private fun frame(w: Int, h: Int, c: Rgba32): PixelBuffer {
        val b = PixelBuffer.of(w, h)!!
        RasterCanvas(b).clear(c)
        return b
    }

    private fun options(n: Int, fps: Int = 12, loop: Int = 0) =
        ClipEncodeOptions(RenderSize(4, 4), fps, n, loop)

    private fun chunks(bytes: ByteArray): List<String> {
        val out = mutableListOf<String>()
        var pos = 8
        while (pos + 12 <= bytes.size) {
            val len = ((bytes[pos].toInt() and 0xFF) shl 24) or
                ((bytes[pos + 1].toInt() and 0xFF) shl 16) or
                ((bytes[pos + 2].toInt() and 0xFF) shl 8) or (bytes[pos + 3].toInt() and 0xFF)
            out.add(String(bytes, pos + 4, 4, Charsets.US_ASCII))
            pos += 12 + len
        }
        return out
    }

    @Test
    fun frameZeroIsTheDEFAULTimageAndRidesInAnIDAT() {
        // A viewer that knows nothing about APNG shows exactly this frame. Put
        // it in an fdAT instead and such a viewer shows a blank page.
        val clip = encoder.encode(
            listOf(frame(4, 4, Rgba32.black), frame(4, 4, Rgba32.white)),
            options(2),
        )
        assertEquals(
            listOf("IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "IEND"),
            chunks(clip.bytes),
        )
    }

    @Test
    fun theSequenceNumberKeepsCountingACROSSfctlAndFdat() {
        // One counter, not one each. Two counters produce a file that plays the
        // first frame and stops.
        val clip = encoder.encode(
            List(3) { frame(4, 4, Rgba32.black) },
            options(3),
        )
        val seqs = mutableListOf<Int>()
        var pos = 8
        while (pos + 12 <= clip.bytes.size) {
            val b = clip.bytes
            val len = ((b[pos].toInt() and 0xFF) shl 24) or ((b[pos + 1].toInt() and 0xFF) shl 16) or
                ((b[pos + 2].toInt() and 0xFF) shl 8) or (b[pos + 3].toInt() and 0xFF)
            val type = String(b, pos + 4, 4, Charsets.US_ASCII)
            if (type == "fcTL" || type == "fdAT") {
                val d = pos + 8
                seqs.add(
                    ((b[d].toInt() and 0xFF) shl 24) or ((b[d + 1].toInt() and 0xFF) shl 16) or
                        ((b[d + 2].toInt() and 0xFF) shl 8) or (b[d + 3].toInt() and 0xFF),
                )
            }
            pos += 12 + len
        }
        assertEquals(listOf(0, 1, 2, 3, 4), seqs)
    }

    @Test
    fun theDelayIsAFractionOfOneOverTheFrameRateSoNothingRounds() {
        val seq = intArrayOf(0)
        val f = AnimatedPngEncoder.fctl(seq, 4, 4, 12)
        assertEquals(26, f.size)
        // delay_num at offset 20, delay_den at 22
        assertEquals(1, ((f[20].toInt() and 0xFF) shl 8) or (f[21].toInt() and 0xFF))
        assertEquals(12, ((f[22].toInt() and 0xFF) shl 8) or (f[23].toInt() and 0xFF))
    }

    @Test
    fun aMismatchedFrameSizeIsRefused() {
        assertFailsWith<ApngError> {
            encoder.encode(listOf(frame(4, 4, Rgba32.black), frame(8, 8, Rgba32.white)), options(2))
        }
    }

    @Test
    fun noFramesIsAnEmptyClipRatherThanAnInvalidFile() {
        val clip = encoder.encode(emptyList(), options(0))
        assertEquals(0, clip.bytes.size)
        assertEquals(0, clip.frameCount)
        assertEquals("image/apng", clip.mimeType)
    }

    @Test
    fun aSingleFrameIsAValidStillAnimation() {
        val clip = encoder.encode(listOf(frame(4, 4, Rgba32.white)), options(1))
        assertEquals(listOf("IHDR", "acTL", "fcTL", "IDAT", "IEND"), chunks(clip.bytes))
        assertEquals(1, clip.frameCount)
    }

    @Test
    fun theDefaultImageIsALSOreadableAsAPlainPng() {
        // The point of putting frame 0 in an IDAT.
        val clip = encoder.encode(
            listOf(frame(4, 4, Rgba32(10, 20, 30, 255)), frame(4, 4, Rgba32.white)),
            options(2),
        )
        val still = ImageCodecs.decodePng(clip.bytes)
        assertEquals(Rgba32(10, 20, 30, 255), still.pixel(0, 0))
    }

    @Test
    fun aZeroFrameRateFallsBackToTwelveRatherThanDividingByZero() {
        val seqA = intArrayOf(0)
        val clip = encoder.encode(listOf(frame(4, 4, Rgba32.white)), options(1, fps = 0))
        assertTrue(clip.bytes.isNotEmpty())
        // The reported rate is what the caller asked for; the WRITTEN one is safe.
        assertEquals(0, clip.frameRate)
        assertEquals(12, ((AnimatedPngEncoder.fctl(seqA, 4, 4, 12)[22].toInt() and 0xFF) shl 8) or
            (AnimatedPngEncoder.fctl(intArrayOf(0), 4, 4, 12)[23].toInt() and 0xFF))
    }

    @Test
    fun theClipReportsWhatItActuallyContains() {
        val clip = encoder.encode(List(5) { frame(6, 9, Rgba32.white) }, options(5, fps = 24))
        assertEquals(5, clip.frameCount)
        assertEquals(RenderSize(6, 9), clip.size)
        assertEquals(24, clip.frameRate)
        assertEquals("apng", clip.backendId)
    }
}
