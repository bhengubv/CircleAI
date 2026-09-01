package com.bhengubv.circleai.media.rendering

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class Rgba32Test {

    @Test
    fun theThreeDigitFormDUPLICATESeachNibble() {
        // #f00 is ff0000, not f00000. Shifting instead of duplicating makes
        // every short colour darker than it was written, and nobody notices
        // until the brand blue is wrong on one screen out of ten.
        assertEquals(Rgba32(255, 0, 0, 255), Rgba32.hex("#f00"))
        assertEquals(Rgba32(0x11, 0x22, 0x33, 255), Rgba32.hex("#123"))
        assertEquals(Rgba32(255, 255, 255, 255), Rgba32.hex("#fff"))
    }

    @Test
    fun sixDigitsIsOpaqueAndEightCarriesTheAlpha() {
        assertEquals(Rgba32(0x0B, 0x1F, 0x3A, 255), Rgba32.hex("#0B1F3A"))
        assertEquals(Rgba32(0x21, 0x96, 0xF3, 0x80), Rgba32.hex("#2196F380"))
    }

    @Test
    fun theHashIsOptionalAndTheCaseDoesNotMatter() {
        assertEquals(Rgba32.hex("#2196F3"), Rgba32.hex("2196f3"))
        assertEquals(Rgba32.hex("#AbCdEf"), Rgba32.hex("aBcDeF"))
    }

    @Test
    fun aLengthThisParserDoesNotKnowIsRefused() {
        for (bad in listOf("", "#", "#12", "#12345", "#1234567", "#123456789")) {
            assertFailsWith<ColourError.Unrecognised>("accepted " + bad) { Rgba32.hex(bad) }
        }
    }

    @Test
    fun aNonHexDigitNamesTheCharacter() {
        val e = assertFailsWith<ColourError.InvalidHexDigit> { Rgba32.hex("#12345g") }
        assertEquals('g', e.digit)
    }

    @Test
    fun theNamedColoursAreWhatTheySay() {
        assertEquals(Rgba32(0, 0, 0, 0), Rgba32.transparent)
        assertEquals(Rgba32(0, 0, 0, 255), Rgba32.black)
        assertEquals(Rgba32(255, 255, 255, 255), Rgba32.white)
        assertEquals(Rgba32(1, 2, 3, 255), Rgba32.rgb(1, 2, 3))
        assertEquals(Rgba32(1, 2, 3, 128), Rgba32.rgb(1, 2, 3).withAlpha(128))
    }
}

class EasingTest {

    private fun close(a: Double, b: Double) = assertTrue(abs(a - b) < 1e-9, a.toString() + " vs " + b)

    @Test
    fun everyCurveStartsAtZeroAndEndsAtOne() {
        for (k in EasingKind.entries) {
            close(0.0, Easing.apply(k, 0.0))
            close(1.0, Easing.apply(k, 1.0))
        }
    }

    @Test
    fun eachCurveBendsTheWayItsNameClaims() {
        // Halfway through, ease-in is behind linear and ease-out is ahead.
        assertTrue(Easing.apply(EasingKind.EASE_IN, 0.5) < 0.5)
        assertTrue(Easing.apply(EasingKind.EASE_OUT, 0.5) > 0.5)
        close(0.5, Easing.apply(EasingKind.LINEAR, 0.5))
        close(0.5, Easing.apply(EasingKind.EASE_IN_OUT, 0.5))
    }

    @Test
    fun aNullMotionIsFullyOpaqueUnscaledAndUnmoved() {
        val s = Easing.evaluate(null, 0.7)
        close(1.0, s.opacity)
        close(1.0, s.scale)
        assertEquals(NormVec.zero, s.translate)
    }

    @Test
    fun aMotionWindowClampsOutsideItself() {
        val m = Motion(startFraction = 0.4, endFraction = 0.6, fromOpacity = 0.0, toOpacity = 1.0)
        close(0.0, Easing.evaluate(m, 0.0).opacity)
        close(0.0, Easing.evaluate(m, 0.4).opacity)
        close(0.5, Easing.evaluate(m, 0.5).opacity)
        close(1.0, Easing.evaluate(m, 0.6).opacity)
        close(1.0, Easing.evaluate(m, 1.0).opacity)
    }

    @Test
    fun aZeroLengthWindowSNAPSratherThanDividingByZero() {
        // What a caller writes when they want a layer to appear at one instant.
        val m = Motion(startFraction = 0.5, endFraction = 0.5, fromOpacity = 0.0, toOpacity = 1.0)
        close(0.0, Easing.evaluate(m, 0.49).opacity)
        close(1.0, Easing.evaluate(m, 0.5).opacity)
        close(1.0, Easing.evaluate(m, 0.9).opacity)
    }

    @Test
    fun fadeInStartsInvisibleAndIsDoneAQuarterOfTheWayThrough() {
        close(0.0, Easing.evaluate(Motion.fadeIn, 0.0).opacity)
        close(1.0, Easing.evaluate(Motion.fadeIn, 0.25).opacity)
        close(1.0, Easing.evaluate(Motion.fadeIn, 1.0).opacity)
    }

    @Test
    fun fadeOutEndsFullyGone() {
        close(1.0, Easing.evaluate(Motion.fadeOut, 0.0).opacity)
        close(0.0, Easing.evaluate(Motion.fadeOut, 1.0).opacity)
    }

    @Test
    fun kenBurnsZoomsAndDriftsButNeverFades() {
        val start = Easing.evaluate(Motion.kenBurns, 0.0)
        val end = Easing.evaluate(Motion.kenBurns, 1.0)
        close(1.0, start.scale)
        close(1.12, end.scale)
        close(1.0, start.opacity)
        close(1.0, end.opacity)
        assertTrue(end.translate.x > start.translate.x)
    }
}

class MediaSpecTest {

    @Test
    fun aStillIsOneFrameAndSaysSo() {
        val s = MediaSpec.still(RenderSize.square1080, Rgba32.black)
        assertTrue(s.isStill)
        assertEquals(1, s.frameCount)
    }

    @Test
    fun aClipIsDurationTimesFrameRate() {
        assertEquals(72, MediaSpec(RenderSize.square1080, Rgba32.black, duration = 6.0, frameRate = 12).frameCount)
        assertEquals(240, MediaSpec(RenderSize.square1080, Rgba32.black, duration = 8.0, frameRate = 30).frameCount)
    }

    @Test
    fun aVeryShortClipIsStillAtLeastOneFrame() {
        // 0.01s at 12fps rounds to 0. One frame is right; zero is a clip that
        // encodes to nothing and reports success.
        val s = MediaSpec(RenderSize.square1080, Rgba32.black, duration = 0.01, frameRate = 12)
        assertEquals(1, s.frameCount)
    }

    @Test
    fun aZeroFrameRateDoesNotDivideOrMultiplyByZero() {
        val s = MediaSpec(RenderSize.square1080, Rgba32.black, duration = 4.0, frameRate = 0)
        assertEquals(4, s.frameCount)
    }

    @Test
    fun tokensReplaceOnlyWhatIsThere() {
        val t = MediaSpec.applyTokens("Hello {{name}}, you owe {{amount}}.", mapOf("name" to "Thabo"))
        // The unmatched key is LEFT ALONE, not blanked: a half-substituted
        // template is diagnosable on sight, one with holes in it is not.
        assertEquals("Hello Thabo, you owe {{amount}}.", t)
    }

    @Test
    fun noTokensLeavesTheTemplateExactlyAsItWas() {
        assertEquals("{{a}}", MediaSpec.applyTokens("{{a}}", null))
        assertEquals("{{a}}", MediaSpec.applyTokens("{{a}}", emptyMap()))
    }

    @Test
    fun theStandardSizesAreTheAspectRatiosTheyClaim() {
        assertEquals(1080L * 1080L, RenderSize.square1080.pixelCount)
        assertEquals(1080, RenderSize.portrait1080x1920.width)
        assertEquals(1920, RenderSize.portrait1080x1920.height)
        assertEquals(1920, RenderSize.landscape1920x1080.width)
        assertEquals(540, RenderSize.preview540x960.width)
    }
}

class MediaTemplatesTest {

    @Test
    fun theSolidColourSourceIsOnePixel() {
        val s = MediaTemplates.solidColor(Rgba32(1, 2, 3, 4)) as RawImageSource
        assertEquals(1, s.width)
        assertEquals(1, s.height)
        assertContentEquals(byteArrayOf(1, 2, 3, 4), s.rgba)
    }

    @Test
    fun theSocialAdStacksBackgroundScrimAndHeadlineInThatOrder() {
        val spec = MediaTemplates.socialAd(
            RenderSize.portrait1080x1920,
            MediaTemplates.solidColor(Rgba32.white),
            "Half price this week",
            "At every branch",
        )
        assertEquals(listOf("bg", "scrim"), spec.images.map { it.id })
        assertEquals(listOf("headline", "subline"), spec.texts.map { it.id })

        // The scrim must sit ABOVE the photo or it does not make text readable.
        assertTrue(spec.images[1].zOrder > spec.images[0].zOrder)
        assertTrue(spec.texts.all { it.zOrder > spec.images.maxOf { i -> i.zOrder } })
        assertEquals(6.0, spec.duration)
    }

    @Test
    fun theSocialAdDropsTheSublineWhenThereIsNothingToSay() {
        val spec = MediaTemplates.socialAd(RenderSize.square1080, null, "Headline", "   ")
        assertEquals(listOf("headline"), spec.texts.map { it.id })
        // And with no background image there is still a scrim and a colour.
        assertEquals(listOf("scrim"), spec.images.map { it.id })
    }

    @Test
    fun aFullyTransparentScrimIsNotDrawnAtAll() {
        val spec = MediaTemplates.socialAd(
            RenderSize.square1080, null, "Headline", scrimColor = Rgba32.transparent,
        )
        assertTrue(spec.images.isEmpty())
    }

    @Test
    fun theBackgroundMovesUnderKenBurns() {
        val spec = MediaTemplates.socialAd(
            RenderSize.square1080, MediaTemplates.solidColor(Rgba32.white), "H",
        )
        assertEquals(Motion.kenBurns, spec.images.first { it.id == "bg" }.motion)
        assertEquals(ContentFit.COVER, spec.images.first { it.id == "bg" }.fit)
    }

    @Test
    fun theCvCardStaggersNameTitleAndContact() {
        // Each one starts after the last: a card where everything arrives at
        // once reads as a slide, not an introduction.
        val spec = MediaTemplates.videoCvCard(
            RenderSize.portrait1080x1920, null,
            "Thabo Mokoena", "Forklift Operator (Code 14)", "071 000 0000",
        )
        val starts = spec.texts.map { it.motion!!.startFraction }
        assertEquals(starts.sorted(), starts)
        assertEquals(listOf("name", "title", "contact"), spec.texts.map { it.id })
        assertEquals(8.0, spec.duration)
    }

    @Test
    fun theCvCardWithoutAPortraitStillLaysOutTheText() {
        val spec = MediaTemplates.videoCvCard(RenderSize.square1080, null, "N", "T")
        assertTrue(spec.images.isEmpty())
        assertEquals(2, spec.texts.size)
    }

    @Test
    fun fromHtmlCarriesTheTemplateAndDrawsNothingItself() {
        val spec = MediaTemplates.fromHtml(
            RenderSize.square1080, "<h1>{{title}}</h1>", mapOf("title" to "Hello"),
        )
        assertNotNull(spec.html)
        assertTrue(spec.images.isEmpty())
        assertTrue(spec.texts.isEmpty())
        assertEquals("<h1>Hello</h1>", MediaSpec.applyTokens(spec.html!!.html, spec.html!!.tokens))
    }
}
