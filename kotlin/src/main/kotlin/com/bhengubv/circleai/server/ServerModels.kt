// ServerModels.kt
//
// Kotlin port of the CircleAI.Inference.Server model layer. C# is the EXACT
// spec. Covers:
//   • OpenAI-compatible DTOs (ChatCompletion.cs, Embeddings.cs, ErrorResponse.cs)
//   • Companion DTOs (CompanionDtos.cs)
//   • IInferenceServerModelRegistry + InferenceServerModelRegistry (ModelRegistry.cs)
//   • ServerCounters (ServerCounters.cs)
//   • INativeRuntimeStatus + NativeRuntimeStatus (INativeRuntimeStatus.cs)
//
// JSON field names are pinned with @SerialName so the wire shape matches the
// public OpenAI Chat Completions / Embeddings API byte-for-byte.

package com.bhengubv.circleai.server

import com.bhengubv.circleai.embeddings.ITextEmbedder
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

// ── OpenAI: chat completions ─────────────────────────────────────────────────

/** OpenAI-shaped chat-completion request body. */
@Serializable
data class ChatCompletionRequest(
    @SerialName("model") val model: String = "",
    @SerialName("messages") val messages: List<ChatCompletionMessage> = emptyList(),
    @SerialName("temperature") val temperature: Float? = null,
    @SerialName("top_p") val topP: Float? = null,
    @SerialName("max_tokens") val maxTokens: Int? = null,
    @SerialName("stream") val stream: Boolean = false,
    @SerialName("stop") val stop: List<String>? = null,
    @SerialName("user") val user: String? = null,
)

/** One message in the chat completion conversation. */
@Serializable
data class ChatCompletionMessage(
    @SerialName("role") val role: String = "user",
    @SerialName("content") val content: String = "",
    @SerialName("name") val name: String? = null,
    @SerialName("reasoning_content") val reasoningContent: String? = null,
)

/** OpenAI-shaped successful chat completion response. */
@Serializable
data class ChatCompletionResponse(
    @SerialName("id") val id: String = "",
    @SerialName("object") val objectType: String = "chat.completion",
    @SerialName("created") val created: Long = 0,
    @SerialName("model") val model: String = "",
    @SerialName("choices") val choices: List<ChatCompletionChoice> = emptyList(),
    @SerialName("usage") val usage: UsageInfo = UsageInfo(),
)

/** One choice in a non-streaming chat completion response. */
@Serializable
data class ChatCompletionChoice(
    @SerialName("index") val index: Int = 0,
    @SerialName("message") val message: ChatCompletionMessage = ChatCompletionMessage(),
    @SerialName("finish_reason") val finishReason: String = "stop",
)

/** Token-usage block. */
@Serializable
data class UsageInfo(
    @SerialName("prompt_tokens") val promptTokens: Int = 0,
    @SerialName("completion_tokens") val completionTokens: Int = 0,
    @SerialName("total_tokens") val totalTokens: Int = 0,
)

/** One SSE delta frame in a streamed chat completion. */
@Serializable
data class ChatCompletionStreamChunk(
    @SerialName("id") val id: String = "",
    @SerialName("object") val objectType: String = "chat.completion.chunk",
    @SerialName("created") val created: Long = 0,
    @SerialName("model") val model: String = "",
    @SerialName("choices") val choices: List<ChatCompletionStreamChoice> = emptyList(),
)

/** One delta in a streamed chat completion chunk. */
@Serializable
data class ChatCompletionStreamChoice(
    @SerialName("index") val index: Int = 0,
    @SerialName("delta") val delta: ChatCompletionDelta = ChatCompletionDelta(),
    @SerialName("finish_reason") val finishReason: String? = null,
)

/** Delta payload — only non-null fields are emitted between SSE frames. */
@Serializable
data class ChatCompletionDelta(
    @SerialName("role") val role: String? = null,
    @SerialName("content") val content: String? = null,
    @SerialName("reasoning_content") val reasoningContent: String? = null,
)

// ── OpenAI: embeddings ───────────────────────────────────────────────────────

/**
 * OpenAI-shaped embeddings request. C# models `input` as a raw JsonElement to
 * accept either a single string or an array; Kotlin exposes both explicit
 * fields plus a normalisation helper so handlers get a `List<String>`.
 */
data class EmbeddingsRequest(
    val model: String = "",
    /** Either one string ([single]) or a list ([many]) — exactly one is set. */
    val single: String? = null,
    val many: List<String>? = null,
    val user: String? = null,
) {
    /**
     * Normalise `input` into a list of strings (OpenAI accepts both a single
     * string and an array). Returns a failure message when neither is set or
     * the array is empty (mirrors the C# TryNormaliseInput validation).
     */
    fun normaliseInput(): Result<List<String>> {
        if (single != null) return Result.success(listOf(single))
        if (many != null) {
            if (many.isEmpty()) {
                return Result.failure(IllegalArgumentException("'input' array must not be empty."))
            }
            return Result.success(many)
        }
        return Result.failure(IllegalArgumentException("'input' must be a string or array of strings."))
    }
}

/** OpenAI-shaped embeddings response. */
@Serializable
data class EmbeddingsResponse(
    @SerialName("object") val objectType: String = "list",
    @SerialName("data") val data: List<EmbeddingDatum> = emptyList(),
    @SerialName("model") val model: String = "",
    @SerialName("usage") val usage: UsageInfo = UsageInfo(),
)

/** One embedding row in the response. */
@Serializable
data class EmbeddingDatum(
    @SerialName("object") val objectType: String = "embedding",
    @SerialName("index") val index: Int = 0,
    @SerialName("embedding") val embedding: List<Float> = emptyList(),
)

// ── OpenAI: error envelope ───────────────────────────────────────────────────

/** Inner error body. */
@Serializable
data class ErrorBody(
    @SerialName("message") val message: String = "",
    @SerialName("type") val type: String = "invalid_request_error",
    @SerialName("param") val param: String? = null,
    @SerialName("code") val code: String? = null,
)

/** OpenAI-shaped error envelope: `{"error": {...}}`. */
@Serializable
data class ErrorResponse(
    @SerialName("error") val error: ErrorBody = ErrorBody(),
) {
    companion object {
        fun of(message: String, type: String, code: String? = null): ErrorResponse =
            ErrorResponse(ErrorBody(message = message, type = type, code = code))
    }
}

// ── Companion DTOs ───────────────────────────────────────────────────────────

/** POST /v1/companion/turn request body. */
@Serializable
data class CompanionTurnRequest(
    @SerialName("session_id") val sessionId: String = "",
    @SerialName("identity_id") val identityId: String = "",
    @SerialName("message") val message: String = "",
    @SerialName("stream") val stream: Boolean = false,
    @SerialName("agentic") val agentic: Boolean = false,
)

/** POST /v1/companion/turn response body. */
@Serializable
data class CompanionTurnResponse(
    @SerialName("session_id") val sessionId: String = "",
    @SerialName("reply") val reply: String = "",
    @SerialName("agentic") val agentic: Boolean = false,
    @SerialName("turn_index") val turnIndex: Int = 0,
)

// ── IInferenceServerModelRegistry ────────────────────────────────────────────

/**
 * In-process registry of bridge instances keyed by logical model ID (the value
 * clients pass in the `model` field of an OpenAI request). Ports
 * IInferenceServerModelRegistry.
 */
interface IInferenceServerModelRegistry {
    /** Register a bridge under [modelId]. */
    fun register(modelId: String, bridge: IInferenceBridge)

    /** Register an embedder under [modelId]. */
    fun registerEmbedder(modelId: String, embedder: ITextEmbedder)

    /** Remove the bridge registered under [modelId]. Returns `true` when found. */
    fun deregister(modelId: String): Boolean

    /** Look up a bridge. Returns `null` when the model is not registered. */
    fun resolve(modelId: String): IInferenceBridge?

    /** Look up an embedder. */
    fun resolveEmbedder(modelId: String): ITextEmbedder?

    /** List every model ID currently served (chat + embedding). */
    fun allModelIds(): List<String>

    /** List chat-capable model IDs only. */
    fun chatModelIds(): List<String>
}

/** Default thread-safe implementation (ports InferenceServerModelRegistry). */
class InferenceServerModelRegistry : IInferenceServerModelRegistry {
    private val chat = ConcurrentHashMap<String, IInferenceBridge>()
    private val embed = ConcurrentHashMap<String, ITextEmbedder>()

    override fun register(modelId: String, bridge: IInferenceBridge) {
        require(modelId.isNotBlank()) { "modelId required" }
        chat[modelId] = bridge
    }

    override fun registerEmbedder(modelId: String, embedder: ITextEmbedder) {
        require(modelId.isNotBlank()) { "modelId required" }
        embed[modelId] = embedder
    }

    override fun deregister(modelId: String): Boolean = chat.remove(modelId) != null

    override fun resolve(modelId: String): IInferenceBridge? = chat[modelId]

    override fun resolveEmbedder(modelId: String): ITextEmbedder? = embed[modelId]

    override fun allModelIds(): List<String> =
        (chat.keys + embed.keys).distinct()

    override fun chatModelIds(): List<String> = chat.keys.toList()
}

// ── ServerCounters ───────────────────────────────────────────────────────────

/** Thread-safe counters for diagnostics rendering (ports ServerCounters). */
class ServerCounters {
    private val total = AtomicLong(0)
    private val rejected = AtomicLong(0)
    private val failed = AtomicLong(0)
    private val active = AtomicInteger(0)

    /** UTC time the server process started. */
    val startedAt: Instant = Instant.now()

    /** Total requests accepted (including those that subsequently failed). */
    val totalRequests: Long get() = total.get()

    /** Requests rejected at admission (e.g. concurrency cap, auth fail). */
    val rejectedRequests: Long get() = rejected.get()

    /** Requests that admitted but failed downstream (timeout, model error). */
    val failedRequests: Long get() = failed.get()

    /** Requests currently in flight. */
    val activeRequests: Int get() = active.get()

    /** Mark a request as accepted (admission passed). */
    fun accountAdmitted() {
        total.incrementAndGet()
        active.incrementAndGet()
    }

    /** Mark a request as completed (admission was previously counted). */
    fun accountCompleted() {
        active.decrementAndGet()
    }

    /** Mark a request as rejected at admission (not counted in total). */
    fun accountRejected() {
        rejected.incrementAndGet()
    }

    /** Mark a request as failed downstream. */
    fun accountFailed() {
        failed.incrementAndGet()
    }
}

// ── INativeRuntimeStatus ─────────────────────────────────────────────────────

/**
 * Singleton holder of the last-known [NativeRuntimePaths]. Written by the bridge
 * factory after every successful native prep, read by the diagnostics endpoint.
 * Ports INativeRuntimeStatus.
 */
interface INativeRuntimeStatus {
    /** Most recent prep result, or `null` before the first model load. */
    val latest: NativeRuntimePaths?

    /** Record the result of a successful prep run. */
    fun update(paths: NativeRuntimePaths)
}

/** Thread-safe default (ports NativeRuntimeStatus). */
class NativeRuntimeStatus : INativeRuntimeStatus {
    @Volatile
    private var _latest: NativeRuntimePaths? = null

    override val latest: NativeRuntimePaths?
        get() = _latest

    override fun update(paths: NativeRuntimePaths) {
        _latest = paths
    }
}
