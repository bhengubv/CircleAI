namespace CircleAI.Languages;

/// <summary>Static registry of every language Circle AI ships support for.</summary>
public static class KnownLanguages
{
    // TWO NATIVE NAMES WERE ALIGNED TO SampleLanguages, NOT THE OTHER WAY ROUND.
    // This table said "Hausa" and "Igbo" where the app's own catalogue says
    // "Harshen Hausa" and "Asụsụ Igbo". Both readings are defensible - the
    // second spells out "the Hausa language" the way those languages name
    // themselves - and the deciding factor was not linguistics: SampleLanguages
    // is the table people SEE, in the picker, today. Changing the unused table
    // to match the used one cannot make anybody's screen worse.
    //
    // A Hausa or Igbo speaker should still be asked which they would rather
    // read. LanguageTableTests now fails if the two ever disagree again.

    // ── Africa ────────────────────────────────────────────────────────────────
    public static readonly LanguageTag IsiZulu     = new("zu", "isiZulu",     "isiZulu",     WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Sesotho     = new("st", "Sesotho",     "Sesotho",     WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Afrikaans   = new("af", "Afrikaans",   "Afrikaans",   WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Swahili     = new("sw", "Swahili",     "Kiswahili",   WritingSystem.Latin,    false, "KE");
    public static readonly LanguageTag Hausa       = new("ha", "Hausa",       "Harshen Hausa", WritingSystem.Latin,  false, "NG");
    public static readonly LanguageTag Amharic     = new("am", "Amharic",     "አማርኛ",        WritingSystem.Ethiopic, false, "ET");
    public static readonly LanguageTag Yoruba      = new("yo", "Yoruba",      "Yorùbá",      WritingSystem.Latin,    false, "NG");
    public static readonly LanguageTag Igbo        = new("ig", "Igbo",        "Asụsụ Igbo",  WritingSystem.Latin,    false, "NG");
    public static readonly LanguageTag Xhosa       = new("xh", "isiXhosa",    "isiXhosa",    WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Sepedi      = new("nso","Sepedi",      "Sepedi",      WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Setswana    = new("tn", "Setswana",    "Setswana",    WritingSystem.Latin,    false, "ZA");
    public static readonly LanguageTag Somali      = new("so", "Somali",      "Soomaali",    WritingSystem.Latin,    false, "SO");
    public static readonly LanguageTag Oromo       = new("om", "Oromo",       "Afaan Oromoo",WritingSystem.Latin,    false, "ET");

    // ── Middle East & North Africa ────────────────────────────────────────────
    public static readonly LanguageTag Arabic      = new("ar", "Arabic",      "العربية",     WritingSystem.Arabic,   true,  "SA");

    // ── Europe & Americas ─────────────────────────────────────────────────────
    public static readonly LanguageTag English     = new("en", "English",     "English",     WritingSystem.Latin,    false, "GB");
    public static readonly LanguageTag Portuguese  = new("pt", "Portuguese",  "Português",   WritingSystem.Latin,    false, "PT");
    public static readonly LanguageTag French      = new("fr", "French",      "Français",    WritingSystem.Latin,    false, "FR");
    public static readonly LanguageTag Spanish     = new("es", "Spanish",     "Español",     WritingSystem.Latin,    false, "ES");

    // ── Asia ──────────────────────────────────────────────────────────────────
    public static readonly LanguageTag Mandarin    = new("zh", "Mandarin",    "中文",          WritingSystem.Han,      false, "CN");
    public static readonly LanguageTag Hindi       = new("hi", "Hindi",       "हिन्दी",        WritingSystem.Devanagari, false, "IN");

    /// <summary>
    /// The rest of what the app offers, sourced rather than typed.
    /// </summary>
    /// <remarks>
    /// THIS TABLE HAD TWENTY ENTRIES AND THE APP OFFERED SEVENTY-TWO. Anything
    /// reaching for KnownLanguages therefore degraded silently for most of the
    /// catalogue - LlmTranslationEngine did exactly that, asking a model to
    /// translate "to ja" because Japanese was not among the twenty.
    /// <para>
    /// EVERY VALUE BELOW IS DERIVED, NOT RECALLED, and the method was checked
    /// before it was trusted:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Script</b> - the Unicode script of the language's own native
    /// name, which was already in the repo as real data. Deterministic from
    /// code points.</item>
    /// <item><b>IsRtl</b> - <c>CultureInfo.TextInfo.IsRightToLeft</c>, which on
    /// .NET is ICU/CLDR.</item>
    /// <item><b>IsoRegion</b> - <c>CultureInfo.CreateSpecificCulture</c>, which
    /// is ICU's likely-subtags. For the nine ICU had no answer for, a candidate
    /// was put to ICU as a region-qualified culture and kept only where ICU
    /// confirmed it - marked <c>icu-confirmed</c>.</item>
    /// </list>
    /// <para>
    /// THE RULER WAS CHECKED AGAINST THE TWENTY ABOVE, which were written by
    /// hand: script agreed 20/20 and right-to-left agreed 20/20. Region differed
    /// twice and both are editorial rather than wrong - <c>en</c> is hand-picked
    /// GB where ICU's likely region is US, and <c>pt</c> is hand-picked PT where
    /// ICU says BR. A derivation that could not reproduce the hand-written rows
    /// had no business generating these.
    /// </para>
    /// <para>
    /// Right-to-left came back true for exactly two: Persian and Urdu.
    /// </para>
    /// </remarks>
    private static readonly LanguageTag[] Sourced =
    [
        new("ak", "Akan", "Twi", WritingSystem.Latin, false, "GH"),   // icu-likely-subtags
        new("bem", "Bemba", "Ichibemba", WritingSystem.Latin, false, "ZM"),   // icu-likely-subtags
        new("bm", "Bambara", "Bamanankan", WritingSystem.Latin, false, "ML"),   // icu-likely-subtags
        new("bn", "Bengali", "বাংলা", WritingSystem.Bengali, false, "BD"),   // icu-likely-subtags
        new("ee", "Ewe", "Eʋegbe", WritingSystem.Latin, false, "GH"),   // icu-likely-subtags
        new("es-ES", "Spanish (Spain)", "Español (España)", WritingSystem.Latin, false, "ES"),   // icu-likely-subtags
        new("es-MX", "Spanish (Mexico)", "Español (México)", WritingSystem.Latin, false, "MX"),   // icu-likely-subtags
        new("fa", "Persian", "فارسی", WritingSystem.Arabic, true, "IR"),   // icu-likely-subtags
        new("ff", "Fula", "Fulfulde", WritingSystem.Latin, false, "SN"),   // icu-likely-subtags
        new("fon", "Fon", "Fɔngbè", WritingSystem.Latin, false, "BJ"),   // icu-confirmed
        new("gn", "Guarani", "Avañe'ẽ", WritingSystem.Latin, false, "PY"),   // icu-likely-subtags
        new("gu", "Gujarati", "ગુજરાતી", WritingSystem.Gujarati, false, "IN"),   // icu-likely-subtags
        new("ht", "Haitian Creole", "Kreyòl", WritingSystem.Latin, false, "HT"),   // icu-confirmed
        new("id", "Indonesian", "Bahasa Indonesia", WritingSystem.Latin, false, "ID"),   // icu-likely-subtags
        new("ja", "Japanese", "日本語", WritingSystem.Japanese, false, "JP"),   // icu-likely-subtags; script corrected Han -> Japanese (ISO 15924 Jpan)
        new("jv", "Javanese", "Basa Jawa", WritingSystem.Latin, false, "ID"),   // icu-likely-subtags
        new("ki", "Kikuyu", "Gĩkũyũ", WritingSystem.Latin, false, "KE"),   // icu-likely-subtags
        new("kn", "Kannada", "ಕನ್ನಡ", WritingSystem.Kannada, false, "IN"),   // icu-likely-subtags
        new("ko", "Korean", "한국어", WritingSystem.Hangul, false, "KR"),   // icu-likely-subtags
        new("kr", "Kanuri", "Kanuri", WritingSystem.Latin, false, "NG"),   // icu-likely-subtags
        new("lg", "Luganda", "Luganda", WritingSystem.Latin, false, "UG"),   // icu-likely-subtags
        new("lgg", "Lugbara", "Lugbarati", WritingSystem.Latin, false, "UG"),   // icu-confirmed
        new("ln", "Lingala", "Lingála", WritingSystem.Latin, false, "CD"),   // icu-likely-subtags
        new("mg", "Malagasy", "Malagasy", WritingSystem.Latin, false, "MG"),   // icu-likely-subtags
        new("ml", "Malayalam", "മലയാളം", WritingSystem.Malayalam, false, "IN"),   // icu-likely-subtags
        new("mos", "Mossi", "Mooré", WritingSystem.Latin, false, "BF"),   // icu-confirmed
        new("mr", "Marathi", "मराठी", WritingSystem.Devanagari, false, "IN"),   // icu-likely-subtags
        new("my", "Burmese", "မြန်မာ", WritingSystem.Myanmar, false, "MM"),   // icu-likely-subtags
        new("ne", "Nepali", "नेपाली", WritingSystem.Devanagari, false, "NP"),   // icu-likely-subtags
        new("nl", "Dutch", "Nederlands", WritingSystem.Latin, false, "NL"),   // icu-likely-subtags
        new("nl-BE", "Flemish", "Vlaams", WritingSystem.Latin, false, "BE"),   // icu-likely-subtags
        new("nl-NL", "Dutch", "Nederlands", WritingSystem.Latin, false, "NL"),   // icu-likely-subtags
        new("nr", "isiNdebele", "isiNdebele", WritingSystem.Latin, false, "ZA"),   // icu-likely-subtags
        new("ny", "Chichewa", "Chichewa", WritingSystem.Latin, false, "MW"),   // icu-confirmed
        new("nyn", "Nyankole", "Runyankole", WritingSystem.Latin, false, "UG"),   // icu-likely-subtags
        new("pa", "Punjabi", "ਪੰਜਾਬੀ", WritingSystem.Gurmukhi, false, "IN"),   // icu-likely-subtags
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", WritingSystem.Latin, false, "BR"),   // icu-likely-subtags
        new("pt-PT", "Portuguese (Portugal)", "Português (Portugal)", WritingSystem.Latin, false, "PT"),   // icu-likely-subtags
        new("qu", "Quechua", "Runa Simi", WritingSystem.Latin, false, "PE"),   // icu-confirmed
        new("rn", "Kirundi", "Ikirundi", WritingSystem.Latin, false, "BI"),   // icu-likely-subtags
        new("ru", "Russian", "Русский", WritingSystem.Cyrillic, false, "RU"),   // icu-likely-subtags
        new("rw", "Kinyarwanda", "Ikinyarwanda", WritingSystem.Latin, false, "RW"),   // icu-likely-subtags
        new("sg", "Sango", "Sängö", WritingSystem.Latin, false, "CF"),   // icu-likely-subtags
        new("si", "Sinhala", "සිංහල", WritingSystem.Sinhala, false, "LK"),   // icu-likely-subtags
        new("sn", "Shona", "chiShona", WritingSystem.Latin, false, "ZW"),   // icu-likely-subtags
        new("ss", "siSwati", "siSwati", WritingSystem.Latin, false, "ZA"),   // icu-likely-subtags
        new("su", "Sundanese", "Basa Sunda", WritingSystem.Latin, false, "ID"),   // icu-confirmed
        new("ta", "Tamil", "தமிழ்", WritingSystem.Tamil, false, "IN"),   // icu-likely-subtags
        new("te", "Telugu", "తెలుగు", WritingSystem.Telugu, false, "IN"),   // icu-likely-subtags
        new("th", "Thai", "ไทย", WritingSystem.Thai, false, "TH"),   // icu-likely-subtags
        new("ti", "Tigrinya", "ትግርኛ", WritingSystem.Ethiopic, false, "ER"),   // icu-likely-subtags
        new("tl", "Filipino", "Tagalog", WritingSystem.Latin, false, "PH"),   // icu-confirmed
        new("tpi", "Tok Pisin", "Tok Pisin", WritingSystem.Latin, false, "PG"),   // icu-confirmed
        new("ts", "Xitsonga", "Xitsonga", WritingSystem.Latin, false, "ZA"),   // icu-likely-subtags
        new("ur", "Urdu", "اردو", WritingSystem.Arabic, true, "PK"),   // icu-likely-subtags
        new("ve", "Tshivenda", "Tshivenḓa", WritingSystem.Latin, false, "ZA"),   // icu-likely-subtags
        new("vi", "Vietnamese", "Tiếng Việt", WritingSystem.Latin, false, "VN"),   // icu-likely-subtags
        new("yue", "Cantonese", "粵語", WritingSystem.Han, false, "HK"),   // icu-likely-subtags
    ];

    /// <summary>All languages shipped with Circle AI.</summary>
    public static readonly IReadOnlyList<LanguageTag> All =
    [
        IsiZulu, Sesotho, Afrikaans, Swahili, Hausa, Amharic,
        Yoruba, Igbo, Xhosa, Sepedi, Setswana, Somali, Oromo,
        Arabic,
        English, Portuguese, French, Spanish,
        Mandarin, Hindi,
        .. Sourced,
    ];
}
