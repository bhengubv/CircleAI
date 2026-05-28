// Languages.kt
//
// Android/Kotlin port of Circle.AI.Languages portable layer.
//
// Covers:
//   WritingSystem        — enum: Latin | Arabic | Ethiopic | Geez | Devanagari | Han | Cyrillic | Hebrew | Greek | Other
//   LanguageTag          — BCP-47 tag enriched with display metadata
//   DetectionResult      — result of language detection
//   ScriptNormalisationResult — result of script normalisation
//   KnownLanguages       — static registry of all 20 shipped languages
//   ILanguageDetector    — detects the BCP-47 language of a text
//   ILanguageRegistry    — registry of all supported BCP-47 tags

package com.bhengubv.circleai.android.languages

// ---------------------------------------------------------------------------
// WritingSystem
// ---------------------------------------------------------------------------

/** The writing system / script used by a language. */
enum class WritingSystem {
    Latin, Arabic, Ethiopic, Geez, Devanagari,
    Han, Cyrillic, Hebrew, Greek, Other
}

// ---------------------------------------------------------------------------
// LanguageTag
// ---------------------------------------------------------------------------

/**
 * A BCP-47 language tag enriched with display metadata.
 */
data class LanguageTag(
    /** IETF BCP-47 language tag (e.g. "zu", "en", "ar"). */
    val bcpTag: String,
    /** English display name (e.g. "isiZulu", "English"). */
    val displayName: String,
    /** Native display name (e.g. "isiZulu", "العربية"). */
    val nativeName: String,
    /** The primary writing system used by this language. */
    val script: WritingSystem,
    /** True if this language is written right-to-left. */
    val isRtl: Boolean,
    /** ISO 3166-1 alpha-2 primary region code (e.g. "ZA", "GB"). */
    val isoRegion: String
) {
    companion object {
        /** Sentinel value returned when language detection fails. */
        val Unknown: LanguageTag = LanguageTag("und", "Unknown", "Unknown", WritingSystem.Latin, false, "")
    }
}

// ---------------------------------------------------------------------------
// DetectionResult
// ---------------------------------------------------------------------------

/** Result of language detection. */
data class DetectionResult(
    /** The detected language tag. */
    val language: LanguageTag,
    /** Confidence score 0.0–1.0. */
    val confidence: Float,
    /** True if the detection is considered reliable. */
    val isReliable: Boolean
)

// ---------------------------------------------------------------------------
// ScriptNormalisationResult
// ---------------------------------------------------------------------------

/** Result of script normalisation. */
data class ScriptNormalisationResult(
    val input: String,
    val normalised: String,
    val detectedLanguage: LanguageTag
)

// ---------------------------------------------------------------------------
// KnownLanguages
// ---------------------------------------------------------------------------

/** Static registry of every language Circle AI ships support for. */
object KnownLanguages {

    // ── Africa ────────────────────────────────────────────────────────────────
    val IsiZulu   = LanguageTag("zu",  "isiZulu",    "isiZulu",       WritingSystem.Latin,       false, "ZA")
    val Sesotho   = LanguageTag("st",  "Sesotho",    "Sesotho",       WritingSystem.Latin,       false, "ZA")
    val Afrikaans = LanguageTag("af",  "Afrikaans",  "Afrikaans",     WritingSystem.Latin,       false, "ZA")
    val Swahili   = LanguageTag("sw",  "Swahili",    "Kiswahili",     WritingSystem.Latin,       false, "KE")
    val Hausa     = LanguageTag("ha",  "Hausa",      "Hausa",         WritingSystem.Latin,       false, "NG")
    val Amharic   = LanguageTag("am",  "Amharic",    "አማርኛ",          WritingSystem.Ethiopic,    false, "ET")
    val Yoruba    = LanguageTag("yo",  "Yoruba",     "Yorùbá",        WritingSystem.Latin,       false, "NG")
    val Igbo      = LanguageTag("ig",  "Igbo",       "Igbo",          WritingSystem.Latin,       false, "NG")
    val Xhosa     = LanguageTag("xh",  "isiXhosa",   "isiXhosa",      WritingSystem.Latin,       false, "ZA")
    val Sepedi    = LanguageTag("nso", "Sepedi",     "Sepedi",        WritingSystem.Latin,       false, "ZA")
    val Setswana  = LanguageTag("tn",  "Setswana",   "Setswana",      WritingSystem.Latin,       false, "ZA")
    val Somali    = LanguageTag("so",  "Somali",     "Soomaali",      WritingSystem.Latin,       false, "SO")
    val Oromo     = LanguageTag("om",  "Oromo",      "Afaan Oromoo",  WritingSystem.Latin,       false, "ET")

    // ── Middle East & North Africa ─────────────────────────────────────────────
    val Arabic    = LanguageTag("ar",  "Arabic",     "العربية",       WritingSystem.Arabic,      true,  "SA")

    // ── Europe & Americas ──────────────────────────────────────────────────────
    val English    = LanguageTag("en", "English",    "English",       WritingSystem.Latin,       false, "GB")
    val Portuguese = LanguageTag("pt", "Portuguese", "Português",     WritingSystem.Latin,       false, "PT")
    val French     = LanguageTag("fr", "French",     "Français",      WritingSystem.Latin,       false, "FR")
    val Spanish    = LanguageTag("es", "Spanish",    "Español",       WritingSystem.Latin,       false, "ES")

    // ── Asia ───────────────────────────────────────────────────────────────────
    val Mandarin   = LanguageTag("zh", "Mandarin",   "中文",           WritingSystem.Han,         false, "CN")
    val Hindi      = LanguageTag("hi", "Hindi",      "हिन्दी",         WritingSystem.Devanagari,  false, "IN")

    /** All languages shipped with Circle AI (declaration order — 20 entries). */
    val all: List<LanguageTag> = listOf(
        IsiZulu, Sesotho, Afrikaans, Swahili, Hausa, Amharic,
        Yoruba, Igbo, Xhosa, Sepedi, Setswana, Somali, Oromo,
        Arabic,
        English, Portuguese, French, Spanish,
        Mandarin, Hindi
    )
}

// ---------------------------------------------------------------------------
// ILanguageDetector
// ---------------------------------------------------------------------------

/** Detects the BCP-47 language of a piece of text. */
interface ILanguageDetector {
    /**
     * Detects the most likely language.
     * Returns [LanguageTag.Unknown] with confidence 0 when detection fails.
     */
    suspend fun detectAsync(text: String): DetectionResult

    /**
     * Returns up to [maxResults] candidates ranked by confidence.
     */
    suspend fun detectMultipleAsync(text: String, maxResults: Int = 3): List<DetectionResult>
}

// ---------------------------------------------------------------------------
// ILanguageRegistry
// ---------------------------------------------------------------------------

/** Registry of all BCP-47 language tags that Circle AI understands. */
interface ILanguageRegistry {
    /** Returns the [LanguageTag] for the given BCP-47 tag, or null if not found. */
    fun getByBcpTag(bcpTag: String): LanguageTag?

    /** Returns all known language tags. */
    fun getAll(): List<LanguageTag>

    /** Returns all languages whose primary region matches [isoRegion]. */
    fun getForRegion(isoRegion: String): List<LanguageTag>

    /** Returns true if the given BCP-47 tag is in the registry. */
    fun isSupported(bcpTag: String): Boolean
}
