// Video.kt
//
// Kotlin port of CircleAI.Video — the C# reference is the EXACT spec
// (Contracts.cs, Primitives.cs, NullImplementations.cs).
//
// The video contract surface: three interfaces — one generator (IVideoGenerator),
// one script rewriter (IStyleScript), one style catalogue (IStyleReference).
// Null implementations ship out of the box; the in-memory style catalogue is a
// complete thread-safe implementation suitable for production use.
//
// Driving use case: txtMe Video Mail. Sender calls, no answer, types a message.
// Recipient's B! renders the message as a short styled video — public-domain or
// original-character voice — gated at the BestFit selector's MinVramGb dimension.
//
// Design fidelity notes:
//   * C# `record`                    -> Kotlin `data class`.
//   * C# `readonly record struct`    -> Kotlin `data class` (StyleId, VideoResolution).
//   * C# implicit `operator string`  -> `StyleId.toString()` + [asString] helper.
//   * C# `ValueTask<T>`              -> `suspend fun`.
//   * C# `ValueTask` (void)          -> `suspend fun` returning Unit.
//   * C# `ReadOnlyMemory<byte>`      -> `ByteArray`.
//   * C# `TimeSpan`                  -> `java.time.Duration`.
//   * C# `IReadOnlyList<T>`          -> `List<T>`.
//   * C# `Nullable<StyleId>` field named `StyleId` -> `styleId: StyleId?` (the
//     field is renamed to lowerCamel per Kotlin convention; the type keeps its
//     PascalCase name, resolving the C# name/type collision cleanly).
//
// CONCURRENCY: the in-memory style catalogue guards its map under a single
// monitor and never holds the lock across a suspension or a callback; List
// returns a defensive copy so callers cannot mutate catalogue state.

package com.bhengubv.circleai.video

import java.time.Duration

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/**
 * Identifier for one registered style (e.g. "pooh-1926", "noir-detective",
 * "space-opera"). Mirrors C# `StyleId` — a value wrapper over a string whose
 * `ToString()` and implicit string conversion both surface [value].
 */
data class StyleId(val value: String) {
    override fun toString(): String = value

    companion object {
        /** Kotlin analogue of the C# implicit `operator string(StyleId)`. */
        fun asString(id: StyleId): String = id.value
    }
}

/** Output resolution for a generated video. Mirrors C# `VideoResolution`. */
data class VideoResolution(val width: Int, val height: Int) {
    companion object {
        val P480: VideoResolution get() = VideoResolution(720, 480)
        val P720: VideoResolution get() = VideoResolution(1280, 720)
        val P1080: VideoResolution get() = VideoResolution(1920, 1080)
    }
}

/**
 * One reference frame the generator can ground style on — public-domain
 * illustration, original-character render, etc. Mirrors C# `StyleReferenceFrame`.
 */
data class StyleReferenceFrame(
    val imageBytes: ByteArray,
    val mimeType: String,
    val caption: String? = null,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is StyleReferenceFrame) return false
        return mimeType == other.mimeType &&
            caption == other.caption &&
            imageBytes.contentEquals(other.imageBytes)
    }

    override fun hashCode(): Int {
        var r = imageBytes.contentHashCode()
        r = 31 * r + mimeType.hashCode()
        r = 31 * r + (caption?.hashCode() ?: 0)
        return r
    }
}

/**
 * Attribution + license metadata for one style — lets txtMe (and any other
 * consumer) display the source to the user before rendering. Mirrors C#
 * `StyleAttribution`.
 */
data class StyleAttribution(
    val source: String,
    val license: String,
    val url: String? = null,
)

/**
 * One style the host has registered with the catalogue. Picked up by
 * [IStyleReference.getAsync]. Mirrors C# `StyleReference`.
 */
data class StyleReference(
    val id: StyleId,
    val displayName: String,
    val shortDescription: String,
    val attribution: StyleAttribution,
    val voicePersonaId: String?,
    val frames: List<StyleReferenceFrame>,
)

/**
 * Audio track produced by CircleAI.Speech for the generator to embed. Mirrors C#
 * `AudioTrack`.
 */
data class AudioTrack(
    val audioPcm16Mono: ByteArray,
    val sampleRateHz: Int,
    val duration: Duration,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is AudioTrack) return false
        return sampleRateHz == other.sampleRateHz &&
            duration == other.duration &&
            audioPcm16Mono.contentEquals(other.audioPcm16Mono)
    }

    override fun hashCode(): Int {
        var r = audioPcm16Mono.contentHashCode()
        r = 31 * r + sampleRateHz
        r = 31 * r + duration.hashCode()
        return r
    }
}

/**
 * One generation request — text + optional style + optional grounding image +
 * optional audio. Mirrors C# `VideoGenerationRequest`. The C# field named
 * `StyleId` (of type `StyleId?`) is spelled [styleId] here per Kotlin
 * convention.
 */
data class VideoGenerationRequest(
    val prompt: String,
    val duration: Duration,
    val resolution: VideoResolution,
    val frameRate: Int = 24,
    val styleId: StyleId? = null,
    val referenceImage: StyleReferenceFrame? = null,
    val audioTrack: AudioTrack? = null,
    val seed: Long? = null,
)

/** One generation outcome. Mirrors C# `VideoGenerationResult`. */
data class VideoGenerationResult(
    val videoBytes: ByteArray,
    val mimeType: String,
    val duration: Duration,
    val frameCount: Int,
    val resolution: VideoResolution,
    val backendId: String,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VideoGenerationResult) return false
        return mimeType == other.mimeType &&
            duration == other.duration &&
            frameCount == other.frameCount &&
            resolution == other.resolution &&
            backendId == other.backendId &&
            videoBytes.contentEquals(other.videoBytes)
    }

    override fun hashCode(): Int {
        var r = videoBytes.contentHashCode()
        r = 31 * r + mimeType.hashCode()
        r = 31 * r + duration.hashCode()
        r = 31 * r + frameCount
        r = 31 * r + resolution.hashCode()
        r = 31 * r + backendId.hashCode()
        return r
    }
}

/** One style-script request — raw user message + chosen voice. Mirrors C# `StyleScriptRequest`. */
data class StyleScriptRequest(
    val sourceMessage: String,
    val style: StyleId,
    val speakerHint: String? = null,
    val languageHint: String? = null,
)

/**
 * One style-script outcome — the rewritten line + voice + estimated duration.
 * Mirrors C# `StyleScriptResult`.
 */
data class StyleScriptResult(
    val rewrittenText: String,
    val style: StyleId,
    val voicePersonaId: String?,
    val estimatedSpokenDuration: Duration,
)

// =====================================================================
// Contract interfaces (Contracts.cs)
// =====================================================================

/**
 * Generate a short video from a text prompt (and optional style + reference
 * frame + audio track). The first concrete backend is CogVideoX-2B; LTX-Video
 * distilled-2B follows. Both run on-device (≤ 12 GB VRAM quantised) under MNN.
 * Mirrors C# `IVideoGenerator`.
 */
interface IVideoGenerator {
    /** Backend self-identification — "cogvideox-2b", "ltx-video-2b-distilled", "null". */
    val backendId: String

    /** Synthesise the requested video. Throws if the device cannot satisfy the request. */
    suspend fun generateAsync(request: VideoGenerationRequest): VideoGenerationResult
}

/**
 * Rewrite a user message in a chosen style's voice. Runs against the existing
 * IChatGenerator with a style-specific system prompt — no new model needed for
 * this leg. Mirrors C# `IStyleScript`.
 */
interface IStyleScript {
    /** Backend self-identification — "circleai-llm", "null". */
    val backendId: String

    /** Rewrite the source message in the requested style. */
    suspend fun rewriteAsync(request: StyleScriptRequest): StyleScriptResult
}

/**
 * Catalogue of registered styles — public-domain illustrations,
 * original-character renders, genre presets (noir, space-opera,
 * storybook-watercolour, claymation, anime, …). Lets the txtMe UI present a
 * picker and lets the generator look up grounding frames. Mirrors C#
 * `IStyleReference`.
 */
interface IStyleReference {
    /** Backend self-identification — "in-memory", "embedded-defaults", "null". */
    val backendId: String

    /** Register a style (typically at host startup). */
    suspend fun registerAsync(style: StyleReference)

    /** Look up one style by id, or null when unknown. */
    suspend fun getAsync(id: StyleId): StyleReference?

    /** Enumerate every registered style — drives picker UIs. */
    suspend fun listAsync(): List<StyleReference>
}

// =====================================================================
// Null / in-memory implementations (NullImplementations.cs)
// =====================================================================

/**
 * Returns an empty video — zero bytes, declared mime type "video/mp4". Useful as
 * the DI default. A real consumer that ends up with this backend should fall
 * back to audio-only style mail. Mirrors C# `NullVideoGenerator`.
 */
class NullVideoGenerator private constructor() : IVideoGenerator {
    override val backendId: String get() = "null"

    override suspend fun generateAsync(request: VideoGenerationRequest): VideoGenerationResult =
        VideoGenerationResult(
            videoBytes = ByteArray(0),
            mimeType = "video/mp4",
            duration = Duration.ZERO,
            frameCount = 0,
            resolution = request.resolution,
            backendId = "null",
        )

    companion object {
        val Instance = NullVideoGenerator()
    }
}

/**
 * Returns the source message unchanged with a zero estimated duration. Useful so
 * consumers can swap in a real LLM-backed rewriter (typically a thin wrapper over
 * IChatGenerator + a per-style system prompt) without changing the wiring.
 * Mirrors C# `NullStyleScript`.
 */
class NullStyleScript private constructor() : IStyleScript {
    override val backendId: String get() = "null"

    override suspend fun rewriteAsync(request: StyleScriptRequest): StyleScriptResult =
        StyleScriptResult(
            rewrittenText = request.sourceMessage,
            style = request.style,
            voicePersonaId = null,
            estimatedSpokenDuration = Duration.ZERO,
        )

    companion object {
        val Instance = NullStyleScript()
    }
}

/**
 * Thread-safe in-memory style catalogue. The default implementation — hosting
 * layers (txtMe, content authoring tools) register their style packs on startup
 * and the picker reads from here. Suitable for production use until a persistent
 * store lands. Mirrors C# `InMemoryStyleReference`, including the
 * OrdinalIgnoreCase key semantics (last-write-wins per case-insensitive id).
 */
class InMemoryStyleReference : IStyleReference {

    // OrdinalIgnoreCase map: key on the lowercased id, retain the full record.
    private val byId = HashMap<String, StyleReference>()
    private val gate = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun registerAsync(style: StyleReference) {
        synchronized(gate) { byId[style.id.value.lowercase()] = style }
    }

    override suspend fun getAsync(id: StyleId): StyleReference? =
        synchronized(gate) { byId[id.value.lowercase()] }

    override suspend fun listAsync(): List<StyleReference> =
        synchronized(gate) { ArrayList(byId.values) }
}
