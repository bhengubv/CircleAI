// LanguagesTranslation.kt
//
// Kotlin port of CircleAI.Languages.Translation — the C# reference is the EXACT
// spec. On-device translation: meaning, not just words, via the on-device LLM.
// No network call, no data leaving the device.
//
// Covers (C# file -> Kotlin type):
//   TranslationTypes.cs      -> TranslationMode, TranslationRequest,
//                               TranslationResult, ConversationTurn
//   ITranslationEngine.cs    -> ITranslationEngine
//   ILiveTranslator.cs       -> ILiveTranslator
//   LlmTranslationEngine.cs  -> LlmTranslationEngine
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `with { … }` -> `copy(...)`.
//   * C# `IAsyncEnumerable<T>` -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `DateTimeOffset.UtcNow` -> `java.time.Instant.now()`.
//   * Backed by `inference.IChatGenerator` (already ported): `generateAsync`
//     returns the reply String; `streamAsync` returns `Flow<String>`.
//   * `ChatMessage` is `models.ChatMessage(id, role, content, createdAt)`; the
//     C# `new ChatMessage("user", text)` maps to a fresh id + role="user".
//   * `IsLanguagePairSupportedAsync` always returns `true` — the on-device LLM
//     handles any pair it was trained on.

package com.bhengubv.circleai.languagestranslation

import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.UUID

// =====================================================================
// TranslationTypes (TranslationTypes.cs)
// =====================================================================

enum class TranslationMode { Standard, Conversational, Document, Technical, Legal, Medical }

/** A request to translate a piece of text between two languages. */
data class TranslationRequest(
    val text: String,
    val sourceBcpTag: String,
    val targetBcpTag: String,
    val mode: TranslationMode = TranslationMode.Standard,
    val contextHint: String? = null,
)

/** Result of a completed translation. */
data class TranslationResult(
    val originalText: String,
    val translatedText: String,
    val sourceBcpTag: String,
    val targetBcpTag: String,
    val confidence: Float,
    val translatedAt: Instant,
)

/** One turn in a live bidirectional conversation. */
data class ConversationTurn(
    val speakerBcpTag: String,
    val originalText: String,
    val translatedText: String?,
    val timestamp: Instant,
)

// =====================================================================
// ITranslationEngine (ITranslationEngine.cs)
// =====================================================================

/**
 * On-device translation engine. No network call, no data leaving the device.
 * Translates meaning — not just words — using the on-device LLM.
 */
interface ITranslationEngine {
    suspend fun translate(request: TranslationRequest): TranslationResult

    fun streamTranslate(request: TranslationRequest): Flow<String>

    suspend fun isLanguagePairSupported(sourceBcpTag: String, targetBcpTag: String): Boolean
}

// =====================================================================
// ILiveTranslator (ILiveTranslator.cs)
// =====================================================================

/**
 * Bidirectional live conversation translator. Party A speaks [partyABcpTag];
 * party B speaks [partyBBcpTag]. Each turn is translated in real-time so both
 * parties hear each other. Runs entirely on-device. No API call. No data leaves
 * the device. Example: you speak Zulu, they hear English — and vice versa.
 */
interface ILiveTranslator : ITranslationEngine {
    fun streamConversation(
        inputStream: Flow<ConversationTurn>,
        partyABcpTag: String,
        partyBBcpTag: String,
    ): Flow<ConversationTurn>
}

// =====================================================================
// LlmTranslationEngine (LlmTranslationEngine.cs)
// =====================================================================

/**
 * [ITranslationEngine] backed by the on-device LLM via [IChatGenerator]. All
 * processing is on-device — no API calls, no data leaving the device.
 */
class LlmTranslationEngine(private val generator: IChatGenerator) : ILiveTranslator {

    override suspend fun translate(request: TranslationRequest): TranslationResult {
        val messages = listOf(userMessage(buildPrompt(request)))
        val translated = generator.generateAsync(messages)

        return TranslationResult(
            originalText = request.text,
            translatedText = translated.trim(),
            sourceBcpTag = request.sourceBcpTag,
            targetBcpTag = request.targetBcpTag,
            confidence = 0.9f,
            translatedAt = Instant.now(),
        )
    }

    override fun streamTranslate(request: TranslationRequest): Flow<String> = flow {
        val messages = listOf(userMessage(buildPrompt(request)))
        generator.streamAsync(messages).collect { token -> emit(token) }
    }

    override suspend fun isLanguagePairSupported(
        sourceBcpTag: String,
        targetBcpTag: String,
    ): Boolean = true // On-device LLM handles any pair it was trained on.

    override fun streamConversation(
        inputStream: Flow<ConversationTurn>,
        partyABcpTag: String,
        partyBBcpTag: String,
    ): Flow<ConversationTurn> = flow {
        inputStream.collect { turn ->
            val targetTag = if (turn.speakerBcpTag == partyABcpTag) partyBBcpTag else partyABcpTag

            val req = TranslationRequest(
                text = turn.originalText,
                sourceBcpTag = turn.speakerBcpTag,
                targetBcpTag = targetTag,
                mode = TranslationMode.Conversational,
            )

            val result = translate(req)
            emit(turn.copy(translatedText = result.translatedText))
        }
    }

    private companion object {
        fun userMessage(content: String): ChatMessage =
            ChatMessage(id = UUID.randomUUID().toString(), role = "user", content = content)

        fun buildPrompt(r: TranslationRequest): String =
            "Translate the following text from ${r.sourceBcpTag} to ${r.targetBcpTag}. " +
                "Mode: ${r.mode}. Preserve meaning and cultural context, not just literal words. " +
                (if (r.contextHint != null) "Context: ${r.contextHint}. " else "") +
                "Return only the translation with no explanation.\n\n${r.text}"
    }
}
