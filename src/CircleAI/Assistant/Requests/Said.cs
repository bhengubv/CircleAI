// Said.cs
//
// WHAT SOMEBODY SAID, READY TO BE SAID BACK. Every shortcut that quotes a person needs
// the same two things: the wake phrase off the front ("Hey, B. Remember that...") and
// what they said about themselves turned round ("my appointment" -> "your appointment").
// One owner for both, so the shortcuts cannot disagree about either.

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Assistant.Requests;

/// <summary>Quoting a person back to them.</summary>
public static partial class Said
{
    /// <summary><paramref name="said"/> without the wake phrase in front of it.</summary>
    /// <remarks>
    /// THE PHRASE IS PASSED IN, as it is to <see cref="Opener"/>: it is a per-device
    /// setting with one owner. Whisper punctuates it ("Hey, B."), so the words are matched,
    /// not the string.
    /// </remarks>
    public static string WithoutWakePhrase(string? said, string? wakePhrase)
    {
        var text = said?.Trim() ?? string.Empty;
        var words = Words(wakePhrase);
        if (words.Length == 0 || text.Length == 0) return text;

        var pattern = @"^\s*" + string.Join(@"[\s,\.!]+", words.Select(Regex.Escape)) + @"(?:[\s,\.!]+|$)";
        return Regex.Replace(text, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    /// <summary>What a person said about themselves, as it is said back to them.</summary>
    /// <remarks>
    /// "My clinic appointment moved to Friday" becomes "Your clinic appointment moved to
    /// Friday"; "your wake word" becomes "my wake word". One pass, so nothing is turned
    /// round twice. Every word keeps its own capital, except "I", which is always written
    /// upper-case and so says nothing: its replacement is lower-case, and a capital at
    /// the start of a sentence is the caller's to add.
    /// </remarks>
    public static string ToSecondPerson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Pronoun().Replace(text!.Trim(), m =>
        {
            var key = Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ").Replace('’', '\'');
            var swap = key switch
            {
                "i am" => "you are",
                "i was" => "you were",
                "i'm" => "you're",
                "i've" => "you've",
                "i'll" => "you'll",
                "i'd" => "you'd",
                "i" => "you",
                "me" => "you",
                "my" => "your",
                "mine" => "yours",
                "myself" => "yourself",
                "your" => "my",
                "yours" => "mine",
                "yourself" => "myself",
                _ => m.Value,
            };
            var capital = !key.StartsWith('i') && char.IsUpper(m.Value[0]);
            return capital ? char.ToUpperInvariant(swap[0]) + swap[1..] : swap;
        });
    }

    /// <summary><paramref name="text"/> made fit to follow "that" or "me" mid-sentence.</summary>
    /// <remarks>A pronoun or an article loses its capital; a name keeps it.</remarks>
    public static string Lead(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = text!.Trim();
        var first = t.Split(' ', 2)[0].Trim(',', '.').ToLowerInvariant().Replace('’', '\'');
        return Lowered.Contains(first) ? char.ToLowerInvariant(t[0]) + t[1..] : t;
    }

    /// <summary>Whether two words are the same word, allowing an ending ("meal", "meals").</summary>
    public static bool SameWord(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length < 4 || b.Length < 4 || Math.Abs(a.Length - b.Length) > 3) return false;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The words, with the punctuation a recogniser adds taken off.</summary>
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var cleaned = new System.Text.StringBuilder(text!.Length);
        foreach (var c in text!)
            cleaned.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '’' ? c : ' ');
        return cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static readonly string[] Lowered =
        ["your", "you", "you're", "you've", "you'll", "you'd", "yours", "yourself", "my", "the", "a", "an", "our", "this", "that"];

    [GeneratedRegex(@"\b(?:I\s+am|I\s+was|I['’]m|I['’]ve|I['’]ll|I['’]d|myself|mine|my|me|I|yourself|yours|your)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pronoun();
}
