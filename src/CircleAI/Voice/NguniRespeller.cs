#nullable enable

// NguniRespeller.cs
//
// Turns an English pronunciation into an isiZulu SPELLING.
//
// The voice is grapheme-driven — the letters are the tokens — so the only way to
// make a borrowed word sound right is to write it the way the host language writes
// it. A curated table (LoanwordRespeller) covers the words people have already
// settled on. This covers the rest, by doing what a speaker does with a word they
// have never seen written: hear it, then spell it in their own orthography.
//
// Two steps, and only the second lives here:
//
//   1. English word → IPA          — espeak, out of process (it is GPL; we never
//                                    link it) or any other G2P
//   2. IPA → isiZulu spelling      — this file
//
// The structural rule is what makes it work at all. Nguni syllables are
// consonant-vowel: no clusters, no word-final consonants. So /kəmˈpjuːtə/ cannot
// be written as it stands — the /mp/ and the final consonant have to be opened out
// with vowels. That is precisely why "computer" is written ikhompiyutha and "SMS"
// is esemese. We are not approximating the language; we are following its rule.
//
// This will not produce the exact spelling a speaker would choose every time —
// conventions vary and some words are simply settled by usage. Where usage HAS
// settled, the curated table wins and this never runs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CircleAI.Voice;

/// <summary>Writes an IPA pronunciation using isiZulu/Nguni orthography.</summary>
public static class NguniRespeller
{
    /// <summary>IPA consonant → the letters isiZulu uses for that sound.</summary>
    /// <remarks>
    /// Aspirated stops take the h that isiZulu writes them with — English initial
    /// /p t k/ are aspirated, and writing them plain would give the ejective-ish
    /// letters instead, which are different sounds in the language rather than a
    /// mild accent. English sounds isiZulu lacks are mapped to the nearest
    /// available: /θ/ and /ð/ to th and d, /r/ to r, /v/ to v.
    /// </remarks>
    private static readonly Dictionary<string, string> Consonants = new()
    {
        ["p"] = "ph", ["b"] = "b",  ["t"] = "th", ["d"] = "d",
        ["k"] = "kh", ["g"] = "g",  ["m"] = "m",  ["n"] = "n",
        ["ŋ"] = "ng", ["f"] = "f",  ["v"] = "v",  ["s"] = "s",
        ["z"] = "z",  ["ʃ"] = "sh", ["ʒ"] = "j",  ["h"] = "h",
        ["l"] = "l",  ["r"] = "r",  ["w"] = "w",  ["j"] = "y",
        ["θ"] = "th", ["ð"] = "d",  ["ʧ"] = "tsh", ["ʤ"] = "j",
        ["tʃ"] = "tsh", ["dʒ"] = "j", ["ɹ"] = "r", ["ɫ"] = "l",
    };

    /// <summary>IPA vowel → the five isiZulu vowels, nearest match.</summary>
    /// <remarks>
    /// isiZulu has five vowels; English has around twenty. This is lossy by
    /// definition and that is fine — a borrowed word is expected to lose its
    /// original vowel qualities. Diphthongs become vowel sequences, which is how
    /// "WiFi" ends up wayifayi.
    /// </remarks>
    private static readonly Dictionary<string, string> Vowels = new()
    {
        ["i"] = "i",  ["ɪ"] = "i",  ["iː"] = "i",  ["e"] = "e",
        ["ɛ"] = "e",  ["æ"] = "a",  ["a"] = "a",   ["ɑ"] = "a",
        ["ɑː"] = "a", ["ʌ"] = "a",  ["ə"] = "e",   ["ɜ"] = "e",
        ["ɜː"] = "e", ["ɒ"] = "o",  ["ɔ"] = "o",   ["ɔː"] = "o",
        ["o"] = "o",  ["oʊ"] = "o", ["u"] = "u",   ["ʊ"] = "u",
        ["uː"] = "u", ["aɪ"] = "ayi", ["aʊ"] = "awu", ["ɔɪ"] = "oyi",
        ["eɪ"] = "eyi", ["ɪə"] = "iye", ["eə"] = "eya", ["ʊə"] = "uwa",
    };

    private const string DefaultVowel = "e";   // the vowel epenthesis reaches for

    /// <summary>
    /// Writes <paramref name="ipa"/> in isiZulu orthography, obeying the
    /// consonant-vowel rule.
    /// </summary>
    public static string FromIpa(string? ipa)
    {
        if (string.IsNullOrWhiteSpace(ipa)) return string.Empty;

        var units = Parse(ipa);
        var sb = new StringBuilder(units.Count * 2);
        var pendingConsonant = false;

        foreach (var (text, isVowel) in units)
        {
            if (isVowel)
            {
                sb.Append(text);
                pendingConsonant = false;
                continue;
            }

            // Two consonants in a row cannot stand: open the cluster with a vowel.
            // This is the rule that turns /mp/ into "mpi"-shaped syllables and is
            // why the written forms look longer than the English.
            if (pendingConsonant) sb.Append(DefaultVowel);
            sb.Append(text);
            pendingConsonant = true;
        }

        // A word cannot end on a consonant either.
        if (pendingConsonant) sb.Append(DefaultVowel);

        return sb.ToString();
    }

    /// <summary>Splits IPA into consonant and vowel units, longest match first.</summary>
    private static List<(string Text, bool IsVowel)> Parse(string ipa)
    {
        var units = new List<(string, bool)>();
        var i = 0;

        while (i < ipa.Length)
        {
            // Stress marks, length marks handled with their vowel, and anything
            // else we do not model: skip rather than emit a letter for it.
            var c = ipa[i];
            if (c is 'ˈ' or 'ˌ' or '.' or ' ' or '͡' ||
                CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                i++;
                continue;
            }

            var matched = false;
            for (var len = Math.Min(2, ipa.Length - i); len >= 1 && !matched; len--)
            {
                var slice = ipa.Substring(i, len);

                // Try the vowel WITH a following length mark, so /iː/ is one unit.
                if (i + len < ipa.Length && ipa[i + len] == 'ː' && Vowels.TryGetValue(slice + "ː", out var longV))
                {
                    units.Add((longV, true));
                    i += len + 1;
                    matched = true;
                }
                else if (Vowels.TryGetValue(slice, out var v))
                {
                    units.Add((v, true));
                    i += len;
                    matched = true;
                }
                else if (Consonants.TryGetValue(slice, out var cns))
                {
                    units.Add((cns, false));
                    i += len;
                    matched = true;
                }
            }

            if (!matched) i++;      // a symbol we do not model contributes nothing
        }

        return units;
    }
}
