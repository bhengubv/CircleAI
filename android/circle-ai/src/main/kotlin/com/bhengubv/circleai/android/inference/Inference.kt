// Inference.kt
//
// Android/Kotlin port of Circle.AI.Inference portable layer.
//
// Covers:
//   GenerationOptions  — knobs for a single generation call
//   IChatGenerator     — contract for an on-device chat-style text generator
//
// Note: ChatMessage is declared in com.bhengubv.circleai.android.models.Models to avoid
// duplication. The inference layer imports it from there.

package com.bhengubv.circleai.android.inference

import com.bhengubv.circleai.android.models.ChatFragment
import com.bhengubv.circleai.android.models.ChatFragmentKind
import com.bhengubv.circleai.android.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

// ---------------------------------------------------------------------------
// GenerationOptions
// ---------------------------------------------------------------------------

/**
 * Knobs for a single generation call.
 * All fields have sensible defaults and are immutable after construction.
 */
data class GenerationOptions(
    /** Maximum number of new tokens to produce. */
    val maxTokens: Int = 2048,
    /** Sampling temperature. 0 = greedy; higher = more random. */
    val temperature: Float = 0.7f,
    /** Nucleus sampling cutoff (top-p). 1.0 disables. */
    val topP: Float = 0.9f,
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
}
