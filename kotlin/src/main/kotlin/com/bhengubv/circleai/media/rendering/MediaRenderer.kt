// MediaRenderer.kt
//
// The renderer: composes a MediaSpec onto a raster canvas, frame by frame, plus
// the fail-closed nulls and the Companion adapter for the domain.

package com.bhengubv.circleai.media.rendering

import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * Decodes bytes to an RGBA PixelBuffer for COMPOSITING.
 *
 * Separate from the vision IImageDecoder, which decodes to packed RGB24 for a
 * model. Different contract, different name, so neither gets wired where the
 * other belongs.
 */
interface IMediaImageDecoder {
    val backendId: String
    fun decode(bytes: ByteArray, mimeHint: String? = null): PixelBuffer?
}

/**
 * No decoder is wired. Encoded layers are SKIPPED rather than drawn as
 * garbage, so a spec built from raw pixels still renders on any build.
 */
class NullMediaImageDecoder : IMediaImageDecoder {
    override val backendId: String get() = "null"
    override fun decode(bytes: ByteArray, mimeHint: String?): PixelBuffer? = null

    companion object { val instance = NullMediaImageDecoder() }
}

/** Adapts the managed PNG/BMP decoder onto the compositing seam. */
class ManagedMediaImageDecoder(
    private val inner: IImageDecoder = ManagedImageDecoder.instance,
) : IMediaImageDecoder {
    override val backendId: String get() = inner.backendId
    override fun decode(bytes: ByteArray, mimeHint: String?): PixelBuffer? =
        inner.tryDecode(bytes, mimeHint)
}

/** Renders HTML frames. The real path is a WebView capture in the host. */
interface IHtmlFrameProvider {
    val backendId: String
    fun renderHtmlFrames(
        html: HtmlTemplateSource,
        size: RenderSize,
        frameCount: Int,
        frameRate: Int,
    ): List<PixelBuffer>
}

class NullHtmlFrameProvider : IHtmlFrameProvider {
    override val backendId: String get() = "null"
    override fun renderHtmlFrames(
        html: HtmlTemplateSource,
        size: RenderSize,
        frameCount: Int,
        frameRate: Int,
    ): List<PixelBuffer> = emptyList()

    companion object { val instance = NullHtmlFrameProvider() }
}

interface IMediaRenderer {
    val backendId: String
    fun renderStill(spec: MediaSpec, posterFraction: Double = 0.0): PixelBuffer?
    fun frames(spec: MediaSpec): List<PixelBuffer>
    fun renderClip(spec: MediaSpec, encoder: IVideoEncoder): EncodedClip
}

/** Composes a spec onto a raster canvas, frame by frame. */
class ManagedMediaRenderer(
    private val decoder: IMediaImageDecoder = NullMediaImageDecoder.instance,
    private val font: BitmapFont = BitmapFont.default,
) : IMediaRenderer {

    override val backendId: String get() = "managed"

    override fun renderStill(spec: MediaSpec, posterFraction: Double): PixelBuffer? =
        compose(spec, min(1.0, max(0.0, posterFraction)), decodeLayers(spec))

    /**
     * The FIRST frame is at progress 0 and the LAST at 1, so a fade-in starts
     * fully transparent and a fade-out ends fully gone. Dividing by the frame
     * count instead of one less leaves every clip a frame short of its own
     * ending.
     */
    override fun frames(spec: MediaSpec): List<PixelBuffer> {
        val n = spec.frameCount
        val decoded = decodeLayers(spec)
        val out = ArrayList<PixelBuffer>(n)
        for (i in 0 until n) {
            val g = if (n <= 1) 0.0 else i.toDouble() / (n - 1).toDouble()
            compose(spec, g, decoded)?.let { out.add(it) }
        }
        return out
    }

    override fun renderClip(spec: MediaSpec, encoder: IVideoEncoder): EncodedClip {
        val options = ClipEncodeOptions(spec.size, max(1, spec.frameRate), spec.frameCount)
        return encoder.encode(frames(spec), options)
    }

    private fun decodeLayers(spec: MediaSpec): List<Pair<ImageLayer, PixelBuffer>> =
        spec.images.mapNotNull { layer ->
            val px = when (val s = layer.source) {
                is RawImageSource -> PixelBuffer.of(s.width, s.height, s.rgba)
                is EncodedImageSource -> decoder.decode(s.bytes, s.mimeHint)
            }
            px?.let { layer to it }
        }

    private fun compose(
        spec: MediaSpec,
        g: Double,
        layers: List<Pair<ImageLayer, PixelBuffer>>,
    ): PixelBuffer? {
        val canvas = RasterCanvas.of(spec.size.width, spec.size.height) ?: return null
        canvas.clear(spec.background)

        // A STABLE sort by z-order: two layers at the same depth keep the order
        // the caller listed them in, so a spec renders the same way twice.
        for ((layer, pixels) in layers.sortedBy { it.first.zOrder }) {
            val m = Easing.evaluate(layer.motion, g)
            val opacity = layer.opacity * m.opacity
            if (opacity <= 0) continue
            val r = placeRect(layer.rect, spec.size, m.scale, m.translate)
            canvas.drawImage(pixels, r[0], r[1], r[2], r[3], layer.fit, opacity)
        }

        for (overlay in spec.texts.sortedBy { it.zOrder }) {
            if (overlay.text.isEmpty()) continue
            val m = Easing.evaluate(overlay.motion, g)
            if (m.opacity <= 0) continue
            val r = placeRect(overlay.rect, spec.size, 1.0, m.translate)

            // A fully transparent colour means NOT SET, so it becomes white
            // rather than invisible ink - the default TextOverlay colour is
            // transparent, and a caller who never set one still wants to read it.
            val color = if (overlay.color.a == 0) Rgba32.white else overlay.color
            val fontPx = max(
                BitmapFont.ROWS,
                (overlay.fontHeightFraction * spec.size.height).roundToInt(),
            )

            canvas.drawText(
                font = font,
                text = overlay.text,
                rx = r[0].roundToInt(),
                ry = r[1].roundToInt(),
                rw = r[2].roundToInt(),
                rh = r[3].roundToInt(),
                pixelHeight = fontPx,
                color = color,
                align = overlay.align,
                box = overlay.boxColor,
                letterSpacingFraction = overlay.letterSpacingFraction,
                lineSpacingFraction = overlay.lineSpacingFraction,
                opacity = m.opacity,
            )
        }
        return canvas.buffer
    }

    companion object {
        /**
         * Scale is applied about the rectangle CENTRE, not its origin, so a
         * Ken Burns zoom pushes out evenly instead of sliding down and right.
         */
        fun placeRect(
            rect: NormRect,
            size: RenderSize,
            scale: Double,
            translate: NormVec,
        ): DoubleArray {
            var x = rect.x * size.width
            var y = rect.y * size.height
            var w = rect.w * size.width
            var h = rect.h * size.height

            val cx = x + w / 2.0
            val cy = y + h / 2.0
            w *= scale
            h *= scale
            x = cx - w / 2.0
            y = cy - h / 2.0

            x += translate.x * size.width
            y += translate.y * size.height
            return doubleArrayOf(x, y, w, h)
        }
    }
}

/** Renders nothing: a 1x1 transparent still and an empty clip. */
class NullMediaRenderer : IMediaRenderer {
    override val backendId: String get() = "null"
    override fun renderStill(spec: MediaSpec, posterFraction: Double): PixelBuffer? =
        PixelBuffer.of(1, 1)
    override fun frames(spec: MediaSpec): List<PixelBuffer> = emptyList()
    override fun renderClip(spec: MediaSpec, encoder: IVideoEncoder): EncodedClip =
        EncodedClip(ByteArray(0), encoder.outputMimeType, 0, spec.size, spec.frameRate, "null")

    companion object { val instance = NullMediaRenderer() }
}

/**
 * Emits an empty video/mp4 and is the HONEST GAP MARKER for real video.
 *
 * A genuine H.264 clip needs an encoder that is not feasible in managed code on
 * a low-end phone; the de-Googled on-device path is AOSP MediaCodec wired
 * through IVideoEncoder from the host. For a real pure-managed clip, use
 * AnimatedPngEncoder. The frames are deliberately NOT consumed, so nothing is
 * composited for output that will be thrown away.
 */
class NullVideoEncoder : IVideoEncoder {
    override val backendId: String get() = "null"
    override val outputMimeType: String get() = "video/mp4"
    override fun encode(frames: List<PixelBuffer>, options: ClipEncodeOptions): EncodedClip =
        EncodedClip(
            ByteArray(0), "video/mp4", options.frameCount, options.size, options.frameRate, "null",
        )

    companion object { val instance = NullVideoEncoder() }
}
