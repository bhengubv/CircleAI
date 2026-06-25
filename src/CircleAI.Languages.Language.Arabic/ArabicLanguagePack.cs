using CircleAI.Languages.Language;

namespace CircleAI.Languages.Language.Arabic;

/// <summary>
/// Arabic language pack for Circle AI.
/// Provides idiomatic expressions, cultural context, and prompt tuning
/// to make the AI reason naturally in Arabic (العربية).
/// </summary>
public sealed class ArabicLanguagePack : ILanguagePack
{
    public static readonly ArabicLanguagePack Instance = new();

    public LanguagePackMetadata Metadata { get; } = new(
        BcpTag:          "ar",
        DisplayName:     "Arabic",
        NativeName:      "العربية",
        PrimaryRegion:   "SA",
        SpokenInRegions: ["SA","EG","MA","AE"],
        PackVersion:     new Version(1, 0));

    private static readonly Dictionary<string, string> Idioms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"]            = "مرحبا",
        ["peace be upon you"]= "السلام عليكم",
        ["good morning"]     = "صباح الخير",
        ["good evening"]     = "مساء الخير",
        ["goodbye"]          = "مع السلامة",
        ["thank you"]        = "شكرا",
        ["please"]           = "من فضلك",
        ["yes"]              = "نعم",
        ["no"]               = "لا",
        ["sorry"]            = "آسف",
        ["how are you"]      = "كيف حالك",
        ["I am fine"]        = "أنا بخير",
        ["water"]            = "ماء",
        ["food"]             = "طعام",
        ["family"]           = "عائلة",
        ["friend"]           = "صديق",
        ["love"]             = "حب",
        ["mother"]           = "أم",
        ["father"]           = "أب",
        ["child"]            = "طفل",
    };

    private static readonly Dictionary<string, CulturalNote[]> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["greeting"] =
        [
            new CulturalNote(
                "greeting",
                "Use 'صباح الخير' in the morning. Show respect to elders.",
                ["صباح الخير", "مساء الخير"])
        ]
    };

    public string? GetIdiomaticExpression(string phrase)
        => Idioms.TryGetValue(phrase, out var v) ? v : null;

    public string AdaptSystemPrompt(string basePrompt)
        => $"You are a culturally aware AI assistant for Arabic speakers. " +
           $"Respond in Arabic (العربية) unless instructed otherwise. " +
           $"Use natural, idiomatic expressions. Respect regional customs. " +
           $"\n\n{basePrompt}";

    public IReadOnlyList<CulturalNote> GetCulturalNotes(string context)
        => Notes.TryGetValue(context, out var n) ? n : [];

    public string GetGreeting(string timeOfDay)
        => timeOfDay.ToLowerInvariant() switch
        {
            "morning" or "am" => "صباح الخير",
            _                 => "مساء الخير"
        };

    public IReadOnlyDictionary<string, string> GetLocaleHints()
        => new Dictionary<string, string>
        {
            ["bcp_tag"]     = "ar",
            ["region"]      = "SA",
            ["rtl"]         = "true",
            ["date_format"] = "dd/MM/yyyy"
        };
}
