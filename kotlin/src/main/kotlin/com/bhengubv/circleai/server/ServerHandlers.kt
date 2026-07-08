// ServerHandlers.kt
//
// Kotlin port of the CircleAI.Inference.Server hosting + endpoint layer. C# is
// the EXACT spec. Per the port rules, the OpenAI-compatible HTTP endpoints are
// ported as in-memory handlers (no real socket server) exposed behind classes;
// each reproduces the routing logic + HTTP status codes + OpenAI JSON envelope
// faithfully. Covers:
//   • AdmissionControl (AdmissionControl.cs)
//   • ICompanionSessionResolver + InMemoryCompanionSessionResolver
//     (CompanionEndpoint.cs, InMemoryCompanionSessionResolver.cs)
//   • MnnInferenceBridgeFactory (MnnInferenceBridgeFactory.cs) — in-memory
//   • ChatCompletionsHandler / EmbeddingsHandler / CompanionTurnHandler /
//     AdminHandler — the endpoint routing (ChatCompletionsEndpoint.cs,
//     EmbeddingsEndpoint.cs, CompanionEndpoint.cs, AdminEndpoints.cs)

package com.bhengubv.circleai.server

import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.ICompanionSessionFactory
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.inference.BundleFileSpec
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.inference.LocalChatGenerator
import com.bhengubv.circleai.inference.ModelDownloadService
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Semaphore
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.max

// ── AdmissionControl ─────────────────────────────────────────────────────────

/**
 * Bounded admission gate — at most [maxConcurrentRequests] requests in flight.
 * Excess requests are rejected immediately (no queueing). Ports AdmissionControl.
 */
class AdmissionControl(
    options: InferenceServerOptions,
    private val counters: ServerCounters,
) {
    /** Maximum admitted-at-once requests. */
    val maxConcurrentRequests: Int = max(1, options.maxConcurrentRequests)

    private val gate = Semaphore(maxConcurrentRequests)

    /**
     * Attempt to acquire one slot. Returns a [Slot] the caller MUST [Slot.close]
     * (use `use { }`). Returns `null` when saturated — the endpoint responds 503.
     */
    fun tryEnter(): Slot? {
        return if (gate.tryAcquire()) {
            counters.accountAdmitted()
            Slot(gate, counters)
        } else {
            counters.accountRejected()
            null
        }
    }

    /** One admitted slot; releasing decrements the active count exactly once. */
    class Slot(private val gate: Semaphore, private val counters: ServerCounters) : AutoCloseable {
        private val disposed = AtomicBoolean(false)
        override fun close() {
            if (disposed.compareAndSet(false, true)) {
                gate.release()
                counters.accountCompleted()
            }
        }
    }
}

// ── ICompanionSessionResolver ────────────────────────────────────────────────

/**
 * Resolves an [ICompanionSession] for a given session_id + identity_id. Ports
 * ICompanionSessionResolver.
 */
interface ICompanionSessionResolver {
    suspend fun resolveAsync(sessionId: String, identityId: String): ICompanionSession?
}

/**
 * In-process resolver. Caches one [ICompanionSession] per (sessionId,
 * identityId) pair and constructs missing sessions via
 * [ICompanionSessionFactory]. Construction is single-flighted per key via a
 * per-key [Mutex]; a failed construction drops the cache slot so the next caller
 * can retry cleanly. Ports InMemoryCompanionSessionResolver.
 */
class InMemoryCompanionSessionResolver(
    private val factory: ICompanionSessionFactory,
    private val defaultInterface: InterfaceKind = InterfaceKind.Web,
) : ICompanionSessionResolver {

    private val sessions = ConcurrentHashMap<Pair<String, String>, ICompanionSession>()
    private val locks = ConcurrentHashMap<Pair<String, String>, Mutex>()

    /** Number of currently cached sessions. Diagnostics only. */
    val cachedSessionCount: Int get() = sessions.size

    override suspend fun resolveAsync(sessionId: String, identityId: String): ICompanionSession? {
        if (sessionId.isBlank() || identityId.isBlank()) return null

        val key = sessionId to identityId
        sessions[key]?.let { return it }

        // Single-flight construction per key.
        val lock = locks.computeIfAbsent(key) { Mutex() }
        lock.withLock {
            sessions[key]?.let { return it }
            val session = factory.createAsync(identityId, defaultInterface)
            sessions[key] = session
            return session
        }
    }
}

// ── MnnInferenceBridgeFactory (in-memory) ────────────────────────────────────

/**
 * One registry entry the bridge factory resolves a model from. Mirrors the
 * fields `MnnInferenceBridgeFactory` reads off `ModelEntry`.
 */
data class ServerModelEntry(
    val modelId: String,
    val version: String = "1.0.0",
    val url: String? = null,
    val checksum: String? = null,
    val repo: String? = null,
    val quantization: String? = null,
    val bundleFiles: List<BundleFileSpec>? = null,
) {
    val isBundle: Boolean get() = !bundleFiles.isNullOrEmpty()
}

/**
 * In-memory port of MnnInferenceBridgeFactory. Preserves the C# pipeline
 * structure — resolve entry → probe host → (native prep) → ensure model on disk
 * → construct generator → wrap as LocalProcessInferenceBridge — but injects the
 * generator + download service so no native runtime is required. The native
 * self-check is represented by [nativeStatus] being stamped with a
 * deterministic [NativeRuntimePaths].
 *
 * @param probe Host capability probe (for the descriptor's memory sizing).
 * @param registryLookup Resolves a [ServerModelEntry] for a model id, or null.
 * @param modelDownload Download service that materialises the model on disk.
 * @param generatorFactory Builds an [IChatGenerator] from a resolved model path
 *   (defaults to a deterministic [LocalChatGenerator]).
 * @param nativeStatus Optional holder stamped after a successful "prep".
 */
class MnnInferenceBridgeFactory(
    private val probe: ICapabilityProbe,
    private val registryLookup: (String) -> ServerModelEntry?,
    private val modelDownload: ModelDownloadService,
    private val generatorFactory: (modelPath: String, contextSize: Int) -> IChatGenerator =
        { _, _ -> LocalChatGenerator() },
    private val nativeStatus: INativeRuntimeStatus? = null,
) : IBridgeFactory {

    override suspend fun createAsync(
        modelId: String,
        backend: BackendKind,
        tier: CapabilityTier,
    ): IInferenceBridge {
        require(modelId.isNotBlank()) { "modelId required" }

        // 1. Resolve the model entry from the registry FIRST (fail fast).
        val entry = registryLookup(modelId)
            ?: throw IllegalStateException(
                "Model '$modelId' is not in the registry. Add it or pre-register it via an alternative IBridgeFactory.",
            )

        var downloadUri: String? = null
        if (!entry.isBundle) {
            if (entry.url.isNullOrBlank()) {
                throw IllegalStateException(
                    "Registry entry for '$modelId' has neither BundleFiles nor a valid Url.",
                )
            }
            downloadUri = entry.url
        } else if (entry.repo.isNullOrBlank()) {
            throw IllegalStateException(
                "Registry entry for '$modelId' has BundleFiles but no Repo path — bundle URLs cannot be built.",
            )
        }

        // 2 & 3. Probe host (and, in production, fetch + prep the MNN runtime).
        val profile = probe.probeAsync()

        // 4. Stamp native status (the in-memory analog of the native self-check).
        nativeStatus?.update(
            NativeRuntimePaths(
                rid = "${profile.os.name.lowercase()}-${profile.arch.name.lowercase()}",
                expectedNativeDir = "(in-memory)",
                mnnBridgePath = "(in-memory)",
                mnnBridgeLoaded = true,
                mnnCoreFetchedPath = "(in-memory)",
                mnnCoreFlattenedPath = "(in-memory)",
                mnnCorePreloaded = true,
                flattenError = null,
                preloadError = null,
            ),
        )

        // 5. Ensure the model is on disk.
        val modelPath: String = if (entry.isBundle) {
            val modelDir = modelDownload.ensureBundleAsync(modelId, entry.repo!!, entry.bundleFiles!!, null)
            modelDownload.writeInstalledManifestAsync(modelDir, modelId, entry.version, entry.repo, entry.bundleFiles)
            "$modelDir/config.json"
        } else {
            modelDownload.ensureModelAsync(modelId, downloadUri!!, entry.checksum, null)
        }

        // 6. Construct the chat generator (4096 ctx — Qwen 3 family default).
        val generator = generatorFactory(modelPath, 4096)

        // 7. Build a descriptor + wrap as IInferenceBridge.
        val descriptor = ModelDescriptor(
            modelId = modelId,
            version = entry.version,
            format = ModelFormat.Gguf,
            contextWindowTokens = 4096,
            vocabSize = 151_936,
            parameterCount = 0L,
            quantisationLabel = entry.quantization,
            approximateMemoryBytes = approxMemoryFromTier(tier),
        )
        return LocalProcessInferenceBridge(generator, descriptor, probe)
    }

    private fun approxMemoryFromTier(tier: CapabilityTier): Long = when (tier) {
        CapabilityTier.Tier0_Tiny -> 1L * 1024 * 1024 * 1024
        CapabilityTier.Tier1_Small -> 2L * 1024 * 1024 * 1024
        CapabilityTier.Tier2_Medium -> 6L * 1024 * 1024 * 1024
        CapabilityTier.Tier3_Large -> 12L * 1024 * 1024 * 1024
        CapabilityTier.Tier4_Frontier -> 24L * 1024 * 1024 * 1024
    }
}

// ── Handler result ───────────────────────────────────────────────────────────

/**
 * The outcome of an in-memory endpoint invocation: an HTTP status code plus a
 * response body (either a typed success payload or an [ErrorResponse]). This is
 * the port of ASP.NET's `IResult` — it lets tests assert both status and body
 * without a socket server.
 */
data class HandlerResult<out T>(
    val statusCode: Int,
    val body: T?,
    val error: ErrorResponse? = null,
    /** SSE frames written when the request streamed; empty otherwise. */
    val streamFrames: List<Any> = emptyList(),
) {
    val isSuccess: Boolean get() = statusCode in 200..299 && error == null

    companion object {
        fun <T> ok(body: T): HandlerResult<T> = HandlerResult(200, body)
        fun <T> error(status: Int, err: ErrorResponse): HandlerResult<T> = HandlerResult(status, null, err)
    }
}

// ── ChatCompletionsHandler (POST /v1/chat/completions) ───────────────────────

/**
 * In-memory port of ChatCompletionsEndpoint. Non-streaming returns a
 * [ChatCompletionResponse]; streaming collects OpenAI-shaped SSE frames into
 * [HandlerResult.streamFrames].
 */
class ChatCompletionsHandler(
    private val registry: IInferenceServerModelRegistry,
    private val admission: AdmissionControl,
    private val counters: ServerCounters,
) {
    suspend fun handle(body: ChatCompletionRequest): HandlerResult<ChatCompletionResponse> {
        if (body.model.isBlank()) {
            return HandlerResult.error(400, ErrorResponse.of("Missing or empty 'model' field.", "invalid_request_error", "missing_model"))
        }
        if (body.messages.isEmpty()) {
            return HandlerResult.error(400, ErrorResponse.of("Missing 'messages' array.", "invalid_request_error", "missing_messages"))
        }

        val bridge = registry.resolve(body.model)
            ?: return HandlerResult.error(404, ErrorResponse.of("Model '${body.model}' is not loaded.", "invalid_request_error", "model_not_found"))

        val slot = admission.tryEnter()
            ?: return HandlerResult.error(
                503,
                ErrorResponse.of(
                    "Server is at concurrency cap (${admission.maxConcurrentRequests}). Retry after a brief delay.",
                    "server_busy", "concurrency_cap",
                ),
            )

        slot.use {
            val request = buildInferenceRequest(body)
            return if (body.stream) streamResponse(bridge, request, body) else nonStreamResponse(bridge, request, body)
        }
    }

    private suspend fun nonStreamResponse(
        bridge: IInferenceBridge,
        request: InferenceRequest,
        body: ChatCompletionRequest,
    ): HandlerResult<ChatCompletionResponse> {
        val resp = try {
            bridge.completeAsync(request)
        } catch (e: Exception) {
            counters.accountFailed()
            return HandlerResult.error(500, ErrorResponse.of(e.message ?: "bridge failure", "internal_error", "bridge_failure"))
        }

        if (resp.status == InferenceStatus.Failed) {
            counters.accountFailed()
            return HandlerResult.error(500, ErrorResponse.of(resp.failureMessage ?: "Inference failed.", "internal_error", "inference_failed"))
        }

        val response = ChatCompletionResponse(
            id = "chatcmpl-${UUID.randomUUID().toString().replace("-", "")}",
            created = System.currentTimeMillis() / 1000,
            model = body.model,
            choices = listOf(
                ChatCompletionChoice(
                    index = 0,
                    message = ChatCompletionMessage(
                        role = "assistant",
                        content = resp.outputText,
                        reasoningContent = resp.reasoningText,
                    ),
                    finishReason = mapFinish(resp.status),
                ),
            ),
            usage = UsageInfo(
                promptTokens = resp.promptTokenCount,
                completionTokens = resp.outputTokenCount,
                totalTokens = resp.promptTokenCount + resp.outputTokenCount,
            ),
        )
        return HandlerResult.ok(response)
    }

    private suspend fun streamResponse(
        bridge: IInferenceBridge,
        request: InferenceRequest,
        body: ChatCompletionRequest,
    ): HandlerResult<ChatCompletionResponse> {
        val id = "chatcmpl-${UUID.randomUUID().toString().replace("-", "")}"
        val created = System.currentTimeMillis() / 1000
        val frames = ArrayList<Any>()

        // First frame: role announcement.
        frames.add(
            ChatCompletionStreamChunk(
                id = id, created = created, model = body.model,
                choices = listOf(ChatCompletionStreamChoice(0, ChatCompletionDelta(role = "assistant"))),
            ),
        )

        try {
            bridge.streamFragmentsAsync(request).collect { f ->
                if (f.text.isEmpty()) return@collect
                val delta = if (f.kind == InferenceFragmentKind.Reasoning) {
                    ChatCompletionDelta(reasoningContent = f.text)
                } else {
                    ChatCompletionDelta(content = f.text)
                }
                frames.add(
                    ChatCompletionStreamChunk(
                        id = id, created = created, model = body.model,
                        choices = listOf(ChatCompletionStreamChoice(0, delta)),
                    ),
                )
            }
        } catch (e: Exception) {
            counters.accountFailed()
            frames.add(
                ChatCompletionStreamChunk(
                    id = id, created = created, model = body.model,
                    choices = listOf(
                        ChatCompletionStreamChoice(0, ChatCompletionDelta(content = "[error: ${e.message}]"), "error"),
                    ),
                ),
            )
        }

        // Final frame: stop reason + [DONE].
        frames.add(
            ChatCompletionStreamChunk(
                id = id, created = created, model = body.model,
                choices = listOf(ChatCompletionStreamChoice(0, ChatCompletionDelta(), "stop")),
            ),
        )
        frames.add(SSE_DONE)
        return HandlerResult(200, null, null, frames)
    }

    private fun buildInferenceRequest(body: ChatCompletionRequest): InferenceRequest {
        val prompt = body.messages.joinToString("\n") { "<|${it.role}|>\n${it.content}\n<|end|>" }
        val metadata = HashMap<String, String>()
        if (!body.user.isNullOrEmpty()) metadata["user"] = body.user
        return InferenceRequest(
            id = UUID.randomUUID(),
            modelId = body.model,
            prompt = prompt,
            maxOutputTokens = body.maxTokens ?: 512,
            temperature = body.temperature ?: 0.7f,
            topP = body.topP ?: 0.9f,
            stopSequences = body.stop ?: emptyList(),
            metadata = metadata,
            requestedAt = java.time.Instant.now(),
        )
    }

    private fun mapFinish(status: InferenceStatus): String = when (status) {
        InferenceStatus.Completed -> "stop"
        InferenceStatus.StoppedByToken -> "stop"
        InferenceStatus.StoppedByLength -> "length"
        InferenceStatus.Cancelled -> "cancelled"
        else -> "error"
    }

    companion object {
        /** Terminal SSE sentinel matching OpenAI's `data: [DONE]`. */
        const val SSE_DONE = "[DONE]"
    }
}

// ── EmbeddingsHandler (POST /v1/embeddings) ──────────────────────────────────

/** In-memory port of EmbeddingsEndpoint. */
class EmbeddingsHandler(
    private val registry: IInferenceServerModelRegistry,
    private val admission: AdmissionControl,
    private val counters: ServerCounters,
) {
    suspend fun handle(body: EmbeddingsRequest): HandlerResult<EmbeddingsResponse> {
        if (body.model.isBlank()) {
            return HandlerResult.error(400, ErrorResponse.of("Missing or empty 'model' field.", "invalid_request_error", "missing_model"))
        }

        val embedder = registry.resolveEmbedder(body.model)
            ?: return HandlerResult.error(404, ErrorResponse.of("Embedding model '${body.model}' is not loaded.", "invalid_request_error", "model_not_found"))

        val inputs = body.normaliseInput().getOrElse { ex ->
            return HandlerResult.error(400, ErrorResponse.of(ex.message ?: "invalid input", "invalid_request_error", "invalid_input"))
        }

        val slot = admission.tryEnter()
            ?: return HandlerResult.error(503, ErrorResponse.of("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap"))

        slot.use {
            val data = ArrayList<EmbeddingDatum>(inputs.size)
            var totalChars = 0
            try {
                for (i in inputs.indices) {
                    val vec = embedder.generateAsync(inputs[i])
                    data.add(EmbeddingDatum(index = i, embedding = vec.toList()))
                    totalChars += inputs[i].length
                }
            } catch (e: Exception) {
                counters.accountFailed()
                return HandlerResult.error(500, ErrorResponse.of(e.message ?: "embedding failure", "internal_error", "embedding_failure"))
            }

            val estimatedPromptTokens = max(1, totalChars / 4)
            return HandlerResult.ok(
                EmbeddingsResponse(
                    data = data,
                    model = body.model,
                    usage = UsageInfo(
                        promptTokens = estimatedPromptTokens,
                        completionTokens = 0,
                        totalTokens = estimatedPromptTokens,
                    ),
                ),
            )
        }
    }
}

// ── CompanionTurnHandler (POST /v1/companion/turn) ───────────────────────────

/** In-memory port of CompanionEndpoint. */
class CompanionTurnHandler(
    private val resolver: ICompanionSessionResolver,
    private val admission: AdmissionControl,
    private val counters: ServerCounters,
) {
    suspend fun handle(body: CompanionTurnRequest): HandlerResult<CompanionTurnResponse> {
        if (body.sessionId.isBlank() || body.identityId.isBlank() || body.message.isBlank()) {
            return HandlerResult.error(400, ErrorResponse.of("session_id, identity_id, and message are all required.", "invalid_request_error", "missing_field"))
        }

        val session = resolver.resolveAsync(body.sessionId, body.identityId)
            ?: return HandlerResult.error(404, ErrorResponse.of("No Companion session for session_id='${body.sessionId}', identity_id='${body.identityId}'.", "invalid_request_error", "session_not_found"))

        val slot = admission.tryEnter()
            ?: return HandlerResult.error(503, ErrorResponse.of("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap"))

        slot.use {
            if (body.stream) {
                val frames = ArrayList<Any>()
                try {
                    session.streamAsync(body.message).collect { chunk ->
                        if (chunk.isEmpty()) return@collect
                        frames.add(mapOf("session_id" to body.sessionId, "delta" to chunk))
                    }
                } catch (e: Exception) {
                    counters.accountFailed()
                    frames.add(mapOf("session_id" to body.sessionId, "error" to (e.message ?: "error")))
                }
                return HandlerResult(200, null, null, frames)
            }

            return try {
                val reply = if (body.agentic) session.agentAsync(body.message) else session.sendAsync(body.message)
                HandlerResult.ok(
                    CompanionTurnResponse(
                        sessionId = body.sessionId,
                        reply = reply,
                        agentic = body.agentic,
                        turnIndex = session.history.size,
                    ),
                )
            } catch (e: Exception) {
                counters.accountFailed()
                HandlerResult.error(500, ErrorResponse.of(e.message ?: "companion failure", "internal_error", "companion_failure"))
            }
        }
    }
}

// ── AdminHandler (/v1/admin/*) ───────────────────────────────────────────────

/** Response body for the lifecycle listing. */
data class AdminLifecycleResponse(
    val totalAllocatedVramBytes: Long,
    val totalAllocatedRamBytes: Long,
    val loaded: List<ModelLoadState>,
)

/** In-memory port of AdminEndpoints (load / unload / lifecycle listing). */
class AdminHandler(
    private val manager: IModelLifecycleManager,
    private val factory: IBridgeFactory,
) {
    /** GET /v1/admin/lifecycle */
    fun lifecycle(): HandlerResult<AdminLifecycleResponse> =
        HandlerResult.ok(
            AdminLifecycleResponse(
                totalAllocatedVramBytes = manager.totalAllocatedVramBytes,
                totalAllocatedRamBytes = manager.totalAllocatedRamBytes,
                loaded = manager.list(),
            ),
        )

    /** POST /v1/admin/models/load */
    suspend fun load(body: AdminLoadRequest): HandlerResult<LoadResult> {
        if (body.modelId.isBlank()) {
            return HandlerResult.error(400, ErrorResponse.of("Missing 'modelId'.", "invalid_request_error", "missing_model"))
        }
        val backend = BackendKind.entries.firstOrNull { it.name.equals(body.backend, ignoreCase = true) }
            ?: return HandlerResult.error(400, ErrorResponse.of("Unknown backend '${body.backend}'. Valid: Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML.", "invalid_request_error", "invalid_backend"))
        val tier = CapabilityTier.entries.firstOrNull { it.name.equals(body.tier, ignoreCase = true) }
            ?: return HandlerResult.error(400, ErrorResponse.of("Unknown tier '${body.tier}'. Valid: Tier0_Tiny..Tier4_Frontier.", "invalid_request_error", "invalid_tier"))

        val descriptor = ModelLoadDescriptor(
            modelId = body.modelId,
            backend = backend,
            requestedTier = tier,
            vramRequiredBytes = max(0, body.vramRequiredBytes),
            ramRequiredBytes = max(0, body.ramRequiredBytes),
            bridgeFactory = { factory.createAsync(body.modelId, backend, tier) },
        )

        val result = manager.loadAsync(descriptor)
        return when (result.outcome) {
            LoadOutcome.Loaded, LoadOutcome.AlreadyLoaded -> HandlerResult.ok(result)
            LoadOutcome.InsufficientVram, LoadOutcome.InsufficientRam ->
                HandlerResult.error(507, ErrorResponse.of(result.rationale, "resource_exhausted", result.outcome.name))
            LoadOutcome.FactoryFailed ->
                HandlerResult.error(500, ErrorResponse.of(result.rationale, "internal_error", "factory_failed"))
        }
    }

    /** DELETE /v1/admin/models/{modelId} */
    suspend fun unload(modelId: String): HandlerResult<Map<String, String>> {
        return when (manager.unloadAsync(modelId)) {
            UnloadOutcome.Unloaded -> HandlerResult.ok(mapOf("outcome" to "Unloaded", "modelId" to modelId))
            UnloadOutcome.NotLoaded -> HandlerResult.error(404, ErrorResponse.of("Model '$modelId' is not loaded.", "invalid_request_error", "not_loaded"))
        }
    }
}
