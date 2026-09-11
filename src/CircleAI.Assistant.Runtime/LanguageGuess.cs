// LanguageGuess.cs
//
// Which of the eleven a sentence is in, decided from the WORDS.
//
// WHY NOT THE PICKER I BUILT FIRST. Because a picker is a persistent setting for
// a property that is not persistent. South African households do not speak one
// language; a clan will move through two or three in a single conversation, and a
// person who chose isiZulu on Monday is speaking English by the third sentence.
// Setting it once is wrong almost immediately, and being answered in the language
// you were not just speaking is the exact insult this product exists to avoid.
//
// WHY NOT WHISPER'S OWN DETECTION EITHER. That is AUDIO language identification on
// a tiny model, and it cannot separate the Nguni languages — isiXhosa and siSwati
// both come back as "zu", and a great deal comes back as "und". Audio LID is a
// hard problem being solved badly.
//
// TEXT LID IS A MUCH EASIER PROBLEM, and by the time the turn needs an answer the
// transcript already exists. These languages are lexically far apart in exactly
// the places that matter: the subject concords differ in the first syllable of
// almost every verb, and the high-frequency function words share almost nothing
// across families. isiZulu marks first person "ngi-" where isiXhosa marks it
// "ndi-"; that single contrast separates the two languages that audio LID confuses
// most, and it appears in nearly every sentence a person says about themselves.
//
// Deliberately a SCORER, not a classifier: it returns the best match with a margin
// and an explicit "unsure", so the caller can fall back rather than commit to a
// coin-flip on a two-word utterance.
//
// NOT UNDER Voice/, THOUGH IT WAS AT FIRST. That folder is excluded from the
// chat-only build, so putting it there quietly made the rule voice-only — and
// typing is where this works BEST, since a typed sentence carries none of the
// transcription noise a spoken one does. The rule is "answer in the language you
// were asked in", not "answer in the language you were SPOKEN to in", so it lives
// where both the microphone and the keyboard can reach it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Samples.It;

/// <summary>Guesses which South African language a piece of text is in.</summary>
public static class LanguageGuess
{
    /// <summary>
    /// Markers per language: the words a speaker can hardly avoid using.
    /// </summary>
    /// <remarks>
    /// Function words and concords, not vocabulary. Nouns are borrowed freely
    /// across these languages and prove nothing; "ngiyabonga" and "ndiyabulela"
    /// prove a great deal. Prefix entries end with '-' and match word starts, which
    /// is how the agglutinative languages are caught — the concord is fused to the
    /// verb, so there is no separate word to look for.
    /// </remarks>
    private static readonly (string Code, string[] Markers)[] Table =
    {
        ("en",  new[] { "the", "is", "what", "how", "you", "and", "please", "today", "tell", "can" }),
        ("af",  new[] { "die", "het", "nie", "ek", "jy", "wat", "is", "en", "hoe", "asseblief", "vandag" }),
        // Nguni. ngi- vs ndi- vs ngi/si- is the family's cleanest separator.
        ("zu",  new[] { "ngi-", "ngiya", "uku-", "yini", "kanjani", "wena", "ngicela", "sawubona", "ngoba" }),
        ("xh",  new[] { "ndi-", "ndiya", "uku-", "ntoni", "kunjani", "molo", "ndicela", "kodwa", "ewe" }),
        ("ss",  new[] { "ngi-", "kubona", "yini", "sawubona", "ngiyabonga", "lokhu", "kutsi", "ngicela" }),
        ("nr",  new[] { "ngi-", "lokha", "njani", "ngiyathokoza", "khona", "ukuthi" }),
        // Sotho-Tswana. ke/go/re are shared, so the discriminators are spellings.
        ("st",  new[] { "ke", "ho", "hore", "eng", "joang", "dumela", "kea", "leboha", "haholo" }),
        ("nso", new[] { "ke", "go", "gore", "eng", "bjang", "thobela", "kea", "leboga", "kudu" }),
        ("tn",  new[] { "ke", "go", "gore", "eng", "jang", "dumela", "kea", "leboga", "thata" }),
        ("ts",  new[] { "hi", "ku", "leswaku", "yini", "kwihi", "avuxeni", "ndza", "khensa" }),
        ("ve",  new[] { "ndi", "u", "uri", "mini", "hani", "ndaa", "livhuwa", "vhukuma" }),
    };

    /// <summary>
    /// Which languages are close enough that a tie between them can be broken.
    /// </summary>
    /// <remarks>
    /// WITHIN A FAMILY, GUESSING IS DEFENSIBLE. The Nguni languages share the "ngi-"
    /// concord, so a sentence can genuinely tie isiZulu against siSwati; they are
    /// close enough that the wrong one still reads as a neighbour's dialect. ACROSS
    /// families it is not defensible — isiZulu against Sesotho is not a near miss,
    /// it is a different language — so a cross-family tie stays unsure and the
    /// conversation keeps the language it already had.
    /// </remarks>
    private static readonly string[][] Families =
    {
        new[] { "zu", "xh", "ss", "nr" },   // Nguni
        new[] { "nso", "tn", "st" },        // Sotho-Tswana
    };

    /// <summary>
    /// Which to prefer when the evidence cannot separate two of the same family.
    /// </summary>
    /// <remarks>
    /// FIRST-LANGUAGE SPEAKERS, most to fewest. isiZulu has roughly ten times the
    /// speakers of siSwati or isiNdebele, so on a coin-flip it is the better bet by
    /// an order of magnitude. This orders ties only — it never overrides a language
    /// that the words actually favour.
    /// </remarks>
    private static readonly string[] Prior = { "zu", "xh", "nso", "tn", "st", "ss", "ve", "nr" };

    /// <summary>
    /// The best guess, or null when the text is too short or too ambiguous.
    /// </summary>
    /// <remarks>
    /// Null means "do not change anything" — the caller keeps whatever language the
    /// conversation was already in. That is the right failure: a wrong switch is
    /// far more jarring than no switch, and short utterances ("yes", "thanks") carry
    /// almost no evidence either way.
    /// </remarks>
    /// <summary>
    /// The language a writing system settles on its own, or null for Latin.
    /// </summary>
    /// <remarks>
    /// KANA BEFORE HAN, deliberately. Japanese writes with both — 首都 is Han
    /// inside an otherwise Kana sentence — so a text containing any Kana is
    /// Japanese even when most of its characters are Han. Testing Han first
    /// would call every Japanese sentence Chinese.
    /// <para>
    /// Counted rather than first-hit: a single stray character in a quotation
    /// should not decide the language of a paragraph.
    /// </para>
    /// </remarks>
    internal static string? ScriptOf(string text)
    {
        int hangul = 0, kana = 0, han = 0, letters = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            var c = (int)ch;
            if (c is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF) hangul++;
            else if (c is >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF) kana++;
            else if (c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF) han++;
        }
        if (letters == 0) return null;

        // A tenth of the letters is enough: CJK words are short, and a two-word
        // Japanese reply is still unmistakably Japanese.
        var floor = Math.Max(1, letters / 10);
        if (hangul >= floor) return "ko";
        if (kana >= floor) return "ja";
        if (han >= floor) return "zh";
        return null;
    }

    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // SCRIPT FIRST, BECAUSE IT IS CERTAIN. The marker table below separates
        // languages that share the Latin alphabet, which is a matter of evidence
        // and can be unsure. A writing system is not: Hangul is Korean, Kana is
        // Japanese, Han without Kana is Chinese. Nothing else uses them.
        //
        // Without this, CJK fell through to "unsure" and the previous language
        // stood — so a Japanese question was answered in whatever was spoken
        // last. whisper-tiny is no help either: on this audio it called Korean
        // "Thai" and returned nothing at all for Japanese.
        if (ScriptOf(text) is { } script) return script;

        var words = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+");
        if (words.Length == 0) return null;

        var scores = new Dictionary<string, int>(Table.Length);

        foreach (var (code, markers) in Table)
        {
            var score = 0;
            foreach (var w in words)
            {
                if (w.Length == 0) continue;

                // BEST match per word, not the first. "ngicela" is both isiZulu's
                // whole word and the "ngi-" concord that three Nguni languages
                // share; stopping at the concord threw away the evidence that
                // actually separates them and every such sentence tied.
                var hit = 0;
                foreach (var m in markers)
                {
                    if (m.EndsWith('-'))
                    {
                        if (w.StartsWith(m[..^1], System.StringComparison.Ordinal)) hit = Math.Max(hit, Prefix);
                    }
                    else if (w == m) { hit = Whole; break; }   // nothing beats a whole word
                }
                score += hit;
            }
            scores[code] = score;
        }

        var ranked   = scores.OrderByDescending(kv => kv.Value).ToArray();
        var topScore = ranked[0].Value;
        if (topScore == 0) return null;

        var tied = ranked.Where(kv => kv.Value == topScore).Select(kv => kv.Key).ToArray();
        if (tied.Length == 1) return tied[0];

        // Tied. Break it only when every claimant is from one family, where the
        // wrong pick is a neighbouring language rather than a different one.
        foreach (var family in Families)
        {
            if (!tied.All(family.Contains)) continue;
            foreach (var code in Prior)
                if (tied.Contains(code)) return code;
        }

        // Tied across families: genuinely unsure. Say so and keep the language the
        // conversation already had, which is far less jarring than a wrong switch.
        return null;
    }

    /// <summary>A whole function word matched — strong evidence.</summary>
    private const int Whole = 2;

    /// <summary>A shared concord matched — evidence of the family, little more.</summary>
    private const int Prefix = 1;

    /// <summary>What each language is called, written the way its speakers write it.</summary>
    /// <remarks>
    /// ENDONYMS. A person should see their language as they spell it — "isiZulu",
    /// not "Zulu". Lives here rather than beside the voice because the typed build
    /// needs to name a language too and has no voice stack to ask.
    /// </remarks>
    /// <remarks>
    /// Both code lengths, because callers do not agree on one. Detect returns
    /// two-letter codes, but the transcriber reports ISO-639-3 for several of these
    /// and the voice table accepts either — so "zul" must name isiZulu exactly as
    /// "zu" does, or a turn silently loses its language on the way to the model.
    /// </remarks>
    private static readonly (string Code, string Name)[] Names =
    {
        ("en", "English"),    ("eng", "English"),
        ("af", "Afrikaans"),  ("afr", "Afrikaans"),
        ("nr", "isiNdebele"), ("nbl", "isiNdebele"),
        ("xh", "isiXhosa"),   ("xho", "isiXhosa"),
        ("zu", "isiZulu"),    ("zul", "isiZulu"),
        ("nso", "Sepedi"),    ("ns",  "Sepedi"),
        ("st", "Sesotho"),    ("sot", "Sesotho"),
        ("tn", "Setswana"),   ("tsn", "Setswana"),
        ("ss", "siSwati"),    ("ssw", "siSwati"),
        ("ve", "Tshivenda"),  ("ven", "Tshivenda"),
        ("ts", "Xitsonga"),   ("tso", "Xitsonga"),
    };

    /// <summary>The display name for a code, defaulting to English.</summary>
    public static string NameOf(string? code)
    {
        foreach (var (c, n) in Names)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return n;
        return "English";
    }

    /// <summary>
    /// The name to put in a "reply only in X" instruction, or null when there is
    /// nothing worth saying.
    /// </summary>
    /// <remarks>
    /// Null for English and for anything unrecognised, so an English turn does not
    /// carry a pointless instruction — and so an unknown code cannot order the
    /// model into a language nobody asked for.
    /// </remarks>
    public static string? InstructionNameFor(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var (c, n) in Names)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase))
                return n == "English" ? null : n;
        return null;
    }
}
