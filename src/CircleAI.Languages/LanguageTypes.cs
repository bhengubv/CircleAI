// LanguageTypes.cs — core language primitives
namespace CircleAI.Languages;

/// <summary>The script a language is written in.</summary>
/// <remarks>
/// ELEVEN OF THESE WERE MISSING AND TWELVE LANGUAGES COLLAPSED TO "Other".
/// Bengali, Gujarati, Kannada, Korean, Malayalam, Burmese, Punjabi, Sinhala,
/// Tamil, Telugu and Thai are all offered by the app and none of them had a
/// value here, so the one field that says how their text must be rendered said
/// "something else". Sourced from the Unicode script of each language's own
/// native name - see KnownLanguages.
/// <para>
/// APPENDED, NEVER INSERTED. Nothing persists or switches on this today
/// (verified: WritingSystem appears only in this file and KnownLanguages), but
/// an enum whose values are written down anywhere cannot be renumbered later
/// without silently relabelling every stored row - the same rule
/// ModelModality carries.
/// </para>
/// <para>
/// <c>Japanese</c> rather than <c>Han</c> for ja: the script is ISO 15924
/// <c>Jpan</c>, which is Han PLUS hiragana and katakana. Deriving from the
/// native name alone gives Han, because the word 日本語 happens to contain no
/// kana - a case where one word under-represents the writing system.
/// </para>
/// </remarks>
public enum WritingSystem
{
    Latin, Arabic, Ethiopic, Geez, Devanagari,
    Han, Cyrillic, Hebrew, Greek, Other,

    Bengali, Gujarati, Gurmukhi, Hangul, Japanese,
    Kannada, Malayalam, Myanmar, Sinhala, Tamil, Telugu, Thai,
}

/// <summary>A BCP-47 language tag enriched with display metadata.</summary>
public sealed record LanguageTag(
    string BcpTag,
    string DisplayName,
    string NativeName,
    WritingSystem Script,
    bool IsRtl,
    string IsoRegion)
{
    public static readonly LanguageTag Unknown =
        new("und", "Unknown", "Unknown", WritingSystem.Latin, false, "");
}

/// <summary>Result of language detection.</summary>
public sealed record DetectionResult(
    LanguageTag Language,
    float Confidence,
    bool IsReliable);

/// <summary>Result of script normalisation.</summary>
public sealed record ScriptNormalisationResult(
    string Input,
    string Normalised,
    LanguageTag DetectedLanguage);
