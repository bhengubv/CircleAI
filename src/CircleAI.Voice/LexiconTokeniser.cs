#nullable enable

// LexiconTokeniser.cs
//
// Pronunciation as a FILE, which is what makes these voices shippable.
//
// The espeak family needs a phonemizer PROCESS — and because espeak-ng is
// GPL-3.0 it cannot be linked into a permissively licensed app, so it lives in
// a second APK. That breaks the one-app-one-install rule, and it is fragile:
// the OEM memory manager killed that process mid-turn and the assistant went
// silent. Kokoro needs misaki, which is Python and cannot run in-process at all.
//
// This family ships `lexicon.txt` and `tokens.txt` beside the model — a word to
// phoneme table and a phoneme to id table. No process, no runtime, no licence
// wall. It is the same mechanism behind Chinese, Japanese and Cantonese in this
// catalogue.
//
// WHY A HAND-ROLLED LONGEST MATCH RATHER THAN sherpa-onnx. Taking the library
// was tried first and is normally right — reimplementing a maintained tokeniser
// is how a project becomes a laughing stock. Measured on the catalogued Japanese
// voice, though, sherpa scored CER 0.47 against this code's 0.12, because that
// model is declared `language = Chinese` with `jieba = 1` and sherpa correctly
// segments it as Mandarin. The model is bilingual zh-jp; the metadata is not.
// So this is not a replacement for sherpa — it is a narrower reader for a
// specific bilingual model whose declared language is wrong. If the model is
// ever republished with correct metadata, take the library instead.

using System;
using System.Collections.Generic;
using System.IO;

namespace CircleAI.Voice;

/// <summary>Turns text into model tokens using a voice's own lexicon files.</summary>
public sealed class LexiconTokeniser
{
    readonly Dictionary<string, long[]> _words;
    readonly int _longest;

    /// <summary>Blank id, interleaved between tokens when the model expects it.</summary>
    public long Blank { get; init; }

    /// <summary>Symbols the lexicon had no entry for on the last call.</summary>
    public IReadOnlyList<string> LastUnmapped { get; private set; } = Array.Empty<string>();

    LexiconTokeniser(Dictionary<string, long[]> words, int longest)
    {
        _words = words;
        _longest = longest;
    }

    /// <summary>The lexicon beside <paramref name="modelPath"/>, or null if absent.</summary>
    /// <remarks>
    /// Absence is the normal case — most voices in this catalogue are phoneme or
    /// grapheme driven — so this returns null rather than throwing, and the
    /// caller keeps its existing path.
    /// </remarks>
    public static LexiconTokeniser? TryLoadForModel(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir)) return null;

        var lex = Path.Combine(dir, "lexicon.txt");
        var tok = Path.Combine(dir, "tokens.txt");
        if (!File.Exists(lex) || !File.Exists(tok)) return null;

        // tokens.txt is "<symbol> <id>" per line. The symbol may be a space, so
        // split on the LAST space rather than the first.
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(tok))
        {
            var cut = line.LastIndexOf(' ');
            if (cut <= 0 || !long.TryParse(line[(cut + 1)..], out var id)) continue;
            ids[line[..cut]] = id;
        }
        if (ids.Count == 0) return null;

        // lexicon.txt is "<word> <phoneme> <phoneme> ...".
        var words = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var longest = 1;
        foreach (var line in File.ReadLines(lex))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var seq = new List<long>(parts.Length - 1);
            for (var i = 1; i < parts.Length; i++)
                if (ids.TryGetValue(parts[i], out var id)) seq.Add(id);

            if (seq.Count == 0) continue;
            words[parts[0]] = seq.ToArray();
            if (parts[0].Length > longest) longest = parts[0].Length;
        }

        return words.Count == 0 ? null : new LexiconTokeniser(words, longest);
    }

    /// <summary>Segments <paramref name="text"/> and returns the model's tokens.</summary>
    /// <remarks>
    /// LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
    /// overlap: あい, あいさつ and あいかわらず all start the same way, and taking
    /// the shortest would pronounce a different word. Falls back to the single
    /// character when no word matches, which is how a lexicon keyed on both words
    /// and characters degrades gracefully.
    /// </remarks>
    public long[] Encode(string text, bool interleaveBlank = true)
    {
        var outIds = new List<long>(text.Length * 4);
        var unmapped = new List<string>();

        var i = 0;
        while (i < text.Length)
        {
            var taken = 0;
            var max = Math.Min(_longest, text.Length - i);
            for (var len = max; len > 0; len--)
            {
                if (!_words.TryGetValue(text.Substring(i, len), out var seq)) continue;
                outIds.AddRange(seq);
                taken = len;
                break;
            }

            if (taken == 0)
            {
                var c = text[i];
                if (!char.IsWhiteSpace(c)) unmapped.Add(c.ToString());
                taken = 1;
            }
            i += taken;
        }

        LastUnmapped = unmapped;
        if (!interleaveBlank) return outIds.ToArray();

        // add_blank: a blank opens the utterance and follows every token.
        var padded = new List<long>(outIds.Count * 2 + 1) { Blank };
        foreach (var id in outIds) { padded.Add(id); padded.Add(Blank); }
        return padded.ToArray();
    }
}
