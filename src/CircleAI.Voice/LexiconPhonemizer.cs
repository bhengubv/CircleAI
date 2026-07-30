#nullable enable

// LexiconPhonemizer.cs
//
// Turns text into phonemes by DICTIONARY LOOKUP, for the languages whose script
// does not encode pronunciation.
//
// Chinese characters carry meaning, not sound, so no character-driven model can
// read them and no letter-to-sound rule can help. The usual answer is a Python
// G2P library (pypinyin, jieba, MeCab) — which cannot run on the phone. But the
// sherpa-onnx builds of these voices ship the mapping as a plain lexicon.txt
// beside the model: 195,828 entries for Mandarin, 21,806 for Cantonese. That is
// a lookup table, and a lookup table is something C# can do on a Kirin 710.
//
// This is the same shape as NchltPhonemizer, which already serves the eleven
// South African languages from CC-BY dictionary data.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CircleAI.Voice;

/// <summary>
/// Supplies the parallel TONE ids some voices require alongside phonemes.
/// </summary>
/// <remarks>
/// MeloTTS declares a <c>tones</c> input beside <c>x</c>: tone is a separate
/// channel, not a symbol in the phoneme sequence. Cantonese instead writes tone
/// into the phoneme string itself (<c>˥</c>), so it needs none of this.
/// </remarks>
public interface IToneSource
{
    /// <summary>Tone id per phoneme from the last call. Empty when the voice has no tone channel.</summary>
    IReadOnlyList<long> LastTones { get; }
}

/// <summary>
/// Phonemizes by longest-match lookup against a shipped <c>lexicon.txt</c>.
/// </summary>
public sealed class LexiconPhonemizer : IPhonemizer, IToneSource
{
    private readonly Dictionary<string, (string[] Phones, long[] Tones)> _lexicon;
    private readonly int _longestEntry;
    private List<long> _tones = new();

    /// <inheritdoc />
    public IReadOnlyList<long> LastTones => _tones;

    /// <summary>Entries loaded — 0 means every lookup will fail.</summary>
    public int EntryCount => _lexicon.Count;

    /// <summary>Words the last call could not find, in order of first appearance.</summary>
    public IReadOnlyList<string> LastUnknownWords { get; private set; } = Array.Empty<string>();

    private LexiconPhonemizer(Dictionary<string, (string[], long[])> lexicon, int longest)
    {
        _lexicon = lexicon;
        _longestEntry = longest;
    }

    /// <summary>Loads a sherpa-onnx style <c>lexicon.txt</c>.</summary>
    /// <remarks>
    /// Each line is <c>word phone phone … [tone tone …]</c>. Mandarin appends one
    /// tone digit per phone; Cantonese appends none and carries tone inside the
    /// phone symbols. The two are told apart by shape rather than by a flag,
    /// because the file itself is the only thing that knows.
    /// </remarks>
    public static LexiconPhonemizer Load(string lexiconPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexiconPath);
        if (!File.Exists(lexiconPath))
            throw new FileNotFoundException($"lexicon not found: {lexiconPath}", lexiconPath);

        var map = new Dictionary<string, (string[], long[])>(StringComparer.Ordinal);
        var longest = 1;

        foreach (var raw in File.ReadLines(lexiconPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;      // a word with no pronunciation is unusable

            var word = parts[0];
            var rest = parts.AsSpan(1);

            // Trailing run of bare integers, exactly half the remainder, is the
            // tone channel. Anything else is all phonemes.
            var phones = rest.Length;
            long[] tones = Array.Empty<long>();
            if (rest.Length % 2 == 0)
            {
                var half = rest.Length / 2;
                var tail = true;
                for (int i = half; i < rest.Length; i++)
                    if (!long.TryParse(rest[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    { tail = false; break; }

                if (tail)
                {
                    phones = half;
                    tones = new long[half];
                    for (int i = 0; i < half; i++)
                        tones[i] = long.Parse(rest[half + i], CultureInfo.InvariantCulture);
                }
            }

            var ph = new string[phones];
            for (int i = 0; i < phones; i++) ph[i] = rest[i];

            map[word] = (ph, tones);
            if (word.Length > longest) longest = word.Length;
        }

        return new LexiconPhonemizer(map, longest);
    }

    /// <summary>
    /// Text → phoneme symbols, matching the longest lexicon entry at each point.
    /// </summary>
    /// <remarks>
    /// Chinese is written without spaces, so the segmentation IS the lookup:
    /// scanning for the longest entry that matches from the current position is
    /// the standard maximum-matching approach, and it is what makes multi-character
    /// words come out as words rather than as strings of isolated characters.
    /// </remarks>
    public IReadOnlyList<string> Phonemize(string text)
    {
        var phones = new List<string>();
        var tones = new List<long>();
        var unknown = new List<string>();
        if (string.IsNullOrEmpty(text)) { _tones = tones; LastUnknownWords = unknown; return phones; }

        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            var matched = false;
            var max = Math.Min(_longestEntry, text.Length - i);
            for (var len = max; len >= 1; len--)
            {
                var candidate = text.Substring(i, len);
                if (!_lexicon.TryGetValue(candidate, out var entry) &&
                    !_lexicon.TryGetValue(candidate.ToLowerInvariant(), out entry))
                    continue;

                phones.AddRange(entry.Phones);
                // A voice with a tone channel needs one tone per phone; pad with 0
                // so the two arrays never drift out of step, which would silently
                // apply the wrong tone to every syllable after the first gap.
                for (var k = 0; k < entry.Phones.Length; k++)
                    tones.Add(k < entry.Tones.Length ? entry.Tones[k] : 0);

                i += len;
                matched = true;
                break;
            }

            if (!matched)
            {
                var ch = char.ConvertFromUtf32(char.IsHighSurrogate(text[i]) && i + 1 < text.Length
                    ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i]);
                if (!unknown.Contains(ch)) unknown.Add(ch);
                i += ch.Length;
            }
        }

        _tones = tones;
        LastUnknownWords = unknown;
        return phones;
    }
}
