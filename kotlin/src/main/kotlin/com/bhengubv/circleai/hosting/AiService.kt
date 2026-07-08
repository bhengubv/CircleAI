// AiService.kt
//
// Kotlin port of the CircleAI.Hosting butler service surface — the C# reference
// is the EXACT spec (IAIService.cs, AIService.cs, FallbackAIService.cs).
//
// IAIService is the single contract for a long-lived B! butler process: the
// service holds the loaded generator in memory for the process lifetime so
// callers don't pay the load cost per request. AIService is the default impl.
//
// The C# AIService is deeply coupled to a native QwenTextGenerator, a model
// loader/selector/registry, RAG + skill context builders, and persona/affect
// enrichment. Following the porting directive ("inject external/native/cloud
// dependencies behind interfaces; no stubs"), the Kotlin AIService takes the
// generator through a factory (IChatGenerator seam) and wires the enrichment that
// maps to the existing Kotlin memory types (persona, affect, device context,
// episodic RAG). Every algorithm that survives that mapping — start/stop gating,
// system-prompt enrichment ordering, the agentic tool-call loop, Qwen tool-call
// parsing, feedback→persona adaptation, upgrade detection — is byte-identical.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.device.NullDeviceContext
import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.memory.AffectState
import com.bhengubv.circleai.memory.EpisodicMemoryEntry
import com.bhengubv.circleai.memory.FeedbackPolarity
import com.bhengubv.circleai.memory.FeedbackSignal
import com.bhengubv.circleai.memory.PersonaState
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.models.UpgradeInfo
import com.bhengubv.circleai.registry.ModelRegistryService
import com.bhengubv.circleai.tools.ToolInvocation
import com.bhengubv.circleai.tools.ToolResult
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import java.time.Duration
import java.time.Instant
import java.util.UUID

// =====================================================================
// IAIService (IAIService.cs)
// =====================================================================

/**
 * Long-lived butler service. Owns the loaded chat generator and exposes ask /
 * chat / stream / tool / agentic entry points. Implementations are thread-safe.
 * Mirrors C# `IAIService` (which is `IAsyncDisposable` → [AutoCloseable] here,
 * plus a suspend [disposeAsync]).
 */
interface IAIService {
    /** True once [startAsync] has completed and the model is loaded. */
    val isReady: Boolean

    /** Resolves the model file, loads it, and (optionally) runs a warm-up. Idempotent. */
    suspend fun startAsync()

    /** Releases the model handle and shuts the service down. */
    suspend fun stopAsync()

    /** Convenience wrapper for a single user question. */
    suspend fun askAsync(question: String): String

    /** Generates a complete assistant reply for the supplied conversation. */
    suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions? = null): String

    /** Streams the assistant reply token-by-token. */
    fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions? = null): Flow<String>

    /** Routes a tool invocation to the configured tool bridge. */
    suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult

    /**
     * Agentic run: generates, detects embedded tool calls, executes them, and
     * re-prompts until the model produces plain text or the iteration cap is hit.
     */
    suspend fun agenticChatAsync(prompt: String, options: GenerationOptions? = null): String

    /** Records a feedback signal against a past B! response. */
    suspend fun submitFeedbackAsync(signal: FeedbackSignal)

    /**
     * Compares installed models against the registry, returning one [UpgradeInfo]
     * per detected upgrade. Default: empty list.
     */
    suspend fun checkForUpgradesAsync(): List<UpgradeInfo> = emptyList()

    /** Pre-warm the loaded generator without a full user-facing call. Default: [startAsync]. */
    suspend fun prewarmAsync() = startAsync()

    /** Releases all resources. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()
}

// =====================================================================
// AIService (AIService.cs)
// =====================================================================

/**
 * Default [IAIService]. Loads a generator once (via the injected
 * [generatorFactory]) and serves all downstream callers from that single handle.
 *
 * Threading model:
 *  - [startAsync] is idempotent and serialised by a [Mutex].
 *  - [chatAsync] / [streamAsync] are safe to call concurrently.
 *  - [disposeAsync] tears the generator down.
 *
 * Mirrors C# `AIService`. The generator is resolved from [generatorFactory]
 * (given the resolved model path/id) rather than constructing a native
 * QwenTextGenerator directly — the C# `_generatorFactory` seam.
 */
class AIService(
    private val options: AIOptions,
    private val generatorFactory: (String) -> IChatGenerator,
    private val modelRegistry: ModelRegistryService? = null,
) : IAIService {

    private val startGate = Mutex()

    private var generator: IChatGenerator? = null
    private var started = false
    private var disposed = false
    private var resolvedModelId: String? = null

    private var personaCache: PersonaState? = null

    override val isReady: Boolean get() = started && generator != null && !disposed

    override suspend fun checkForUpgradesAsync(): List<UpgradeInfo> {
        throwIfDisposed()
        if (modelRegistry == null || options.modelStorageDirectory.isNullOrBlank()) {
            return emptyList()
        }
        return try {
            modelRegistry.checkForUpgradesAsync(options.modelStorageDirectory)
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            emptyList()
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    override suspend fun startAsync() {
        throwIfDisposed()
        if (started) return

        startGate.withLock {
            if (started) return

            val modelPath = resolveModelPath()

            val gen = generatorFactory(modelPath)
            generator = gen

            if (options.warmOnStart) {
                try {
                    warmUp()
                } catch (ce: CancellationException) {
                    throw ce
                } catch (_: Exception) {
                    // Warm-up failed; continue anyway.
                }
            }

            started = true

            fireObserver { it.onStartedAsync() }

            if (options.checkForUpgradesOnStart) {
                val upgrades = checkForUpgradesAsync()
                for (u in upgrades) {
                    fireObserver { it.onUpgradeAvailableAsync(u) }
                }
            }
        }
    }

    override suspend fun stopAsync() {
        if (disposed) return

        trySavePersona()

        startGate.withLock {
            (generator as? AutoCloseable)?.let { runCatching { it.close() } }
            generator = null
            started = false
            personaCache = null

            fireObserver { it.onStoppedAsync() }
        }
    }

    // ── Single-turn inference ────────────────────────────────────────────────

    override suspend fun askAsync(question: String): String {
        require(question.isNotEmpty()) { "question is required" }
        // Pass only the user message — prepareMessages injects the enriched system prompt.
        val messages = listOf(userMessage(question))
        return chatAsync(messages, options.defaultGenerationOptions)
    }

    override suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions?): String {
        ensureStarted()
        val gen = generator ?: throw IllegalStateException("Butler is not ready.")

        val userQuery = messages.lastOrNull { it.role.equals("user", ignoreCase = true) }?.content ?: ""
        val prepared = prepareMessages(messages, userQuery)
        val effectiveOptions = options ?: this.options.defaultGenerationOptions ?: GenerationOptions()

        val correlationId = UUID.randomUUID()
        val started = System.nanoTime()
        val response = gen.generateAsync(prepared, effectiveOptions)
        val elapsed = Duration.ofNanos(System.nanoTime() - started)

        tryStoreEpisode(userQuery, response)

        fireObserver {
            it.onChatCompletedAsync(AIChatEvent(correlationId, prepared, response, elapsed, Instant.now()))
        }

        return response
    }

    override fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions?): Flow<String> = flow {
        ensureStarted()
        val gen = generator ?: throw IllegalStateException("Butler is not ready.")

        val userQuery = messages.lastOrNull { it.role.equals("user", ignoreCase = true) }?.content ?: ""
        val prepared = prepareMessages(messages, userQuery)
        val effectiveOptions = options ?: this@AIService.options.defaultGenerationOptions ?: GenerationOptions()

        val correlationId = UUID.randomUUID()
        val started = System.nanoTime()
        var tokenCount = 0
        var firstToken = true
        val sb = StringBuilder()

        gen.streamAsync(prepared, effectiveOptions).collect { piece ->
            if (firstToken) {
                firstToken = false
                fireObserver {
                    it.onStreamStartedAsync(
                        AIStreamEvent(correlationId, prepared, Duration.ofNanos(System.nanoTime() - started), 0, Instant.now()),
                    )
                }
            }
            sb.append(piece)
            tokenCount++
            emit(piece)
        }

        val elapsed = Duration.ofNanos(System.nanoTime() - started)
        tryStoreEpisode(userQuery, sb.toString())

        fireObserver {
            it.onStreamCompletedAsync(AIStreamEvent(correlationId, prepared, elapsed, tokenCount, Instant.now()))
        }
    }

    override suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult {
        throwIfDisposed()

        val bridge = options.toolBridge
        if (bridge == null) {
            val failResult = ToolResult(toolName = invocation.toolName, success = false, error = "No tool bridge configured.")
            fireObserver {
                it.onToolInvokedAsync(AIToolEvent(UUID.randomUUID(), invocation, failResult, Duration.ZERO, Instant.now()))
            }
            return failResult
        }

        val correlationId = UUID.randomUUID()
        val started = System.nanoTime()
        val result = bridge.invokeAsync(invocation)
        val elapsed = Duration.ofNanos(System.nanoTime() - started)

        fireObserver {
            it.onToolInvokedAsync(AIToolEvent(correlationId, invocation, result, elapsed, Instant.now()))
        }

        return result
    }

    // ── Agentic loop ─────────────────────────────────────────────────────────

    override suspend fun agenticChatAsync(prompt: String, options: GenerationOptions?): String {
        require(prompt.isNotEmpty()) { "prompt is required" }
        ensureStarted()
        val gen = generator ?: throw IllegalStateException("Butler is not ready.")

        val maxIter = maxOf(
            1,
            this.options.agenticMaxIterations
                ?: com.bhengubv.circleai.device.DeviceTierDefaults.agenticMaxIterations(
                    com.bhengubv.circleai.device.DeviceTier.DESKTOP,
                ),
        )
        val effectiveOptions = options ?: this.options.defaultGenerationOptions ?: GenerationOptions()

        val history = ArrayList<ChatMessage>()
        history.add(userMessage(prompt))

        var lastResponse = ""
        for (iteration in 0 until maxIter) {
            val prepared = prepareMessages(history, prompt)

            val started = System.nanoTime()
            val response = gen.generateAsync(prepared, effectiveOptions)
            val elapsed = Duration.ofNanos(System.nanoTime() - started)

            lastResponse = response
            history.add(assistantMessage(response))

            fireObserver {
                it.onChatCompletedAsync(AIChatEvent(UUID.randomUUID(), prepared, response, elapsed, Instant.now()))
            }

            val invocation = parseToolCall(response) ?: break

            val bridge = options.let { this.options.toolBridge }
            if (this.options.toolBridge == null) {
                history.add(toolMessage("{\"tool\": \"${invocation.toolName}\", \"error\": \"No tool bridge configured.\"}"))
                continue
            }

            val toolResult = invokeToolAsync(invocation)
            val toolContent = if (toolResult.success) {
                "{\"tool\": \"${toolResult.toolName}\", \"result\": ${jsonValue(toolResult.result)}}"
            } else {
                "{\"tool\": \"${toolResult.toolName}\", \"error\": ${jsonValue(toolResult.error)}}"
            }
            history.add(toolMessage(toolContent))
        }

        tryStoreEpisode(prompt, lastResponse)
        return lastResponse
    }

    // ── Feedback ─────────────────────────────────────────────────────────────

    override suspend fun submitFeedbackAsync(signal: FeedbackSignal) {
        throwIfDisposed()
        val store = options.feedbackStore ?: return

        try {
            store.save(signal)

            val persona = ensurePersona()
            when (signal.polarity) {
                FeedbackPolarity.Positive -> persona.positiveSignals++
                FeedbackPolarity.Negative -> persona.negativeSignals++
                FeedbackPolarity.Neutral -> {}
            }
            persona.totalInteractions++

            // Recent-signal-driven verbosity adaptation (recency window of 20).
            val recentSignals = store.getRecent(options.personaUserId, 20)
            var netPolarity = 0
            for (s in recentSignals) {
                netPolarity += when (s.polarity) {
                    FeedbackPolarity.Positive -> 1
                    FeedbackPolarity.Negative -> -1
                    FeedbackPolarity.Neutral -> 0
                }
            }
            // Mirror the C# FeedbackAnalyser verbosity mapping: a run of negatives
            // shortens verbosity, a run of positives lengthens it.
            if (netPolarity < 0) {
                persona.verbosity = when (persona.verbosity) {
                    "detailed" -> "balanced"
                    else -> "brief"
                }
            } else if (netPolarity > 0) {
                persona.verbosity = when (persona.verbosity) {
                    "brief" -> "balanced"
                    else -> "detailed"
                }
            }

            trySavePersona()
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            // Failed to store feedback signal; non-fatal.
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    override suspend fun disposeAsync() {
        if (disposed) return
        disposed = true

        trySavePersona()
        runCatching { stopAsync() }

        (generator as? AutoCloseable)?.let { runCatching { it.close() } }
        generator = null
    }

    // ── Private — startup helpers ─────────────────────────────────────────────

    private suspend fun ensureStarted() {
        throwIfDisposed()
        if (started) return
        startAsync()
    }

    private fun resolveModelPath(): String {
        // 1. Explicit path wins.
        val path = options.modelPath
        if (!path.isNullOrBlank()) {
            resolvedModelId = options.modelId
            return path
        }
        // 2. Otherwise fall back to modelId (the generator factory owns fetching).
        val modelId = options.modelId
            ?: throw IllegalStateException(
                "AIService needs either AIOptions.modelPath or AIOptions.modelId (the generator factory resolves the file).",
            )
        resolvedModelId = modelId
        return modelId
    }

    override suspend fun prewarmAsync() {
        throwIfDisposed()
        if (!started) {
            startAsync()
            return
        }
        warmUp()
    }

    private suspend fun warmUp() {
        val gen = generator ?: return
        val warmMessages = listOf(
            ChatMessage(id = UUID.randomUUID().toString(), role = "system", content = options.systemPrompt),
            userMessage("."),
        )
        val warmOptions = GenerationOptions(maxTokens = 1, temperature = 0f)
        gen.generateAsync(warmMessages, warmOptions)
    }

    // ── Private — context enrichment ──────────────────────────────────────────

    private suspend fun prepareMessages(messages: List<ChatMessage>, userQuery: String): List<ChatMessage> {
        val systemContent = buildEnrichedSystemPrompt(userQuery)
        val hasSystem = messages.any { it.role.equals("system", ignoreCase = true) }

        val prepared = ArrayList<ChatMessage>(messages.size + 1)
        if (hasSystem) {
            // Caller supplied their own system message — honour it as-is.
            prepared.addAll(messages)
        } else {
            if (systemContent.isNotBlank()) {
                prepared.add(ChatMessage(id = UUID.randomUUID().toString(), role = "system", content = systemContent))
            }
            prepared.addAll(messages)
        }
        return prepared
    }

    private suspend fun buildEnrichedSystemPrompt(userQuery: String): String {
        val sb = StringBuilder(options.systemPrompt)

        // 1. Persona hints.
        try {
            val persona = ensurePersona()
            val hint = persona.toSystemPromptHint()
            if (hint.isNotBlank()) {
                sb.appendLine()
                sb.append(hint)
            }
        } catch (_: Exception) { /* persona load failure is non-fatal */ }

        // 1b. Affect state.
        val affectStore = options.affectStore
        if (affectStore != null) {
            try {
                val affect: AffectState = affectStore.loadAsync(options.personaUserId)
                val hint = affect.toSystemPromptHint()
                if (hint.isNotBlank()) {
                    sb.appendLine()
                    sb.append(hint)
                }
            } catch (_: Exception) { /* affect load failure is non-fatal */ }
        }

        // 2. Device context.
        val ctx = options.deviceContext
        if (ctx != null && ctx !is NullDeviceContext) {
            val ctxLines = ArrayList<String>()
            ctx.localTime?.let {
                val z = it.atZone(java.time.ZoneOffset.UTC)
                val stamp = "%04d-%02d-%02d %02d:%02d".format(z.year, z.monthValue, z.dayOfMonth, z.hour, z.minute)
                ctxLines.add("Local time: $stamp (${ctx.timeZoneId ?: "UTC"})")
            }
            ctx.locationHint?.takeIf { it.isNotBlank() }?.let { ctxLines.add("Location: $it") }
            ctx.batteryLevel?.let {
                val pct = (it * 100).toInt()
                val charging = if (ctx.isCharging == true) " (charging)" else ""
                ctxLines.add("Battery: $pct%$charging")
            }
            ctx.networkType?.takeIf { it.isNotBlank() }?.let { ctxLines.add("Network: $it") }
            ctx.activeAppId?.takeIf { it.isNotBlank() }?.let { ctxLines.add("Active app: $it") }

            if (ctxLines.isNotEmpty()) {
                sb.appendLine()
                sb.appendLine("[Device context]")
                for (line in ctxLines) sb.appendLine(line)
            }
        }

        // 3. RAG context (relevant past exchanges) — recency-only.
        val episodic = options.episodicMemory
        if (episodic != null && options.ragTopK > 0 && userQuery.isNotBlank()) {
            try {
                val recent = episodic.getRecent(options.personaUserId, options.ragTopK)
                if (recent.isNotEmpty()) {
                    sb.appendLine()
                    sb.appendLine("[Relevant past context]")
                    for (entry in recent) {
                        sb.appendLine("- ${entry.content}")
                    }
                }
            } catch (_: Exception) { /* RAG failure is non-fatal */ }
        }

        return sb.toString()
    }

    // ── Private — persona helpers ─────────────────────────────────────────────

    private suspend fun ensurePersona(): PersonaState {
        personaCache?.let { return it }
        val store = options.personaStore
        val persona = if (store == null) {
            PersonaState(options.personaUserId)
        } else {
            store.loadAsync(options.personaUserId)
        }
        personaCache = persona
        return persona
    }

    private suspend fun trySavePersona() {
        val persona = personaCache ?: return
        val store = options.personaStore ?: return
        try {
            store.saveAsync(persona)
        } catch (_: Exception) { /* non-fatal */ }
    }

    // ── Private — episodic memory ─────────────────────────────────────────────

    private suspend fun tryStoreEpisode(userText: String, assistantText: String) {
        val episodic = options.episodicMemory ?: return
        if (userText.isBlank()) return
        try {
            val entry = EpisodicMemoryEntry(
                id = UUID.randomUUID().toString(),
                userId = options.personaUserId,
                content = "User: $userText\nAssistant: $assistantText",
                embedding = FloatArray(0),
                tags = listOfNotNull(options.deviceContext?.activeAppId),
            )
            episodic.save(entry)
        } catch (_: Exception) { /* non-fatal */ }
    }

    // ── Private — observer ────────────────────────────────────────────────────

    private suspend fun fireObserver(action: suspend (IAIObserver) -> Unit) {
        val obs = options.observer ?: return
        try {
            action(obs)
        } catch (ce: CancellationException) {
            // respect cancellation silently
        } catch (_: Exception) {
            // observer errors are non-fatal
        }
    }

    private fun throwIfDisposed() {
        if (disposed) throw IllegalStateException("AIService is disposed.")
    }

    private companion object {
        const val TOOL_CALL_OPEN = "<tool_call>"
        const val TOOL_CALL_CLOSE = "</tool_call>"
        val JSON = Json

        fun userMessage(content: String) =
            ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = content)

        fun assistantMessage(content: String) =
            ChatMessage(id = UUID.randomUUID().toString(), role = "assistant", content = content)

        fun toolMessage(content: String) =
            ChatMessage(id = UUID.randomUUID().toString(), role = "tool", content = content)

        fun jsonValue(v: Any?): String =
            JSON.encodeToString(JsonPrimitive.serializer(), JsonPrimitive(v?.toString()))

        /**
         * Attempts to parse a tool call from Qwen3's native
         * `<tool_call>...</tool_call>` format. Returns null when absent. Mirrors
         * C# `AIService.ParseToolCall`.
         */
        fun parseToolCall(response: String): ToolInvocation? {
            if (response.isBlank()) return null

            val start = response.indexOf(TOOL_CALL_OPEN)
            if (start < 0) return null

            val contentStart = start + TOOL_CALL_OPEN.length
            val end = response.indexOf(TOOL_CALL_CLOSE, contentStart)
            if (end < 0) return null

            val jsonText = response.substring(contentStart, end).trim()
            if (jsonText.isBlank()) return null

            return try {
                val root = JSON.parseToJsonElement(jsonText) as? JsonObject ?: return null

                // Support both {"name":...} and {"tool_name":...} spellings.
                val toolName = (root["name"] as? JsonPrimitive)?.contentOrNull
                    ?: (root["tool_name"] as? JsonPrimitive)?.contentOrNull
                if (toolName.isNullOrBlank()) return null

                val args = LinkedHashMap<String, Any?>()
                (root["arguments"] as? JsonObject)?.let { argsObj ->
                    for ((name, value) in argsObj) {
                        args[name] = when (value) {
                            is JsonPrimitive -> if (value.isString) value.content else value.content
                            else -> value.toString()
                        }
                    }
                }

                ToolInvocation(toolName = toolName, arguments = args)
            } catch (_: Exception) {
                null
            }
        }
    }
}

// =====================================================================
// FallbackAIService (FallbackAIService.cs)
// =====================================================================

/**
 * Wraps a local [IAIService] with a cloud [IAIService] fallback. Local inference
 * is preferred; cloud is used transparently when local is unavailable. Mirrors
 * C# `FallbackAIService` — the RAM probe uses the JVM's max-heap view since the
 * portable core has no `GC.GetGCMemoryInfo`. The cloud client is an [IAIService]
 * (the C# `AIApiClient` implements `IAIService`).
 */
class FallbackAIService(
    private val local: IAIService,
    private val cloud: IAIService,
    private val ramThresholdBytes: Long = 2L * 1024 * 1024 * 1024,
    private val availableRamProvider: () -> Long = { defaultAvailableRam() },
) : IAIService {

    private var active: IAIService? = null
    private var disposed = false

    override val isReady: Boolean get() = active?.isReady ?: false

    override suspend fun startAsync() {
        val availableRam = availableRamProvider()

        if (availableRam >= ramThresholdBytes) {
            try {
                local.startAsync()
                active = local
                return
            } catch (_: Exception) {
                // Local start failed — fall back to cloud.
            }
        }

        cloud.startAsync()
        active = cloud
    }

    override suspend fun stopAsync() {
        active?.stopAsync()
    }

    private val activeOrThrow: IAIService
        get() = active ?: throw IllegalStateException(
            "FallbackAIService has not been started. Call startAsync first.",
        )

    override suspend fun askAsync(question: String): String = activeOrThrow.askAsync(question)

    override suspend fun chatAsync(messages: List<ChatMessage>, options: GenerationOptions?): String =
        activeOrThrow.chatAsync(messages, options)

    override fun streamAsync(messages: List<ChatMessage>, options: GenerationOptions?): Flow<String> =
        activeOrThrow.streamAsync(messages, options)

    override suspend fun agenticChatAsync(prompt: String, options: GenerationOptions?): String =
        activeOrThrow.agenticChatAsync(prompt, options)

    override suspend fun invokeToolAsync(invocation: ToolInvocation): ToolResult =
        activeOrThrow.invokeToolAsync(invocation)

    override suspend fun submitFeedbackAsync(signal: FeedbackSignal) =
        activeOrThrow.submitFeedbackAsync(signal)

    override suspend fun disposeAsync() {
        if (disposed) return
        disposed = true
        local.disposeAsync()
        cloud.disposeAsync()
    }

    private companion object {
        fun defaultAvailableRam(): Long =
            try {
                Runtime.getRuntime().maxMemory()
            } catch (_: Exception) {
                0L
            }
    }
}
