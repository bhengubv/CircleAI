package com.bhengubv.circleai.media.rendering

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ManagedMediaRendererTest {

    private val renderer = ManagedMediaRenderer()

    private fun solid(w: Int, h: Int, c: Rgba32): ImageSource {
        val px = ByteArray(w * h * 4)
        for (i in 0 until w * h) {
            px[i * 4] = c.r.toByte()
            px[i * 4 + 1] = c.g.toByte()
            px[i * 4 + 2] = c.b.toByte()
            px[i * 4 + 3] = c.a.toByte()
        }
        return RawImageSource(px, w, h)
    }

    @Test
    fun anEmptySpecRendersTheBackgroundAndNothingElse() {
        val spec = MediaSpec.still(RenderSize(8, 8), Rgba32(10, 20, 30, 255))
        val still = renderer.renderStill(spec)!!
        assertEquals(8, still.width)
        assertEquals(Rgba32(10, 20, 30, 255), still.pixel(0, 0))
        assertEquals(Rgba32(10, 20, 30, 255), still.pixel(7, 7))
    }

    @Test
    fun aZeroSizedSpecIsNullRatherThanACrash() {
        assertNull(renderer.renderStill(MediaSpec.still(RenderSize(0, 8), Rgba32.black)))
    }

    @Test
    fun aRawLayerIsDrawnOverTheBackground() {
        val spec = MediaSpec.still(
            RenderSize(8, 8), Rgba32.black,
            images = listOf(ImageLayer(solid(2, 2, Rgba32.white), NormRect(0.0, 0.0, 0.5, 0.5), ContentFit.FILL)),
        )
        val still = renderer.renderStill(spec)!!
        assertEquals(Rgba32.white, still.pixel(1, 1))
        assertEquals(Rgba32.black, still.pixel(7, 7))
    }

    @Test
    fun anEncodedLayerIsSKIPPEDwhenNoDecoderIsWired() {
        // Skipped, not drawn as garbage: a spec built from raw pixels must keep
        // working on a build with no image decoder at all.
        val spec = MediaSpec.still(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(ImageLayer(EncodedImageSource(byteArrayOf(1, 2, 3)), NormRect.full)),
        )
        val still = renderer.renderStill(spec)!!
        assertEquals(Rgba32.black, still.pixel(2, 2))
    }

    @Test
    fun anEncodedLayerIsDrawnOnceADecoderIsWired() {
        val png = PngWriter.encode(
            PixelBuffer.of(2, 2)!!.also { RasterCanvas(it).clear(Rgba32.white) },
        )
        val withDecoder = ManagedMediaRenderer(ManagedMediaImageDecoder())
        val spec = MediaSpec.still(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(ImageLayer(EncodedImageSource(png, "image/png"), NormRect.full, ContentFit.FILL)),
        )
        assertEquals(Rgba32.white, withDecoder.renderStill(spec)!!.pixel(2, 2))
    }

    @Test
    fun zOrderDecidesWhatIsOnTop() {
        val spec = MediaSpec.still(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(
                ImageLayer(solid(1, 1, Rgba32.white), NormRect.full, ContentFit.FILL, zOrder = 10),
                ImageLayer(solid(1, 1, Rgba32.rgb(255, 0, 0)), NormRect.full, ContentFit.FILL, zOrder = 1),
            ),
        )
        // The z-10 white layer is listed FIRST but drawn LAST.
        assertEquals(Rgba32.white, renderer.renderStill(spec)!!.pixel(2, 2))
    }

    @Test
    fun layersAtTheSameDepthKeepTheOrderTheyWereListedIn() {
        // A stable sort, so the same spec renders the same way twice. An
        // unstable one produces a diff nobody can reproduce.
        val spec = MediaSpec.still(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(
                ImageLayer(solid(1, 1, Rgba32.rgb(255, 0, 0)), NormRect.full, ContentFit.FILL, zOrder = 0),
                ImageLayer(solid(1, 1, Rgba32.rgb(0, 255, 0)), NormRect.full, ContentFit.FILL, zOrder = 0),
            ),
        )
        repeat(5) { assertEquals(Rgba32.rgb(0, 255, 0), renderer.renderStill(spec)!!.pixel(2, 2)) }
    }

    @Test
    fun aFullyTransparentLayerIsNotDrawnAtAll() {
        val spec = MediaSpec.still(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(ImageLayer(solid(1, 1, Rgba32.white), NormRect.full, ContentFit.FILL, opacity = 0.0)),
        )
        assertEquals(Rgba32.black, renderer.renderStill(spec)!!.pixel(2, 2))
    }

    @Test
    fun theFirstFrameIsAtProgressZeroAndTheLastAtOne() {
        // A fade-in has to START fully transparent and a fade-out has to END
        // fully gone. Dividing by the frame count instead of one less leaves
        // every clip a frame short of its own ending.
        val spec = MediaSpec(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(
                ImageLayer(solid(1, 1, Rgba32.white), NormRect.full, ContentFit.FILL, motion = Motion.fadeOut),
            ),
            duration = 1.0, frameRate = 5,
        )
        val frames = renderer.frames(spec)
        assertEquals(5, frames.size)
        assertEquals(Rgba32.white, frames.first().pixel(2, 2))
        assertEquals(Rgba32.black, frames.last().pixel(2, 2))
    }

    @Test
    fun aStillProducesExactlyOneFrame() {
        val frames = renderer.frames(MediaSpec.still(RenderSize(4, 4), Rgba32.black))
        assertEquals(1, frames.size)
    }

    @Test
    fun thePosterFractionPicksAMomentAndIsClamped() {
        val spec = MediaSpec(
            RenderSize(4, 4), Rgba32.black,
            images = listOf(
                ImageLayer(solid(1, 1, Rgba32.white), NormRect.full, ContentFit.FILL, motion = Motion.fadeOut),
            ),
            duration = 1.0, frameRate = 5,
        )
        assertEquals(Rgba32.white, renderer.renderStill(spec, 0.0)!!.pixel(2, 2))
        assertEquals(Rgba32.black, renderer.renderStill(spec, 1.0)!!.pixel(2, 2))
        // Out of range is clamped, not wrapped or thrown.
        assertEquals(Rgba32.white, renderer.renderStill(spec, -3.0)!!.pixel(2, 2))
        assertEquals(Rgba32.black, renderer.renderStill(spec, 9.0)!!.pixel(2, 2))
    }

    @Test
    fun aTextOverlayWithNoColourSetDrawsWHITEnotInvisibleInk() {
        // TextOverlay defaults its colour to transparent, so a caller who never
        // picked one still has to be able to read the caption.
        val spec = MediaSpec.still(
            RenderSize(120, 60), Rgba32.black,
            texts = listOf(TextOverlay("HI", NormRect.full, fontHeightFraction = 0.3)),
        )
        val still = renderer.renderStill(spec)!!
        val anyWhite = (0 until 120).any { x ->
            (0 until 60).any { y -> still.pixel(x, y) == Rgba32.white }
        }
        assertTrue(anyWhite, "the caption drew nothing visible")
    }

    @Test
    fun anEmptyTextOverlayIsSkipped() {
        val spec = MediaSpec.still(
            RenderSize(40, 40), Rgba32.black,
            texts = listOf(TextOverlay("", NormRect.full)),
        )
        val still = renderer.renderStill(spec)!!
        assertTrue((0 until 40).all { x -> (0 until 40).all { y -> still.pixel(x, y) == Rgba32.black } })
    }

    @Test
    fun scaleIsAppliedAboutTheCENTREnotTheOrigin() {
        // A Ken Burns zoom has to push out evenly. Scaling about the origin
        // slides the whole picture down and to the right as it grows.
        val r = ManagedMediaRenderer.placeRect(
            NormRect(0.25, 0.25, 0.5, 0.5), RenderSize(100, 100), 2.0, NormVec.zero,
        )
        assertEquals(0.0, r[0])
        assertEquals(0.0, r[1])
        assertEquals(100.0, r[2])
        assertEquals(100.0, r[3])
    }

    @Test
    fun translateMovesTheRectangleByAFractionOfTheCanvas() {
        val r = ManagedMediaRenderer.placeRect(
            NormRect(0.0, 0.0, 1.0, 1.0), RenderSize(200, 100), 1.0, NormVec(0.1, 0.2),
        )
        assertEquals(20.0, r[0])
        assertEquals(20.0, r[1])
    }

    @Test
    fun renderClipHandsTheEncoderTheRightOptions() {
        val spec = MediaSpec(RenderSize(4, 4), Rgba32.black, duration = 1.0, frameRate = 6)
        val clip = renderer.renderClip(spec, AnimatedPngEncoder.instance)
        assertEquals(6, clip.frameCount)
        assertEquals("image/apng", clip.mimeType)
        assertEquals(RenderSize(4, 4), clip.size)
    }

    @Test
    fun aSpecRendersTheSameBytesTwice() {
        // Determinism is the whole contract of a managed renderer: no clock, no
        // random, no platform font.
        val spec = MediaTemplates.socialAd(
            RenderSize(64, 64), null, "SALE TODAY", "ALL BRANCHES", duration = 0.5, frameRate = 4,
        )
        val a = renderer.renderClip(spec, AnimatedPngEncoder.instance)
        val b = renderer.renderClip(spec, AnimatedPngEncoder.instance)
        assertEquals(a, b)
    }
}

class MediaNullsTest {

    private val spec = MediaSpec.still(RenderSize(16, 9), Rgba32.black)

    @Test
    fun theNullRendererDrawsAOnePixelStillAndNoFrames() {
        val r = NullMediaRenderer.instance
        assertEquals("null", r.backendId)
        val still = r.renderStill(spec)!!
        assertEquals(1, still.width)
        assertEquals(1, still.height)
        assertTrue(r.frames(spec).isEmpty())
    }

    @Test
    fun theNullRendererClipCarriesTheEncoderMimeTypeAndNoBytes() {
        val clip = NullMediaRenderer.instance.renderClip(spec, AnimatedPngEncoder.instance)
        assertEquals("image/apng", clip.mimeType)
        assertEquals(0, clip.bytes.size)
        assertEquals("null", clip.backendId)
    }

    @Test
    fun theNullVideoEncoderIsTheHonestGapMarkerForRealVideo() {
        // It advertises video/mp4 and emits nothing. That is deliberate: a real
        // H.264 clip needs a platform encoder, and pretending otherwise would
        // ship a zero-byte file somebody tries to send.
        val e = NullVideoEncoder.instance
        assertEquals("video/mp4", e.outputMimeType)
        val clip = e.encode(
            listOf(PixelBuffer.of(4, 4)!!),
            ClipEncodeOptions(RenderSize(4, 4), 12, 24),
        )
        assertEquals(0, clip.bytes.size)
        // The intended length is still REPORTED, from the options.
        assertEquals(24, clip.frameCount)
        assertEquals(12, clip.frameRate)
    }

    @Test
    fun theNullImageDecoderDecodesNothing() {
        assertNull(NullMediaImageDecoder.instance.decode(byteArrayOf(1, 2, 3)))
        assertEquals("null", NullMediaImageDecoder.instance.backendId)
    }

    @Test
    fun theNullHtmlProviderYieldsNoFrames() {
        val p = NullHtmlFrameProvider.instance
        assertEquals("null", p.backendId)
        assertTrue(p.renderHtmlFrames(HtmlTemplateSource("<h1>x</h1>"), RenderSize(4, 4), 3, 12).isEmpty())
    }
}
