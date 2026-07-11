// ArabicLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Arabic — the C# reference is the
// EXACT spec. Arabic language pack: idioms, cultural context, prompt tuning.
// C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive idiom/notes
// lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values copied verbatim.
// Note: locale hint rtl = "true".

package com.bhengubv.circleai.languageslanguagearabic

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Arabic language pack for Circle AI. Provides idiomatic expressions, cultural
 * context, and prompt tuning to make the AI reason naturally in Arabic (العربية).
 */
object ArabicLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "ar",
        displayName = "Arabic",
        nativeName = "العربية",
        primaryRegion = "SA",
        spokenInRegions = listOf("SA", "EG", "MA", "AE"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "مرحبا",
        "peace be upon you" to "السلام عليكم",
        "good morning" to "صباح الخير",
        "good evening" to "مساء الخير",
        "goodbye" to "مع السلامة",
        "thank you" to "شكرا",
        "please" to "من فضلك",
        "yes" to "نعم",
        "no" to "لا",
        "sorry" to "آسف",
        "how are you" to "كيف حالك",
        "I am fine" to "أنا بخير",
        "water" to "ماء",
        "food" to "طعام",
        "family" to "عائلة",
        "friend" to "صديق",
        "love" to "حب",
        "mother" to "أم",
        "father" to "أب",
        "child" to "طفل",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'صباح الخير' in the morning. Show respect to elders.",
                listOf("صباح الخير", "مساء الخير"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Arabic speakers. " +
            "Respond in Arabic (العربية) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "صباح الخير"
            else -> "مساء الخير"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "ar",
        "region" to "SA",
        "rtl" to "true",
        "date_format" to "dd/MM/yyyy",
    )
}
