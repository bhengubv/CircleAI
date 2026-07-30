#nullable enable

// GeezRomanizer.cs
//
// Ethiopic (Ge'ez) script → Latin, because the Amharic and Tigrinya voices do not
// read Ethiopic at all.
//
// Meta ships those two MMS models with `is_uroman: true`: their vocabularies are
// 28 and 27 LATIN letters, and they expect text already transliterated. Feeding
// them ኣማርኛ was never going to work — measured on the P30, Amharic lost 43
// distinct characters and produced 3.2 s of noise for a 15 s paragraph. The model
// has simply never seen an Ethiopic codepoint.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of EIGHT
// codepoints is one consonant across its vowel orders.
//
//   ሀ ሁ ሂ ሃ ሄ ህ ሆ ሇ     U+1200..U+1207   h + (ä u i a e ə o oa)
//   ለ ሉ ሊ ላ ሌ ል ሎ ሏ     U+1208..U+120F   l + the same eight
//
// So consonant = (cp - 0x1200) / 8 and vowel = (cp - 0x1200) % 8. Two small
// tables replace three hundred entries, and every character in the block is
// covered including ones no test phrase happens to contain.

using System;
using System.Collections.Generic;
using System.Text;

namespace CircleAI.Voice;

/// <summary>Transliterates Ethiopic script into Latin for uroman-style voices.</summary>
public static class GeezRomanizer
{
    private const int Base = 0x1200;
    private const int OrdersPerConsonant = 8;

    /// <summary>
    /// Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices
    /// hold 27-28 plain Latin letters, so a transliteration carrying ḥ, š or ṣ
    /// would be dropped as surely as the Ethiopic was.
    /// </summary>
    /// <remarks>
    /// Six rows are LABIALISED — the consonant carries a built-in /w/. Writing
    /// ኰ as plain "k" turns ኳ from "kwa" into "ka", which silently changes the
    /// word; እንኳን ("welcome") came out "nkan" instead of "enkwan".
    /// </remarks>
    private static readonly string[] Consonants =
    {
        "h",  "l",  "h",  "m",  "s",  "r",  "s",  "sh",   // ሀ ለ ሐ መ ሠ ረ ሰ ሸ
        "q",  "qw", "q",  "qw", "b",  "v",  "t",  "ch",   // ቀ ቈ ቐ ቘ በ ቨ ተ ቸ
        "h",  "hw", "n",  "ny", "",   "k",  "kw", "k",    // ኀ ኈ ነ ኘ አ ከ ኰ ኸ
        "kw", "w",  "",   "z",  "zh", "y",  "d",  "d",    // ዀ ወ ዐ ዘ ዠ የ ደ ዸ
        "j",  "g",  "gw", "ng", "t",  "ch", "p",  "ts",   // ጀ ገ ጐ ጘ ጠ ጨ ጰ ጸ
        "ts", "f",  "p",  "ry", "my", "fy"                // ፀ ፈ ፐ and rare tail rows
    };

    /// <summary>
    /// Vowel per order. The sixth (ə) is silent — it marks a bare consonant,
    /// which is why ሰላም is "selam" and not "selami".
    /// </summary>
    private static readonly string[] Vowels = { "e", "u", "i", "a", "e", "", "o", "wa" };

    /// <summary>
    /// Rows whose consonant is a glottal or pharyngeal that Latin does not write.
    /// With no consonant in front of it the first-order vowel is heard as "a",
    /// which is why አማርኛ is "amarnya" rather than "emarnya".
    /// </summary>
    private static readonly HashSet<int> SilentConsonantRows = new() { 20, 26 };  // አ, ዐ

    /// <summary>Ethiopic punctuation, mapped so sentence splitting still works.</summary>
    private static readonly Dictionary<char, string> Punctuation = new()
    {
        ['፠'] = " ",   // ፠ section
        ['፡'] = " ",   // ፡ word separator
        ['።'] = ".",   // ። full stop
        ['፣'] = ",",   // ፣ comma
        ['፤'] = ";",   // ፤ semicolon
        ['፥'] = ":",   // ፥ colon
        ['፦'] = ":",   // ፦ preface colon
        ['፧'] = "?",   // ፧ question mark
        ['፨'] = " ",   // ፨ paragraph separator
    };

    /// <summary>True when <paramref name="text"/> contains any Ethiopic character.</summary>
    public static bool IsEthiopic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
            if (c is >= 'ሀ' and <= '፿' or >= 'ᎀ' and <= '᎟') return true;
        return false;
    }

    /// <summary>
    /// Ethiopic → Latin. Characters outside the script pass through untouched, so
    /// mixed text (numerals, Latin names, punctuation) survives intact.
    /// </summary>
    public static string Romanize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (Punctuation.TryGetValue(c, out var p)) { sb.Append(p); continue; }

            var i = c - Base;
            if (i < 0 || i >= Consonants.Length * OrdersPerConsonant)
            {
                // Ethiopic digits and rarely-used supplement blocks have no sound
                // we can render; anything else is not Ethiopic and is left alone.
                if (c is >= '፩' and <= '፼') continue;
                sb.Append(c);
                continue;
            }

            var row = i / OrdersPerConsonant;
            var order = i % OrdersPerConsonant;

            var consonant = Consonants[row];
            var vowel = Vowels[order];

            if (consonant.Length == 0)
            {
                // The glottal and pharyngeal rows write no consonant in Latin, so
                // the vowel IS the character. First order is heard as "a" (አማርኛ →
                // "amarnya"), and the sixth — silent after a real consonant — must
                // still sound here, or እ disappears entirely and "enkwan" collapses
                // to "nkan".
                if (order == 0) vowel = "a";
                else if (vowel.Length == 0) vowel = "e";
            }

            sb.Append(consonant).Append(vowel);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Romanises Ethiopic, then hands the Latin result on as individual characters.
/// </summary>
/// <remarks>
/// Sits where <see cref="PassthroughPhonemizer"/> would for a grapheme voice. The
/// model's tokens ARE Latin letters, so once the script is converted the rest of
/// the pipeline is unchanged.
/// </remarks>
public sealed class GeezPhonemizer : IPhonemizer
{
    /// <summary>The romanised form of the last call — for reporting what was actually spoken.</summary>
    public string LastRomanised { get; private set; } = "";

    public IReadOnlyList<string> Phonemize(string text)
    {
        LastRomanised = GeezRomanizer.Romanize(text);
        return string.IsNullOrEmpty(LastRomanised)
            ? Array.Empty<string>()
            : PiperVoiceConfig.SplitPhonemeString(LastRomanised);
    }
}
