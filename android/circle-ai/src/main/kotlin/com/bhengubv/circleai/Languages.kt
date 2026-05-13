package com.bhengubv.circleai

enum class WritingSystem { LATIN, ARABIC, HANZI, DEVANAGARI, ETHIOPIC, HEBREW, CYRILLIC, OTHER }

data class LanguageTag(
    val bcpTag: String,
    val englishName: String,
    val nativeName: String,
    val writingSystem: WritingSystem,
    val isRtl: Boolean,
    val primaryRegion: String
)

data class DetectionResult(
    val language: LanguageTag,
    val confidence: Double
)

interface ILanguageDetector {
    suspend fun detect(text: String): List<DetectionResult>
}

interface ILanguageRegistry {
    fun getAll(): List<LanguageTag>
    fun findByBcpTag(tag: String): LanguageTag?
    fun count(): Int
}

object KnownLanguages : ILanguageRegistry {
    private val languages = listOf(
        LanguageTag("zu",  "Zulu",       "isiZulu",          WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("st",  "Sotho",      "Sesotho",          WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("af",  "Afrikaans",  "Afrikaans",        WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("sw",  "Swahili",    "Kiswahili",        WritingSystem.LATIN,      false, "KE"),
        LanguageTag("ha",  "Hausa",      "Hausa",            WritingSystem.LATIN,      false, "NG"),
        LanguageTag("am",  "Amharic",    "አማርኛ",  WritingSystem.ETHIOPIC,   false, "ET"),
        LanguageTag("yo",  "Yoruba",     "Yorùbá", WritingSystem.LATIN,      false, "NG"),
        LanguageTag("ig",  "Igbo",       "Igbo",             WritingSystem.LATIN,      false, "NG"),
        LanguageTag("xh",  "Xhosa",      "isiXhosa",         WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("nso", "Sepedi",     "Sesotho sa Leboa", WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("tn",  "Tswana",     "Setswana",         WritingSystem.LATIN,      false, "ZA"),
        LanguageTag("so",  "Somali",     "Soomaali",         WritingSystem.LATIN,      false, "SO"),
        LanguageTag("om",  "Oromo",      "Oromoo",           WritingSystem.LATIN,      false, "ET"),
        LanguageTag("ar",  "Arabic",     "العربية", WritingSystem.ARABIC, true, "SA"),
        LanguageTag("en",  "English",    "English",          WritingSystem.LATIN,      false, "US"),
        LanguageTag("pt",  "Portuguese", "Português",   WritingSystem.LATIN,      false, "BR"),
        LanguageTag("fr",  "French",     "Français",    WritingSystem.LATIN,      false, "FR"),
        LanguageTag("es",  "Spanish",    "Español",     WritingSystem.LATIN,      false, "ES"),
        LanguageTag("zh",  "Chinese",    "中文",     WritingSystem.HANZI,      false, "CN"),
        LanguageTag("hi",  "Hindi",      "हिन्दी", WritingSystem.DEVANAGARI, false, "IN")
    )

    override fun getAll(): List<LanguageTag> = languages
    override fun count(): Int = languages.size
    override fun findByBcpTag(tag: String): LanguageTag? = languages.find { it.bcpTag == tag }
}
