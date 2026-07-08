// Hosting.kt
//
// Kotlin port of the CircleAI.Hosting observability + configuration surface —
// the C# reference is the EXACT spec (IAIObserver.cs, AIOptions.cs). Neutral
// observer hook with event records + the butler configuration bag.
//
// Cancellation is via coroutine cancellation, so the C# `CancellationToken`
// parameters are dropped. `ValueTask`/`Task` map to `suspend fun`; event
// `record`s map to `data class`. `Guid` -> UUID, `DateTimeOffset` -> Instant,
// `TimeSpan` -> Duration.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.catalog.ModelScopeCatalogClient
import com.bhengubv.circleai.device.IDeviceContext
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.memory.IAffectStore
import com.bhengubv.circleai.memory.IEpisodicMemoryStore
import com.bhengubv.circleai.memory.IFeedbackStore
import com.bhengubv.circleai.memory.IGoalStore
import com.bhengubv.circleai.memory.IPersonaStore
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.models.UpgradeInfo
import com.bhengubv.circleai.selector.ChatCapability
import com.bhengubv.circleai.tools.IToolBridge
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import java.security.SecureRandom
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.UUID

// ---------------------------------------------------------------------------
// Event records (IAIObserver.cs)
// ---------------------------------------------------------------------------

/**
 * Payload delivered to [IAIObserver.onChatCompletedAsync]. Carries the full
 * conversation and the model's reply. Mirrors C# `AIChatEvent`.
 */
data class AIChatEvent(
    val correlationId: UUID,
    val messages: List<ChatMessage>,
    val response: String,
    val elapsed: Duration,
    val timestamp: Instant,
)

/**
 * Payload delivered to [IAIObserver.onStreamStartedAsync] and
 * [IAIObserver.onStreamCompletedAsync]. Mirrors C# `AIStreamEvent`.
 */
data class AIStreamEvent(
    val correlationId: UUID,
    val messages: List<ChatMessage>,
    val elapsed: Duration,
    val tokenCount: Int,
    val timestamp: Instant,
)

/** Payload delivered to [IAIObserver.onToolInvokedAsync]. Mirrors C# `AIToolEvent`. */
data class AIToolEvent(
    val correlationId: UUID,
    val invocation: ToolInvocation,
    val result: ToolResult,
    val elapsed: Duration,
    val timestamp: Instant,
)

/**
 * (RT-04) Why a brownout swap fired. Sized so future causes can be added
 * without breaking ABI. Mirrors C# `BrownoutReason`.
 */
enum class BrownoutReason {
    /** OS-reported memory pressure. */
    MemoryPressure,
    /** Battery dropped below the brownout floor. */
    BatteryFloor,
    /** Thermal throttle declared the runtime must downshift. */
    ThermalCritical,
    /** Application requested the swap explicitly. */
    Manual,
}

// ---------------------------------------------------------------------------
// IAIObserver (IAIObserver.cs)
// ---------------------------------------------------------------------------

/**
 * Observability hook for the butler service. Receives lifecycle and inference
 * events. All methods are optional (default = no-op) and must complete quickly.
 * Mirrors C# `IAIObserver`.
 *
 * Thread safety: methods may be called concurrently. Implementations must be
 * thread-safe. Error isolation: exceptions thrown by observer methods are
 * caught by [AIService] and never propagate to the caller.
 */
interface IAIObserver {
    /** Called once after the model has loaded and Butler is ready. */
    suspend fun onStartedAsync() {}

    /** Called once when Butler is stopping / being disposed. */
    suspend fun onStoppedAsync() {}

    /** Called after a complete (non-streaming) chat response has been generated. */
    suspend fun onChatCompletedAsync(event: AIChatEvent) {}

    /** Called when a streaming response emits its first token (tokenCount == 0). */
    suspend fun onStreamStartedAsync(event: AIStreamEvent) {}

    /** Called after a streaming response has finished (all tokens yielded, or cancelled). */
    suspend fun onStreamCompletedAsync(event: AIStreamEvent) {}

    /** Called after a tool invocation has completed (success or failure). */
    suspend fun onToolInvokedAsync(event: AIToolEvent) {}

    /**
     * Called once when [AIService.startAsync] has resolved which model to load.
     * Fires before the actual file fetch/load so observers can surface progress UI.
     */
    suspend fun onModelFetchingAsync(modelId: String, autoSelected: Boolean) {}

    /**
     * Called when upgrade detection finds a model upgrade between what's installed
     * and what the registry now advertises. Fires once per detected upgrade.
     */
    suspend fun onUpgradeAvailableAsync(upgrade: UpgradeInfo) {}

    /**
     * (RT-04) Called when the runtime hot-swaps from one model in the fallback
     * chain to the next under memory pressure.
     */
    suspend fun onBrownoutAsync(from: String, to: String, reason: BrownoutReason) {}
}

/** Default no-op observer — hosts can subclass and override what they need. */
open class AIObserverBase : IAIObserver

// ---------------------------------------------------------------------------
// AIOptions (AIOptions.cs)
// ---------------------------------------------------------------------------

/**
 * Configuration for [AIService] and the loopback transport. All fields have safe
 * defaults so callers can `AIOptions()` and get a working instance. Mirrors C#
 * `AIOptions`.
 */
data class AIOptions(
    // ── Model ──
    val modelId: String? = null,
    val modelPath: String? = null,

    // ── Inference ──
    val systemPrompt: String = "You are B!, a helpful on-device assistant.",
    val defaultGenerationOptions: GenerationOptions? = null,
    val contextSize: Int? = null,
    val threadCount: Int? = null,
    val warmOnStart: Boolean = true,

    // ── Tools ──
    val toolBridge: IToolBridge? = null,

    // ── Observers ──
    val observer: IAIObserver? = null,

    // ── v2.0 Sensorium ──
    val deviceContext: IDeviceContext? = null,
    val catalogClient: ModelScopeCatalogClient? = null,
    val requiredCapabilities: Int = ChatCapability.DEFAULT,
    val checkForUpgradesOnStart: Boolean = false,
    val modelStorageDirectory: String? = null,

    // ── v2.0 Memory / RAG ──
    val episodicMemory: IEpisodicMemoryStore? = null,
    val ragTopK: Int = 5,

    // ── v2.0 Persona evolution ──
    val personaStore: IPersonaStore? = null,
    val personaUserId: String = "default",

    // ── v2.0 Feedback signals ──
    val feedbackStore: IFeedbackStore? = null,

    // ── v2.0 Agentic loop ──
    val agenticMaxIterations: Int? = null,

    // ── HttpLoopbackEndpoint configuration ──
    val loopbackPort: Int = 0,
    val loopbackToken: String? = null,

    // ── v2.1 Native runtime ──
    val nativeLibDir: String? = null,

    // ── v2.1 Model management ──
    val modelStorageDir: String = "models",
    val wifiOnlyModelDownload: Boolean = true,

    // ── v2.1 Cloud fallback ──
    val cloudFallbackEnabled: Boolean = false,
    val cloudFallbackEndpoint: String? = null,
    val cloudFallbackToken: String? = null,
    val cloudFallbackRamThresholdBytes: Long = 2L * 1024 * 1024 * 1024,

    // ── v2.1 Thermal management ──
    val thermalPauseEnabled: Boolean = true,
    val thermalService: IThermalThrottleService? = null,

    // ── v3.0 Scheduled tasks ──
    val scheduledTaskStore: IScheduledTaskStore? = null,

    // ── v3.0 Affect model ──
    val affectStore: IAffectStore? = null,

    // ── v3.0 Goal tracking ──
    val goalStore: IGoalStore? = null,
) {
    companion object {
        /**
         * Generates a cryptographically random 32-byte token, base64-encoded.
         * Used by [com.bhengubv.circleai.hosting.HttpLoopbackEndpoint] when
         * [loopbackToken] is null. Mirrors C# `AIOptions.GenerateRandomToken`.
         */
        fun generateRandomToken(): String {
            val bytes = ByteArray(32)
            SecureRandom().nextBytes(bytes)
            return Base64.getEncoder().encodeToString(bytes)
        }
    }
}
