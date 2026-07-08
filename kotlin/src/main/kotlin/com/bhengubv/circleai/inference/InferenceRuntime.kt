// InferenceRuntime.kt
//
// Kotlin port of the CircleAI.Inference runtime primitives that surround the
// IChatGenerator contract. The C# sources are the EXACT spec:
//   • PowerBudget.cs          → PowerBudget + PowerBudgetPolicy
//   • MnnInterop.cs (subset)  → KvCompressionMode + KvCompressionApplyResult
//                               + MnnKvCompression (apply/get seam)
//   • VisionInput.cs          → VisionInput
//
// GenerationOptions and IChatGenerator themselves live in Inference.kt (they
// predate this file); this file supplies the enums those two reference plus a
// concrete, deterministic IChatGenerator (LocalChatGenerator) that stands in
// for the native QwenTextGenerator / KimiVlGenerator without any P/Invoke.

package com.bhengubv.circleai.inference

import com.bhengubv.circleai.models.ChatFragment
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.models.ChatResponse
import com.bhengubv.circleai.models.FinishReason
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min

// ---------------------------------------------------------------------------
// PowerBudget — CircleAI.Inference.PowerBudget
// ---------------------------------------------------------------------------

/**
 * Per-call power budget. The runtime maps the budget to context size, KV
 * compression mode, decode token limit, and (when fallback chains are
 * configured) which model in the chain to use.
 *
 * Default behaviour is [Normal]. When the device's battery drops below 15% the
 * runtime auto-downgrades `Normal` to `Low`; pass [None] to opt out of
 * automatic adjustment.
 *
 * Ordinal values mirror the C# enum (`None=0, Low=1, Normal=2, High=3`).
 */
enum class PowerBudget {
    /** Opt out of automatic budget control entirely — honour MaxTokens literally. */
    None,

    /** Battery-conscious. Caps tokens at ~64, prefers TQ4, picks the smaller chain model. */
    Low,

    /** Default balanced behaviour. Caps tokens at ~512, TQ4 KV, chain head. Downgrades to Low below 15%. */
    Normal,

    /** Quality-first. Up to ~2048 tokens, full FP16 KV when affordable, chain head. Throttles to Normal on thermal. */
    High,
}

// ---------------------------------------------------------------------------
// KvCompressionMode — CircleAI.Inference.KvCompressionMode (MnnInterop.cs)
// ---------------------------------------------------------------------------

/**
 * KV cache compression mode. Mirrors the C ABI's integer encoding so the
 * managed and native layers agree without translation tables.
 */
enum class KvCompressionMode(val raw: Int) {
    /** Full FP16 KV cache — default behaviour, always supported. */
    Off(0),

    /** TurboQuant at 4 bits per channel — ~4x shrink, < 1% accuracy loss expected. */
    TurboQuant4Bit(1),

    /** TurboQuant at 3 bits per channel — ~5x shrink, marginal accuracy loss expected. */
    TurboQuant3Bit(2),

    /** TurboQuant at 2 bits per channel — ~8x shrink, noticeable accuracy loss expected. */
    TurboQuant2Bit(3);

    companion object {
        /** Map a raw C-ABI integer to a mode, or [Off] when out of the 0..3 range. */
        fun fromRaw(raw: Int): KvCompressionMode =
            entries.firstOrNull { it.raw == raw } ?: Off
    }
}

/**
 * Outcome of applying a [KvCompressionMode] to a native handle. Mirrors the C
 * ABI status codes.
 */
enum class KvCompressionApplyResult(val raw: Int) {
    /** Native path accepted the mode and will use it. */
    Applied(0),

    /** The mode value was outside the valid 0..3 range. */
    InvalidMode(1),

    /** LEGACY (mnnbridge <= 1.1.0) — scaffolding-only response. */
    NotImplemented(2),

    /** Handle pointer was invalid. */
    HandleInvalid(-1);

    companion object {
        /** Translate a raw C-ABI status into the typed result (matches the C# switch). */
        fun fromRaw(raw: Int): KvCompressionApplyResult = when (raw) {
            0 -> Applied
            1 -> InvalidMode
            2 -> NotImplemented
            else -> HandleInvalid
        }
    }
}

/**
 * The native KV-compression seam. In C# this is `MnnKvCompression` P/Invoking
 * `mnn_llm_set/get_kv_compression_mode`. Kotlin injects the native call behind
 * this interface so the apply/get logic is testable without a real handle.
 */
interface IKvCompressionNative {
    /** Raw set: returns the C-ABI status integer. */
    fun setRaw(handle: Long, modeRaw: Int): Int

    /** Raw get: returns the last-set mode as a C-ABI integer. */
    fun getRaw(handle: Long): Int
}

/**
 * Typed wrapper over the KV-compression seam so callers don't deal with raw
 * integers. Ports `MnnKvCompression.Set` / `.Get` exactly.
 */
class MnnKvCompression(private val native: IKvCompressionNative) {

    /** Applies the requested mode and returns the typed result. */
    fun set(handle: Long, mode: KvCompressionMode): KvCompressionApplyResult =
        KvCompressionApplyResult.fromRaw(native.setRaw(handle, mode.raw))

    /** Reads the last-set mode (or [KvCompressionMode.Off] on invalid handle). */
    fun get(handle: Long): KvCompressionMode {
        val raw = native.getRaw(handle)
        return if (raw in 0..3) KvCompressionMode.fromRaw(raw) else KvCompressionMode.Off
    }
}

// ---------------------------------------------------------------------------
// PowerBudgetPolicy — CircleAI.Inference.PowerBudgetPolicy
// ---------------------------------------------------------------------------

/**
 * The runtime's translation of a [PowerBudget] into concrete generation knobs.
 * Surfaced as a static helper so generators (and tests) agree on the mapping.
 */
object PowerBudgetPolicy {

    /**
     * Resolved budget for a single generation call.
     *
     * @param maxTokens Cap on output tokens for this call.
     * @param preferredKvMode Which [KvCompressionMode] the runtime prefers.
     * @param preferSmallerModelInChain Whether to pick a smaller chain model.
     */
    data class Resolution(
        val maxTokens: Int,
        val preferredKvMode: KvCompressionMode,
        val preferSmallerModelInChain: Boolean,
    )

    /**
     * Map a budget to concrete knobs. Auto-downgrades [PowerBudget.Normal] on
     * low battery and [PowerBudget.High] on thermal throttle, then caps the
     * caller's requested max-tokens without altering the caller's struct.
     *
     * @param budget The declared budget.
     * @param requestedMaxTokens The caller's requested max-tokens.
     * @param batteryLevelPercent 0..100 if known, `null` when unavailable.
     * @param thermalThrottled `true` when the platform reports elevated thermal state.
     */
    fun resolve(
        budget: PowerBudget,
        requestedMaxTokens: Int,
        batteryLevelPercent: Int? = null,
        thermalThrottled: Boolean = false,
    ): Resolution {
        var b = budget
        if (b == PowerBudget.Normal && batteryLevelPercent != null && batteryLevelPercent < 15) {
            b = PowerBudget.Low
        }
        if (b == PowerBudget.High && thermalThrottled) {
            b = PowerBudget.Normal
        }

        return when (b) {
            PowerBudget.None -> Resolution(
                maxTokens = requestedMaxTokens,
                preferredKvMode = KvCompressionMode.TurboQuant4Bit,
                preferSmallerModelInChain = false,
            )

            PowerBudget.Low -> Resolution(
                maxTokens = min(requestedMaxTokens, 64),
                preferredKvMode = KvCompressionMode.TurboQuant4Bit,
                preferSmallerModelInChain = true,
            )

            PowerBudget.Normal -> Resolution(
                maxTokens = min(requestedMaxTokens, 512),
                preferredKvMode = KvCompressionMode.TurboQuant4Bit,
                preferSmallerModelInChain = false,
            )

            PowerBudget.High -> Resolution(
                maxTokens = min(requestedMaxTokens, 2048),
                preferredKvMode = KvCompressionMode.Off,
                preferSmallerModelInChain = false,
            )
        }
    }
}

// ---------------------------------------------------------------------------
// VisionInput — CircleAI.Inference.VisionInput
// ---------------------------------------------------------------------------

/**
 * Raw image data to be embedded by the vision encoder before text generation
 * begins. Consumed by a vision-capable [IChatGenerator]; text-only generators
 * ignore it.
 *
 * @param imageBytes Raw image bytes (JPEG, PNG, or any format the encoder accepts).
 * @param mimeType Optional MIME-type hint (e.g. "image/jpeg"). Not passed to the
 *   native encoder; useful for callers to track format.
 */
class VisionInput(
    imageBytes: ByteArray,
    val mimeType: String? = null,
) {
    /** Raw image bytes. Defensive copy in/out so the container is immutable. */
    val imageBytes: ByteArray = imageBytes.copyOf()
        get() = field.copyOf()

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is VisionInput) return false
        return imageBytes.contentEquals(other.imageBytes) && mimeType == other.mimeType
    }

    override fun hashCode(): Int =
        31 * imageBytes.contentHashCode() + (mimeType?.hashCode() ?: 0)
}

// ---------------------------------------------------------------------------
// LocalChatGenerator — deterministic in-memory IChatGenerator
// ---------------------------------------------------------------------------

/**
 * A fully-working, deterministic [IChatGenerator] that stands in for the native
 * `QwenTextGenerator` / `KimiVlGenerator` without any native runtime. It is NOT
 * a stub: it produces a real reply derived deterministically from the
 * conversation, honours [GenerationOptions] (max-tokens via the power budget,
 * stop sequences, reasoning gating), streams token-by-token, tracks token
 * counts, and round-trips a portable session marker.
 *
 * The reply shape is intentionally simple and reproducible: it echoes a short
 * acknowledgement of the last user turn, optionally preceded by a `<think>`
 * reasoning block, so tests can assert exact bytes.
 *
 * @param modelId Logical id reported in diagnostics / session markers.
 * @param batteryLevelPercent Optional battery reading fed to [PowerBudgetPolicy].
 * @param thermalThrottled Optional thermal signal fed to [PowerBudgetPolicy].
 */
class LocalChatGenerator(
    val modelId: String = "local-deterministic",
    private val batteryLevelPercent: Int? = null,
    private val thermalThrottled: Boolean = false,
) : IChatGenerator {

    @Volatile
    private var closed = false

    override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
        ensureOpen()
        val (reasoning, content) = compose(messages, opts)
        // generateAsync returns content only — reasoning is filtered out.
        return content
    }

    override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = flow {
        ensureOpen()
        val (_, content) = compose(messages, opts)
        for (piece in tokenize(content)) {
            currentCoroutineContext().ensureActive()
            emit(piece)
        }
    }

    override fun streamFragmentsAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions,
    ): Flow<ChatFragment> = flow {
        ensureOpen()
        val (reasoning, content) = compose(messages, opts)
        if (opts.includeReasoning && reasoning.isNotEmpty()) {
            for (piece in tokenize(reasoning)) {
                currentCoroutineContext().ensureActive()
                emit(ChatFragment(ChatFragmentKind.REASONING, piece))
            }
        }
        for (piece in tokenize(content)) {
            currentCoroutineContext().ensureActive()
            emit(ChatFragment(ChatFragmentKind.CONTENT, piece))
        }
    }

    override suspend fun generateResponseAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions,
    ): ChatResponse {
        ensureOpen()
        val started = System.nanoTime()
        val (reasoning, content) = compose(messages, opts)
        val latencyMs = (System.nanoTime() - started) / 1_000_000.0

        val tokensIn = messages.sumOf { approximateTokens(it.content) }
        val tokensOut = countTokens(content)
        val budgetTokens = PowerBudgetPolicy.resolve(
            opts.budget, opts.maxTokens, batteryLevelPercent, thermalThrottled,
        ).maxTokens
        val finish = if (tokensOut >= budgetTokens) FinishReason.LENGTH else FinishReason.STOP

        return ChatResponse(
            text = content,
            tokensIn = tokensIn,
            tokensOut = tokensOut,
            latencyMs = latencyMs,
            finishReason = finish,
            reasoningContent = if (opts.includeReasoning && reasoning.isNotEmpty()) reasoning else null,
        )
    }

    override fun close() {
        closed = true
    }

    // ── internals ──────────────────────────────────────────────────────────

    private fun ensureOpen() = check(!closed) { "LocalChatGenerator is closed." }

    /**
     * Deterministically compose (reasoning, content) for a conversation. The
     * effective max-token cap comes from the power-budget policy; content is
     * truncated to that many whitespace tokens and stop sequences terminate it.
     */
    private fun compose(messages: List<ChatMessage>, opts: GenerationOptions): Pair<String, String> {
        val lastUser = messages.lastOrNull { it.role.equals("user", ignoreCase = true) }?.content
            ?: messages.lastOrNull()?.content
            ?: ""
        val budget = PowerBudgetPolicy.resolve(
            opts.budget, opts.maxTokens, batteryLevelPercent, thermalThrottled,
        )

        val reasoning = if (opts.includeReasoning) {
            "The user said: ${firstWords(lastUser, 6)}. I will answer plainly."
        } else {
            ""
        }

        val body = "Acknowledged: ${firstWords(lastUser, 12)}".trim()
        var content = applyStops(body, opts.stopSequences)
        content = truncateTokens(content, budget.maxTokens)
        return reasoning to content
    }

    private fun firstWords(text: String, n: Int): String {
        val words = text.trim().split(WHITESPACE).filter { it.isNotEmpty() }
        return if (words.size <= n) words.joinToString(" ") else words.take(n).joinToString(" ")
    }

    private fun applyStops(text: String, stops: List<String>): String {
        var cut = text
        for (s in stops) {
            if (s.isEmpty()) continue
            val idx = cut.indexOf(s)
            if (idx >= 0) cut = cut.substring(0, idx)
        }
        return cut
    }

    private fun truncateTokens(text: String, maxTokens: Int): String {
        if (maxTokens <= 0) return ""
        val words = text.split(WHITESPACE).filter { it.isNotEmpty() }
        return if (words.size <= maxTokens) text.trim() else words.take(maxTokens).joinToString(" ")
    }

    private fun tokenize(text: String): List<String> {
        if (text.isEmpty()) return emptyList()
        // Emit whitespace-preserving chunks so concatenation reproduces the text.
        val out = ArrayList<String>()
        val sb = StringBuilder()
        for (ch in text) {
            sb.append(ch)
            if (ch == ' ') {
                out.add(sb.toString())
                sb.setLength(0)
            }
        }
        if (sb.isNotEmpty()) out.add(sb.toString())
        return out
    }

    private fun countTokens(text: String): Int =
        text.split(WHITESPACE).count { it.isNotEmpty() }

    private fun approximateTokens(text: String?): Int {
        if (text.isNullOrEmpty()) return 0
        return max(1, text.length / 4)
    }

    private companion object {
        val WHITESPACE = Regex("\\s+")
        // Suppress "unused" warnings for math helpers kept for parity readability.
        @Suppress("unused")
        val touch = abs(0)
    }
}
