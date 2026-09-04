namespace CircleAI.Samples.It;

/// <summary>
/// Something to translate and what to translate it into, recognised from
/// "how do you say X in Y".
/// </summary>
/// <remarks>
/// THIS IS THE SENTENCE THE APP WAS WORST AT. Asked "how do you say hello in
/// isiZulu" it did one of two things, and both were wrong: it answered with a
/// paragraph ABOUT translation from the general model, or - had the router been
/// loosened enough to catch it - it threw the person onto the Translator screen,
/// where they then had to set two languages and say it again. The router's own
/// note says eating that sentence is worse than no router at all, and it is
/// right, because navigating somewhere is not an answer to it.
///
/// <para>
/// Doing it is. The sentence names its own target language, which is the setup
/// the screen would have asked for, so there is nothing left to ask - and that
/// is the difference between a menu you reach by voice and an assistant.
/// </para>
/// <para>
/// THE TARGET MUST BE A LANGUAGE THIS APP ACTUALLY HAS. Parsing "in the morning"
/// as a language would turn every sentence ending in "in something" into a
/// translation, so the tail is looked up in <see cref="SampleLanguages"/> - the
/// same table the picker renders - and anything not in it is not a request.
/// That also keeps the catalogue the one owner of what a language is called.
/// </para>
/// </remarks>
/// <param name="Text">The words to translate, as they were said.</param>
/// <param name="Language">The target, from the app's own catalogue.</param>
public sealed record TranslationRequest(string Text, SampleLanguage Language)
{
    /// <summary>The ways somebody asks for this, longest first.</summary>
    /// <remarks>
    /// Longest first because "how do you say" contains "say": matching the short
    /// form first would leave "how do you" glued to the front of the text and
    /// solemnly translate that too.
    /// </remarks>
    private static readonly string[] Openers =
    [
        "how do you say", "how do i say", "how would you say",
        "what is the word for", "what is",
        "translate", "say",
    ];

    /// <summary>The words that introduce the target language.</summary>
    private static readonly string[] Into = ["into", "in", "to"];

    /// <summary>Language names as the matcher sees them, normalised once.</summary>
    /// <remarks>
    /// Both the English name and the endonym, because somebody asking for isiZulu
    /// is as likely to say "isiZulu" as "Zulu" - and the endonym is what the
    /// picker shows them. Longest first so "spanish spain" beats "spanish".
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<(string Word, SampleLanguage Language)>> Names =
        new(() => SampleLanguages.All.Values
            .SelectMany(l => new[] { l.Name, l.Native }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => (Word: VoiceDestinations.Normalise(n!), Language: l)))
            .Where(x => x.Word.Length >= 2)
            .DistinctBy(x => x.Word, StringComparer.Ordinal)
            .OrderByDescending(x => x.Word.Length)
            .ToList());

    /// <summary>
    /// What this sentence is asking to be translated, or null if it is not.
    /// </summary>
    /// <remarks>
    /// NULL IS THE SAFE ANSWER. An unrecognised sentence falls through to an
    /// ordinary turn and gets the answer it would have got anyway; a wrongly
    /// recognised one answers a question nobody asked in a language nobody
    /// wanted. The shape required is deliberately narrow for that reason: an
    /// opener, some words, and a language this app actually has.
    /// </remarks>
    public static TranslationRequest? Parse(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return null;

        var text = VoiceDestinations.Normalise(heard);
        if (text.Length == 0) return null;

        var opener = Openers.FirstOrDefault(o => text.StartsWith(o + " ", StringComparison.Ordinal));
        if (opener is null) return null;

        var rest = text[(opener.Length + 1)..].Trim();
        if (rest.Length == 0) return null;

        // Matched against the TAIL, longest language name first. A sentence may
        // well contain "in" earlier - "how do you say the man in the moon in
        // French" - and taking the first one would translate "the man" into a
        // language called "the moon in french", which is nothing at all.
        foreach (var (word, language) in Names.Value)
        {
            foreach (var into in Into)
            {
                var suffix = $" {into} {word}";
                if (suffix.Length >= rest.Length) continue;
                if (!rest.EndsWith(suffix, StringComparison.Ordinal)) continue;

                var body = rest[..^suffix.Length].Trim();
                if (body.Length == 0) continue;

                return new TranslationRequest(body, language);
            }
        }

        return null;
    }
}
