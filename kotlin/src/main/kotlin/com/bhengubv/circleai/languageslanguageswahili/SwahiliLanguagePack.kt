// SwahiliLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Swahili — the C# reference is the
// EXACT spec. Swahili language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.

package com.bhengubv.circleai.languageslanguageswahili

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Swahili language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in Swahili
 * (Kiswahili).
 */
object SwahiliLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "sw",
        displayName = "Swahili",
        nativeName = "Kiswahili",
        primaryRegion = "KE",
        spokenInRegions = listOf("KE", "TZ", "UG"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Habari",
        "hello (informal)" to "Mambo",
        "good morning" to "Habari ya asubuhi",
        "good evening" to "Habari ya jioni",
        "goodbye" to "Kwaheri",
        "goodbye (sleep)" to "Usiku mwema",
        "thank you" to "Asante",
        "thank you (very)" to "Asante sana",
        "please" to "Tafadhali",
        "yes" to "Ndio",
        "no" to "Hapana",
        "how are you" to "Habari yako",
        "I am fine" to "Nzuri",
        "sorry" to "Pole",
        "family" to "familia",
        "love" to "upendo",
        "water" to "maji",
        "food" to "chakula",
        "mother" to "mama",
        "father" to "baba",
        "child" to "mtoto",
        "friend" to "rafiki",
        "no problem" to "Hakuna matata",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Habari' in the morning. Show respect to elders.",
                listOf("Habari", "Usiku mwema"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Swahili speakers. " +
            "Respond in Swahili (Kiswahili) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Habari"
            else -> "Usiku mwema"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "sw",
        "region" to "KE",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
