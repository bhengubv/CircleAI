// HostingOptions.kt
//
// Two host-facing options, and the default on one of them is the whole point.
//
// Ported from src/CircleAI.Hosting/{AIOptions, VoiceOptions}.cs.

package com.bhengubv.circleai.hosting

/**
 * Whether context enrichment applies when the CALLER owns the system turn.
 *
 * The default is [Always], and the default is the point. Before this existed the
 * behaviour was effectively [OnlyWhenAbsent] and undocumented: a host that set
 * its own system prompt silently lost persona, device context, recall and skill
 * context. That presents as "the assistant forgot", which nobody debugs as a
 * dropped feature — they debug it as a bad model.
 *
 * Silently losing memory grounding is worse than receiving grounding you did not
 * explicitly ask for, and either way the caller's own instructions still lead
 * and are never rewritten.
 */
enum class SystemPromptEnrichment {
    /** Persona, device context, recall and skill context are appended AFTER the
     *  caller's own system prompt. */
    Always,

    /** Enrichment applies only when the caller supplies NO system turn. Choose
     *  this for full control, accepting that recall and persona are not
     *  injected. */
    OnlyWhenAbsent
}

/** How a host configures the voice loop. */
data class VoiceOptions(
    /** The phrase, lower-cased. Matching is case-insensitive downstream, but the
     *  stored form is normalised here so two hosts writing "Hey B" and "hey b"
     *  do not produce two configurations of the same thing. */
    val wakeWord: String = "hey b",

    /** 16 kHz, which is what every wake and speech model in the catalogue was
     *  trained at. A host that captures at 44.1 and does not say so gets
     *  features computed against the wrong time base — the model runs, burns
     *  battery, and never fires. */
    val sampleRateHz: Int = 16_000,

    /** OFF by default. A microphone that opens itself the moment a library is
     *  constructed is not a decision a library gets to make. */
    val autoStart: Boolean = false,

    /** "null" by default: silent, so a host that never wires a real engine gets
     *  a working pipeline with no audio rather than a crash at the first reply. */
    val ttsBackend: String = "null",

    /**
     * How long a person may pause before the turn is treated as finished.
     *
     * 800 ms is a deliberate compromise: shorter and the assistant interrupts
     * somebody thinking mid-sentence; longer and every exchange feels slow.
     */
    val endOfSpeechSilenceMs: Int = 800
) {
    init {
        require(sampleRateHz > 0) { "sampleRateHz must be positive." }
    }

    /** The normalised form, so two hosts spelling the phrase differently agree. */
    val normalisedWakeWord: String get() = wakeWord.trim().lowercase()
}

/**
 * The ids a cloud-fallback chat provider is registered under.
 *
 * Named constants rather than literals scattered through registration code: a
 * typo in one of these is a provider that is configured, present, and never
 * selected, with nothing anywhere reporting a problem.
 */
object ProviderIds {
    const val OPEN_AI = "openai"
    const val ANTHROPIC = "anthropic"
    const val GEMINI = "gemini"
    const val GROQ = "groq"
    const val CEREBRAS = "cerebras"
    const val TOGETHER = "together"
    const val DEEP_SEEK = "deepseek"

    val all = listOf(OPEN_AI, ANTHROPIC, GEMINI, GROQ, CEREBRAS, TOGETHER, DEEP_SEEK)
}
