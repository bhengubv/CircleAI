// isiZuluLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.isiZulu — the C# reference is the
// EXACT spec. isiZulu language pack: idiomatic expressions, cultural context,
// and prompt tuning so the AI reasons naturally in isiZulu.
//
// Fidelity notes:
//   * C# `sealed class` + `static readonly Instance` -> Kotlin `object`.
//   * C# `StringComparer.OrdinalIgnoreCase` idiom/notes maps -> lookup via
//     lower-cased keys (Locale.ROOT), preserving the C# case-insensitive get.
//   * C# `Version(1,0)` PackVersion -> `"1.0"`.
//   * Idiom values, cultural notes, greeting and locale hints are copied
//     verbatim from the C# pack.

package com.bhengubv.circleai.languageslanguageisizulu

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * isiZulu language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in isiZulu.
 */
object isiZuluLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "zu",
        displayName = "isiZulu",
        nativeName = "isiZulu",
        primaryRegion = "ZA",
        spokenInRegions = listOf("ZA"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Sawubona",
        "hello (plural)" to "Sanibonani",
        "goodbye" to "Sala kahle",
        "goodbye (sleep)" to "Lala kahle",
        "thank you" to "Ngiyabonga",
        "thank you (pl)" to "Siyabonga",
        "please" to "Ngicela",
        "yes" to "Yebo",
        "no" to "Cha",
        "how are you" to "Unjani",
        "I am fine" to "Ngikhona",
        "sorry" to "Uxolo",
        "family" to "umndeni",
        "love" to "uthando",
        "water" to "amanzi",
        "food" to "ukudla",
        "mother" to "umama",
        "father" to "ubaba",
        "child" to "ingane",
        "friend" to "umngani",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Sawubona' in the morning. Show respect to elders.",
                listOf("Sawubona", "Lala kahle"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for isiZulu speakers. " +
            "Respond in isiZulu (isiZulu) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Sawubona"
            else -> "Lala kahle"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "zu",
        "region" to "ZA",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
