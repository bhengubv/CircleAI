// AfrikaansLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Afrikaans — the C# reference is the
// EXACT spec. Afrikaans language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.

package com.bhengubv.circleai.languageslanguageafrikaans

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Afrikaans language pack for Circle AI. Provides idiomatic expressions,
 * cultural context, and prompt tuning to make the AI reason naturally in
 * Afrikaans.
 */
object AfrikaansLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "af",
        displayName = "Afrikaans",
        nativeName = "Afrikaans",
        primaryRegion = "ZA",
        spokenInRegions = listOf("ZA", "NA"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Hallo",
        "good morning" to "Goeie môre",
        "good afternoon" to "Goeie middag",
        "good evening" to "Goeie naand",
        "goodbye" to "Totsiens",
        "thank you" to "Dankie",
        "please" to "Asseblief",
        "yes" to "Ja",
        "no" to "Nee",
        "sorry" to "Jammer",
        "how are you" to "Hoe gaan dit",
        "I am fine" to "Dit gaan goed",
        "water" to "water",
        "food" to "kos",
        "family" to "familie",
        "friend" to "vriend",
        "love" to "liefde",
        "mother" to "ma",
        "father" to "pa",
        "child" to "kind",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Goeie môre' in the morning. Show respect to elders.",
                listOf("Goeie môre", "Totsiens"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Afrikaans speakers. " +
            "Respond in Afrikaans (Afrikaans) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Goeie môre"
            else -> "Totsiens"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "af",
        "region" to "ZA",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
