// PortugueseLanguagePack.kt
//
// Kotlin port of CircleAI.Languages.Language.Portuguese — the C# reference is
// the EXACT spec. Portuguese language pack: idioms, cultural context, prompt
// tuning. C# `sealed class`+`Instance` -> Kotlin `object`; case-insensitive
// idiom/notes lookup via lower-cased keys; `Version(1,0)` -> "1.0". Values
// copied verbatim.

package com.bhengubv.circleai.languageslanguageportuguese

import com.bhengubv.circleai.languageslanguage.CulturalNote
import com.bhengubv.circleai.languageslanguage.ILanguagePack
import com.bhengubv.circleai.languageslanguage.LanguagePackMetadata
import java.util.Locale

/**
 * Portuguese language pack for Circle AI. Provides idiomatic expressions,
 * cultural context, and prompt tuning to make the AI reason naturally in
 * Portuguese (Português).
 */
object PortugueseLanguagePack : ILanguagePack {

    override val metadata: LanguagePackMetadata = LanguagePackMetadata(
        bcpTag = "pt",
        displayName = "Portuguese",
        nativeName = "Português",
        primaryRegion = "PT",
        spokenInRegions = listOf("PT", "BR", "MZ", "AO"),
        packVersion = "1.0",
    )

    private val idioms: Map<String, String> = mapOf(
        "hello" to "Olá",
        "good morning" to "Bom dia",
        "good afternoon" to "Boa tarde",
        "good evening" to "Boa noite",
        "goodbye" to "Adeus",
        "see you later" to "Até logo",
        "thank you" to "Obrigado",
        "thank you (f)" to "Obrigada",
        "please" to "Por favor",
        "sorry" to "Desculpe",
        "yes" to "Sim",
        "no" to "Não",
        "how are you" to "Como está",
        "I am fine" to "Estou bem",
        "water" to "água",
        "food" to "comida",
        "family" to "família",
        "friend" to "amigo",
        "love" to "amor",
        "mother" to "mãe",
        "father" to "pai",
        "child" to "criança",
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    private val notes: Map<String, List<CulturalNote>> = mapOf(
        "greeting" to listOf(
            CulturalNote(
                "greeting",
                "Use 'Bom dia' in the morning. Show respect to elders.",
                listOf("Bom dia", "Boa noite"),
            ),
        ),
    ).mapKeys { it.key.lowercase(Locale.ROOT) }

    override fun getIdiomaticExpression(phrase: String): String? =
        idioms[phrase.lowercase(Locale.ROOT)]

    override fun adaptSystemPrompt(basePrompt: String): String =
        "You are a culturally aware AI assistant for Portuguese speakers. " +
            "Respond in Portuguese (Português) unless instructed otherwise. " +
            "Use natural, idiomatic expressions. Respect regional customs. " +
            "\n\n$basePrompt"

    override fun getCulturalNotes(context: String): List<CulturalNote> =
        notes[context.lowercase(Locale.ROOT)] ?: emptyList()

    override fun getGreeting(timeOfDay: String): String =
        when (timeOfDay.lowercase(Locale.ROOT)) {
            "morning", "am" -> "Bom dia"
            else -> "Boa noite"
        }

    override fun getLocaleHints(): Map<String, String> = mapOf(
        "bcp_tag" to "pt",
        "region" to "PT",
        "rtl" to "false",
        "date_format" to "dd/MM/yyyy",
    )
}
