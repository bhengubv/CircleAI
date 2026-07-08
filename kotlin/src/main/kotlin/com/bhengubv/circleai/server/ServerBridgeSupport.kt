// ServerBridgeSupport.kt
//
// Supporting hosting-bridge + runtime-capability types that the
// CircleAI.Inference.Server work-unit contracts depend on. Ported from their
// C# definitions so the Server contracts are real (no stubs):
//   • CircleAI.Runtime.Backends.BackendKind / CapabilityTier
//   • CircleAI.Runtime.Capabilities.HostProfile (+ enums/records) / ICapabilityProbe
//   • CircleAI.Hosting.InferenceBridge.ModelDescriptor / ModelFormat
//   • CircleAI.Hosting.InferenceBridge.DeviceCapabilities
//   • CircleAI.Hosting.InferenceBridge.InferenceRequest / InferenceResponse /
//     InferenceStatus / InferenceFragment / InferenceFragmentKind / IInferenceBridge
//   • CircleAI.Hosting.InferenceBridge.LocalProcessInferenceBridge
//   • CircleAI.Inference.NativeRuntimePrep.NativeRuntimePaths
//
// These live in the server package because that is the work unit that needs
// them; the full Runtime/Hosting trees are ported separately.

package com.bhengubv.circleai.server

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.UUID
import kotlin.math.max

// ── CircleAI.Runtime.Backends ───────────────────────────────────────────────

/** MNN execution backend (ports BackendKind). Ordinals match the C# enum. */
enum class BackendKind {
    Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML;

    /** `true` for GPU-class backends that draw from a VRAM ceiling. */
    val isGpuClass: Boolean
        get() = this == Cuda || this == Vulkan || this == Metal || this == OpenCL

    companion object {
        /** Parse a case-insensitive backend name, defaulting to [Cpu]. */
        fun parse(name: String): BackendKind =
            entries.firstOrNull { it.name.equals(name, ignoreCase = true) } ?: Cpu
    }
}

/** Capability tier mapping to a model size band (ports CapabilityTier). */
enum class CapabilityTier {
    Tier0_Tiny, Tier1_Small, Tier2_Medium, Tier3_Large, Tier4_Frontier;

    companion object {
        /** Parse a case-insensitive tier name, defaulting to [Tier1_Small]. */
        fun parse(name: String): CapabilityTier =
            entries.firstOrNull { it.name.equals(name, ignoreCase = true) } ?: Tier1_Small
    }
}

// ── CircleAI.Runtime.Capabilities ───────────────────────────────────────────

enum class OperatingSystemKind { Unknown, Windows, Linux, MacOS, Android, IOS, HarmonyOS }
enum class ArchitectureKind { Unknown, X86, X64, Arm, Arm64, Loong64 }
enum class GpuVendor { None, Nvidia, Amd, Intel, Apple, Qualcomm, Huawei, Arm, Other }
enum class NpuVendor { None, AppleNeuralEngine, QualcommHexagon, HuaweiAscend, IntelVpu, CambriconMlu, Other }

/** Discovered GPU details. */
data class GpuInfo(
    val vendor: GpuVendor,
    val model: String,
    val vramBytes: Long,
    val driverVersion: String?,
)

/** Discovered NPU details. */
data class NpuInfo(val vendor: NpuVendor, val model: String)

/** Full host capability snapshot — the result of [ICapabilityProbe.probeAsync]. */
data class HostProfile(
    val os: OperatingSystemKind,
    val osVersion: String,
    val arch: ArchitectureKind,
    val cpuModel: String,
    val logicalCoreCount: Int,
    val physicalCoreCount: Int,
    val totalPhysicalMemoryBytes: Long,
    val gpu: GpuInfo?,
    val npu: NpuInfo?,
    val probedAt: Instant,
) {
    fun hasUsableGpu(minimumVramBytes: Long = 2L * 1024 * 1024 * 1024): Boolean =
        gpu != null && gpu.vramBytes >= minimumVramBytes

    val is64Bit: Boolean
        get() = arch == ArchitectureKind.X64 || arch == ArchitectureKind.Arm64 || arch == ArchitectureKind.Loong64
}

/** Discovers the host's hardware capabilities. Ports ICapabilityProbe. */
interface ICapabilityProbe {
    /** Runs the probe. MUST NOT throw — unresolved fields come back Unknown/null/0. */
    suspend fun probeAsync(): HostProfile
}

/**
 * A fixed in-memory probe returning a caller-supplied [HostProfile]. Lets the
 * lifecycle manager / bridge factory run deterministically without reading real
 * hardware.
 */
class FixedCapabilityProbe(private val profile: HostProfile) : ICapabilityProbe {
    override suspend fun probeAsync(): HostProfile = profile

    companion object {
        /** A modest CPU-only host: 16 GB RAM, 8 cores, no GPU/NPU. */
        fun cpuHost(totalRamBytes: Long = 16L * 1024 * 1024 * 1024): FixedCapabilityProbe =
            FixedCapabilityProbe(
                HostProfile(
                    os = OperatingSystemKind.Linux,
                    osVersion = "0.0",
                    arch = ArchitectureKind.X64,
                    cpuModel = "generic-x64",
                    logicalCoreCount = 8,
                    physicalCoreCount = 8,
                    totalPhysicalMemoryBytes = totalRamBytes,
                    gpu = null,
                    npu = null,
                    probedAt = Instant.now(),
                ),
            )

        /** A GPU host with a discrete card of [vramBytes] VRAM. */
        fun gpuHost(
            vramBytes: Long = 24L * 1024 * 1024 * 1024,
            totalRamBytes: Long = 64L * 1024 * 1024 * 1024,
        ): FixedCapabilityProbe = FixedCapabilityProbe(
            HostProfile(
                os = OperatingSystemKind.Linux,
                osVersion = "0.0",
                arch = ArchitectureKind.X64,
                cpuModel = "generic-x64",
                logicalCoreCount = 16,
                physicalCoreCount = 16,
                totalPhysicalMemoryBytes = totalRamBytes,
                gpu = GpuInfo(GpuVendor.Nvidia, "generic-gpu", vramBytes, null),
                npu = null,
                probedAt = Instant.now(),
            ),
        )
    }
}

// ── CircleAI.Hosting.InferenceBridge — descriptor / device caps ──────────────

/** On-disk encoding format of a model weight artefact (ports ModelFormat). */
enum class ModelFormat { Gguf, Onnx, CoreMl, Tflite, Unknown }

/** Canonical descriptor for a single loaded model (ports ModelDescriptor). */
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

/** Static-ish capabilities report from the device hosting the bridge. */
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

// ── CircleAI.Hosting.InferenceBridge — request / response ────────────────────

/** Terminal state of a single inference call (ports InferenceStatus). */
enum class InferenceStatus { Completed, StoppedByToken, StoppedByLength, Failed, Cancelled }

/** One completion request submitted to an [IInferenceBridge] (ports InferenceRequest). */
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
        /** Convenience factory stamping a fresh id + timestamp with sane defaults. */
        fun create(
            modelId: String,
            prompt: String,
            maxOutputTokens: Int = 256,
            temperature: Float = 0.7f,
            topP: Float = 0.95f,
        ): InferenceRequest {
            require(modelId.isNotEmpty()) { "modelId required" }
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

/** Result of a single completion call (ports InferenceResponse). */
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

/** Kind of fragment a streaming bridge emits (ports InferenceFragmentKind). */
enum class InferenceFragmentKind { Content, Reasoning }

/** A single fragment emitted by [IInferenceBridge.streamFragmentsAsync]. */
data class InferenceFragment(val kind: InferenceFragmentKind, val text: String)

/** Cross-OS contract for an inference daemon (ports IInferenceBridge). */
interface IInferenceBridge {
    /** Descriptor for every model currently loaded by the bridge. */
    suspend fun listLoadedModelsAsync(): List<ModelDescriptor>

    /** `true` when a model with [modelId] is loaded and ready. */
    suspend fun isModelLoadedAsync(modelId: String): Boolean

    /** Run a single completion and return the full response. */
    suspend fun completeAsync(request: InferenceRequest): InferenceResponse

    /** Stream tokens (content only). */
    fun streamCompletionAsync(request: InferenceRequest): Flow<String>

    /**
     * Stream tokens tagged with their kind. Default wraps
     * [streamCompletionAsync] and tags every chunk as
     * [InferenceFragmentKind.Content].
     */
    fun streamFragmentsAsync(request: InferenceRequest): Flow<InferenceFragment> = flow {
        streamCompletionAsync(request).collect { chunk ->
            emit(InferenceFragment(InferenceFragmentKind.Content, chunk))
        }
    }

    /** The bridge's view of the hardware it is running on. */
    suspend fun getDeviceCapabilitiesAsync(): DeviceCapabilities
}

/**
 * In-process reference bridge that wraps any [IChatGenerator] and exposes it
 * through the bridge contract. Ports LocalProcessInferenceBridge: outcome
 * classification (stop-by-token / stop-by-length / completed / cancelled /
 * failed), ~4-char token estimation, and reasoning surfacing.
 */
class LocalProcessInferenceBridge(
    private val chatGenerator: IChatGenerator,
    private val descriptor: ModelDescriptor,
    private val capabilityProbe: ICapabilityProbe? = null,
) : IInferenceBridge, AutoCloseable {

    override suspend fun listLoadedModelsAsync(): List<ModelDescriptor> = listOf(descriptor)

    override suspend fun isModelLoadedAsync(modelId: String): Boolean = descriptor.modelId == modelId

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
        val options = buildOptions(request)

        val started = System.nanoTime()
        var output: String
        var reasoning: String? = null
        var status: InferenceStatus
        var failureMessage: String? = null

        try {
            val response = chatGenerator.generateResponseAsync(messages, options)
            output = response.text
            reasoning = response.reasoningContent
            status = determineStatus(output, request)
        } catch (e: Exception) {
            output = ""
            status = InferenceStatus.Failed
            failureMessage = e.message
        }

        val millis = (System.nanoTime() - started) / 1_000_000.0
        return InferenceResponse(
            requestId = request.id,
            modelId = request.modelId,
            outputText = output,
            outputTokenCount = estimateTokenCount(output),
            promptTokenCount = estimateTokenCount(request.prompt),
            status = status,
            inferenceMillis = millis,
            failureMessage = failureMessage,
            completedAt = Instant.now(),
            reasoningText = reasoning,
        )
    }

    override fun streamCompletionAsync(request: InferenceRequest): Flow<String> = flow {
        if (descriptor.modelId != request.modelId) return@flow
        val messages = listOf(ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = request.prompt))
        val options = buildOptions(request)

        var hasYielded = false
        chatGenerator.streamAsync(messages, options).collect { chunk ->
            hasYielded = true
            emit(chunk)
        }
        if (!hasYielded) {
            emit(chatGenerator.generateAsync(messages, options))
        }
    }

    override fun streamFragmentsAsync(request: InferenceRequest): Flow<InferenceFragment> = flow {
        if (descriptor.modelId != request.modelId) return@flow
        val messages = listOf(ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = request.prompt))
        val options = buildOptions(request)
        chatGenerator.streamFragmentsAsync(messages, options).collect { f ->
            val kind = if (f.kind == ChatFragmentKind.REASONING) {
                InferenceFragmentKind.Reasoning
            } else {
                InferenceFragmentKind.Content
            }
            emit(InferenceFragment(kind, f.text))
        }
    }

    override suspend fun getDeviceCapabilitiesAsync(): DeviceCapabilities {
        val profile = capabilityProbe?.probeAsync()
        if (profile == null) {
            return DeviceCapabilities(
                osName = "Unknown",
                osVersion = "0.0",
                physicalMemoryBytes = 0,
                cpuCoreCount = Runtime.getRuntime().availableProcessors(),
                hasGpu = false,
                gpuName = null,
                gpuMemoryBytes = null,
                hasNpu = false,
                npuName = null,
                hasTransportLayerEncryption = false,
            )
        }
        return DeviceCapabilities(
            osName = profile.os.name,
            osVersion = profile.osVersion,
            physicalMemoryBytes = profile.totalPhysicalMemoryBytes,
            cpuCoreCount = profile.logicalCoreCount,
            hasGpu = profile.gpu != null,
            gpuName = profile.gpu?.model,
            gpuMemoryBytes = profile.gpu?.vramBytes,
            hasNpu = profile.npu != null,
            npuName = profile.npu?.model,
            hasTransportLayerEncryption = false,
        )
    }

    override fun close() {
        chatGenerator.close()
    }

    private fun buildOptions(request: InferenceRequest): GenerationOptions =
        GenerationOptions(
            maxTokens = request.maxOutputTokens,
            temperature = request.temperature,
            topP = request.topP,
            stopSequences = request.stopSequences,
        )

    private fun determineStatus(output: String, request: InferenceRequest): InferenceStatus {
        for (s in request.stopSequences) {
            if (s.isNotEmpty() && output.contains(s)) return InferenceStatus.StoppedByToken
        }
        val produced = estimateTokenCount(output)
        return if (produced >= request.maxOutputTokens) InferenceStatus.StoppedByLength else InferenceStatus.Completed
    }

    private fun estimateTokenCount(text: String): Int {
        if (text.isEmpty()) return 0
        return max(1, text.length / 4)
    }
}

// ── CircleAI.Inference.NativeRuntimePrep.NativeRuntimePaths ──────────────────

/**
 * Last-known native-runtime paths produced by native prep. Surfaced through
 * diagnostics so DLL-not-found failures are debuggable from the wire.
 */
data class NativeRuntimePaths(
    val rid: String,
    val expectedNativeDir: String,
    val mnnBridgePath: String,
    val mnnBridgeLoaded: Boolean,
    val mnnCoreFetchedPath: String,
    val mnnCoreFlattenedPath: String,
    val mnnCorePreloaded: Boolean,
    val flattenError: String?,
    val preloadError: String?,
)
