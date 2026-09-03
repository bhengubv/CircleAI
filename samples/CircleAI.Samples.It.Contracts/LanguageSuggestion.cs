namespace CircleAI.Samples.It;

/// <summary>What to offer somebody who has not chosen yet, and why.</summary>
/// <param name="Tags">Best first. Empty only when nothing at all is supported.</param>
/// <param name="Reason">
/// How it was arrived at, in plain words, so a setup screen can say "because
/// your phone is set to isiZulu" rather than presenting a list from nowhere.
/// </param>
/// <param name="FromDevice">
/// True when the phone itself said so. False means this is a guess from where
/// the phone is, and a screen should be more tentative about it.
/// </param>
public sealed record LanguageChoices(
    IReadOnlyList<string> Tags, string Reason, bool FromDevice);

/// <summary>
/// Suggesting a first language from the phone instead of assuming English.
/// </summary>
/// <remarks>
/// THE APP DEFAULTED EVERY PERSON ON EARTH TO ENGLISH. Not the catalogue - that
/// holds seventy-five languages and English is one row of it - but the code
/// around it: StoredSpokenLanguage.Default was the constant "en", and seven more
/// screens each carried their own `?? "en"`. Nothing anywhere read the device
/// locale. A phone set to isiZulu, bought in Soweto, opened in English and
/// invited its owner to go and find their own language in a list of seventy-five.
///
/// <para>
/// THE PHONE'S OWN SETTING COMES FIRST, because it is not a guess: Android
/// carries an ordered list of the locales somebody deliberately configured, and
/// that is the closest thing to being asked. Only when none of those is
/// supported does this fall back to the REGION, and only when that fails too
/// does it reach for English.
/// </para>
/// <para>
/// THE REGION TABLE IS A SUGGESTION AND NOTHING MORE. It offers languages this
/// app can actually speak that are widely spoken where the phone is - it is not
/// a claim about who anybody is, it is the difference between a sensible first
/// screen and an alphabetical list. Everything here is reversible in one tap,
/// and English is always offered alongside rather than instead.
/// </para>
/// <para>
/// It is deliberately not exhaustive. A country nobody has checked is left out
/// rather than guessed at, because a wrong suggestion presented confidently is
/// worse than an honest fallback - and the fallback says which happened.
/// </para>
/// </remarks>
public static class LanguageSuggestion
{
    /// <summary>The one language assumed when the phone will not say.</summary>
    /// <remarks>
    /// STILL ENGLISH, AND THAT IS A FALLBACK RATHER THAN A DEFAULT. The
    /// difference is that it is now reached only after the phone has been asked
    /// twice and had no answer, and the reason says so out loud.
    /// </remarks>
    public const string LastResort = "en";

    /// <summary>
    /// Languages this app speaks that are widely spoken in a given country.
    /// </summary>
    /// <remarks>
    /// Ordered by reach within the country, and every tag is one the catalogue
    /// actually carries - a suggestion pointing at a language the app cannot
    /// speak would be a worse first screen than none.
    /// </remarks>
    private static readonly Dictionary<string, string[]> ByRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        // Southern Africa
        ["ZA"] = ["zu", "xh", "af", "nso", "tn", "st", "ts", "ss", "ve", "nr", "en"],
        ["ZW"] = ["sn", "nr", "en"],
        ["BW"] = ["tn", "en"],
        ["LS"] = ["st", "en"],
        ["SZ"] = ["ss", "en"],
        ["NA"] = ["af", "en"],
        ["ZM"] = ["bem", "ny", "en"],
        ["MW"] = ["ny", "en"],
        ["MZ"] = ["ts"],

        // East Africa
        ["TZ"] = ["sw", "en"],
        ["KE"] = ["sw", "ki", "so", "en"],
        ["UG"] = ["lg", "nyn", "sw", "lgg", "en"],
        ["RW"] = ["rw", "sw", "fr", "en"],
        ["BI"] = ["rn", "fr", "en"],
        ["ET"] = ["am", "om", "ti"],
        ["ER"] = ["ti", "ar"],
        ["SO"] = ["so", "ar"],
        ["MG"] = ["mg", "fr"],

        // West and Central Africa
        ["NG"] = ["ha", "yo", "ig", "ff", "kr", "en"],
        ["GH"] = ["ak", "ee", "ha", "en"],
        ["BJ"] = ["fon", "fr"],
        ["BF"] = ["mos", "fr"],
        ["ML"] = ["bm", "ff", "fr"],
        ["SN"] = ["ff", "fr"],
        ["NE"] = ["ha", "kr", "fr"],
        ["CD"] = ["ln", "sw", "fr"],
        ["CG"] = ["ln", "fr"],
        ["CF"] = ["sg", "fr"],

        // South and South-East Asia
        ["IN"] = ["hi", "bn", "mr", "te", "ta", "gu", "kn", "ml", "pa", "ur", "en"],
        ["PK"] = ["ur", "pa", "en"],
        ["BD"] = ["bn"],
        ["LK"] = ["si", "ta"],
        ["NP"] = ["ne"],
        ["MM"] = ["my"],
        ["TH"] = ["th"],
        ["VN"] = ["vi"],
        ["ID"] = ["id", "jv", "su"],
        ["PH"] = ["tl", "en"],

        // East Asia
        ["JP"] = ["ja"],
        ["KR"] = ["ko"],
        ["CN"] = ["zh", "yue"],
        ["HK"] = ["yue", "zh", "en"],
        ["TW"] = ["zh"],

        // Elsewhere the catalogue reaches
        ["RU"] = ["ru"],
        ["IR"] = ["fa"],
        ["AF"] = ["fa"],
        ["HT"] = ["ht", "fr"],
        ["PY"] = ["gn"],
        ["PG"] = ["tpi", "en"],
        ["FR"] = ["fr"],
    };

    /// <summary>Which languages to offer first, given what the phone says.</summary>
    /// <param name="deviceLocales">
    /// The phone's own locales, best first - "zu-ZA", "en-ZA". Android carries an
    /// ordered list because somebody set it; the order is theirs, not ours.
    /// </param>
    /// <param name="region">Two-letter country, when the phone will say.</param>
    /// <param name="supported">
    /// What this build can actually speak. Passed in rather than read from the
    /// catalogue here, because a suggestion that outruns the installed voices is
    /// the same broken promise as the language count on the home screen.
    /// </param>
    public static LanguageChoices For(
        IEnumerable<string>? deviceLocales,
        string? region,
        IEnumerable<string>? supported = null)
    {
        var can = supported is null
            ? new HashSet<string>(SampleLanguages.All.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase);

        // THE PHONE'S OWN SETTING, IN THE PHONE'S OWN ORDER. Not a guess: it is
        // the nearest thing to having asked.
        var chosen = (deviceLocales ?? [])
            .Select(Primary)
            .Where(t => t.Length > 0 && can.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chosen.Count > 0)
        {
            var named = SampleLanguages.Find(chosen[0])?.Name ?? chosen[0];
            return new LanguageChoices(chosen, $"your phone is set to {named}", FromDevice: true);
        }

        // Nothing the phone asked for is available. Offer what is spoken where it
        // is - tentatively, and with English alongside rather than instead.
        if (region is { Length: > 0 } && ByRegion.TryGetValue(Region(region), out var local))
        {
            var here = local.Where(can.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (here.Count > 0)
                // NOT "where you are". This is handed a country and does not
                // know how the caller got it - a locale says where somebody is
                // FROM. The caller that does know says so; see Pair.
                return new LanguageChoices(here, "widely spoken there", FromDevice: false);
        }

        // Asked twice, no answer. English, and the reason says exactly that.
        return can.Contains(LastResort)
            ? new LanguageChoices([LastResort],
                "your phone did not say which language you speak", FromDevice: false)
            : new LanguageChoices([], "nothing is installed yet", FromDevice: false);
    }

    /// <summary>Your language, and the language of the people around you.</summary>
    /// <param name="mine">What the owner speaks - their locale's answer.</param>
    /// <param name="where">Where the phone actually is, and how that was decided.</param>
    /// <remarks>
    /// THE INTERPRETER PAIR WAS HARD-CODED TO ENGLISH AND isiZULU, in two files
    /// that had to agree. That is a fine guess in Johannesburg and a useless one
    /// everywhere else: a South African in Tokyo needs English and JAPANESE, and
    /// no amount of reading his locale more carefully will ever produce the
    /// second - his locale says South Africa, because he is South African.
    /// <para>
    /// So the two sides come from two different questions. Yours from what you
    /// speak; theirs from where you are standing. Only a signal that actually
    /// knows position - a network, a timezone - is allowed to answer the second,
    /// which is why a locale-derived country returns no partner rather than
    /// pretending isiZulu is spoken in Tokyo.
    /// </para>
    /// </remarks>
    public static (string Mine, string? Theirs, string Reason) Pair(
        string mine, Whereabouts where, IEnumerable<string>? supported = null)
    {
        // A locale is not a location. It is the honest answer to "where are you
        // from" and the wrong answer to "who is standing in front of you".
        if (where.Source is CountrySource.Unknown or CountrySource.Locale)
            return (mine, null, "nothing on this phone knows where you are");

        var here = For(null, where.Here, supported).Tags
            .FirstOrDefault(t => !string.Equals(t, mine, StringComparison.OrdinalIgnoreCase));

        if (here is null)
            return (mine, null, $"nothing this app speaks is widely spoken in {where.Here}");

        var named = SampleLanguages.Find(here)?.Name ?? here;
        return (mine, here, $"{named}, from {where.Explain}");
    }

    /// <summary>The single best tag to start with.</summary>
    /// <remarks>
    /// For the places that need one value rather than a list - a stored default,
    /// a first turn. It is still the phone's answer where there is one.
    /// </remarks>
    public static string Best(
        IEnumerable<string>? deviceLocales,
        string? region,
        IEnumerable<string>? supported = null)
        => For(deviceLocales, region, supported).Tags.FirstOrDefault() ?? LastResort;

    /// <summary>"zu-ZA" and "zu_ZA" and " ZU " all mean "zu".</summary>
    private static string Primary(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var t = tag.Trim();
        var cut = t.IndexOfAny(['-', '_']);
        if (cut > 0) t = t[..cut];
        return t.ToLowerInvariant();
    }

    /// <summary>"en-ZA" or "ZA" or "za" all mean "ZA".</summary>
    private static string Region(string region)
    {
        var t = region.Trim();
        var cut = t.IndexOfAny(['-', '_']);
        if (cut >= 0 && cut + 1 < t.Length) t = t[(cut + 1)..];
        return t.ToUpperInvariant();
    }
}
