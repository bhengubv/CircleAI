// Inference.kt
//
// Kotlin port of Circle.AI.Inference portable layer.
//
// Covers:
//   GenerationOptions  — knobs for a single generation call
//   IChatGenerator     — contract for an on-device chat-style text generator
//
// Note: ChatMessage is declared in com.bhengubv.circleai.models.Models to avoid
// duplication. The inference layer imports it from there.

package com.bhengubv.circleai.inference

import com.bhengubv.circleai.models.ChatFragment
import com.bhengubv.circleai.models.ChatFragmentKind
import com.bhengubv.circleai.models.ChatMessage
import com.bhengubv.circleai.models.ChatResponse
import com.bhengubv.circleai.models.FinishReason
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import java.io.File
import java.time.Instant
import kotlin.math.max

// ---------------------------------------------------------------------------
// GenerationOptions
// ---------------------------------------------------------------------------

/**
 * Knobs for a single generation call.
 * All fields have sensible defaults and are immutable after construction.
 *
 * Mirrors CircleAI.Inference.GenerationOptions (C# is the spec): `MaxTokens`
 * defaults to 512, and the full knob set includes top-k, an optional seed, the
 * declarative [PowerBudget], and the prefix-cache opt-in.
 */
data class GenerationOptions(
    /** Maximum number of new tokens to produce. */
    val maxTokens: Int = 512,
    /** Sampling temperature. 0 = greedy; higher = more random. */
    val temperature: Float = 0.7f,
    /** Nucleus sampling cutoff (top-p). 1.0 disables. */
    val topP: Float = 0.9f,
    /** Top-k cutoff. 0 disables. */
    val topK: Int = 40,
    /** Optional RNG seed. `null` means non-deterministic. */
    val seed: Int? = null,
    /**
     * Optional substrings that will end generation when matched in the emitted
     * output (e.g. role-tag boundaries).
     */
    val stopSequences: List<String> = emptyList(),
    /** Optional system prompt to prepend before the conversation messages. */
    val systemPrompt: String = "",
    /**
     * Whether to surface the model's reasoning trace (Qwen3
     * `<think>…</think>`) on the call.
     *
     * When `true` (default) the generator separates reasoning from the final
     * answer: `ChatResponse.reasoningContent` gets the reasoning,
     * `ChatResponse.text` gets the answer. Streaming callers see fragments
     * tagged with `ChatFragmentKind.REASONING`.
     *
     * When `false` the generator still runs reasoning (this is per-call output
     * gating, NOT a thinking disable) but the reasoning text is dropped — only
     * the final answer reaches the caller. Use this for JSON-strict consumers.
     */
    val includeReasoning: Boolean = true,
    /**
     * (RT-11) Declarative power budget for this call. The runtime maps the
     * budget to context size, KV compression, and decode token limit. Default
     * [PowerBudget.Normal] auto-downgrades to `Low` below 15% battery. Pass
     * [PowerBudget.None] to honour [maxTokens] literally.
     */
    val budget: PowerBudget = PowerBudget.Normal,
    /**
     * (RT-06) Whether the runtime should consult the cross-session prefix cache
     * for a warm (modelId, systemPrompt) snapshot before resetting the model
     * handle. Default `false`.
     */
    val usePrefixCache: Boolean = false,
)

// ---------------------------------------------------------------------------
// IChatGenerator
// ---------------------------------------------------------------------------

/**
 * Contract for an on-device chat-style text generator. Implementations own
 * native model state and must be [AutoCloseable].
 */
interface IChatGenerator : AutoCloseable {
    /**
     * Generates a complete assistant reply for the given conversation.
     */
    suspend fun generateAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions = GenerationOptions()
    ): String

    /**
     * Streams the assistant reply token-by-token (or piece-by-piece) as it is
     * decoded. Each emitted string is the next chunk to append to the output —
     * callers should concatenate them in order. Content only — any reasoning
     * inside `<think>…</think>` is filtered out. Use [streamFragmentsAsync]
     * when you also need the reasoning stream.
     */
    fun streamAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions = GenerationOptions()
    ): Flow<String>

    /**
     * Fragment-aware streaming variant. Yields each piece tagged as either
     * [ChatFragmentKind.CONTENT] or [ChatFragmentKind.REASONING] so the caller
     * can route the model's `<think>` block into a separate `reasoning_content`
     * field (o1 / DeepSeek style).
     *
     * Default implementation wraps [streamAsync] and tags every chunk as
     * `CONTENT`; generators that surface reasoning override this method.
     */
    fun streamFragmentsAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions = GenerationOptions()
    ): Flow<ChatFragment> =
        streamAsync(messages, opts).map { ChatFragment(ChatFragmentKind.CONTENT, it) }

    /**
     * (RT-02) Save the current model session — KV cache + history — to [path]
     * so the conversation can survive an OOM kill and resume later via
     * [loadSessionAsync].
     *
     * Default implementation writes a portable marker file containing the
     * generator type name + a UTC timestamp so callers always get a
     * non-throwing round-trip. Native generators (Qwen, KimiVl) override to
     * call the MNN session primitives. Returns `true` on success.
     */
    suspend fun saveSessionAsync(path: String): Boolean {
        require(path.isNotBlank()) { "path required" }
        val marker = "circleai-session-marker\ntype:${this::class.qualifiedName}\nsaved_utc:${Instant.now()}\n"
        File(path).writeText(marker)
        return true
    }

    /**
     * (RT-02) Load a previously-saved session from [path]. Default
     * implementation verifies the marker file written by the default
     * [saveSessionAsync]. Native generators override to restore real KV-cache
     * state. Returns `true` on success.
     */
    suspend fun loadSessionAsync(path: String): Boolean {
        require(path.isNotBlank()) { "path required" }
        val f = File(path)
        if (!f.exists()) return false
        return f.readText().startsWith("circleai-session-marker")
    }

    /**
     * Structured-response variant: returns the assistant reply alongside token
     * counts, finish reason, and latency. Default implementation wraps
     * [generateAsync] with an approximate token count (word split) and
     * [FinishReason.STOP]; native generators override to report exact
     * native-reported values (and to surface [ChatResponse.reasoningContent]).
     */
    suspend fun generateResponseAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions = GenerationOptions(),
    ): ChatResponse {
        val started = System.nanoTime()
        val text = generateAsync(messages, opts)
        val latencyMs = (System.nanoTime() - started) / 1_000_000.0
        val tokensIn = messages.sumOf { approximateTokenCount(it.content) }
        val tokensOut = approximateTokenCount(text)
        return ChatResponse(
            text = text,
            tokensIn = tokensIn,
            tokensOut = tokensOut,
            latencyMs = latencyMs,
            finishReason = FinishReason.STOP,
        )
    }

    private fun approximateTokenCount(text: String?): Int {
        if (text.isNullOrEmpty()) return 0
        // Crude approximation — 1 token ~= 4 chars in English.
        return max(1, text.length / 4)
    }
}
