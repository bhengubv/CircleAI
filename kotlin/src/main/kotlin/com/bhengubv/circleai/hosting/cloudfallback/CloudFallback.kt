// CloudFallback.kt
//
// Kotlin port of CircleAI.Hosting.CloudFallback — the C# reference is the EXACT
// spec (CloudFallbackChain.cs, BackupBrainOrchestrator.cs,
// OpenAiCompatibleChatGeneratorBase.cs + the concrete provider generators,
// ServerSentEventsReader.cs, Options.cs).
//
// CloudFallbackChain walks an ordered list of IChatGenerator and streams from
// the first one ready (start-of-call ordering). BackupBrainOrchestrator does
// mid-call between-turn failover with degraded/cooldown health. The cloud
// generators speak the OpenAI / Anthropic / Gemini wire formats behind an
// injected [ICloudHttpTransport] seam so they are deterministic-testable; a
// [LocalFakeChatGenerator] provides a deterministic local generator for tests.

package com.bhengubv.circleai.hosting.cloudfallback

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.toList
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import java.time.Duration
import java.time.Instant

// =====================================================================
// IConfigurableChatGenerator (CloudFallbackChain.cs)
// =====================================================================

/**
 * Reports whether a generator can currently serve calls. Cloud generators expose
 * this via the API-key check; on-device generators that don't implement it are
 * presumed always ready. Mirrors C# `IConfigurableChatGenerator`.
 */
interface IConfigurableChatGenerator : IChatGenerator {
    /** True when the generator can serve calls (e.g. API key present). */
    val isConfigured: Boolean

    /** Display name (e.g. "OpenAI · gpt-4o-mini"). */
    val engineLabel: String

    /** Human-readable explanation of the current state. */
    val statusMessage: String
}

// =====================================================================
// CloudFallbackChain (CloudFallbackChain.cs)
// =====================================================================

/**
 * Tries an ordered list of [IChatGenerator]s and streams from the first one
 * ready. A generator that yields a fail-soft "[… not configured]" frame doesn't
 * count as ready — the chain skips it. Generators that throw are also skipped.
 * Mirrors C# `CloudFallbackChain`.
 */
class CloudFallbackChain(
    generators: Iterable<IChatGenerator>,
) : IChatGenerator {

    val generators: List<IChatGenerator> = generators.toList()

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        for (g in generators) {
            if (!isReady(g)) continue
            try {
                return g.generateAsync(messages, opts)
            } catch (ce: kotlinx.coroutines.CancellationException) {
                throw ce
            } catch (_: Exception) {
                // Fall through to the next generator.
            }
        }
        return NO_GENERATOR
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        for (g in generators) {
            if (!isReady(g)) continue

            // Buffer the stream so we can decide whether to commit to this
            // generator based on its first real frame (mirrors the C# enumerator
            // move-next probing). The fail-soft sentinel makes us move on.
            var yielded = false
            var faulted = false
            val collected = ArrayList<String>()
            try {
                g.streamAsync(messages, opts).collect { chunk ->
                    if (!yielded && isFailSoftFrame(chunk)) {
                        // Generator declined the call (e.g. no API key) — stop.
                        throw DeclinedException
                    }
                    yielded = true
                    collected.add(chunk)
                }
            } catch (ce: kotlinx.coroutines.CancellationException) {
                throw ce
            } catch (_: DeclinedException) {
                // Move to next generator.
            } catch (_: Exception) {
                faulted = true
            }

            for (c in collected) emit(c)
            if (yielded && !faulted) return@flow
            if (yielded && faulted) return@flow // faulted mid-stream after yielding — stop
            // not yielded → try next generator
        }
        emit(NO_GENERATOR)
    }

    override fun close() {
        for (g in generators) runCatching { g.close() }
    }

    private object DeclinedException : Exception() {
        private fun readResolve(): Any = DeclinedException
    }

    companion object {
        internal const val NO_GENERATOR =
            "[CloudFallbackChain: no configured generator could serve the request]"

        private fun isReady(g: IChatGenerator): Boolean =
            g !is IConfigurableChatGenerator || g.isConfigured

        internal fun isFailSoftFrame(chunk: String): Boolean =
            chunk.startsWith("[") &&
                (chunk.contains("not configured", ignoreCase = true) ||
                    chunk.contains("CloudFallbackChain", ignoreCase = true))
    }
}

// =====================================================================
// BackupBrainOrchestrator (BackupBrainOrchestrator.cs)
// =====================================================================

/** Health state of one brain in the chain. Mirrors C# `BrainHealth`. */
enum class BrainHealth { Healthy, Degraded, CoolingDown }

/** Snapshot of brain health for monitoring. Mirrors C# `BrainStatus`. */
data class BrainStatus(val label: String, val health: BrainHealth, val consecutiveFailures: Int)

/** Policy knobs. Mirrors C# `BackupBrainPolicy`. */
data class BackupBrainPolicy(
    val degradedAfterFailures: Int = 2,
    val coolDownDuration: Duration? = null,
    val maxRetriesPerTurn: Int = 3,
) {
    val coolDownDurationOrDefault: Duration get() = coolDownDuration ?: Duration.ofSeconds(30)
}

/**
 * Wraps an ordered set of brains; switches on failure, retries the primary on
 * cool-down. Different from [CloudFallbackChain] (start-of-call ordering) — this
 * is between-turn failover. Mirrors C# `BackupBrainOrchestrator`.
 */
class BackupBrainOrchestrator(
    brains: Iterable<IChatGenerator>,
    private val policy: BackupBrainPolicy = BackupBrainPolicy(),
    private val clock: () -> Instant = { Instant.now() },
) : IChatGenerator {

    private val brains: List<BrainEntry>

    init {
        val list = brains.map { BrainEntry(it) }
        require(list.isNotEmpty()) { "At least one brain is required." }
        this.brains = list
    }

    val statuses: List<BrainStatus>
        get() {
            val now = clock()
            return brains.map { e ->
                synchronized(e.gate) {
                    val h = e.healthAt(now, policy.coolDownDurationOrDefault)
                    val label = (e.brain as? IConfigurableChatGenerator)?.engineLabel ?: e.brain::class.simpleName ?: "brain"
                    BrainStatus(label, h, e.consecutive)
                }
            }
        }

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        val maxRetries = minOf(policy.maxRetriesPerTurn, brains.size)
        val tried = HashSet<BrainEntry>()
        for (attempt in 0 until maxRetries) {
            val pick = pickAvailable(tried) ?: break
            tried.add(pick)
            try {
                val result = pick.brain.generateAsync(messages, opts)
                pick.recordSuccess()
                return result
            } catch (ce: kotlinx.coroutines.CancellationException) {
                throw ce
            } catch (_: Exception) {
                pick.recordFailure(policy.degradedAfterFailures, clock())
            }
        }
        return ALL_FAILED
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        val maxRetries = minOf(policy.maxRetriesPerTurn, brains.size)
        val tried = HashSet<BrainEntry>()
        for (attempt in 0 until maxRetries) {
            val pick = pickAvailable(tried) ?: break
            tried.add(pick)

            var streamedAny = false
            var failed = false
            val collected = ArrayList<String>()
            try {
                pick.brain.streamAsync(messages, opts).collect { chunk ->
                    streamedAny = true
                    collected.add(chunk)
                }
            } catch (ce: kotlinx.coroutines.CancellationException) {
                throw ce
            } catch (_: Exception) {
                failed = true
            }

            for (c in collected) emit(c)

            if (failed) {
                pick.recordFailure(policy.degradedAfterFailures, clock())
                if (!streamedAny) continue // try the backup
            }
            if (streamedAny) {
                pick.recordSuccess()
                return@flow
            }
        }
        emit(ALL_FAILED)
    }

    override fun close() { /* nothing owned */ }

    private fun pickAvailable(skip: Set<BrainEntry>): BrainEntry? {
        val now = clock()
        for (e in brains) {
            if (e in skip) continue
            synchronized(e.gate) {
                val h = e.healthAt(now, policy.coolDownDurationOrDefault)
                if (h == BrainHealth.Healthy || h == BrainHealth.CoolingDown) return e
            }
        }
        // None healthy — pick first untried brain anyway (degraded might recover).
        for (e in brains) {
            if (e !in skip) return e
        }
        return null
    }

    companion object {
        internal const val ALL_FAILED = "[All brains failed.]"
    }

    private class BrainEntry(val brain: IChatGenerator) {
        val gate = Any()
        var consecutive = 0
        var degradedSince: Instant = Instant.MIN
        var isDegraded = false

        fun healthAt(now: Instant, coolDown: Duration): BrainHealth {
            if (!isDegraded) return BrainHealth.Healthy
            return if (Duration.between(degradedSince, now) >= coolDown) BrainHealth.CoolingDown
            else BrainHealth.Degraded
        }

        fun recordSuccess() {
            synchronized(gate) {
                consecutive = 0
                isDegraded = false
            }
        }

        fun recordFailure(threshold: Int, now: Instant) {
            synchronized(gate) {
                consecutive++
                if (consecutive >= threshold) {
                    isDegraded = true
                    degradedSince = now
                }
            }
        }
    }
}

// =====================================================================
// Options (Options.cs)
// =====================================================================

/** OpenAI Chat Completions options. Mirrors C# `OpenAiChatOptions`. */
data class OpenAiChatOptions(
    val baseAddress: String = "https://api.openai.com",
    val apiKey: String? = null,
    val model: String = "gpt-4o-mini",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
)

/** Anthropic Messages options. Mirrors C# `AnthropicChatOptions`. */
data class AnthropicChatOptions(
    val baseAddress: String = "https://api.anthropic.com",
    val apiKey: String? = null,
    val model: String = "claude-3-5-sonnet-latest",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
    val anthropicVersion: String = "2023-06-01",
)

/** Google Gemini options. Mirrors C# `GeminiChatOptions`. */
data class GeminiChatOptions(
    val baseAddress: String = "https://generativelanguage.googleapis.com",
    val apiKey: String? = null,
    val model: String = "gemini-2.0-flash",
    val temperature: Float = 0.7f,
    val maxOutputTokens: Int = 1024,
)

/** Groq options. OpenAI-compatible at `/openai/v1/chat/completions`. Mirrors C# `GroqChatOptions`. */
data class GroqChatOptions(
    val baseAddress: String = "https://api.groq.com",
    val apiKey: String? = null,
    val model: String = "llama-3.3-70b-versatile",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
)

/** Cerebras options. Mirrors C# `CerebrasChatOptions`. */
data class CerebrasChatOptions(
    val baseAddress: String = "https://api.cerebras.ai",
    val apiKey: String? = null,
    val model: String = "llama3.3-70b",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
)

/** Together AI options. Mirrors C# `TogetherChatOptions`. */
data class TogetherChatOptions(
    val baseAddress: String = "https://api.together.xyz",
    val apiKey: String? = null,
    val model: String = "meta-llama/Llama-3.3-70B-Instruct-Turbo",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
)

/** DeepSeek options. Mirrors C# `DeepSeekChatOptions`. */
data class DeepSeekChatOptions(
    val baseAddress: String = "https://api.deepseek.com",
    val apiKey: String? = null,
    val model: String = "deepseek-chat",
    val temperature: Float = 0.7f,
    val maxTokens: Int = 1024,
)

// =====================================================================
// HTTP transport seam + SSE reader (ServerSentEventsReader.cs)
// =====================================================================

/** One HTTP response from the cloud transport. */
data class CloudHttpResponse(val statusCode: Int, val bodyLines: List<String>)

/**
 * The seam every cloud generator posts through. The C# generators use a real
 * HttpClient; the Kotlin port injects this so hosts wire a real transport while
 * tests supply a deterministic fake. [postSse] posts a JSON body to [path] on
 * [baseAddress] with [headers] and returns the raw response lines (the SSE
 * stream is parsed by [ServerSentEventsReader]).
 */
interface ICloudHttpTransport {
    suspend fun postSse(
        baseAddress: String,
        path: String,
        headers: Map<String, String>,
        jsonBody: String,
    ): CloudHttpResponse
}

/**
 * Reads `data: …` frames from streaming HTTP body lines and yields each frame's
 * payload. Frames containing `[DONE]` terminate cleanly. Mirrors C#
 * `ServerSentEventsReader`.
 */
object ServerSentEventsReader {
    fun readFrames(lines: List<String>): List<String> {
        val out = ArrayList<String>()
        for (line in lines) {
            if (!line.startsWith("data:")) continue
            val payload = line.substring(5).trimStart()
            if (payload == "[DONE]") break
            out.add(payload)
        }
        return out
    }
}

// =====================================================================
// OpenAiCompatibleChatGeneratorBase (OpenAiCompatibleChatGeneratorBase.cs)
// =====================================================================

private val JSON = Json { encodeDefaults = false }

private fun truncate(value: String, max: Int): String =
    if (value.length <= max) value else value.substring(0, max) + "…"

private fun messagesJson(messages: List<ChatMessage>): JsonArray =
    JsonArray(messages.map {
        JsonObject(mapOf("role" to JsonPrimitive(it.role), "content" to JsonPrimitive(it.content)))
    })

/**
 * Shared OpenAI-compatible streaming chat generator. Groq, Cerebras, Together,
 * and DeepSeek subclass this — each supplies its provider id, model, and base
 * address. Mirrors C# `OpenAiCompatibleChatGeneratorBase`.
 */
abstract class OpenAiCompatibleChatGeneratorBase(
    private val transport: ICloudHttpTransport,
) : IChatGenerator, IConfigurableChatGenerator {

    abstract val id: String
    abstract override val engineLabel: String
    protected abstract val apiKey: String?
    protected abstract val model: String
    protected abstract val defaultTemperature: Float
    protected abstract val defaultMaxTokens: Int
    protected abstract val baseAddress: String
    protected open val chatCompletionsPath: String get() = "/v1/chat/completions"

    override val isConfigured: Boolean get() = !apiKey.isNullOrBlank()
    override val statusMessage: String get() = if (isConfigured) "Ready · $model" else "$id API key not configured."

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        val sb = StringBuilder()
        streamAsync(messages, opts).toList().forEach { sb.append(it) }
        return sb.toString()
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        if (!isConfigured) {
            emit("[$statusMessage]")
            return@flow
        }

        val body = JsonObject(
            mapOf(
                "model" to JsonPrimitive(model),
                "stream" to JsonPrimitive(true),
                "temperature" to JsonPrimitive(opts.temperature),
                "max_tokens" to JsonPrimitive(opts.maxTokens),
                "messages" to messagesJson(messages),
            ),
        )
        val response = transport.postSse(
            baseAddress, chatCompletionsPath,
            mapOf("Authorization" to "Bearer $apiKey"),
            JSON.encodeToString(JsonObject.serializer(), body),
        )

        if (response.statusCode !in 200..299) {
            val error = response.bodyLines.joinToString("\n")
            emit("[$id error ${response.statusCode}: ${truncate(error, 240)}]")
            return@flow
        }

        for (frame in ServerSentEventsReader.readFrames(response.bodyLines)) {
            val delta = extractOpenAiDelta(frame)
            if (!delta.isNullOrEmpty()) emit(delta)
        }
    }

    override fun close() { /* transport is owned by the host */ }

    private fun extractOpenAiDelta(frame: String): String? = try {
        val root = JSON.parseToJsonElement(frame) as? JsonObject
        val choices = root?.get("choices") as? JsonArray
        val delta = (choices?.getOrNull(0) as? JsonObject)?.get("delta") as? JsonObject
        (delta?.get("content") as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
    } catch (_: Exception) {
        null
    }
}

// =====================================================================
// Concrete OpenAI-compatible generators
// =====================================================================

/** Mirrors C# `GroqChatGenerator`. */
class GroqChatGenerator(transport: ICloudHttpTransport, private val options: GroqChatOptions) :
    OpenAiCompatibleChatGeneratorBase(transport) {
    override val id = "groq"
    override val engineLabel get() = "Groq · ${options.model}"
    override val apiKey get() = options.apiKey
    override val model get() = options.model
    override val defaultTemperature get() = options.temperature
    override val defaultMaxTokens get() = options.maxTokens
    override val baseAddress get() = options.baseAddress
    override val chatCompletionsPath get() = "/openai/v1/chat/completions"
}

/** Mirrors C# `CerebrasChatGenerator`. */
class CerebrasChatGenerator(transport: ICloudHttpTransport, private val options: CerebrasChatOptions) :
    OpenAiCompatibleChatGeneratorBase(transport) {
    override val id = "cerebras"
    override val engineLabel get() = "Cerebras · ${options.model}"
    override val apiKey get() = options.apiKey
    override val model get() = options.model
    override val defaultTemperature get() = options.temperature
    override val defaultMaxTokens get() = options.maxTokens
    override val baseAddress get() = options.baseAddress
}

/** Mirrors C# `TogetherChatGenerator`. */
class TogetherChatGenerator(transport: ICloudHttpTransport, private val options: TogetherChatOptions) :
    OpenAiCompatibleChatGeneratorBase(transport) {
    override val id = "together"
    override val engineLabel get() = "Together · ${options.model}"
    override val apiKey get() = options.apiKey
    override val model get() = options.model
    override val defaultTemperature get() = options.temperature
    override val defaultMaxTokens get() = options.maxTokens
    override val baseAddress get() = options.baseAddress
}

/** Mirrors C# `DeepSeekChatGenerator`. */
class DeepSeekChatGenerator(transport: ICloudHttpTransport, private val options: DeepSeekChatOptions) :
    OpenAiCompatibleChatGeneratorBase(transport) {
    override val id = "deepseek"
    override val engineLabel get() = "DeepSeek · ${options.model}"
    override val apiKey get() = options.apiKey
    override val model get() = options.model
    override val defaultTemperature get() = options.temperature
    override val defaultMaxTokens get() = options.maxTokens
    override val baseAddress get() = options.baseAddress
}

// =====================================================================
// OpenAiChatGenerator (OpenAiChatGenerator.cs)
// =====================================================================

/** OpenAI Chat Completions generator. Mirrors C# `OpenAiChatGenerator`. */
class OpenAiChatGenerator(
    private val transport: ICloudHttpTransport,
    private val options: OpenAiChatOptions,
) : IChatGenerator, IConfigurableChatGenerator {

    val id = "openai"
    override val engineLabel get() = "OpenAI · ${options.model}"
    override val isConfigured get() = !options.apiKey.isNullOrBlank()
    override val statusMessage get() = if (isConfigured) "Ready · ${options.model}" else "OpenAI API key not configured."

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        val sb = StringBuilder()
        streamAsync(messages, opts).toList().forEach { sb.append(it) }
        return sb.toString()
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        if (!isConfigured) {
            emit("[$statusMessage]")
            return@flow
        }
        val body = JsonObject(
            mapOf(
                "model" to JsonPrimitive(options.model),
                "stream" to JsonPrimitive(true),
                "temperature" to JsonPrimitive(opts.temperature),
                "max_tokens" to JsonPrimitive(opts.maxTokens),
                "messages" to messagesJson(messages),
            ),
        )
        val response = transport.postSse(
            options.baseAddress, "/v1/chat/completions",
            mapOf("Authorization" to "Bearer ${options.apiKey}"),
            JSON.encodeToString(JsonObject.serializer(), body),
        )
        if (response.statusCode !in 200..299) {
            emit("[OpenAI error ${response.statusCode}: ${truncate(response.bodyLines.joinToString("\n"), 240)}]")
            return@flow
        }
        for (frame in ServerSentEventsReader.readFrames(response.bodyLines)) {
            val delta = try {
                val root = JSON.parseToJsonElement(frame) as? JsonObject
                val choices = root?.get("choices") as? JsonArray
                val delta = (choices?.getOrNull(0) as? JsonObject)?.get("delta") as? JsonObject
                (delta?.get("content") as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
            } catch (_: Exception) {
                null
            }
            if (!delta.isNullOrEmpty()) emit(delta)
        }
    }

    override fun close() {}
}

// =====================================================================
// AnthropicChatGenerator (AnthropicChatGenerator.cs)
// =====================================================================

/** Anthropic Messages generator. System prompt rides out-of-band. Mirrors C# `AnthropicChatGenerator`. */
class AnthropicChatGenerator(
    private val transport: ICloudHttpTransport,
    private val options: AnthropicChatOptions,
) : IChatGenerator, IConfigurableChatGenerator {

    val id = "anthropic"
    override val engineLabel get() = "Anthropic · ${options.model}"
    override val isConfigured get() = !options.apiKey.isNullOrBlank()
    override val statusMessage get() = if (isConfigured) "Ready · ${options.model}" else "Anthropic API key not configured."

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        val sb = StringBuilder()
        streamAsync(messages, opts).toList().forEach { sb.append(it) }
        return sb.toString()
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        if (!isConfigured) {
            emit("[$statusMessage]")
            return@flow
        }

        val system = messages
            .filter { it.role.equals("system", ignoreCase = true) }
            .joinToString("\n\n") { it.content }
        val chat = JsonArray(
            messages.filter { !it.role.equals("system", ignoreCase = true) }
                .map { JsonObject(mapOf("role" to JsonPrimitive(it.role.lowercase()), "content" to JsonPrimitive(it.content))) },
        )

        val fields = LinkedHashMap<String, kotlinx.serialization.json.JsonElement>()
        fields["model"] = JsonPrimitive(options.model)
        fields["max_tokens"] = JsonPrimitive(opts.maxTokens)
        fields["temperature"] = JsonPrimitive(opts.temperature)
        fields["stream"] = JsonPrimitive(true)
        if (system.isNotEmpty()) fields["system"] = JsonPrimitive(system)
        fields["messages"] = chat
        val body = JsonObject(fields)

        val response = transport.postSse(
            options.baseAddress, "/v1/messages",
            mapOf("x-api-key" to options.apiKey!!, "anthropic-version" to options.anthropicVersion),
            JSON.encodeToString(JsonObject.serializer(), body),
        )
        if (response.statusCode !in 200..299) {
            emit("[Anthropic error ${response.statusCode}: ${truncate(response.bodyLines.joinToString("\n"), 240)}]")
            return@flow
        }
        for (frame in ServerSentEventsReader.readFrames(response.bodyLines)) {
            val delta = try {
                val root = JSON.parseToJsonElement(frame) as? JsonObject
                if ((root?.get("type") as? JsonPrimitive)?.contentOrNull == "content_block_delta") {
                    val deltaEl = root["delta"] as? JsonObject
                    (deltaEl?.get("text") as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
                } else null
            } catch (_: Exception) {
                null
            }
            if (!delta.isNullOrEmpty()) emit(delta)
        }
    }

    override fun close() {}
}

// =====================================================================
// GeminiChatGenerator (GeminiChatGenerator.cs)
// =====================================================================

/** Gemini streamGenerateContent generator. Mirrors C# `GeminiChatGenerator`. */
class GeminiChatGenerator(
    private val transport: ICloudHttpTransport,
    private val options: GeminiChatOptions,
) : IChatGenerator, IConfigurableChatGenerator {

    val id = "gemini"
    override val engineLabel get() = "Gemini · ${options.model}"
    override val isConfigured get() = !options.apiKey.isNullOrBlank()
    override val statusMessage get() = if (isConfigured) "Ready · ${options.model}" else "Gemini API key not configured."

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        val sb = StringBuilder()
        streamAsync(messages, opts).toList().forEach { sb.append(it) }
        return sb.toString()
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        if (!isConfigured) {
            emit("[$statusMessage]")
            return@flow
        }

        val system = messages
            .filter { it.role.equals("system", ignoreCase = true) }
            .joinToString("\n\n") { it.content }
        val contents = JsonArray(
            messages.filter { !it.role.equals("system", ignoreCase = true) }.map { m ->
                val role = if (m.role.equals("assistant", ignoreCase = true)) "model" else m.role.lowercase()
                JsonObject(
                    mapOf(
                        "role" to JsonPrimitive(role),
                        "parts" to JsonArray(listOf(JsonObject(mapOf("text" to JsonPrimitive(m.content))))),
                    ),
                )
            },
        )

        val genConfig = JsonObject(
            mapOf(
                "temperature" to JsonPrimitive(opts.temperature),
                "maxOutputTokens" to JsonPrimitive(opts.maxTokens),
            ),
        )
        val fields = LinkedHashMap<String, kotlinx.serialization.json.JsonElement>()
        fields["contents"] = contents
        if (system.isNotEmpty()) {
            fields["systemInstruction"] = JsonObject(
                mapOf("parts" to JsonArray(listOf(JsonObject(mapOf("text" to JsonPrimitive(system)))))),
            )
        }
        fields["generationConfig"] = genConfig
        val body = JsonObject(fields)

        val path = "/v1beta/models/${escape(options.model)}:streamGenerateContent?alt=sse&key=${escape(options.apiKey!!)}"
        val response = transport.postSse(
            options.baseAddress, path, emptyMap(),
            JSON.encodeToString(JsonObject.serializer(), body),
        )
        if (response.statusCode !in 200..299) {
            emit("[Gemini error ${response.statusCode}: ${truncate(response.bodyLines.joinToString("\n"), 240)}]")
            return@flow
        }
        for (frame in ServerSentEventsReader.readFrames(response.bodyLines)) {
            val delta = try {
                val root = JSON.parseToJsonElement(frame) as? JsonObject
                val candidates = root?.get("candidates") as? JsonArray
                val content = (candidates?.getOrNull(0) as? JsonObject)?.get("content") as? JsonObject
                val parts = content?.get("parts") as? JsonArray
                ((parts?.getOrNull(0) as? JsonObject)?.get("text") as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull
            } catch (_: Exception) {
                null
            }
            if (!delta.isNullOrEmpty()) emit(delta)
        }
    }

    override fun close() {}

    private fun escape(s: String): String = java.net.URLEncoder.encode(s, "UTF-8")
}

// =====================================================================
// LocalFakeChatGenerator — deterministic local generator for tests
// =====================================================================

/**
 * Deterministic local [IChatGenerator] for tests + as the sovereign-by-default
 * first entry in a [CloudFallbackChain]. Echoes a fixed reply (or, by default,
 * the last user message) — no network, fully deterministic. Not a stub: it is a
 * complete working generator with a real streaming path.
 */
class LocalFakeChatGenerator(
    private val fixedReply: String? = null,
    private val chunkSize: Int = 8,
) : IChatGenerator {

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String =
        reply(messages)

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        val text = reply(messages)
        var i = 0
        while (i < text.length) {
            val end = minOf(i + chunkSize, text.length)
            emit(text.substring(i, end))
            i = end
        }
    }

    override fun close() {}

    private fun reply(messages: List<ChatMessage>): String =
        fixedReply ?: messages.lastOrNull { it.role.equals("user", ignoreCase = true) }?.content ?: ""
}
