// LanguagesRegistry.kt
//
// Every language the system knows, indexed for lookup; a detector that detects
// nothing and says so; and the script-normaliser seam.
//
// Ported from src/CircleAI.Languages/{DefaultLanguageRegistry, NullLanguageDetector,
// IScriptNormaliser}.cs.

package com.bhengubv.circleai.languages

import java.util.Locale

/**
 * Turns text written in one script into something a caller can work with, and
 * says what it did.
 *
 * A seam rather than an implementation because the right answer is per-script:
 * Ethiopic needs a syllabary walk, Arabic needs bidi handling, and a single
 * "normalise" that tried to do both would do neither.
 */
interface IScriptNormaliser {
    fun normalise(text: String, targetLanguage: LanguageTag? = null): ScriptNormalisationResult

    /** A best-effort Latin rendering, for places that can only show ASCII. */
    fun toAsciiApproximation(text: String): String

    /**
     * Whether any of this text runs right to left. CHECKED rather than assumed
     * from the language tag: a Hebrew name inside an English sentence is still
     * right to left, and the tag says "en".
     */
    fun containsRtl(text: String): Boolean
}

/** Every language the system knows, indexed for lookup. */
class DefaultLanguageRegistry : ILanguageRegistry {

    // Lower-cased keys, because a BCP-47 tag is case-insensitive and "en-za"
    // arriving lower-cased must not read as an unknown language.
    private val byTag: Map<String, LanguageTag> =
        KnownLanguages.All.associateBy { it.bcpTag.lowercase(Locale.ROOT) }

    private val byRegion: Map<String, List<LanguageTag>> =
        KnownLanguages.All.groupBy { it.isoRegion.lowercase(Locale.ROOT) }

    override fun getByBcpTag(bcpTag: String): LanguageTag? =
        byTag[bcpTag.lowercase(Locale.ROOT)]

    override fun getAll(): List<LanguageTag> = KnownLanguages.All

    override fun getForRegion(isoRegion: String): List<LanguageTag> =
        byRegion[isoRegion.lowercase(Locale.ROOT)].orEmpty()

    override fun isSupported(bcpTag: String): Boolean =
        byTag.containsKey(bcpTag.lowercase(Locale.ROOT))
}

/**
 * Detects nothing, and says so.
 *
 * Confidence 0 and `unknown`, never a plausible-looking guess: a detector that
 * quietly answers "English" makes every downstream choice wrong in a way that
 * looks like a working system.
 */
object NullLanguageDetector : ILanguageDetector {

    override suspend fun detectAsync(text: String): DetectionResult =
        DetectionResult(LanguageTag.Unknown, 0f, false)

    override suspend fun detectMultipleAsync(text: String, maxResults: Int): List<DetectionResult> =
        listOf(DetectionResult(LanguageTag.Unknown, 0f, false))
}
