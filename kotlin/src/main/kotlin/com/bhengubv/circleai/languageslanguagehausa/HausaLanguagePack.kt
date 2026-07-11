// HausaLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Hausa — the C# reference is the
// EXACT spec. Hausa language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.

package com.bhengubv.circleai.languageslanguagehausa

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Hausa language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in Hausa.
 */
object HausaLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "ha",
        displayName = "Hausa",
        nativeName = "Hausa",
        primaryRegion = "NG",
        spokenInRegions = listOf("NG", "NE", "GH"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Sannu",
        "good morning" to "Barka da safe",
        "good afternoon" to "Barka da rana",
        "good evening" to "Barka da yamma",
        "goodbye" to "Sai anjima",
        "see you later" to "Sai gobe",
        "thank you" to "Na gode",
        "please" to "Don Allah",
        "yes" to "Eh",
        "no" to "A'a",
        "sorry" to "Yi hakuri",
        "how are you" to "Yaya kake",
        "I am fine" to "Lafiya lau",
        "water" to "ruwa",
        "food" to "abinci",
        "family" to "iyali",
        "friend" to "aboki",
        "love" to "kauna",
        "mother" to "uwa",
        "father" to "uba",
        "child" to "yaro",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Barka da safe' in the morning. Show respect to elders.",
                listOf("Barka da safe", "Sai anjima"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Hausa speakers. " +
            "Respond in Hausa (Hausa) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Barka da safe"
            else -> "Sai anjima"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "ha",
        "region" to "NG",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
