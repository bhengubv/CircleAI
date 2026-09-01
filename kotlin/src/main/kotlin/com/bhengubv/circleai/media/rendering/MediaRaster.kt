// MediaRaster.kt
//
// The pixels: an RGBA8 buffer, a 5x7 bitmap font with no external file, and a
// source-over canvas that composites and draws text onto it.
//
// Nothing here touches a platform graphics API, on purpose. The same code has
// to produce the same bytes on a phone, on a server and in a test.

package com.bhengubv.circleai.media.rendering

import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.floor
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/** Straight RGBA8, row-major, no padding. */
class PixelBuffer private constructor(
    val width: Int,
    val height: Int,
    val pixels: ByteArray,
) {
    val stride: Int get() = width * 4

    fun index(x: Int, y: Int): Int = (y * width + x) * 4

    /** Null outside the buffer rather than throwing: callers sample edges constantly. */
    fun pixel(x: Int, y: Int): Rgba32? {
        if (x < 0 || y < 0 || x >= width || y >= height) return null
        val i = index(x, y)
        return Rgba32(
            pixels[i].toInt() and 0xFF,
            pixels[i + 1].toInt() and 0xFF,
            pixels[i + 2].toInt() and 0xFF,
            pixels[i + 3].toInt() and 0xFF,
        )
    }

    fun at(i: Int): Int = pixels[i].toInt() and 0xFF

    fun write(i: Int, value: Int) { pixels[i] = value.toByte() }

    companion object {
        /** Null for a non-positive dimension - an empty canvas is a caller bug, not an exception. */
        fun of(width: Int, height: Int): PixelBuffer? {
            if (width <= 0 || height <= 0) return null
            return PixelBuffer(width, height, ByteArray(width * height * 4))
        }

        /** Null when the byte count does not match width x height x 4. */
        fun of(width: Int, height: Int, pixels: ByteArray): PixelBuffer? {
            if (width <= 0 || height <= 0) return null
            if (pixels.size != width * height * 4) return null
            return PixelBuffer(width, height, pixels)
        }
    }
}

/**
 * A 5x7 pixel font carried in the binary, with no external file to ship, find
 * or fail to load.
 *
 * Lower case FOLDS to upper: the glyph table has one case, so a mixed-case
 * caption still renders rather than losing half its letters.
 */
class BitmapFont private constructor() {

    private val glyphs: Map<Char, Array<String>> = build()

    fun hasGlyph(c: Char): Boolean = glyphs.containsKey(fold(c))

    fun isPixelOn(c: Char, col: Int, row: Int): Boolean {
        if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return false
        val g = glyphs[fold(c)] ?: return false
        if (row >= g.size) return false
        val line = g[row]
        return col < line.length && line[col] == Char(35)
    }

    companion object {
        const val COLS = 5
        const val ROWS = 7

        val default = BitmapFont()

        fun fold(c: Char): Char = if (c.code in 97..122) Char(c.code - 32) else c

        private fun build(): Map<Char, Array<String>> = mapOf(
            'A' to arrayOf(".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
            'B' to arrayOf("####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."),
            'C' to arrayOf(".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."),
            'D' to arrayOf("####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."),
            'E' to arrayOf("#####", "#....", "#....", "####.", "#....", "#....", "#####"),
            'F' to arrayOf("#####", "#....", "#....", "####.", "#....", "#....", "#...."),
            'G' to arrayOf(".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."),
            'H' to arrayOf("#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"),
            'I' to arrayOf("#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"),
            'J' to arrayOf("..###", "...#.", "...#.", "...#.", "#..#.", "#..#.", ".##.."),
            'K' to arrayOf("#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"),
            'L' to arrayOf("#....", "#....", "#....", "#....", "#....", "#....", "#####"),
            'M' to arrayOf("#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"),
            'N' to arrayOf("#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#"),
            'O' to arrayOf(".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
            'P' to arrayOf("####.", "#...#", "#...#", "####.", "#....", "#....", "#...."),
            'Q' to arrayOf(".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"),
            'R' to arrayOf("####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"),
            'S' to arrayOf(".####", "#....", "#....", ".###.", "....#", "....#", "####."),
            'T' to arrayOf("#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."),
            'U' to arrayOf("#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."),
            'V' to arrayOf("#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."),
            'W' to arrayOf("#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#"),
            'X' to arrayOf("#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"),
            'Y' to arrayOf("#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."),
            'Z' to arrayOf("#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"),
            '0' to arrayOf(".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."),
            '1' to arrayOf("..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."),
            '2' to arrayOf(".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"),
            '3' to arrayOf("#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."),
            '4' to arrayOf("...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."),
            '5' to arrayOf("#####", "#....", "####.", "....#", "....#", "#...#", ".###."),
            '6' to arrayOf(".###.", "#....", "#....", "####.", "#...#", "#...#", ".###."),
            '7' to arrayOf("#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."),
            '8' to arrayOf(".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."),
            '9' to arrayOf(".###.", "#...#", "#...#", ".####", "....#", "....#", ".###."),
            '.' to arrayOf(".....", ".....", ".....", ".....", ".....", ".##..", ".##.."),
            ',' to arrayOf(".....", ".....", ".....", ".....", ".##..", ".##..", ".#..."),
            '!' to arrayOf("..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.."),
            '?' to arrayOf(".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."),
            Char(39) to arrayOf("..#..", "..#..", "..#..", ".....", ".....", ".....", "....."),
            Char(34) to arrayOf(".#.#.", ".#.#.", ".#.#.", ".....", ".....", ".....", "....."),
            '-' to arrayOf(".....", ".....", ".....", "#####", ".....", ".....", "....."),
            '+' to arrayOf(".....", "..#..", "..#..", "#####", "..#..", "..#..", "....."),
            ':' to arrayOf(".....", ".##..", ".##..", ".....", ".##..", ".##..", "....."),
            ';' to arrayOf(".....", ".##..", ".##..", ".....", ".##..", ".##..", ".#..."),
            '/' to arrayOf("....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."),
            '(' to arrayOf("..##.", ".#...", ".#...", ".#...", ".#...", ".#...", "..##."),
            ')' to arrayOf(".##..", "...#.", "...#.", "...#.", "...#.", "...#.", ".##.."),
            '&' to arrayOf(".##..", "#..#.", "#.#..", ".#...", "#.#.#", "#..#.", ".##.#"),
            '%' to arrayOf("##..#", "##.#.", "..#..", ".#...", "#..##", "..#.#", "..#.#"),
            '#' to arrayOf(".#.#.", ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".#.#."),
            '@' to arrayOf(".###.", "#...#", "#.###", "#.#.#", "#.###", "#....", ".###."),
        )
    }
}

/** Source-over compositing onto an RGBA8 buffer. */
class RasterCanvas(val buffer: PixelBuffer) {

    val width: Int get() = buffer.width
    val height: Int get() = buffer.height

    fun clear(c: Rgba32) {
        var i = 0
        while (i < buffer.pixels.size) {
            buffer.write(i, c.r)
            buffer.write(i + 1, c.g)
            buffer.write(i + 2, c.b)
            buffer.write(i + 3, c.a)
            i += 4
        }
    }

    fun fillRect(x0: Int, y0: Int, w: Int, h: Int, color: Rgba32, opacity: Double = 1.0) {
        val a = (color.a / 255.0) * opacity
        if (a <= 0.0) return
        val xs = max(0, x0)
        val ys = max(0, y0)
        val xe = min(width, x0 + w)
        val ye = min(height, y0 + h)
        if (xe <= xs || ye <= ys) return
        for (y in ys until ye) {
            for (x in xs until xe) blend(x, y, color.r, color.g, color.b, a)
        }
    }

    /**
     * Draws src into the destination rectangle under the given fit.
     *
     * CONTAIN fits the whole image inside and leaves bars; COVER fills the
     * rectangle and crops. Both CENTRE what is left over, which is why the
     * offsets are halved rather than zeroed - anchoring at the origin puts
     * every portrait photo down and to the right of where it belongs.
     */
    fun drawImage(
        src: PixelBuffer,
        destX: Double,
        destY: Double,
        destW: Double,
        destH: Double,
        fit: ContentFit,
        opacity: Double = 1.0,
    ) {
        if (src.width <= 0 || src.height <= 0 || destW <= 0 || destH <= 0 || opacity <= 0) return

        var pw = destW
        var ph = destH
        var ox = destX
        var oy = destY
        when (fit) {
            ContentFit.FILL -> Unit
            ContentFit.CONTAIN -> {
                val s = min(destW / src.width, destH / src.height)
                pw = src.width * s
                ph = src.height * s
                ox = destX + (destW - pw) / 2.0
                oy = destY + (destH - ph) / 2.0
            }
            ContentFit.COVER -> {
                val s = max(destW / src.width, destH / src.height)
                pw = src.width * s
                ph = src.height * s
                ox = destX + (destW - pw) / 2.0
                oy = destY + (destH - ph) / 2.0
            }
        }

        // The CLIP is the DESTINATION rectangle, not the placed image, so a
        // cover fit crops instead of spilling over its neighbours.
        val cx0 = max(0, floor(destX).toInt())
        val cy0 = max(0, floor(destY).toInt())
        val cx1 = min(width, ceil(destX + destW).toInt())
        val cy1 = min(height, ceil(destY + destH).toInt())
        if (cx1 <= cx0 || cy1 <= cy0) return

        for (y in cy0 until cy1) {
            val v = ((y + 0.5) - oy) / ph * src.height
            if (v < 0 || v > src.height) continue
            for (x in cx0 until cx1) {
                val u = ((x + 0.5) - ox) / pw * src.width
                if (u < 0 || u > src.width) continue
                val s = sample(src, u - 0.5, v - 0.5)
                if (s[3] <= 0) continue
                blend(x, y, s[0], s[1], s[2], (s[3] / 255.0) * opacity)
            }
        }
    }

    /**
     * Source-over onto a possibly-transparent destination.
     *
     * The result is UNPREMULTIPLIED, which is why each channel is divided by
     * the output alpha. Skipping that step darkens everything drawn over
     * transparency, and the symptom - captions that look right on an opaque
     * background and muddy on a scrim - never points at this line.
     */
    fun blend(x: Int, y: Int, r: Int, g: Int, b: Int, alphaIn: Double) {
        if (alphaIn <= 0.0) return
        if (x < 0 || y < 0 || x >= width || y >= height) return
        val alpha = min(1.0, alphaIn)

        val idx = (y * width + x) * 4
        val da = buffer.at(idx + 3) / 255.0
        val outA = alpha + da * (1.0 - alpha)
        if (outA <= 0.0) {
            for (k in 0 until 4) buffer.write(idx + k, 0)
            return
        }
        val inv = da * (1.0 - alpha)
        buffer.write(idx, clamp255((r * alpha + buffer.at(idx) * inv) / outA))
        buffer.write(idx + 1, clamp255((g * alpha + buffer.at(idx + 1) * inv) / outA))
        buffer.write(idx + 2, clamp255((b * alpha + buffer.at(idx + 2) * inv) / outA))
        buffer.write(idx + 3, clamp255(outA * 255.0))
    }

    companion object {
        /** Null for a non-positive size, matching PixelBuffer. */
        fun of(width: Int, height: Int): RasterCanvas? =
            PixelBuffer.of(width, height)?.let { RasterCanvas(it) }

        fun clamp255(v: Double): Int = when {
            v <= 0 -> 0
            v >= 255 -> 255
            else -> v.roundToInt()
        }

        /** Bilinear, CLAMPED at the edges so a sample never wraps to the far side. */
        fun sample(src: PixelBuffer, fxIn: Double, fyIn: Double): IntArray {
            val maxX = (src.width - 1).toDouble()
            val maxY = (src.height - 1).toDouble()
            val fx = min(max(fxIn, 0.0), maxX)
            val fy = min(max(fyIn, 0.0), maxY)
            val x0 = fx.toInt()
            val y0 = fy.toInt()
            val x1 = if (x0 < maxX) x0 + 1 else x0
            val y1 = if (y0 < maxY) y0 + 1 else y0
            val tx = fx - x0
            val ty = fy - y0

            val w = src.width
            val i00 = (y0 * w + x0) * 4
            val i10 = (y0 * w + x1) * 4
            val i01 = (y1 * w + x0) * 4
            val i11 = (y1 * w + x1) * 4

            val out = IntArray(4)
            for (o in 0 until 4) {
                out[o] = bilinear(src.at(i00 + o), src.at(i10 + o), src.at(i01 + o), src.at(i11 + o), tx, ty)
            }
            return out
        }

        fun bilinear(c00: Int, c10: Int, c01: Int, c11: Int, tx: Double, ty: Double): Int {
            val top = c00 + (c10 - c00) * tx
            val bottom = c01 + (c11 - c01) * tx
            val v = top + (bottom - top) * ty
            return min(255, max(0, v.roundToInt()))
        }

        /**
         * The trailing letter-space of the LAST glyph is not part of the line,
         * so centred text sits centred rather than a space to the left.
         */
        fun lineWidth(charCount: Int, advance: Int, glyphW: Int): Int =
            if (charCount <= 0) 0 else charCount * advance - (advance - glyphW)

        /**
         * Greedy word wrap. Explicit newlines start a new line; a single word
         * longer than the box is NOT broken - it overflows, which is visible,
         * and better than silently losing characters.
         */
        fun wrap(text: String, maxWidth: Int, advance: Int, glyphW: Int): List<String> {
            val result = mutableListOf<String>()
            val paragraphs = text.replace("\r", "").split("\n")

            for (paragraph in paragraphs) {
                val words = paragraph.split(Char(32)).filter { it.isNotEmpty() }
                if (words.isEmpty()) { result.add(""); continue }

                var cur = ""
                for (word in words) {
                    val candidate = if (cur.isEmpty()) word.length else cur.length + 1 + word.length
                    if (cur.isNotEmpty() && lineWidth(candidate, advance, glyphW) > maxWidth) {
                        result.add(cur)
                        cur = word
                    } else {
                        if (cur.isNotEmpty()) cur += " "
                        cur += word
                    }
                }
                result.add(cur)
            }
            return result
        }
    }

    /**
     * Lays out wrapped, aligned text inside a rectangle and draws it.
     *
     * The glyph scale is an INTEGER multiple of the 5x7 cell. A fractional
     * scale would need antialiasing to look like anything, and this pipeline
     * has none - blocky and crisp beats blurry and grey.
     */
    fun drawText(
        font: BitmapFont,
        text: String,
        rx: Int,
        ry: Int,
        rw: Int,
        rh: Int,
        pixelHeight: Int,
        color: Rgba32,
        align: TextAlign,
        box: Rgba32,
        letterSpacingFraction: Double,
        lineSpacingFraction: Double,
        opacity: Double = 1.0,
    ) {
        if (text.isEmpty() || rw <= 0 || rh <= 0 || opacity <= 0) return

        val scale = max(1, (pixelHeight.toDouble() / BitmapFont.ROWS).roundToInt())
        val glyphW = BitmapFont.COLS * scale
        val glyphH = BitmapFont.ROWS * scale
        val letter = max(scale, (glyphW * letterSpacingFraction).roundToInt())
        val advance = glyphW + letter
        val lineH = glyphH + max(scale, (glyphH * lineSpacingFraction).roundToInt())

        val lines = wrap(text, rw, advance, glyphW)
        if (lines.isEmpty()) return

        // The LAST line contributes only its glyph height, not a full line box,
        // so a block of text is vertically centred on its INK rather than on a
        // trailing gap nobody can see.
        val totalH = lines.size * lineH - (lineH - glyphH)
        val startY = ry + max(0, (rh - totalH) / 2)

        if (box.a > 0) {
            var maxW = 0
            for (ln in lines) maxW = max(maxW, lineWidth(ln.length, advance, glyphW))
            if (maxW > 0) {
                val pad = max(scale * 2, glyphW / 2)
                val bx = when (align) {
                    TextAlign.LEFT -> rx
                    TextAlign.RIGHT -> rx + rw - maxW
                    TextAlign.CENTER -> rx + (rw - maxW) / 2
                }
                fillRect(bx - pad, startY - pad, maxW + pad * 2, totalH + pad * 2, box, opacity)
            }
        }

        val inkA = (color.a / 255.0) * opacity
        var y0 = startY
        for (line in lines) {
            val lw = lineWidth(line.length, advance, glyphW)
            val x0 = when (align) {
                TextAlign.LEFT -> rx
                TextAlign.RIGHT -> rx + rw - lw
                TextAlign.CENTER -> rx + (rw - lw) / 2
            }
            var cx = x0
            for (ch in line) {
                if (ch != Char(32)) {
                    for (gy in 0 until BitmapFont.ROWS) {
                        for (gx in 0 until BitmapFont.COLS) {
                            if (font.isPixelOn(ch, gx, gy)) {
                                fillBlock(cx + gx * scale, y0 + gy * scale, scale, color, inkA)
                            }
                        }
                    }
                }
                cx += advance
            }
            y0 += lineH
        }
    }

    private fun fillBlock(x0: Int, y0: Int, size: Int, c: Rgba32, alpha: Double) {
        for (y in y0 until (y0 + size)) {
            for (x in x0 until (x0 + size)) blend(x, y, c.r, c.g, c.b, alpha)
        }
    }
}
