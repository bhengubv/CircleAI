// BuiltInWakePhrases.cs
//
// The wake phrases that ship with the app, by language.
//
// FIVE OF SEVENTY-FIVE. The app speaks seventy-five languages and arrives
// knowing how to be woken in five of them, because a wake phrase is not a
// translation - it is a name with an honorific around it, chosen so the model
// can actually hear it, and each one had to be measured.
//
// This is a plain table rather than a call into the engine because it is read by
// the shared UI, which is loaded by a browser as well as by a phone and
// references nothing. WakeLanguagesTests asserts it agrees with
// WakePhraseBook.CandidatesByLanguage in CircleAI.Voice; a hand-kept copy of
// somebody else's table is otherwise a lie waiting for the next commit.
//
// WHAT IT IS NOT: a picker. There was a select on the settings screen filled
// from this idea - choose the language of your wake phrase - and it let somebody
// run the app in English and wake it with ビーさん. The language comes from
// AppSettings.Language now. This table only answers "does that language arrive
// with a phrase, and what is it", and when the answer is no the screen offers to
// let the owner add one rather than quietly listening in English.

namespace CircleAI.Samples.It;

/// <summary>What the app knows how to be woken with, before anybody adds theirs.</summary>
public static class BuiltInWakePhrases
{
    /// <summary>
    /// The phrases that ship, by language, best first.
    /// </summary>
    /// <remarks>
    /// SEVERAL PER LANGUAGE ON PURPOSE. Japanese has ビーさん, ビーさま and Bee san
    /// - the same name with different honorifics, plus a romanised form for
    /// somebody whose keyboard is not Japanese - and which of them a person
    /// actually says is theirs to pick, not ours to guess. The app used to guess.
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> Phrases { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = ["Hey B"],
            ["ja"] = ["ビーさん", "ビーさま", "Bee san"],
            ["ko"] = ["비 님", "Bee nim"],
            ["zh"] = ["小B", "Xiao B"],
            ["yue"] = ["小B", "Siu B"],
        };

    /// <summary>The phrases that ship for a language, or empty when there are none.</summary>
    /// <remarks>
    /// EMPTY RATHER THAN ENGLISH. Falling back to "Hey B" is what the listener
    /// does at the very bottom of the stack, so that a phone with no usable phrase
    /// still answers to something; but a SCREEN that falls back is a screen that
    /// lies, because the person reading it came to find out what to say.
    /// </remarks>
    public static IReadOnlyList<string> For(string? language)
        => Root(language) is { } code && Phrases.TryGetValue(code, out var list)
            ? list
            : [];

    /// <summary>Whether the app arrives knowing how to be woken in this language.</summary>
    public static bool Has(string? language) => For(language).Count > 0;

    /// <summary>The language part of a tag, so "ja-JP" finds "ja".</summary>
    private static string? Root(string? tag)
    {
        var code = tag?.Trim();
        if (string.IsNullOrEmpty(code)) return null;
        var cut = code.IndexOf('-');
        return cut > 0 ? code[..cut] : code;
    }
}
