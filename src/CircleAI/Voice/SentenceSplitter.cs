#nullable enable

// SentenceSplitter.cs
//
// Cuts a passage into the units a VITS voice should synthesise one at a time.
//
// Why this has to exist: the voices in use here were trained on text with the
// punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
// ':' at all — only letters, space, apostrophe and hyphen. Feeding a paragraph
// in one pass therefore produces one unbroken run of speech: no pause between
// sentences, because there is no token that could encode one. The pause has to
// come from outside the model.
//
// It deliberately splits at SENTENCE boundaries only, never at commas. Each
// synthesis is an independent utterance and a VITS model ends every utterance
// with falling, sentence-final prosody. Cutting at a comma would therefore make
// each clause land like a finished sentence — worse prosody than the run-on it
// was meant to fix. A comma's pause is not worth a false full stop.

using System;
using System.Collections.Generic;
using System.Text;

namespace CircleAI.Voice;

/// <summary>One unit of speech, plus the silence that should follow it.</summary>
/// <param name="Text">The text to synthesise. Never empty or whitespace.</param>
/// <param name="TrailingPauseMs">
/// Silence to append after this segment, in milliseconds. 0 for the final
/// segment — trailing silence at the end of a passage serves nothing.
/// </param>
public readonly record struct SpeechSegment(string Text, int TrailingPauseMs);

/// <summary>
/// Splits text into sentence-sized units for synthesis.
/// </summary>
public static class SentenceSplitter
{
    // Pause lengths are the perceptual point of this class, so they are named
    // rather than buried. A full stop reads longer than a colon; a paragraph
    // break longer than either.
    private const int SentencePauseMs  = 280;
    private const int ClausePauseMs    = 200;   // ':' and ';' — a lighter break
    private const int ParagraphPauseMs = 400;
    private const int ForcedPauseMs    = 60;    // an over-long run cut for latency

    /// <summary>
    /// Beyond this many characters a segment is cut even without punctuation.
    /// A single unbroken clause of this size is already several seconds of
    /// audio, and on a phone the whole segment must render before ANY of it can
    /// play. The cut is taken at a word boundary and given only a token pause.
    /// </summary>
    public const int MaxCharsPerSegment = 220;

    /// <summary>
    /// Splits <paramref name="text"/> into segments. Returns a single segment
    /// when there is no sentence punctuation, and an empty list for blank input.
    /// </summary>
    public static IReadOnlyList<SpeechSegment> Split(string? text)
    {
        var segments = new List<SpeechSegment>();
        if (string.IsNullOrWhiteSpace(text)) return segments;

        var current = new StringBuilder();
        var pending = SentencePauseMs;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '\r') continue;
            if (c is '\n')
            {
                Flush(segments, current, ParagraphPauseMs);
                continue;
            }

            current.Append(c);

            if (IsTerminator(c) && EndsSentence(text, i))
            {
                Flush(segments, current, c is ':' or ';' ? ClausePauseMs : SentencePauseMs);
                continue;
            }

            if (current.Length >= MaxCharsPerSegment)
            {
                CutAtWordBoundary(segments, current);
            }
        }

        Flush(segments, current, pending);

        // Nothing should follow the last word — a trailing pause is dead air.
        if (segments.Count > 0)
            segments[^1] = segments[^1] with { TrailingPauseMs = 0 };

        return segments;
    }

    /// <summary>
    /// Characters that end a sentence, across the scripts we speak.
    /// </summary>
    /// <remarks>
    /// A Latin-only list silently under-splits every language that punctuates
    /// differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
    /// segments from the same five-sentence text that gave six in eleven other
    /// languages, because Devanagari and Bengali end sentences with the danda
    /// '।' and Urdu with '۔' — none of which were listed. The paragraph ran
    /// together exactly as it did before the splitter existed, for about a
    /// billion people, and nothing failed loudly enough to notice.
    /// </remarks>
    private static bool IsTerminator(char c) => c is
        '.' or '!' or '?' or ':' or ';'                 // Latin / Cyrillic / Greek
        or '।' or '॥'                         // । ॥  danda, double danda — Devanagari, Bengali, Gurmukhi…
        or '۔' or '؟' or '؛'             // ۔ ؟ ؛  Arabic script — Urdu, Arabic, Persian, Pashto
        or '。' or '！' or '？'             // 。！？  CJK ideographic + fullwidth
        or '．' or '：' or '；'             // ．：；  fullwidth
        or '።'                                     // ።  Ethiopic — Amharic, Tigrinya
        or '។'                                     // ។  Khmer khan
        or '၊' or '။';                        // ၊ ။  Myanmar little/section

    /// <summary>
    /// True when the terminator at <paramref name="i"/> really ends a sentence.
    /// </summary>
    /// <remarks>
    /// A period between digits is a decimal ("3.5"), and one followed directly by
    /// a letter is usually an abbreviation or a URL — splitting there would cut a
    /// word in half and insert a pause inside it.
    /// </remarks>
    private static bool EndsSentence(string text, int i)
    {
        // Absorb any run of closing punctuation ("...", "?!", ".").
        int j = i + 1;
        while (j < text.Length && (IsTerminator(text[j]) || text[j] is '"' or '\'' or ')' or ']')) j++;

        if (j >= text.Length) return true;              // end of input

        // Only SOME terminators can appear inside a token — '.' in 3.5 and
        // co.za, ':' in 12:30. For those, a following space is what separates a
        // sentence end from a decimal point. The rest cannot occur mid-token in
        // any script, and demanding a space after them would never split Chinese,
        // Japanese, Khmer, Thai or Burmese at all: those scripts write without
        // spaces between words, so their full stop is followed by the next letter.
        if (!MayOccurInsideAToken(text[i])) return true;

        if (!char.IsWhiteSpace(text[j])) return false;  // 3.5, e.g., co.za

        if (text[i] is '.' && i > 0 && char.IsDigit(text[i - 1]) && j < text.Length
            && j + 1 < text.Length && char.IsDigit(text[j + 1]))
            return false;

        return true;
    }

    /// <summary>
    /// True for terminators that can legitimately appear inside a token, and so
    /// need a following space before they may be read as ending a sentence.
    /// </summary>
    private static bool MayOccurInsideAToken(char c) => c is '.' or ':' or ';';

    /// <summary>
    /// Cuts an over-long run at the last space, so the break lands between words
    /// rather than inside one. With no space to use, the run is left intact —
    /// a mid-word cut would be audibly worse than a long segment.
    /// </summary>
    private static void CutAtWordBoundary(List<SpeechSegment> segments, StringBuilder current)
    {
        var s = current.ToString();
        int cut = s.LastIndexOf(' ');
        if (cut <= 0) return;

        var head = s[..cut].Trim();
        if (head.Length > 0) segments.Add(new SpeechSegment(head, ForcedPauseMs));

        current.Clear();
        current.Append(s[(cut + 1)..]);
    }

    private static void Flush(List<SpeechSegment> segments, StringBuilder current, int pauseMs)
    {
        var s = current.ToString().Trim();
        current.Clear();
        if (s.Length == 0) return;

        // The terminator STAYS in the segment text, deliberately.
        //
        // It is tempting to strip it — this class has already turned it into a
        // pause, and the MMS voices have no token for it. But the SA-11 voice's
        // vocabulary DOES carry '?' and '.', so it can render a real question rise
        // that no inserted silence could imitate. Stripping would have discarded
        // that from all eleven South African languages to tidy up a log line.
        //
        // So: voices that can speak punctuation keep their own prosody and gain a
        // modest pause on top; voices that cannot simply drop the character, and
        // the inserted pause is the only break they get. Diagnostics distinguish
        // the two cases rather than the splitter guessing at the vocabulary.

        // A segment of nothing but punctuation has no sound to make, and the
        // voice has no token for it either.
        bool hasSpeech = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) { hasSpeech = true; break; }
        }
        if (!hasSpeech) return;

        segments.Add(new SpeechSegment(s, pauseMs));
    }
}
