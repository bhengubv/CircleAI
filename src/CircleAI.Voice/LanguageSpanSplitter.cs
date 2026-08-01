#nullable enable

// LanguageSpanSplitter.cs
//
// People do not speak one language per sentence.
//
// "Igama lami ngu-CircleAI" is isiZulu with an English name inside it, and read
// wholly in isiZulu the name comes out mangled — the listener hears the machine
// fail at a word they know perfectly well. South African speech is full of this:
// brand names, acronyms, borrowed nouns, all carried inside an African-language
// sentence with an isiZulu or Sesotho prefix glued on the front.
//
// A multi-lingual model takes ONE language id per utterance, so the fix is to cut
// the text where the language changes and synthesise each run under its own id.
// The engine already splits by sentence and joins the audio; this splits on the
// same principle, one level finer.
//
// Detection here is deliberately CONSERVATIVE. It flags only what is unambiguous:
// internal capitals (CircleAI, WhatsApp, YouTube) and short all-caps acronyms
// (GPS, SMS, ATM). It does NOT try to spot ordinary lowercase English words like
// "computer" — that needs a lexicon per language pair, and guessing wrong is
// worse than not guessing: mispronouncing a native word to "fix" a foreign one
// insults the speaker in their own language.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CircleAI.Voice;

/// <summary>A run of text to be spoken in one language.</summary>
/// <param name="Text">The words, with their spacing preserved.</param>
/// <param name="IsForeign">
/// True when this run is the embedded language (English), false for the
/// surrounding one. The caller maps that to whatever ids its model uses.
/// </param>
public readonly record struct LanguageSpan(string Text, bool IsForeign);

/// <summary>Cuts mixed-language text into runs, each spoken in one language.</summary>
public static class LanguageSpanSplitter
{
    /// <summary>
    /// Splits <paramref name="text"/> into spans. Returns a single span when the
    /// text is all one language, which is the overwhelmingly common case — callers
    /// can check <c>Count == 1</c> and take their existing single-language path.
    /// </summary>
    public static IReadOnlyList<LanguageSpan> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<LanguageSpan>();

        var spans = new List<LanguageSpan>();
        var current = new StringBuilder();
        bool? currentIsForeign = null;

        var i = 0;
        while (i < text.Length)
        {
            // Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
            // along with whatever run they follow, so a language change never
            // strands a comma on its own or splits mid-punctuation.
            if (!char.IsLetterOrDigit(text[i]))
            {
                var sepStart = i;
                while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
                current.Append(text[sepStart..i]);
                continue;
            }

            var wordStart = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            var word = text[wordStart..i];
            var foreign = IsForeignWord(word);

            if (currentIsForeign is not null && currentIsForeign != foreign)
            {
                // The run ends at the last word, not at the separators that follow
                // it — those have already been appended and belong to the join.
                spans.Add(new LanguageSpan(current.ToString(), currentIsForeign.Value));
                current.Clear();
            }

            currentIsForeign = foreign;
            current.Append(word);
        }

        if (current.Length > 0 && currentIsForeign is not null)
            spans.Add(new LanguageSpan(current.ToString(), currentIsForeign.Value));

        return spans;
    }

    /// <summary>
    /// Rewrites a run into the form a voice can actually pronounce, without
    /// changing what is displayed.
    /// </summary>
    /// <remarks>
    /// A compound like <c>CircleAI</c> is one token to a synthesiser and it has no
    /// idea where the words are, so it produces a mumble. Written <c>Circle AI</c>
    /// it is two things the voice already knows how to say. This is why the name
    /// came out garbled even after it was correctly switched to English — the
    /// language was right and the word was still unreadable.
    ///
    /// The split is on case boundaries only, which is where the word boundaries
    /// genuinely are in this naming style: <c>CircleAI</c> → <c>Circle AI</c>,
    /// <c>YouTube</c> → <c>You Tube</c>, <c>OpenAPIKey</c> → <c>Open API Key</c>.
    /// Acronyms stay whole, because letters read as letters are correct.
    /// </remarks>
    public static string ToSpokenForm(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 1. Break the compound into words at case boundaries.
        var spaced = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c))
            {
                var prev = text[i - 1];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';

                // lower→Upper is a word boundary (Circle|AI, You|Tube).
                var afterLower = char.IsLower(prev);
                // Upper→Upper→lower ends a run of capitals (API|Key).
                var endOfAcronym = char.IsUpper(prev) && char.IsLower(next);

                if (afterLower || endOfAcronym) spaced.Append(' ');
            }
            spaced.Append(c);
        }

        // 2. Punctuate the acronyms. "AI" as a bare token gets read as a word —
        // "ay" — where "A.I." is read as the letters, which is what it is. Same
        // for GPS, API, SMS. The full stops are for the voice, not the reader.
        var s = spaced.ToString();
        var outp = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length;)
        {
            if (!char.IsUpper(s[i])) { outp.Append(s[i++]); continue; }

            var start = i;
            while (i < s.Length && char.IsUpper(s[i])) i++;
            var run = s[start..i];

            // A lone capital is an ordinary word opening ("Sawubona"), not an
            // acronym, and a run followed by lowercase was already split above.
            if (run.Length < 2) { outp.Append(run); continue; }

            foreach (var ch in run) outp.Append(ch).Append('.');
        }
        return outp.ToString();
    }

    /// <summary>
    /// Is this token unmistakably foreign (English) inside African-language text?
    /// </summary>
    /// <remarks>
    /// Two signals only, both chosen because native orthographies do not produce
    /// them:
    ///
    ///   internal capitals — CircleAI, WhatsApp, MTN's brand spellings
    ///   all-caps, 2-5 letters — GPS, SMS, ATM, PIN
    ///
    /// isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
    /// sentence or a proper noun and nothing else, so neither pattern arises
    /// naturally. A sentence-initial capital is therefore NOT a signal, which is
    /// why only capitals after position zero count.
    /// </remarks>
    public static bool IsForeignWord(string word)
    {
        if (word.Length < 2) return false;

        var upper = 0;
        var lower = 0;
        var hasInternalCapital = false;

        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (!char.IsLetter(c)) continue;
            if (char.IsUpper(c))
            {
                upper++;
                if (i > 0) hasInternalCapital = true;
            }
            else lower++;
        }

        if (hasInternalCapital && lower > 0) return true;              // CircleAI, WhatsApp
        if (upper >= 2 && lower == 0 && word.Length <= 5) return true; // GPS, SMS, ATM
        return false;
    }
}
