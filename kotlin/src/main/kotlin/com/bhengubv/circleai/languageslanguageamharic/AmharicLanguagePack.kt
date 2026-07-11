// AmharicLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Amharic — the C# reference is the
// EXACT spec. Amharic language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.

package com.bhengubv.circleai.languageslanguageamharic

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Amharic language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in Amharic (አማርኛ).
 */
object AmharicLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "am",
        displayName = "Amharic",
        nativeName = "አማርኛ",
        primaryRegion = "ET",
        spokenInRegions = listOf("ET"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "ሰላም",
        "hello (respectful)" to "ጤና ይስጥልኝ",
        "good morning" to "እንደምን አደርክ",
        "good evening" to "መልካም ምሽት",
        "goodbye" to "ቻው",
        "thank you" to "አመሰግናለሁ",
        "please" to "እባክህ",
        "yes" to "አዎ",
        "no" to "አይ",
        "sorry" to "ይቅርታ",
        "how are you" to "እንዴት ነህ",
        "I am fine" to "ደህና ነኝ",
        "water" to "ውሃ",
        "food" to "ምግብ",
        "family" to "ቤተሰብ",
        "friend" to "ጓደኛ",
        "love" to "ፍቅር",
        "mother" to "እናት",
        "father" to "አባት",
        "child" to "ልጅ",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'ጤና ይስጥልኝ' in the morning. Show respect to elders.",
                listOf("ጤና ይስጥልኝ", "መልካም ምሽት"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Amharic speakers. " +
            "Respond in Amharic (አማርኛ) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "ጤና ይስጥልኝ"
            else -> "መልካም ምሽት"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "am",
        "region" to "ET",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
