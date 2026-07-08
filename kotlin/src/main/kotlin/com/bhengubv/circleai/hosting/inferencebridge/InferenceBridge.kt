// InferenceBridge.kt
//
// Kotlin port of CircleAI.Hosting.InferenceBridge — the C# reference is the
// EXACT spec (IInferenceBridge.cs, LocalProcessInferenceBridge.cs,
// InferenceRequest.cs, InferenceResponse.cs, ModelDescriptor.cs,
// DeviceCapabilities.cs, MockInferenceBridge.cs).
//
// Cross-OS contract for an inference daemon: one model loaded once per device,
// shared by every app via an OS-specific IPC channel. This package ships only
// the contract + the in-process reference impl (LocalProcessInferenceBridge)
// and the deterministic MockInferenceBridge. The C# LocalProcessInferenceBridge
// derives from CircleAIComponentBase and delegates device probing to
// ICapabilityProbe; the portable Kotlin core has neither, so device-capability
// reporting is injected behind [IDeviceCapabilityProbe] with a deterministic
// default. All wire types + status classification are byte-identical.

package com.bhengubv.circleai.hosting.inferencebridge

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.UUID
import kotlin.math.max

// =====================================================================
// ModelDescriptor (ModelDescriptor.cs)
// =====================================================================

/** On-disk encoding format of a model weight artefact. Mirrors C# `ModelFormat`. */
enum class ModelFormat {
    /** llama.cpp GGUF (general GGML universal format). */
    Gguf,
    /** ONNX Runtime model file. */
    Onnx,
    /** Apple Core ML model package. */
    CoreMl,
    /** TensorFlow Lite flatbuffer. */
    Tflite,
    /** Format not recognised or not yet classified. */
    Unknown,
}

/**
 * Canonical descriptor for a single loaded model. The inference bridge publishes
 * one of these per loaded model. Mirrors C# `ModelDescriptor`.
 */
data class ModelDescriptor(
    val modelId: String,
    val version: String,
    val format: ModelFormat,
    val contextWindowTokens: Int,
    val vocabSize: Int,
    val parameterCount: Long,
    val quantisationLabel: String?,
    val approximateMemoryBytes: Long,
)

// =====================================================================
// InferenceStatus + InferenceResponse (InferenceResponse.cs)
// =====================================================================

/** Terminal state of a single inference call. Mirrors C# `InferenceStatus`. */
enum class InferenceStatus {
    /** The model finished generation cleanly (end-of-turn token). */
    Completed,
    /** Generation halted because a StopSequence matched. */
    StoppedByToken,
    /** Generation halted because MaxOutputTokens was reached. */
    StoppedByLength,
    /** The bridge or model failed; see [InferenceResponse.failureMessage]. */
    Failed,
    /** The caller cancelled before generation could finish. */
    Cancelled,
}

/** Result of a single completion call. Mirrors C# `InferenceResponse`. */
data class InferenceResponse(
    val requestId: UUID,
    val modelId: String,
    val outputText: String,
    val outputTokenCount: Int,
    val promptTokenCount: Int,
    val status: InferenceStatus,
    val inferenceMillis: Double,
    val failureMessage: String?,
    val completedAt: Instant,
    val reasoningText: String? = null,
)

// =====================================================================
// InferenceRequest (InferenceRequest.cs)
// =====================================================================

/** One completion request submitted to an [IInferenceBridge]. Mirrors C# `InferenceRequest`. */
data class InferenceRequest(
    val id: UUID,
    val modelId: String,
    val prompt: String,
    val maxOutputTokens: Int,
    val temperature: Float,
    val topP: Float,
    val stopSequences: List<String>,
    val metadata: Map<String, String>,
    val requestedAt: Instant,
) {
    companion object {
        /**
         * Convenience factory that stamps a fresh [id] and [requestedAt] and uses
         * sensible defaults for the remaining knobs. Mirrors C# `InferenceRequest.Create`.
         */
        fun create(
            modelId: String,
            prompt: String,
            maxOutputTokens: Int = 256,
            temperature: Float = 0.7f,
            topP: Float = 0.95f,
        ): InferenceRequest {
            require(modelId.isNotEmpty()) { "modelId is required" }
            return InferenceRequest(
                id = UUID.randomUUID(),
                modelId = modelId,
                prompt = prompt,
                maxOutputTokens = maxOutputTokens,
                temperature = temperature,
                topP = topP,
                stopSequences = emptyList(),
                metadata = emptyMap(),
                requestedAt = Instant.now(),
            )
        }
    }
}

// =====================================================================
// DeviceCapabilities (DeviceCapabilities.cs)
// =====================================================================

/** Static-ish capabilities report from the device hosting the bridge. Mirrors C# `DeviceCapabilities`. */
data class DeviceCapabilities(
    val osName: String,
    val osVersion: String,
    val physicalMemoryBytes: Long,
    val cpuCoreCount: Int,
    val hasGpu: Boolean,
    val gpuName: String?,
    val gpuMemoryBytes: Long?,
    val hasNpu: Boolean,
    val npuName: String?,
    val hasTransportLayerEncryption: Boolean,
)

/**
 * Device-capability probe seam. The C# LocalProcessInferenceBridge delegates to
 * `CircleAI.Runtime.ICapabilityProbe`; the portable Kotlin core injects this
 * instead so hosts can supply real values while tests use a deterministic fake.
 */
interface IDeviceCapabilityProbe {
    suspend fun probeAsync(): DeviceCapabilities
}

/**
 * Deterministic default probe. Reports the JVM's view of the host (core count +
 * max heap) with no GPU/NPU. Keeps the reference bridge fully working with no
 * external native dependency; real hosts inject a platform probe.
 */
class DefaultDeviceCapabilityProbe : IDeviceCapabilityProbe {
    override suspend fun probeAsync(): DeviceCapabilities {
        val rt = Runtime.getRuntime()
        return DeviceCapabilities(
            osName = System.getProperty("os.name") ?: "Unknown",
            osVersion = System.getProperty("os.version") ?: "0",
            physicalMemoryBytes = rt.maxMemory(),
            cpuCoreCount = rt.availableProcessors(),
            hasGpu = false,
            gpuName = null,
            gpuMemoryBytes = null,
            hasNpu = false,
            npuName = null,
            hasTransportLayerEncryption = true,
        )
    }
}

// =====================================================================
// IInferenceBridge (IInferenceBridge.cs)
// =====================================================================

/** Kind of fragment a streaming bridge emits. Mirrors C# `InferenceFragmentKind`. */
enum class InferenceFragmentKind {
    /** Part of the user-facing answer (goes into OpenAI `content`). */
    Content,
    /** Part of the model's reasoning trace (goes into OpenAI `reasoning_content`). */
    Reasoning,
}

/** A single fragment emitted by [IInferenceBridge.streamFragmentsAsync]. Mirrors C# `InferenceFragment`. */
data class InferenceFragment(val kind: InferenceFragmentKind, val text: String)

/**
 * Cross-OS contract for an inference daemon. Mirrors C# `IInferenceBridge`.
 * Cancellation is via coroutine cancellation (no explicit CancellationToken).
 */
interface IInferenceBridge {
    /** Returns a descriptor for every model currently loaded by the bridge. */
    suspend fun listLoadedModelsAsync(): List<ModelDescriptor>

    /** True when a model with [modelId] is currently loaded and ready. */
    suspend fun isModelLoadedAsync(modelId: String): Boolean

    /** Runs a single completion and returns the full response once generation terminates. */
    suspend fun completeAsync(request: InferenceRequest): InferenceResponse

    /** Streams tokens (content only) as the model decodes them. */
    fun streamCompletionAsync(request: InferenceRequest): Flow<String>

    /**
     * Streams tokens tagged with their kind (content vs reasoning). Default
     * implementation wraps [streamCompletionAsync] and tags every chunk as
     * [InferenceFragmentKind.Content]. Mirrors the C# default method.
     */
    fun streamFragmentsAsync(request: InferenceRequest): Flow<InferenceFragment> = flow {
        streamCompletionAsync(request).collect { chunk ->
            emit(InferenceFragment(InferenceFragmentKind.Content, chunk))
        }
    }

    /** Returns the bridge's view of the hardware it is running on. */
    suspend fun getDeviceCapabilitiesAsync(): DeviceCapabilities
}

// =====================================================================
// LocalProcessInferenceBridge (LocalProcessInferenceBridge.cs)
// =====================================================================

/**
 * In-process [IInferenceBridge] implementation. Wraps any [IChatGenerator] and
 * exposes it through the bridge contract. Transport-layer encryption is reported
 * as `true` because there is no cross-process channel. Mirrors C#
 * `LocalProcessInferenceBridge` (the CircleAIComponentBase diagnostics/audit
 * scaffolding is dropped — behaviour, status classification, and token
 * estimation are preserved exactly).
 */
class LocalProcessInferenceBridge(
    private val chatGenerator: IChatGenerator,
    private val descriptor: ModelDescriptor,
    private val capabilityProbe: IDeviceCapabilityProbe = DefaultDeviceCapabilityProbe(),
) : IInferenceBridge {

    override suspend fun listLoadedModelsAsync(): List<ModelDescriptor> = listOf(descriptor)

    override suspend fun isModelLoadedAsync(modelId: String): Boolean {
        require(modelId.isNotEmpty()) { "modelId is required" }
        return descriptor.modelId == modelId
    }

    override suspend fun completeAsync(request: InferenceRequest): InferenceResponse {
        if (descriptor.modelId != request.modelId) {
            return InferenceResponse(
                requestId = request.id,
                modelId = request.modelId,
                outputText = "",
                outputTokenCount = 0,
                promptTokenCount = 0,
                status = InferenceStatus.Failed,
                inferenceMillis = 0.0,
                failureMessage = "Model '${request.modelId}' is not loaded by this bridge (have '${descriptor.modelId}').",
                completedAt = Instant.now(),
            )
        }

        val messages = listOf(ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = request.prompt))
        val options = GenerationOptions(
            maxTokens = request.maxOutputTokens,
            temperature = request.temperature,
            topP = request.topP,
            stopSequences = request.stopSequences,
        )

        val started = System.nanoTime()
        val output: String
        var reasoning: String? = null
        val status: InferenceStatus
        var failureMessage: String? = null

        try {
            val response = chatGenerator.generateResponseAsync(messages, options)
            output = response.text
            reasoning = response.reasoningContent
            status = determineStatus(output, request)
        } catch (ce: CancellationException) {
            val elapsedMs = (System.nanoTime() - started) / 1_000_000.0
            return InferenceResponse(
                requestId = request.id,
                modelId = request.modelId,
                outputText = "",
                outputTokenCount = 0,
                promptTokenCount = estimateTokenCount(request.prompt),
                status = InferenceStatus.Cancelled,
                inferenceMillis = elapsedMs,
                failureMessage = null,
                completedAt = Instant.now(),
            )
        } catch (ex: Exception) {
            val elapsedMs = (System.nanoTime() - started) / 1_000_000.0
            return InferenceResponse(
                requestId = request.id,
                modelId = request.modelId,
                outputText = "",
                outputTokenCount = 0,
                promptTokenCount = estimateTokenCount(request.prompt),
                status = InferenceStatus.Failed,
                inferenceMillis = elapsedMs,
                failureMessage = ex.message,
                completedAt = Instant.now(),
            )
        }

        val elapsedMs = (System.nanoTime() - started) / 1_000_000.0
        return InferenceResponse(
            requestId = request.id,
            modelId = request.modelId,
            outputText = output,
            outputTokenCount = estimateTokenCount(output),
            promptTokenCount = estimateTokenCount(request.prompt),
            status = status,
            inferenceMillis = elapsedMs,
            failureMessage = failureMessage,
            completedAt = Instant.now(),
            reasoningText = reasoning,
        )
    }

    override fun streamCompletionAsync(request: InferenceRequest): Flow<String> = flow {
        if (descriptor.modelId != request.modelId) return@flow

        val messages = listOf(ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = request.prompt))
        val options = GenerationOptions(
            maxTokens = request.maxOutputTokens,
            temperature = request.temperature,
            topP = request.topP,
            stopSequences = request.stopSequences,
        )

        var hasYielded = false
        chatGenerator.streamAsync(messages, options).collect { chunk ->
            hasYielded = true
            emit(chunk)
        }

        if (!hasYielded) {
            // Fallback: generator streamed nothing. Emit the full completion in a
            // single chunk so callers always see >= 1 token.
            emit(chatGenerator.generateAsync(messages, options))
        }
    }

    override fun streamFragmentsAsync(request: InferenceRequest): Flow<InferenceFragment> = flow {
        if (descriptor.modelId != request.modelId) return@flow

        val messages = listOf(ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = request.prompt))
        val options = GenerationOptions(
            maxTokens = request.maxOutputTokens,
            temperature = request.temperature,
            topP = request.topP,
            stopSequences = request.stopSequences,
        )

        chatGenerator.streamFragmentsAsync(messages, options).collect { f ->
            val kind = if (f.kind == ChatFragmentKind.REASONING) InferenceFragmentKind.Reasoning
            else InferenceFragmentKind.Content
            emit(InferenceFragment(kind, f.text))
        }
    }

    override suspend fun getDeviceCapabilitiesAsync(): DeviceCapabilities = capabilityProbe.probeAsync()

    // ── Helpers (byte-identical to C#) ───────────────────────────────────────

    private fun determineStatus(output: String, request: InferenceRequest): InferenceStatus {
        if (request.stopSequences.isNotEmpty()) {
            for (s in request.stopSequences) {
                if (s.isNotEmpty() && output.contains(s)) return InferenceStatus.StoppedByToken
            }
        }
        val produced = estimateTokenCount(output)
        return if (produced >= request.maxOutputTokens) InferenceStatus.StoppedByLength
        else InferenceStatus.Completed
    }

    private fun estimateTokenCount(text: String): Int {
        if (text.isEmpty()) return 0
        // Rough heuristic: ~4 chars per BPE token for English.
        return max(1, text.length / 4)
    }
}

// =====================================================================
// MockInferenceBridge (MockInferenceBridge.cs)
// =====================================================================

/**
 * Deterministic [IInferenceBridge] for tests. Returns the same canned output for
 * every call and reports a single fixed-mock model as loaded. Mirrors C#
 * `MockInferenceBridge`.
 */
class MockInferenceBridge(
    private val cannedOutput: String,
    private val latencyMillis: Int = 0,
    modelId: String = "mock-model",
) : IInferenceBridge {

    init {
        require(latencyMillis >= 0) { "latencyMillis must be non-negative." }
    }

    /** The model descriptor this mock reports as loaded. */
    val descriptor: ModelDescriptor = ModelDescriptor(
        modelId = modelId,
        version = "mock-1.0.0",
        format = ModelFormat.Unknown,
        contextWindowTokens = 4096,
        vocabSize = 32000,
        parameterCount = 0,
        quantisationLabel = null,
        approximateMemoryBytes = 0,
    )

    override suspend fun listLoadedModelsAsync(): List<ModelDescriptor> = listOf(descriptor)

    override suspend fun isModelLoadedAsync(modelId: String): Boolean {
        require(modelId.isNotEmpty()) { "modelId is required" }
        return descriptor.modelId == modelId
    }

    override suspend fun completeAsync(request: InferenceRequest): InferenceResponse {
        val started = System.nanoTime()
        if (latencyMillis > 0) delay(latencyMillis.toLong())
        val elapsedMs = (System.nanoTime() - started) / 1_000_000.0

        return InferenceResponse(
            requestId = request.id,
            modelId = descriptor.modelId,
            outputText = cannedOutput,
            outputTokenCount = max(0, cannedOutput.length / 4),
            promptTokenCount = max(0, request.prompt.length / 4),
            status = InferenceStatus.Completed,
            inferenceMillis = elapsedMs,
            failureMessage = null,
            completedAt = Instant.now(),
        )
    }

    override fun streamCompletionAsync(request: InferenceRequest): Flow<String> = flow {
        if (latencyMillis > 0) delay(latencyMillis.toLong())
        emit(cannedOutput)
    }

    override suspend fun getDeviceCapabilitiesAsync(): DeviceCapabilities =
        DeviceCapabilities(
            osName = "Mock",
            osVersion = "1.0",
            physicalMemoryBytes = 4L * 1024 * 1024 * 1024,
            cpuCoreCount = 1,
            hasGpu = false,
            gpuName = null,
            gpuMemoryBytes = null,
            hasNpu = false,
            npuName = null,
            hasTransportLayerEncryption = true,
        )
}
