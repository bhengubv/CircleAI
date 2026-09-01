// MediaCodecs.kt
//
// PNG and BMP, written and read by hand, plus the APNG animation on top.
//
// APNG rather than a video container because it needs no codec, no licence and
// no platform decoder: every browser and every gallery already opens one, and
// a real H.264 clip needs an encoder that is not feasible in managed code on a
// low-end phone. NullVideoEncoder is where that gap is marked honestly.

package com.bhengubv.circleai.media.rendering

import java.io.ByteArrayOutputStream
import java.util.zip.Deflater
import java.util.zip.Inflater
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

object Crc32 {
    private val table: IntArray = IntArray(256) { i ->
        var c = i
        repeat(8) { c = if (c and 1 != 0) (0xEDB88320.toInt() xor (c ushr 1)) else (c ushr 1) }
        c
    }

    fun update(crc: Int, bytes: ByteArray): Int {
        var c = crc
        for (b in bytes) c = table[(c xor b.toInt()) and 0xFF] xor (c ushr 8)
        return c
    }

    fun compute(bytes: ByteArray): Int = update(-1, bytes) xor -1
}

object Adler32 {
    /** Two rolling sums mod 65521, the largest prime below 2^16. */
    fun compute(bytes: ByteArray): Int {
        var a = 1L
        var b = 0L
        for (byte in bytes) {
            a = (a + (byte.toInt() and 0xFF)) % 65521L
            b = (b + a) % 65521L
        }
        return ((b shl 16) or a).toInt()
    }
}

/**
 * A zlib stream whose deflate payload is STORED - valid, and uncompressed.
 *
 * Kept even though the JVM has a real Deflater, because this is the encoder
 * every port shares: identical bytes out of Kotlin, Swift, C and Go is what
 * makes a fixture comparison meaningful. ImageCodecs.encodePng uses the real
 * deflater where size matters and nothing is being compared.
 */
object ZlibStored {
    fun compress(data: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(0x78) // CMF: deflate, 32K window
        out.write(0x01) // FLG: no dictionary, fastest

        val maxBlock = 65535
        if (data.isEmpty()) {
            out.write(byteArrayOf(0x01, 0x00, 0x00, 0xFF.toByte(), 0xFF.toByte()))
        } else {
            var offset = 0
            while (offset < data.size) {
                val len = min(maxBlock, data.size - offset)
                // One bit for final, two for the type (00 = stored), then LEN
                // and its ONE-COMPLEMENT - which is the field a decoder checks
                // and the easy one to get wrong.
                out.write(if (offset + len >= data.size) 1 else 0)
                out.write(len and 0xFF)
                out.write((len shr 8) and 0xFF)
                val nlen = len.inv() and 0xFFFF
                out.write(nlen and 0xFF)
                out.write((nlen shr 8) and 0xFF)
                out.write(data, offset, len)
                offset += len
            }
        }

        // Adler-32 of the UNCOMPRESSED data, big-endian.
        val adler = Adler32.compute(data)
        out.write((adler ushr 24) and 0xFF)
        out.write((adler ushr 16) and 0xFF)
        out.write((adler ushr 8) and 0xFF)
        out.write(adler and 0xFF)
        return out.toByteArray()
    }
}

object PngWriter {
    val signature = byteArrayOf(
        0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    )

    /**
     * Every row is prefixed with its FILTER byte. Filter 0 (None) keeps this
     * deterministic and cheap, and the format requires the byte either way.
     */
    fun filteredScanlines(image: PixelBuffer): ByteArray {
        val stride = image.stride
        val out = ByteArray(image.height * (1 + stride))
        var di = 0
        for (y in 0 until image.height) {
            out[di++] = 0
            System.arraycopy(image.pixels, y * stride, out, di, stride)
            di += stride
        }
        return out
    }

    /**
     * length, type, data, CRC - and the CRC covers the TYPE as well as the
     * data, which is the part that is easy to miss and produces a file every
     * viewer rejects with no clue why.
     */
    fun chunk(type: String, data: ByteArray): ByteArray {
        val typeBytes = type.toByteArray(Charsets.US_ASCII)
        val out = ByteArrayOutputStream()
        out.write(be32(data.size))
        out.write(typeBytes)
        out.write(data)
        out.write(be32(Crc32.compute(typeBytes + data)))
        return out.toByteArray()
    }

    fun ihdr(width: Int, height: Int): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(be32(width))
        out.write(be32(height))
        // 8-bit, truecolour with alpha, deflate, no filter method, no interlace
        out.write(byteArrayOf(8, 6, 0, 0, 0))
        return out.toByteArray()
    }

    /** A complete single-frame PNG. */
    fun encode(image: PixelBuffer): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(signature)
        out.write(chunk("IHDR", ihdr(image.width, image.height)))
        out.write(chunk("IDAT", ZlibStored.compress(filteredScanlines(image))))
        out.write(chunk("IEND", ByteArray(0)))
        return out.toByteArray()
    }

    fun be32(v: Int): ByteArray = byteArrayOf(
        ((v ushr 24) and 0xFF).toByte(),
        ((v ushr 16) and 0xFF).toByte(),
        ((v ushr 8) and 0xFF).toByte(),
        (v and 0xFF).toByte(),
    )

    fun be16(v: Int): ByteArray =
        byteArrayOf(((v shr 8) and 0xFF).toByte(), (v and 0xFF).toByte())
}

// ------------------------------------------------------------- Clips

class EncodedClip(
    val bytes: ByteArray,
    val mimeType: String,
    val frameCount: Int,
    val size: RenderSize,
    val frameRate: Int,
    val backendId: String,
) {
    override fun equals(other: Any?): Boolean =
        other is EncodedClip && bytes.contentEquals(other.bytes) && mimeType == other.mimeType &&
            frameCount == other.frameCount && size == other.size &&
            frameRate == other.frameRate && backendId == other.backendId

    override fun hashCode(): Int {
        var h = bytes.contentHashCode()
        h = h * 31 + mimeType.hashCode()
        h = h * 31 + frameCount
        h = h * 31 + size.hashCode()
        h = h * 31 + frameRate
        return h * 31 + backendId.hashCode()
    }

    override fun toString(): String =
        "EncodedClip(" + bytes.size + " bytes, " + mimeType + ", " + frameCount + " frames)"
}

data class ClipEncodeOptions(
    val size: RenderSize,
    val frameRate: Int,
    val frameCount: Int,
    /** Zero means loop forever, which is what a caller almost always wants. */
    val loopCount: Int = 0,
)

class ApngError : IllegalArgumentException("All APNG frames must share the first frame dimensions.")

interface IVideoEncoder {
    val backendId: String
    val outputMimeType: String
    fun encode(frames: List<PixelBuffer>, options: ClipEncodeOptions): EncodedClip
}

/** Writes an animated PNG. */
class AnimatedPngEncoder : IVideoEncoder {

    override val backendId: String get() = "apng"
    override val outputMimeType: String get() = "image/apng"

    override fun encode(frames: List<PixelBuffer>, options: ClipEncodeOptions): EncodedClip {
        val delayDen = min(65535, max(1, if (options.frameRate <= 0) 12 else options.frameRate))
        val loop = max(0, options.loopCount)

        val first = frames.firstOrNull()
            ?: return EncodedClip(
                ByteArray(0), outputMimeType, 0, options.size, options.frameRate, backendId,
            )

        val w = first.width
        val h = first.height
        for (f in frames) if (f.width != w || f.height != h) throw ApngError()

        val out = ByteArrayOutputStream()
        out.write(PngWriter.signature)
        out.write(PngWriter.chunk("IHDR", PngWriter.ihdr(w, h)))

        // acTL knows the frame count up front here, so nothing needs patching
        // afterwards the way a streaming encoder would.
        out.write(PngWriter.chunk("acTL", PngWriter.be32(frames.size) + PngWriter.be32(loop)))

        val seq = intArrayOf(0)

        // Frame 0 is the DEFAULT image: fcTL then IDAT, not fdAT. A viewer that
        // knows nothing about APNG shows exactly this frame and nothing else.
        out.write(PngWriter.chunk("fcTL", fctl(seq, w, h, delayDen)))
        out.write(PngWriter.chunk("IDAT", ZlibStored.compress(PngWriter.filteredScanlines(first))))

        for (frame in frames.drop(1)) {
            out.write(PngWriter.chunk("fcTL", fctl(seq, w, h, delayDen)))
            // fdAT carries the sequence number ahead of the same zlib payload an
            // IDAT would hold, and the number keeps counting across both chunk
            // kinds - not one counter each.
            val n = seq[0]
            seq[0] = n + 1
            val fdat = PngWriter.be32(n) + ZlibStored.compress(PngWriter.filteredScanlines(frame))
            out.write(PngWriter.chunk("fdAT", fdat))
        }

        out.write(PngWriter.chunk("IEND", ByteArray(0)))

        return EncodedClip(
            out.toByteArray(), outputMimeType, frames.size,
            RenderSize(w, h), options.frameRate, backendId,
        )
    }

    companion object {
        val instance = AnimatedPngEncoder()

        /**
         * The delay is a FRACTION: numerator 1 over the frame rate, so 12 fps
         * is 1/12 of a second per frame and no rounding is needed anywhere.
         */
        fun fctl(seq: IntArray, w: Int, h: Int, delayDen: Int): ByteArray {
            val out = ByteArrayOutputStream()
            out.write(PngWriter.be32(seq[0]))
            seq[0] = seq[0] + 1
            out.write(PngWriter.be32(w))
            out.write(PngWriter.be32(h))
            out.write(PngWriter.be32(0)) // x offset
            out.write(PngWriter.be32(0)) // y offset
            out.write(PngWriter.be16(1)) // delay numerator
            out.write(PngWriter.be16(delayDen))
            out.write(byteArrayOf(0, 0)) // dispose NONE, blend SOURCE
            return out.toByteArray()
        }
    }
}

// ------------------------------------------------------- PNG and BMP

class ImageFormatError(message: String) : IllegalArgumentException(message)

/** PNG and BMP, both directions. JPEG is delegated to a platform decoder. */
object ImageCodecs {

    /** Encode an RGBA buffer as a PNG, using the real deflater. */
    fun encodePng(image: PixelBuffer): ByteArray {
        val out = ByteArrayOutputStream()
        out.write(PngWriter.signature)
        out.write(PngWriter.chunk("IHDR", PngWriter.ihdr(image.width, image.height)))
        out.write(PngWriter.chunk("IDAT", zlibCompress(PngWriter.filteredScanlines(image))))
        out.write(PngWriter.chunk("IEND", ByteArray(0)))
        return out.toByteArray()
    }

    /** Decode an 8-bit, non-interlaced PNG (grey, greyA, RGB or RGBA) to RGBA. */
    fun decodePng(data: ByteArray): PixelBuffer {
        if (data.size < 8 || !data.copyOfRange(0, 8).contentEquals(PngWriter.signature)) {
            throw ImageFormatError("Not a PNG stream.")
        }

        var pos = 8
        var width = 0
        var height = 0
        var colorType = -1
        var bitDepth = 0
        var interlace = 0
        var haveHeader = false
        val idat = ByteArrayOutputStream()

        while (pos + 12 <= data.size) {
            val len = readBe32(data, pos)
            pos += 4
            if (len < 0 || pos.toLong() + 4 + len + 4 > data.size) {
                throw ImageFormatError("Corrupt PNG chunk.")
            }
            val type = String(data, pos, 4, Charsets.US_ASCII)
            pos += 4
            val chunkStart = pos
            pos += len + 4 // data plus the CRC, which is not validated on read

            when (type) {
                "IHDR" -> {
                    width = readBe32(data, chunkStart)
                    height = readBe32(data, chunkStart + 4)
                    bitDepth = data[chunkStart + 8].toInt() and 0xFF
                    colorType = data[chunkStart + 9].toInt() and 0xFF
                    interlace = data[chunkStart + 12].toInt() and 0xFF
                    haveHeader = true
                    if (width <= 0 || height <= 0) throw ImageFormatError("Invalid PNG dimensions.")
                    if (bitDepth != 8) {
                        throw ImageFormatError(
                            "Unsupported PNG bit depth " + bitDepth + " (this decoder handles 8-bit only).",
                        )
                    }
                    if (interlace != 0) throw ImageFormatError("Interlaced PNG is not supported.")
                    if (colorType != 0 && colorType != 2 && colorType != 4 && colorType != 6) {
                        throw ImageFormatError("Unsupported PNG colour type " + colorType + ".")
                    }
                }
                "IDAT" -> idat.write(data, chunkStart, len)
                "IEND" -> break
            }
        }

        if (!haveHeader) throw ImageFormatError("PNG missing IHDR.")

        val channels = when (colorType) { 0 -> 1; 2 -> 3; 4 -> 2; else -> 4 }
        val raw = zlibDecompress(idat.toByteArray())
        val stride = width * channels
        if (raw.size.toLong() < height.toLong() * (stride + 1)) {
            throw ImageFormatError("PNG scanline data underflow.")
        }

        var cur = ByteArray(stride)
        var prev = ByteArray(stride)
        val outBuf = PixelBuffer.of(width, height)!!
        val outPx = outBuf.pixels

        var ri = 0
        for (y in 0 until height) {
            val filter = raw[ri++].toInt() and 0xFF
            for (x in 0 until stride) {
                val rawv = raw[ri++].toInt() and 0xFF
                val a = if (x >= channels) cur[x - channels].toInt() and 0xFF else 0
                val b = prev[x].toInt() and 0xFF
                val c = if (x >= channels) prev[x - channels].toInt() and 0xFF else 0
                val v = when (filter) {
                    0 -> rawv
                    1 -> rawv + a
                    2 -> rawv + b
                    3 -> rawv + ((a + b) shr 1)
                    4 -> rawv + paeth(a, b, c)
                    else -> throw ImageFormatError("Unknown PNG filter " + filter + ".")
                }
                cur[x] = (v and 0xFF).toByte()
            }

            var di = y * width * 4
            for (x in 0 until width) {
                val s = x * channels
                val r8: Byte
                val g8: Byte
                val b8: Byte
                val a8: Byte
                when (colorType) {
                    0 -> { r8 = cur[s]; g8 = cur[s]; b8 = cur[s]; a8 = 255.toByte() }
                    2 -> { r8 = cur[s]; g8 = cur[s + 1]; b8 = cur[s + 2]; a8 = 255.toByte() }
                    4 -> { r8 = cur[s]; g8 = cur[s]; b8 = cur[s]; a8 = cur[s + 1] }
                    else -> { r8 = cur[s]; g8 = cur[s + 1]; b8 = cur[s + 2]; a8 = cur[s + 3] }
                }
                outPx[di++] = r8
                outPx[di++] = g8
                outPx[di++] = b8
                outPx[di++] = a8
            }

            val swap = prev
            prev = cur
            cur = swap
        }

        return outBuf
    }

    /** Encode an RGBA buffer as a 24-bit bottom-up BMP (BI_RGB). */
    fun encodeBmp(image: PixelBuffer): ByteArray {
        val w = image.width
        val h = image.height
        val rowStride = (w * 3 + 3) / 4 * 4
        val imageSize = rowStride * h
        val fileSize = 54 + imageSize
        val o = ByteArray(fileSize)

        o[0] = 66 // B
        o[1] = 77 // M
        writeLe32(o, 2, fileSize)
        writeLe32(o, 10, 54)
        writeLe32(o, 14, 40)
        writeLe32(o, 18, w)
        writeLe32(o, 22, h) // positive means bottom-up
        writeLe16(o, 26, 1)
        writeLe16(o, 28, 24)
        writeLe32(o, 34, imageSize)
        writeLe32(o, 38, 2835)
        writeLe32(o, 42, 2835)

        val px = image.pixels
        for (y in 0 until h) {
            val srcRow = (h - 1 - y) * w * 4
            var dst = 54 + y * rowStride
            for (x in 0 until w) {
                val s = srcRow + x * 4
                o[dst++] = px[s + 2] // B
                o[dst++] = px[s + 1] // G
                o[dst++] = px[s] // R
            }
        }
        return o
    }

    /** Decode an uncompressed 24- or 32-bit BMP to RGBA. */
    fun decodeBmp(d: ByteArray): PixelBuffer {
        if (d.size < 54 || d[0].toInt() != 66 || d[1].toInt() != 77) {
            throw ImageFormatError("Not a BMP stream.")
        }

        val dataOffset = readLe32(d, 10)
        val width = readLe32(d, 18)
        val rawHeight = readLe32(d, 22)
        val bpp = readLe16(d, 28)
        val compression = readLe32(d, 30)

        if (compression != 0) throw ImageFormatError("Only uncompressed BMP (BI_RGB) is supported.")
        if (bpp != 24 && bpp != 32) throw ImageFormatError("Unsupported BMP bit depth " + bpp + ".")
        if (width <= 0) throw ImageFormatError("Invalid BMP width.")

        // A NEGATIVE height means top-down, not an error. Half the BMPs a
        // screenshot tool produces are written that way.
        val topDown = rawHeight < 0
        val height = abs(rawHeight)
        if (height == 0) throw ImageFormatError("Invalid BMP height.")

        val bytesPP = bpp / 8
        val rowStride = (width * bytesPP + 3) / 4 * 4
        if (d.size.toLong() < dataOffset.toLong() + rowStride.toLong() * height) {
            throw ImageFormatError("BMP pixel data underflow.")
        }

        val outBuf = PixelBuffer.of(width, height)!!
        val outPx = outBuf.pixels
        for (y in 0 until height) {
            val srcRowIndex = if (topDown) y else (height - 1 - y)
            val src = dataOffset + srcRowIndex * rowStride
            var dst = y * width * 4
            for (x in 0 until width) {
                val s = src + x * bytesPP
                outPx[dst++] = d[s + 2] // R
                outPx[dst++] = d[s + 1] // G
                outPx[dst++] = d[s] // B
                outPx[dst++] = if (bytesPP == 4) d[s + 3] else 255.toByte()
            }
        }
        return outBuf
    }

    // ---- helpers

    fun zlibCompress(data: ByteArray): ByteArray {
        val deflater = Deflater(Deflater.BEST_COMPRESSION)
        try {
            deflater.setInput(data)
            deflater.finish()
            val out = ByteArrayOutputStream(max(64, data.size / 2))
            val buf = ByteArray(16384)
            while (!deflater.finished()) {
                val n = deflater.deflate(buf)
                if (n > 0) out.write(buf, 0, n)
            }
            return out.toByteArray()
        } finally {
            deflater.end()
        }
    }

    fun zlibDecompress(data: ByteArray): ByteArray {
        val inflater = Inflater()
        try {
            inflater.setInput(data)
            val out = ByteArrayOutputStream(max(64, data.size * 4))
            val buf = ByteArray(16384)
            while (!inflater.finished()) {
                val n = inflater.inflate(buf)
                if (n == 0 && (inflater.needsInput() || inflater.needsDictionary())) break
                if (n > 0) out.write(buf, 0, n)
            }
            return out.toByteArray()
        } finally {
            inflater.end()
        }
    }

    private fun paeth(a: Int, b: Int, c: Int): Int {
        val p = a + b - c
        val pa = abs(p - a)
        val pb = abs(p - b)
        val pc = abs(p - c)
        if (pa <= pb && pa <= pc) return a
        return if (pb <= pc) b else c
    }

    private fun readBe32(d: ByteArray, i: Int): Int =
        ((d[i].toInt() and 0xFF) shl 24) or ((d[i + 1].toInt() and 0xFF) shl 16) or
            ((d[i + 2].toInt() and 0xFF) shl 8) or (d[i + 3].toInt() and 0xFF)

    private fun readLe32(d: ByteArray, i: Int): Int =
        (d[i].toInt() and 0xFF) or ((d[i + 1].toInt() and 0xFF) shl 8) or
            ((d[i + 2].toInt() and 0xFF) shl 16) or ((d[i + 3].toInt() and 0xFF) shl 24)

    private fun readLe16(d: ByteArray, i: Int): Int =
        (d[i].toInt() and 0xFF) or ((d[i + 1].toInt() and 0xFF) shl 8)

    private fun writeLe32(o: ByteArray, i: Int, v: Int) {
        o[i] = (v and 0xFF).toByte()
        o[i + 1] = ((v shr 8) and 0xFF).toByte()
        o[i + 2] = ((v shr 16) and 0xFF).toByte()
        o[i + 3] = ((v shr 24) and 0xFF).toByte()
    }

    private fun writeLe16(o: ByteArray, i: Int, v: Int) {
        o[i] = (v and 0xFF).toByte()
        o[i + 1] = ((v shr 8) and 0xFF).toByte()
    }
}

/** Decodes bytes to an RGBA buffer for compositing. */
interface IImageDecoder {
    val backendId: String
    fun decode(bytes: ByteArray, mimeHint: String? = null): PixelBuffer
    fun tryDecode(bytes: ByteArray, mimeHint: String? = null): PixelBuffer?
}

/** PNG and BMP in managed code; JPEG is handed to a platform backend. */
class ManagedImageDecoder : IImageDecoder {

    override val backendId: String get() = "managed-png-bmp"

    override fun decode(bytes: ByteArray, mimeHint: String?): PixelBuffer = when {
        looksPng(bytes) -> ImageCodecs.decodePng(bytes)
        looksBmp(bytes) -> ImageCodecs.decodeBmp(bytes)
        looksJpeg(bytes) -> throw ImageFormatError(
            "JPEG decoding needs a platform decoder (Android BitmapFactory) wired through IImageDecoder.",
        )
        else -> throw ImageFormatError("Unrecognised image format; this decoder supports PNG and BMP.")
    }

    override fun tryDecode(bytes: ByteArray, mimeHint: String?): PixelBuffer? = try {
        decode(bytes, mimeHint)
    } catch (e: Exception) {
        null
    }

    companion object {
        val instance = ManagedImageDecoder()

        // The MAGIC decides, not the mime hint. A hint is what the sender
        // claimed; the first bytes are what actually arrived.
        fun looksPng(s: ByteArray): Boolean =
            s.size >= 8 && s.copyOfRange(0, 8).contentEquals(PngWriter.signature)

        fun looksBmp(s: ByteArray): Boolean =
            s.size >= 2 && s[0].toInt() == 66 && s[1].toInt() == 77

        fun looksJpeg(s: ByteArray): Boolean =
            s.size >= 3 && (s[0].toInt() and 0xFF) == 0xFF &&
                (s[1].toInt() and 0xFF) == 0xD8 && (s[2].toInt() and 0xFF) == 0xFF
    }
}
