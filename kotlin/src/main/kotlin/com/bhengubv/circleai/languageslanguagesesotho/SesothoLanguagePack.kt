// SesothoLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Sesotho — the C# reference is the
// EXACT spec. Sesotho language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.

package com.bhengubv.circleai.languageslanguagesesotho

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Sesotho language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in Sesotho.
 */
object SesothoLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "st",
        displayName = "Sesotho",
        nativeName = "Sesotho",
        primaryRegion = "ZA",
        spokenInRegions = listOf("ZA", "LS"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Dumela",
        "hello (plural)" to "Dumelang",
        "goodbye" to "Sala hantle",
        "goodbye (sleep)" to "Robala hantle",
        "thank you" to "Kea leboha",
        "please" to "Ka kopo",
        "yes" to "E",
        "no" to "Che",
        "how are you" to "O phela joang",
        "I am fine" to "Ke phela hantle",
        "sorry" to "Tshwarelo",
        "family" to "lelapa",
        "love" to "lerato",
        "water" to "metsi",
        "food" to "dijo",
        "mother" to "'me",
        "father" to "ntate",
        "child" to "ngwana",
        "friend" to "motswalle",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Dumela' in the morning. Show respect to elders.",
                listOf("Dumela", "Robala hantle"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Sesotho speakers. " +
            "Respond in Sesotho (Sesotho) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Dumela"
            else -> "Robala hantle"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "st",
        "region" to "ZA",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
