// SpeechCloud.kt
//
// Kotlin port of CircleAI.Speech.Cloud.IVoiceIntentRouter + its two impls —
// the C# reference (KeywordVoiceIntentRouter.cs) is the EXACT spec. A generic
// regex-based voice intent router: matches a transcript against a host-supplied
// ordered list of intent definitions, first hit wins, falls through to a
// caller-defined fallback intent (typically "ask-ai") when nothing matches.
//
// Design fidelity notes:
//   * C# `record`                          -> Kotlin `data class`.
//   * C# `System.Text.RegularExpressions.Regex` -> `kotlin.text.Regex`.
//   * C# `ValueTask<T>`                    -> `suspend fun`.
//   * C# `IReadOnlyDictionary<string,string>` -> `Map<String,String>`.
//   * Named-group extraction: skips the implicit numeric "0" full-match group and
//     only surfaces named groups, exactly as the C# does; captured values are
//     trimmed and empty captures dropped.
//
// The cloud STT/TTS backends (Azure, Deepgram, OpenAI, ...) are separate injected
// engines and are NOT part of this work unit — only the rule-based router is
// hermetic and portable, so only it is ported here.

package com.bhengubv.circleai.speechcloud

/**
 * One named intent the router recognises. [pattern] is matched against the
 * trimmed transcript; on a hit, every named group is exposed in
 * [VoiceIntentMatch.captures]. Mirrors C# `VoiceIntent`.
 */
data class VoiceIntent(val name: String, val pattern: Regex)

/** One match outcome. Mirrors C# `VoiceIntentMatch`. */
data class VoiceIntentMatch(
    val intentName: String,
    val transcript: String,
    val captures: Map<String, String>,
)

/**
 * Maps a transcript to one of a host-supplied set of intents. Rule-based,
 * sub-millisecond per attempt, hermetic. Mirrors C# `IVoiceIntentRouter`.
 */
interface IVoiceIntentRouter {
    /** Backend self-identification — "keyword", "null". */
    val backendId: String

    /**
     * Match the transcript against the configured intents. Returns a match for
     * the first hitting intent, or for the fallback intent when nothing matches
     * (whose [VoiceIntentMatch.captures] is empty).
     */
    suspend fun routeAsync(transcript: String): VoiceIntentMatch
}

/**
 * Default [IVoiceIntentRouter]. Takes an ordered list of intents plus a fallback
 * name (typically "ask-ai") and tries each pattern in order. Mirrors C#
 * `KeywordVoiceIntentRouter`.
 */
class KeywordVoiceIntentRouter(
    intents: Iterable<VoiceIntent>,
    private val fallbackIntentName: String = "ask-ai",
) : IVoiceIntentRouter {

    private val intents: List<VoiceIntent> = intents.toList()

    init {
        require(fallbackIntentName.isNotBlank()) { "fallbackIntentName" }
    }

    override val backendId: String get() = "keyword"

    override suspend fun routeAsync(transcript: String): VoiceIntentMatch {
        val text = transcript.trim()
        if (text.isEmpty()) {
            return VoiceIntentMatch(
                intentName = fallbackIntentName,
                transcript = "",
                captures = emptyMap(),
            )
        }

        for (intent in intents) {
            val match = intent.pattern.find(text) ?: continue

            val captures = LinkedHashMap<String, String>()
            for (name in groupNames(intent.pattern)) {
                // Skip the implicit "0" group (the full match) — only surface
                // named groups.
                if (name.toIntOrNull() != null) continue
                val g = try {
                    match.groups[name]
                } catch (_: IllegalArgumentException) {
                    // Pattern has no such named group in this platform view; skip.
                    null
                }
                val value = g?.value
                if (value != null && value.isNotEmpty()) {
                    captures[name] = value.trim()
                }
            }

            return VoiceIntentMatch(
                intentName = intent.name,
                transcript = text,
                captures = captures,
            )
        }

        return VoiceIntentMatch(
            intentName = fallbackIntentName,
            transcript = text,
            captures = emptyMap(),
        )
    }

    private companion object {
        // Extract the named-group identifiers declared in a regex pattern, e.g.
        // "(?<query>.+)" -> ["query"]. Java/Kotlin Regex does not expose group
        // names, so parse them from the pattern text — the same set the C#
        // Regex.GetGroupNames() would surface (named groups only).
        private val NAMED_GROUP = Regex("""\(\?<([a-zA-Z][a-zA-Z0-9]*)>""")

        fun groupNames(pattern: Regex): List<String> =
            NAMED_GROUP.findAll(pattern.pattern).map { it.groupValues[1] }.toList()
    }
}

/** Empty router — always returns the fallback intent. Mirrors C# `NullVoiceIntentRouter`. */
class NullVoiceIntentRouter private constructor() : IVoiceIntentRouter {
    companion object {
        val Instance = NullVoiceIntentRouter()
    }

    override val backendId: String get() = "null"

    override suspend fun routeAsync(transcript: String): VoiceIntentMatch =
        VoiceIntentMatch(
            intentName = "ask-ai",
            transcript = transcript,
            captures = emptyMap(),
        )
}
