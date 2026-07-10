// VisionCloud.kt
//
// Kotlin port of CircleAI.Vision.Cloud — the C# reference is the EXACT spec
// (Contracts.cs, Options.cs, OpenAiImageGenerator.cs, StabilityImageGenerator.cs,
// ImageGeneratorFallbackChain.cs, ServiceCollectionExtensions.cs).
//
// The image-generation counterpart to CircleAI.Vision (which is detection-only):
// an IImageGenerator contract, an OpenAI DALL-E generator (JSON body →
// response_format=url), a Stability AI generator (multipart form → inline
// bytes), a fallback chain, and a null generator.
//
// Design fidelity notes:
//   * C# `record`             -> Kotlin `data class`.
//   * C# `Task<T>`            -> `suspend fun`.
//   * C# `IReadOnlyList<T>`   -> `List<T>`.
//   * C# `byte[]?`            -> `ByteArray?`.
//   * C# HttpClient           -> injected [IImageHttpTransport] seam (mirrors the
//                                CloudFallback ICloudHttpTransport pattern) so the
//                                generators are deterministic-testable; a host
//                                wires a real transport, tests supply a fake.
//   * Math.Clamp(Count, 1, 4) safety, response_format=url path, multipart form
//     field ordering, and the 200-range success gate are ported verbatim.
//
// The DI ServiceCollectionExtensions are a .NET-hosting concern (there is no
// Microsoft.Extensions.DependencyInjection on the Kotlin substrate); the
// generator-id constants they expose are ported as [VisionCloudGeneratorIds] so
// the wire identity is preserved, and hosts compose the fallback chain directly.

package com.bhengubv.circleai.visioncloud

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import java.time.Instant
import kotlin.math.max
import kotlin.math.min

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/**
 * One image-generation request. Mirrors C# `ImageGenerationRequest`.
 *
 * @param prompt Text prompt.
 * @param negativePrompt Optional negative prompt (Stability supports it; OpenAI ignores).
 * @param size Square size in pixels — typical 512 / 768 / 1024 / 1536.
 * @param count Number of images to produce (1..n).
 * @param style Optional style preset id (provider-specific).
 */
data class ImageGenerationRequest(
    val prompt: String,
    val negativePrompt: String? = null,
    val size: Int = 1024,
    val count: Int = 1,
    val style: String? = null,
)

/**
 * One generated image. Either [url] OR [bytes], never both. Mirrors C#
 * `ImageArtifact`.
 */
data class ImageArtifact(
    val generatorId: String,
    val prompt: String,
    val mimeType: String,
    val url: String?,
    val bytes: ByteArray?,
    val generatedAtUtc: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ImageArtifact) return false
        return generatorId == other.generatorId &&
            prompt == other.prompt &&
            mimeType == other.mimeType &&
            url == other.url &&
            generatedAtUtc == other.generatedAtUtc &&
            (bytes?.contentEquals(other.bytes) ?: (other.bytes == null))
    }

    override fun hashCode(): Int {
        var r = generatorId.hashCode()
        r = 31 * r + prompt.hashCode()
        r = 31 * r + mimeType.hashCode()
        r = 31 * r + (url?.hashCode() ?: 0)
        r = 31 * r + (bytes?.contentHashCode() ?: 0)
        r = 31 * r + generatedAtUtc.hashCode()
        return r
    }
}

/** Generate images from a text prompt. Mirrors C# `IImageGenerator`. */
interface IImageGenerator {
    /** Backend self-identification — "openai-images" / "stability" / "null". */
    val generatorId: String

    /** Display label for the UI selector. */
    val displayLabel: String

    /** True when the generator has the credentials it needs. */
    val isConfigured: Boolean

    /** Status message for the UI. */
    val statusMessage: String

    /** Generate images. Fail-soft: empty list when not configured. */
    suspend fun generateAsync(request: ImageGenerationRequest): List<ImageArtifact>
}

/** Empty generator — always returns no images. Mirrors C# `NullImageGenerator`. */
class NullImageGenerator private constructor() : IImageGenerator {
    override val generatorId: String get() = "null"
    override val displayLabel: String get() = "No image generator"
    override val isConfigured: Boolean get() = false
    override val statusMessage: String
        get() = "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable."

    override suspend fun generateAsync(request: ImageGenerationRequest): List<ImageArtifact> = emptyList()

    companion object {
        val Instance = NullImageGenerator()
    }
}

// =====================================================================
// Options (Options.cs)
// =====================================================================

/** OpenAI image-generation options. Mirrors C# `OpenAiImageOptions`. */
data class OpenAiImageOptions(
    val baseAddress: String = "https://api.openai.com",
    val apiKey: String? = null,
    /** Model id. Default `dall-e-3`. */
    val model: String = "dall-e-3",
)

/** Stability AI image-generation options. Mirrors C# `StabilityImageOptions`. */
data class StabilityImageOptions(
    val baseAddress: String = "https://api.stability.ai",
    val apiKey: String? = null,
    /** Model id. Default `sd3.5-large`. */
    val model: String = "sd3.5-large",
    /** Output format. Default `png`. */
    val outputFormat: String = "png",
)

// =====================================================================
// HTTP transport seam
// =====================================================================

/**
 * One image-generation HTTP response. [statusCode] is the HTTP status; on
 * success the payload is in [jsonBody] (OpenAI's JSON envelope) or [imageBytes]
 * (Stability's inline image); on failure [errorBody] carries the error text the
 * C# logs via ILogger. Only the fields relevant to the requested content type
 * are populated.
 */
data class ImageHttpResponse(
    val statusCode: Int,
    val jsonBody: String? = null,
    val imageBytes: ByteArray? = null,
    val errorBody: String? = null,
) {
    val isSuccess: Boolean get() = statusCode in 200..299

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ImageHttpResponse) return false
        return statusCode == other.statusCode &&
            jsonBody == other.jsonBody &&
            errorBody == other.errorBody &&
            (imageBytes?.contentEquals(other.imageBytes) ?: (other.imageBytes == null))
    }

    override fun hashCode(): Int {
        var r = statusCode
        r = 31 * r + (jsonBody?.hashCode() ?: 0)
        r = 31 * r + (imageBytes?.contentHashCode() ?: 0)
        r = 31 * r + (errorBody?.hashCode() ?: 0)
        return r
    }
}

/** A single multipart form field (name → value). Ordering is preserved. */
data class FormField(val name: String, val value: String)

/**
 * The seam the cloud image generators post through — the image analogue of
 * CloudFallback's [com.bhengubv.circleai.hosting.cloudfallback.ICloudHttpTransport].
 * The C# generators use a real HttpClient; the Kotlin port injects this so hosts
 * wire a real transport while tests supply a deterministic fake.
 */
interface IImageHttpTransport {
    /** POST a JSON body to [path] on [baseAddress] with [headers]. */
    suspend fun postJson(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        jsonBody: String,
    ): ImageHttpResponse

    /**
     * POST a multipart/form-data body ([fields] in order) to [path] on
     * [baseAddress], requesting [acceptMime]. Used by Stability, which returns a
     * single image per call as raw bytes.
     */
    suspend fun postMultipart(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        acceptMime: String,
        fields: List<FormField>,
    ): ImageHttpResponse
}

// =====================================================================
// OpenAiImageGenerator (OpenAiImageGenerator.cs)
// =====================================================================

private val JSON = Json { ignoreUnknownKeys = true }

/**
 * [IImageGenerator] backed by OpenAI DALL-E `/v1/images/generations`. Fail-soft
 * when the API key is missing — returns an empty artifact list so a fallback
 * chain can move on. Mirrors C# `OpenAiImageGenerator`, including the
 * `n = clamp(count, 1, 4)` safety, `size = "{size}x{size}"`,
 * `response_format = "url"`, and the `data[].url` extraction.
 */
class OpenAiImageGenerator(
    private val transport: IImageHttpTransport,
    private val options: OpenAiImageOptions,
) : IImageGenerator {

    override val generatorId: String get() = "openai-images"
    override val displayLabel: String get() = "OpenAI · ${options.model}"
    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()
    override val statusMessage: String
        get() = if (isConfigured) "Ready · ${options.model}"
        else "OpenAI API key not configured — set OpenAI:ApiKey to enable."

    override suspend fun generateAsync(request: ImageGenerationRequest): List<ImageArtifact> {
        if (!isConfigured) return emptyList()

        val body = JsonObject(
            mapOf(
                "model" to JsonPrimitive(options.model),
                "prompt" to JsonPrimitive(request.prompt),
                "n" to JsonPrimitive(request.count.coerceIn(1, 4)),
                "size" to JsonPrimitive("${request.size}x${request.size}"),
                "response_format" to JsonPrimitive("url"),
            ),
        )

        val response = transport.postJson(
            options.baseAddress,
            "/v1/images/generations",
            mapOf("Authorization" to "Bearer ${options.apiKey}"),
            JSON.encodeToString(JsonObject.serializer(), body),
        )

        if (!response.isSuccess) {
            // C# logs and returns empty.
            return emptyList()
        }

        val json = response.jsonBody ?: return emptyList()
        val artifacts = ArrayList<ImageArtifact>()
        val root = try {
            JSON.parseToJsonElement(json) as? JsonObject
        } catch (_: Exception) {
            null
        } ?: return emptyList()

        val data = root["data"] as? JsonArray
        if (data != null) {
            for (item in data) {
                val obj = item as? JsonObject ?: continue
                val url = (obj["url"] as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
                if (url != null) {
                    artifacts.add(
                        ImageArtifact(
                            generatorId = generatorId,
                            prompt = request.prompt,
                            mimeType = "image/png",
                            url = url,
                            bytes = null,
                            generatedAtUtc = Instant.now(),
                        ),
                    )
                }
            }
        }
        return artifacts
    }
}

// =====================================================================
// StabilityImageGenerator (StabilityImageGenerator.cs)
// =====================================================================

/**
 * [IImageGenerator] backed by Stability AI
 * `/v2beta/stable-image/generate/sd3`. Stability returns one image per call, so
 * we loop on the caller's behalf to honour Count. Returns images inline as
 * bytes (no remote URL). Mirrors C# `StabilityImageGenerator`, including the
 * `clamp(count, 1, 4)` loop, the prompt/output_format/model form-field ordering,
 * the optional negative_prompt field, and the per-iteration skip on non-success.
 */
class StabilityImageGenerator(
    private val transport: IImageHttpTransport,
    private val options: StabilityImageOptions,
) : IImageGenerator {

    override val generatorId: String get() = "stability"
    override val displayLabel: String get() = "Stability AI · ${options.model}"
    override val isConfigured: Boolean get() = !options.apiKey.isNullOrBlank()
    override val statusMessage: String
        get() = if (isConfigured) "Ready · ${options.model}"
        else "Stability AI API key not configured — set Stability:ApiKey to enable."

    override suspend fun generateAsync(request: ImageGenerationRequest): List<ImageArtifact> {
        if (!isConfigured) return emptyList()

        val artifacts = ArrayList<ImageArtifact>()
        val count = request.count.coerceIn(1, 4)
        for (i in 0 until count) {
            currentCoroutineContext().ensureActive()

            val fields = ArrayList<FormField>()
            fields.add(FormField("prompt", request.prompt))
            fields.add(FormField("output_format", options.outputFormat))
            fields.add(FormField("model", options.model))
            if (!request.negativePrompt.isNullOrEmpty()) {
                fields.add(FormField("negative_prompt", request.negativePrompt))
            }

            val response = transport.postMultipart(
                options.baseAddress,
                "/v2beta/stable-image/generate/sd3",
                mapOf("Authorization" to "Bearer ${options.apiKey}"),
                "image/${options.outputFormat}",
                fields,
            )

            if (!response.isSuccess) {
                // C# logs and continues to the next iteration.
                continue
            }

            val bytes = response.imageBytes ?: continue
            artifacts.add(
                ImageArtifact(
                    generatorId = generatorId,
                    prompt = request.prompt,
                    mimeType = "image/${options.outputFormat}",
                    url = null,
                    bytes = bytes,
                    generatedAtUtc = Instant.now(),
                ),
            )
        }
        return artifacts
    }
}

// =====================================================================
// ImageGeneratorFallbackChain (ImageGeneratorFallbackChain.cs)
// =====================================================================

/**
 * Composite [IImageGenerator] — tries each child in order, skipping those that
 * report [IImageGenerator.isConfigured] = false. Returns the first non-empty
 * artifact list, or empty if everyone failed. Mirrors C#
 * `ImageGeneratorFallbackChain`.
 */
class ImageGeneratorFallbackChain(
    chain: Iterable<IImageGenerator>,
) : IImageGenerator {

    private val chain: List<IImageGenerator> = chain.toList()

    override val generatorId: String get() = "fallback-chain"
    override val displayLabel: String get() = "Fallback (${chain.size})"
    override val isConfigured: Boolean get() = chain.any { it.isConfigured }
    override val statusMessage: String
        get() = if (isConfigured) {
            "Ready · " + chain.filter { it.isConfigured }.joinToString(" → ") { it.generatorId }
        } else {
            "No configured generator in chain."
        }

    override suspend fun generateAsync(request: ImageGenerationRequest): List<ImageArtifact> {
        for (g in chain) {
            if (!g.isConfigured) continue
            val result = try {
                g.generateAsync(request)
            } catch (ce: CancellationException) {
                throw ce
            } catch (_: Exception) {
                // Mirror the chat CloudFallbackChain's fault tolerance: skip a
                // throwing generator and try the next. (The C# awaits directly;
                // guarding is a strict superset that never changes the happy path.)
                continue
            }
            if (result.isNotEmpty()) return result
        }
        return emptyList()
    }
}

// =====================================================================
// Generator-id constants (ServiceCollectionExtensions.cs → GeneratorIds)
// =====================================================================

/**
 * The keyed generator identifiers the C# DI helpers register under. The DI
 * wiring itself is a .NET-hosting concern with no Kotlin analogue; the wire
 * identity is preserved here so hosts can key generators consistently. Mirrors
 * C# `VisionCloudServiceCollectionExtensions.GeneratorIds`.
 */
object VisionCloudGeneratorIds {
    const val OpenAi = "openai-images"
    const val Stability = "stability"
}

// Retained so a reader grepping for the C# Math.Clamp helpers finds the Kotlin
// equivalents used above; `coerceIn` is the idiomatic form.
@Suppress("unused")
private fun clamp(value: Int, low: Int, high: Int): Int = max(low, min(high, value))
