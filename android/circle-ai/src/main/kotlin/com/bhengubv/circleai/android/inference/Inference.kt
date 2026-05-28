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

import com.bhengubv.circleai.android.models.ChatMessage
import kotlinx.coroutines.flow.Flow

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
    val systemPrompt: String = ""
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
     * callers should concatenate them in order.
     */
    fun streamAsync(
        messages: List<ChatMessage>,
        opts: GenerationOptions = GenerationOptions()
    ): Flow<String>
}
