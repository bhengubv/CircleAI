// MediaSpec.kt
//
// Port of CircleAI.Media.Rendering: the declarative half. A MediaSpec says what
// a still or a clip contains in NORMALISED coordinates, so the same spec
// renders at 540x960 for a preview and 1080x1920 for the real thing.
//
// C# -> Kotlin notes:
//   * byte channels become Int in 0..255. Kotlin Byte is signed, and a colour
//     channel that goes negative above 127 is a bug waiting in every blend.
//   * TimeSpan becomes seconds as a Double.
//   * The C# RawImageSource / EncodedImageSource pair becomes a sealed
//     interface, so a layer cannot hold a third kind nobody handles.

package com.bhengubv.circleai.media.rendering

import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.roundToLong

/** Raised when a colour string is not one this parser knows. */
sealed class ColourError(message: String) : IllegalArgumentException(message) {
    class Unrecognised(val hex: String) :
        ColourError("Unrecognised colour " + hex + ". Use #RGB, #RRGGBB or #RRGGBBAA.")

    class InvalidHexDigit(val digit: Char) : ColourError("Invalid hex digit " + digit + ".")
}

/** Straight, unpremultiplied RGBA. Channels are 0..255. */
data class Rgba32(val r: Int, val g: Int, val b: Int, val a: Int) {

    fun withAlpha(alpha: Int): Rgba32 = Rgba32(r, g, b, alpha)

    companion object {
        val transparent = Rgba32(0, 0, 0, 0)
        val black = Rgba32(0, 0, 0, 255)
        val white = Rgba32(255, 255, 255, 255)

        fun rgb(r: Int, g: Int, b: Int): Rgba32 = Rgba32(r, g, b, 255)

        /**
         * #RGB, #RRGGBB or #RRGGBBAA, with or without the hash.
         *
         * The three-digit form DUPLICATES each nibble, so #f00 is ff0000, not
         * f00000. Shifting instead of duplicating makes every short colour
         * darker than it was written, which nobody notices until the brand
         * blue is wrong.
         */
        fun hex(hex: String): Rgba32 {
            val s = if (hex.startsWith("#")) hex.substring(1) else hex

            fun nib(c: Char): Int = when (c) {
                in Char(48)..Char(57) -> c.code - 48
                in Char(97)..Char(102) -> c.code - 97 + 10
                in Char(65)..Char(70) -> c.code - 65 + 10
                else -> throw ColourError.InvalidHexDigit(c)
            }

            fun dup(c: Char): Int { val v = nib(c); return (v shl 4) or v }
            fun hex2(i: Int): Int = (nib(s[i]) shl 4) or nib(s[i + 1])

            return when (s.length) {
                3 -> Rgba32(dup(s[0]), dup(s[1]), dup(s[2]), 255)
                6 -> Rgba32(hex2(0), hex2(2), hex2(4), 255)
                8 -> Rgba32(hex2(0), hex2(2), hex2(4), hex2(6))
                else -> throw ColourError.Unrecognised(hex)
            }
        }
    }
}

// ----------------------------------------------------------- Geometry

data class RenderSize(val width: Int, val height: Int) {
    val pixelCount: Long get() = width.toLong() * height.toLong()

    companion object {
        val square1080 = RenderSize(1080, 1080)
        val portrait1080x1920 = RenderSize(1080, 1920)
        val landscape1920x1080 = RenderSize(1920, 1080)
        val preview540x960 = RenderSize(540, 960)
    }
}

/** A rectangle in 0..1 of the canvas, so a spec is resolution-independent. */
data class NormRect(val x: Double, val y: Double, val w: Double, val h: Double) {
    companion object { val full = NormRect(0.0, 0.0, 1.0, 1.0) }
}

data class NormVec(val x: Double = 0.0, val y: Double = 0.0) {
    companion object { val zero = NormVec() }
}

enum class ContentFit { FILL, CONTAIN, COVER }

enum class TextAlign { LEFT, CENTER, RIGHT }

enum class EasingKind { LINEAR, EASE_IN, EASE_OUT, EASE_IN_OUT }

// ------------------------------------------------------------- Motion

/**
 * How one layer moves across the clip. The fractions are of the WHOLE clip, so
 * a spec does not care how many frames it ends up being rendered at.
 */
data class Motion(
    val startFraction: Double = 0.0,
    val endFraction: Double = 1.0,
    val fromOpacity: Double = 1.0,
    val toOpacity: Double = 1.0,
    val fromScale: Double = 1.0,
    val toScale: Double = 1.0,
    val fromTranslate: NormVec = NormVec.zero,
    val toTranslate: NormVec = NormVec.zero,
    val easing: EasingKind = EasingKind.LINEAR,
) {
    companion object {
        val none = Motion()
        val fadeIn = Motion(
            startFraction = 0.0, endFraction = 0.25,
            fromOpacity = 0.0, toOpacity = 1.0, easing = EasingKind.EASE_OUT,
        )
        val fadeOut = Motion(
            startFraction = 0.75, endFraction = 1.0,
            fromOpacity = 1.0, toOpacity = 0.0, easing = EasingKind.EASE_IN,
        )
        val kenBurns = Motion(
            fromScale = 1.0, toScale = 1.12,
            toTranslate = NormVec(0.03, 0.02), easing = EasingKind.EASE_IN_OUT,
        )
    }
}

/** Where a motion has got to at a given global progress. */
data class MotionState(val opacity: Double, val scale: Double, val translate: NormVec)

object Easing {

    fun lerp(a: Double, b: Double, t: Double): Double = a + (b - a) * t

    fun apply(kind: EasingKind, t: Double): Double = when (kind) {
        EasingKind.EASE_IN -> t * t
        EasingKind.EASE_OUT -> 1.0 - (1.0 - t) * (1.0 - t)
        EasingKind.EASE_IN_OUT -> t * t * (3.0 - 2.0 * t) // smoothstep
        EasingKind.LINEAR -> t
    }

    /**
     * A ZERO-LENGTH window snaps at its end rather than dividing by zero. That
     * is not a theoretical case: it is what a caller writes when they want a
     * layer to appear at one instant.
     */
    fun evaluate(m: Motion?, g: Double): MotionState {
        if (m == null) return MotionState(1.0, 1.0, NormVec.zero)
        val span = m.endFraction - m.startFraction
        val local =
            if (span <= 0.0) (if (g >= m.endFraction) 1.0 else 0.0)
            else max(0.0, min(1.0, (g - m.startFraction) / span))
        val e = apply(m.easing, local)
        return MotionState(
            lerp(m.fromOpacity, m.toOpacity, e),
            lerp(m.fromScale, m.toScale, e),
            NormVec(
                lerp(m.fromTranslate.x, m.toTranslate.x, e),
                lerp(m.fromTranslate.y, m.toTranslate.y, e),
            ),
        )
    }
}

// --------------------------------------------------------- The layers

/** Where a layer gets its pixels from. */
sealed interface ImageSource

/** Already-decoded RGBA, row-major, no padding. */
class RawImageSource(val rgba: ByteArray, val width: Int, val height: Int) : ImageSource {
    override fun equals(other: Any?): Boolean =
        other is RawImageSource && width == other.width && height == other.height &&
            rgba.contentEquals(other.rgba)

    override fun hashCode(): Int = (rgba.contentHashCode() * 31 + width) * 31 + height
}

/** Undecoded bytes; needs a decoder wired, or the layer is skipped. */
class EncodedImageSource(val bytes: ByteArray, val mimeHint: String? = null) : ImageSource {
    override fun equals(other: Any?): Boolean =
        other is EncodedImageSource && mimeHint == other.mimeHint && bytes.contentEquals(other.bytes)

    override fun hashCode(): Int = bytes.contentHashCode() * 31 + (mimeHint?.hashCode() ?: 0)
}

data class ImageLayer(
    val source: ImageSource,
    val rect: NormRect,
    val fit: ContentFit = ContentFit.COVER,
    val opacity: Double = 1.0,
    val motion: Motion? = null,
    val zOrder: Int = 0,
    val id: String? = null,
)

data class TextOverlay(
    val text: String,
    val rect: NormRect,
    val fontHeightFraction: Double = 0.08,
    val color: Rgba32 = Rgba32.transparent,
    val align: TextAlign = TextAlign.CENTER,
    val boxColor: Rgba32 = Rgba32.transparent,
    val letterSpacingFraction: Double = 0.2,
    val lineSpacingFraction: Double = 0.35,
    val motion: Motion? = null,
    val zOrder: Int = 100,
    val id: String? = null,
)

data class HtmlTemplateSource(val html: String, val tokens: Map<String, String>? = null)

data class MediaSpec(
    val size: RenderSize,
    val background: Rgba32,
    val images: List<ImageLayer> = emptyList(),
    val texts: List<TextOverlay> = emptyList(),
    /** Seconds. Zero or less means a still. */
    val duration: Double = 0.0,
    val frameRate: Int = 12,
    val html: HtmlTemplateSource? = null,
) {
    val isStill: Boolean get() = duration <= 0.0

    /** At least one frame, always. A 0.01s clip is still a frame, not nothing. */
    val frameCount: Int
        get() = if (isStill) 1 else max(1, (duration * max(1, frameRate).toDouble()).roundToLong().toInt())

    companion object {
        fun still(
            size: RenderSize,
            background: Rgba32,
            images: List<ImageLayer> = emptyList(),
            texts: List<TextOverlay> = emptyList(),
        ): MediaSpec = MediaSpec(size, background, images, texts, duration = 0.0, frameRate = 1)

        /**
         * Replaces {{key}} with its value. A key with NO token is left alone
         * rather than blanked - a half-substituted template is easy to diagnose
         * on sight, one with holes in it is not.
         */
        fun applyTokens(template: String, tokens: Map<String, String>?): String {
            if (tokens.isNullOrEmpty()) return template
            var out = template
            for ((k, v) in tokens) out = out.replace("{{" + k + "}}", v)
            return out
        }
    }
}

// ------------------------------------------------------- The templates

/** Built-in declarative templates for the common programmatic-media jobs. */
object MediaTemplates {

    /** A 1x1 solid-colour source, useful as a stretched scrim or colour block. */
    fun solidColor(color: Rgba32): ImageSource = RawImageSource(
        byteArrayOf(color.r.toByte(), color.g.toByte(), color.b.toByte(), color.a.toByte()),
        1,
        1,
    )

    /**
     * A short social ad: a full-bleed background under a slow Ken Burns move, a
     * legibility scrim, a fading headline and an optional subline.
     */
    fun socialAd(
        size: RenderSize,
        background: ImageSource?,
        headline: String,
        subline: String? = null,
        backgroundColor: Rgba32? = null,
        textColor: Rgba32? = null,
        scrimColor: Rgba32? = null,
        duration: Double? = null,
        frameRate: Int = 12,
    ): MediaSpec {
        val bg = backgroundColor ?: Rgba32.hex("#0B1F3A")
        val col = textColor ?: Rgba32.white
        val scrim = scrimColor ?: Rgba32(0, 0, 0, 110)

        val images = mutableListOf<ImageLayer>()
        if (background != null) {
            images.add(
                ImageLayer(background, NormRect.full, ContentFit.COVER, motion = Motion.kenBurns, zOrder = 0, id = "bg"),
            )
        }
        if (scrim.a > 0) {
            images.add(
                ImageLayer(
                    solidColor(scrim), NormRect(0.0, 0.45, 1.0, 0.55),
                    ContentFit.FILL, zOrder = 5, id = "scrim",
                ),
            )
        }

        val texts = mutableListOf(
            TextOverlay(
                headline, NormRect(0.08, 0.55, 0.84, 0.2),
                fontHeightFraction = 0.075, color = col, align = TextAlign.CENTER,
                motion = Motion.fadeIn, zOrder = 100, id = "headline",
            ),
        )
        if (!subline.isNullOrBlank()) {
            texts.add(
                TextOverlay(
                    subline, NormRect(0.1, 0.77, 0.8, 0.12),
                    fontHeightFraction = 0.04, color = col, align = TextAlign.CENTER,
                    motion = Motion(
                        startFraction = 0.15, endFraction = 0.4,
                        fromOpacity = 0.0, toOpacity = 1.0, easing = EasingKind.EASE_OUT,
                    ),
                    zOrder = 101, id = "subline",
                ),
            )
        }

        return MediaSpec(size, bg, images, texts, duration ?: 6.0, frameRate)
    }

    /**
     * A video-CV title card: portrait, name, role and an optional contact line,
     * each easing in behind the last.
     */
    fun videoCvCard(
        size: RenderSize,
        portrait: ImageSource?,
        name: String,
        title: String,
        contact: String? = null,
        backgroundColor: Rgba32? = null,
        textColor: Rgba32? = null,
        accentColor: Rgba32? = null,
        duration: Double? = null,
        frameRate: Int = 12,
    ): MediaSpec {
        val bg = backgroundColor ?: Rgba32.hex("#0B1F3A")
        val col = textColor ?: Rgba32.white
        val accent = accentColor ?: Rgba32.hex("#2196F3")

        val images = mutableListOf<ImageLayer>()
        if (portrait != null) {
            images.add(
                ImageLayer(
                    portrait, NormRect(0.3, 0.08, 0.4, 0.34), ContentFit.COVER,
                    motion = Motion(
                        endFraction = 0.2, fromOpacity = 0.0, toOpacity = 1.0,
                        easing = EasingKind.EASE_OUT,
                    ),
                    zOrder = 0, id = "portrait",
                ),
            )
        }

        val texts = mutableListOf(
            TextOverlay(
                name, NormRect(0.05, 0.46, 0.9, 0.12),
                fontHeightFraction = 0.07, color = col, align = TextAlign.CENTER,
                motion = Motion.fadeIn, zOrder = 100, id = "name",
            ),
            TextOverlay(
                title, NormRect(0.05, 0.59, 0.9, 0.08),
                fontHeightFraction = 0.04, color = accent, align = TextAlign.CENTER,
                motion = Motion(
                    startFraction = 0.1, endFraction = 0.35,
                    fromOpacity = 0.0, toOpacity = 1.0, easing = EasingKind.EASE_OUT,
                ),
                zOrder = 101, id = "title",
            ),
        )
        if (!contact.isNullOrBlank()) {
            texts.add(
                TextOverlay(
                    contact, NormRect(0.05, 0.83, 0.9, 0.08),
                    fontHeightFraction = 0.032, color = col, align = TextAlign.CENTER,
                    motion = Motion(
                        startFraction = 0.2, endFraction = 0.5,
                        fromOpacity = 0.0, toOpacity = 1.0, easing = EasingKind.EASE_OUT,
                    ),
                    zOrder = 102, id = "contact",
                ),
            )
        }

        return MediaSpec(size, bg, images, texts, duration ?: 8.0, frameRate)
    }

    /** Wraps raw HTML for the WebView-capture seam; tokens are applied at hand-off. */
    fun fromHtml(
        size: RenderSize,
        html: String,
        tokens: Map<String, String>? = null,
        duration: Double? = null,
        frameRate: Int = 12,
        background: Rgba32? = null,
    ): MediaSpec = MediaSpec(
        size = size,
        background = background ?: Rgba32.white,
        images = emptyList(),
        texts = emptyList(),
        duration = duration ?: 6.0,
        frameRate = frameRate,
        html = HtmlTemplateSource(html, tokens),
    )
}
