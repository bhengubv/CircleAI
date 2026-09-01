package com.bhengubv.circleai.media.rendering

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class PixelBufferTest {

    @Test
    fun aNonPositiveDimensionIsNullNotAnException() {
        assertNull(PixelBuffer.of(0, 10))
        assertNull(PixelBuffer.of(10, 0))
        assertNull(PixelBuffer.of(-1, 10))
    }

    @Test
    fun aWrongLengthPixelArrayIsRefused() {
        // Four bytes per pixel or nothing. A short array here becomes an index
        // error a thousand lines away in the compositor.
        assertNull(PixelBuffer.of(2, 2, ByteArray(15)))
        assertNull(PixelBuffer.of(2, 2, ByteArray(17)))
        assertNotNull(PixelBuffer.of(2, 2, ByteArray(16)))
    }

    @Test
    fun aNewBufferIsFullyTRANSPARENTnotBlack() {
        // Zeroed memory is alpha 0, and that is what the compositor needs: a
        // canvas that starts opaque black would tint everything drawn over it.
        val b = PixelBuffer.of(3, 2)!!
        assertEquals(Rgba32(0, 0, 0, 0), b.pixel(0, 0))
        assertEquals(24, b.pixels.size)
        assertEquals(12, b.stride)
    }

    @Test
    fun readingOutsideTheBufferIsNullNotAWrap() {
        val b = PixelBuffer.of(2, 2)!!
        assertNull(b.pixel(-1, 0))
        assertNull(b.pixel(0, -1))
        assertNull(b.pixel(2, 0))
        assertNull(b.pixel(0, 2))
    }

    @Test
    fun channelsAboveOneTwentySevenComeBackPositive() {
        // The reason channels are Int here: a signed byte reads 200 as -56, and
        // every blend that touches a bright pixel goes wrong quietly.
        val px = ByteArray(4) { 200.toByte() }
        val b = PixelBuffer.of(1, 1, px)!!
        assertEquals(Rgba32(200, 200, 200, 200), b.pixel(0, 0))
    }
}

class BitmapFontTest {

    private val font = BitmapFont.default

    @Test
    fun lowerCaseFoldsToUpperSoAMixedCaseCaptionStillRenders() {
        assertTrue(font.hasGlyph('a'))
        assertTrue(font.hasGlyph('A'))
        for (row in 0 until BitmapFont.ROWS) {
            for (col in 0 until BitmapFont.COLS) {
                assertEquals(
                    font.isPixelOn('A', col, row),
                    font.isPixelOn('a', col, row),
                    "case differs at " + col + "," + row,
                )
            }
        }
    }

    @Test
    fun everyLetterAndDigitHasAGlyph() {
        for (c in 'A'..'Z') assertTrue(font.hasGlyph(c), "missing " + c)
        for (c in '0'..'9') assertTrue(font.hasGlyph(c), "missing " + c)
    }

    @Test
    fun theCommonPunctuationIsThere() {
        for (c in listOf('.', ',', '!', '?', '-', '+', ':', ';', '/', '(', ')', '&', '%', '#', '@')) {
            assertTrue(font.hasGlyph(c), "missing " + c)
        }
        assertTrue(font.hasGlyph(Char(39)), "missing apostrophe")
        assertTrue(font.hasGlyph(Char(34)), "missing quote")
    }

    @Test
    fun anUnknownCharacterIsBlankNotAnError() {
        assertFalse(font.hasGlyph('é'))
        assertFalse(font.isPixelOn('é', 0, 0))
    }

    @Test
    fun samplingOutsideTheCellIsOff() {
        assertFalse(font.isPixelOn('A', -1, 0))
        assertFalse(font.isPixelOn('A', BitmapFont.COLS, 0))
        assertFalse(font.isPixelOn('A', 0, BitmapFont.ROWS))
    }

    @Test
    fun theSpaceCharacterHasNoGlyphSoItDrawsNothing() {
        assertFalse(font.hasGlyph(Char(32)))
    }
}

class RasterCanvasTest {

    @Test
    fun clearFillsEveryPixelWithTheColour() {
        val c = RasterCanvas.of(3, 2)!!
        c.clear(Rgba32(10, 20, 30, 40))
        for (y in 0 until 2) {
            for (x in 0 until 3) assertEquals(Rgba32(10, 20, 30, 40), c.buffer.pixel(x, y))
        }
    }

    @Test
    fun anOpaqueFillReplacesWhatWasThere() {
        val c = RasterCanvas.of(4, 4)!!
        c.clear(Rgba32.black)
        c.fillRect(1, 1, 2, 2, Rgba32.white)
        assertEquals(Rgba32.white, c.buffer.pixel(1, 1))
        assertEquals(Rgba32.black, c.buffer.pixel(0, 0))
        assertEquals(Rgba32.black, c.buffer.pixel(3, 3))
    }

    @Test
    fun aHalfTransparentFillMeetsTheBackgroundInTheMiddle() {
        val c = RasterCanvas.of(2, 2)!!
        c.clear(Rgba32.black)
        c.fillRect(0, 0, 2, 2, Rgba32(255, 255, 255, 128))
        val p = c.buffer.pixel(0, 0)!!
        assertEquals(255, p.a)
        assertTrue(p.r in 126..130, "expected about half, got " + p.r)
    }

    @Test
    fun drawingOverTRANSPARENCYdoesNotDarkenTheResult() {
        // The unpremultiply step. Without it, white at 50% over an empty canvas
        // comes out grey - captions look right on a photo and muddy on a scrim,
        // and nothing about the symptom points at the blend.
        val c = RasterCanvas.of(2, 2)!!
        c.clear(Rgba32.transparent)
        c.fillRect(0, 0, 2, 2, Rgba32(255, 255, 255, 128))
        val p = c.buffer.pixel(0, 0)!!
        assertEquals(255, p.r)
        assertEquals(255, p.g)
        assertEquals(255, p.b)
        assertEquals(128, p.a)
    }

    @Test
    fun aFullyTransparentFillChangesNothing() {
        val c = RasterCanvas.of(2, 2)!!
        c.clear(Rgba32.black)
        c.fillRect(0, 0, 2, 2, Rgba32(255, 0, 0, 0))
        assertEquals(Rgba32.black, c.buffer.pixel(0, 0))
    }

    @Test
    fun fillsAreCLIPPEDratherThanWrappingOrThrowing() {
        val c = RasterCanvas.of(4, 4)!!
        c.clear(Rgba32.black)
        c.fillRect(-10, -10, 12, 12, Rgba32.white)
        assertEquals(Rgba32.white, c.buffer.pixel(1, 1))
        assertEquals(Rgba32.black, c.buffer.pixel(3, 3))

        // Entirely outside: no exception, no effect.
        c.fillRect(100, 100, 5, 5, Rgba32.white)
        c.fillRect(0, 0, 0, 0, Rgba32.white)
    }

    @Test
    fun coverFillsAndCropsWhileContainFitsAndLeavesBars() {
        // A wide source into a square destination.
        val src = PixelBuffer.of(4, 2)!!
        for (i in src.pixels.indices) src.pixels[i] = 255.toByte()

        val cover = RasterCanvas.of(8, 8)!!
        cover.clear(Rgba32.transparent)
        cover.drawImage(src, 0.0, 0.0, 8.0, 8.0, ContentFit.COVER)
        // Cover reaches the top edge; contain does not.
        assertTrue(cover.buffer.pixel(4, 0)!!.a > 0)

        val contain = RasterCanvas.of(8, 8)!!
        contain.clear(Rgba32.transparent)
        contain.drawImage(src, 0.0, 0.0, 8.0, 8.0, ContentFit.CONTAIN)
        assertEquals(0, contain.buffer.pixel(4, 0)!!.a)
        assertTrue(contain.buffer.pixel(4, 4)!!.a > 0)
    }

    @Test
    fun containCENTREStheLeftoverSpaceRatherThanAnchoringAtTheOrigin() {
        // Anchoring at the origin puts every portrait down and to the right of
        // where it belongs, and looks almost right, which is worse.
        val src = PixelBuffer.of(4, 2)!!
        for (i in src.pixels.indices) src.pixels[i] = 255.toByte()
        val c = RasterCanvas.of(8, 8)!!
        c.clear(Rgba32.transparent)
        c.drawImage(src, 0.0, 0.0, 8.0, 8.0, ContentFit.CONTAIN)

        // Equal empty bars top and bottom.
        assertEquals(c.buffer.pixel(4, 0)!!.a, c.buffer.pixel(4, 7)!!.a)
        assertEquals(0, c.buffer.pixel(4, 0)!!.a)
    }

    @Test
    fun aCoverFitCropsToTheDestinationInsteadOfSpillingOver() {
        val src = PixelBuffer.of(8, 2)!!
        for (i in src.pixels.indices) src.pixels[i] = 255.toByte()
        val c = RasterCanvas.of(8, 8)!!
        c.clear(Rgba32.transparent)
        // Destination is only the left half.
        c.drawImage(src, 0.0, 0.0, 4.0, 8.0, ContentFit.COVER)
        assertTrue(c.buffer.pixel(1, 4)!!.a > 0)
        assertEquals(0, c.buffer.pixel(6, 4)!!.a, "cover spilled past its rectangle")
    }

    @Test
    fun samplingClampsAtTheEdgesRatherThanWrappingToTheFarSide() {
        val src = PixelBuffer.of(2, 1)!!
        src.pixels[0] = 255.toByte(); src.pixels[3] = 255.toByte() // red, opaque
        src.pixels[5] = 255.toByte(); src.pixels[7] = 255.toByte() // green, opaque

        // Far past the left edge still reads the left pixel, not the right one.
        val left = RasterCanvas.sample(src, -5.0, 0.0)
        assertEquals(255, left[0])
        assertEquals(0, left[1])

        val right = RasterCanvas.sample(src, 5.0, 0.0)
        assertEquals(0, right[0])
        assertEquals(255, right[1])
    }

    @Test
    fun bilinearInterpolatesBetweenTheFourNeighbours() {
        assertEquals(0, RasterCanvas.bilinear(0, 100, 0, 100, 0.0, 0.0))
        assertEquals(100, RasterCanvas.bilinear(0, 100, 0, 100, 1.0, 0.0))
        assertEquals(50, RasterCanvas.bilinear(0, 100, 0, 100, 0.5, 0.0))
        assertEquals(50, RasterCanvas.bilinear(0, 0, 100, 100, 0.0, 0.5))
    }

    @Test
    fun clampNeverEscapesTheByteRange() {
        assertEquals(0, RasterCanvas.clamp255(-5.0))
        assertEquals(255, RasterCanvas.clamp255(300.0))
        assertEquals(128, RasterCanvas.clamp255(127.6))
    }
}

class TextLayoutTest {

    private val advance = 12
    private val glyphW = 10

    @Test
    fun theTrailingLetterSpaceIsNotPartOfTheLine() {
        // Otherwise centred text sits one space to the left of centre, on every
        // caption, forever.
        assertEquals(0, RasterCanvas.lineWidth(0, advance, glyphW))
        assertEquals(10, RasterCanvas.lineWidth(1, advance, glyphW))
        assertEquals(22, RasterCanvas.lineWidth(2, advance, glyphW))
        assertEquals(34, RasterCanvas.lineWidth(3, advance, glyphW))
    }

    @Test
    fun greedyWrapBreaksAtTheLastWordThatFits() {
        // A line of 5 characters measures 5*12 - 2 = 58, so a box of 60 takes
        // "AA BB" and refuses "AA BB CC" at 94.
        assertEquals(listOf("AA BB", "CC DD"), RasterCanvas.wrap("AA BB CC DD", 60, advance, glyphW))

        // Narrow enough that only one word fits per line.
        assertEquals(
            listOf("AA", "BB", "CC", "DD"),
            RasterCanvas.wrap("AA BB CC DD", 34, advance, glyphW),
        )
    }

    @Test
    fun anExplicitNewlineStartsANewLine() {
        assertEquals(listOf("AA", "BB"), RasterCanvas.wrap("AA\nBB", 1000, advance, glyphW))
    }

    @Test
    fun aBlankLineSurvivesAsABlankLine() {
        assertEquals(listOf("AA", "", "BB"), RasterCanvas.wrap("AA\n\nBB", 1000, advance, glyphW))
    }

    @Test
    fun carriageReturnsAreStrippedSoWindowsTextDoesNotDoubleSpace() {
        assertEquals(listOf("AA", "BB"), RasterCanvas.wrap("AA\r\nBB", 1000, advance, glyphW))
    }

    @Test
    fun aWordLongerThanTheBoxOVERFLOWSratherThanLosingCharacters() {
        // Visible is better than silent. A mid-word break would also be wrong
        // for every language that is not English.
        val lines = RasterCanvas.wrap("ANTIDISESTABLISHMENTARIANISM", 20, advance, glyphW)
        assertEquals(1, lines.size)
        assertEquals("ANTIDISESTABLISHMENTARIANISM", lines[0])
    }

    @Test
    fun runsOfSpacesCollapseRatherThanProducingEmptyWords() {
        assertEquals(listOf("AA BB"), RasterCanvas.wrap("AA    BB", 1000, advance, glyphW))
    }

    @Test
    fun drawnTextPutsInkOnTheCanvasAndAlignmentMovesIt() {
        val left = RasterCanvas.of(200, 40)!!
        left.clear(Rgba32.transparent)
        left.drawText(
            BitmapFont.default, "AB", 0, 0, 200, 40, 14,
            Rgba32.white, TextAlign.LEFT, Rgba32.transparent, 0.2, 0.35,
        )
        val right = RasterCanvas.of(200, 40)!!
        right.clear(Rgba32.transparent)
        right.drawText(
            BitmapFont.default, "AB", 0, 0, 200, 40, 14,
            Rgba32.white, TextAlign.RIGHT, Rgba32.transparent, 0.2, 0.35,
        )

        fun inkColumns(c: RasterCanvas): List<Int> =
            (0 until c.width).filter { x -> (0 until c.height).any { y -> c.buffer.pixel(x, y)!!.a > 0 } }

        val l = inkColumns(left)
        val r = inkColumns(right)
        assertTrue(l.isNotEmpty(), "left-aligned text drew nothing")
        assertTrue(r.isNotEmpty(), "right-aligned text drew nothing")
        assertTrue(r.first() > l.first(), "right alignment did not move the ink")
    }

    @Test
    fun theBoxColourPaintsBehindTheTextWhenItIsSet() {
        val plain = RasterCanvas.of(120, 40)!!
        plain.clear(Rgba32.transparent)
        plain.drawText(
            BitmapFont.default, "HI", 0, 0, 120, 40, 14,
            Rgba32.white, TextAlign.CENTER, Rgba32.transparent, 0.2, 0.35,
        )
        val boxed = RasterCanvas.of(120, 40)!!
        boxed.clear(Rgba32.transparent)
        boxed.drawText(
            BitmapFont.default, "HI", 0, 0, 120, 40, 14,
            Rgba32.white, TextAlign.CENTER, Rgba32(0, 0, 0, 200), 0.2, 0.35,
        )

        fun inked(c: RasterCanvas) =
            (0 until c.width).sumOf { x -> (0 until c.height).count { y -> c.buffer.pixel(x, y)!!.a > 0 } }

        assertTrue(inked(boxed) > inked(plain), "the box painted nothing")
    }

    @Test
    fun emptyTextAndAZeroSizedRectangleDrawNothingAndDoNotThrow() {
        val c = RasterCanvas.of(20, 20)!!
        c.clear(Rgba32.transparent)
        c.drawText(BitmapFont.default, "", 0, 0, 20, 20, 7, Rgba32.white, TextAlign.LEFT, Rgba32.transparent, 0.2, 0.35)
        c.drawText(BitmapFont.default, "HI", 0, 0, 0, 0, 7, Rgba32.white, TextAlign.LEFT, Rgba32.transparent, 0.2, 0.35)
        c.drawText(BitmapFont.default, "HI", 0, 0, 20, 20, 7, Rgba32.white, TextAlign.LEFT, Rgba32.transparent, 0.2, 0.35, opacity = 0.0)
        assertTrue((0 until 20).all { x -> (0 until 20).all { y -> c.buffer.pixel(x, y)!!.a == 0 } })
    }
}
