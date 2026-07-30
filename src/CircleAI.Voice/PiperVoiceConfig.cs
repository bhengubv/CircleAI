#nullable enable

// PiperVoiceConfig.cs
//
// Parses a Piper voice's `.onnx.json` sidecar and turns a phoneme sequence into
// the exact token-id layout the VITS model was trained on.
//
// This is the half that OnnxTtsEngine was missing. The old engine mapped each
// TEXT character to codepoint+1 and interleaved zeros — feeding the model ids it
// was never trained on, which is why it produced silence/garbage. The model does
// not take characters; it takes PHONEME ids from `phoneme_id_map`, in Piper's
// specific BOS / pad / EOS layout, at the sample rate and scales the config
// declares. All of that lives here, read from the real config, not guessed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CircleAI.Voice;

/// <summary>
/// A Piper voice's inference parameters + phoneme→id map, parsed from its
/// <c>&lt;model&gt;.onnx.json</c> sidecar.
/// </summary>
public sealed class PiperVoiceConfig
{
    // Piper's special phoneme symbols (piper-phonemize defaults).
    private const string Pad = "_";  // id 0 — interspersed between phonemes
    private const string Bos = "^";  // id 1 — beginning of sentence
    private const string Eos = "$";  // id 2 — end of sentence

    private readonly IReadOnlyDictionary<string, long[]> _phonemeIdMap;

    public int SampleRate { get; }
    public float NoiseScale { get; }
    public float LengthScale { get; }
    public float NoiseW { get; }

    /// <summary>e.g. <c>espeak</c> (needs a phonemizer) or <c>text</c> (graphemes are phonemes).</summary>
    public string PhonemeType { get; }

    private PiperVoiceConfig(
        IReadOnlyDictionary<string, long[]> map,
        int sampleRate, float noiseScale, float lengthScale, float noiseW, string phonemeType)
    {
        _phonemeIdMap = map;
        SampleRate = sampleRate;
        NoiseScale = noiseScale;
        LengthScale = lengthScale;
        NoiseW = noiseW;
        PhonemeType = phonemeType;
    }

    /// <summary>True when this config has a usable phoneme→id map.</summary>
    public bool HasPhonemeMap => _phonemeIdMap.Count > 0;

    /// <summary>
    /// True when the vocabulary contains Ethiopic characters.
    /// </summary>
    /// <remarks>
    /// A voice that holds only Latin cannot read Ge'ez, however Ethiopic the text
    /// is — MMS ships Amharic and Tigrinya expecting romanised input. Asking the
    /// vocabulary is more reliable than trusting a flag to be set correctly.
    /// </remarks>
    public bool HasEthiopic
    {
        get
        {
            foreach (var k in _phonemeIdMap.Keys)
                foreach (var c in k)
                    if (c is >= 'ሀ' and <= '፿') return true;
            return false;
        }
    }

    /// <summary>
    /// The conventional sidecar path for a model file:
    /// <c>en_US-lessac-medium.onnx</c> → <c>en_US-lessac-medium.onnx.json</c>.
    /// </summary>
    public static string SidecarPathFor(string modelPath) => modelPath + ".json";

    /// <summary>Loads from the sidecar next to <paramref name="modelPath"/>, or null if absent.</summary>
    public static PiperVoiceConfig? TryLoadForModel(string modelPath)
    {
        var sidecar = SidecarPathFor(modelPath);
        return File.Exists(sidecar) ? Load(sidecar) : null;
    }

    /// <summary>Parses a Piper <c>.onnx.json</c> config file.</summary>
    public static PiperVoiceConfig Load(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        return Parse(doc.RootElement);
    }

    /// <summary>Parses a Piper config from an already-loaded JSON root (used by tests).</summary>
    public static PiperVoiceConfig Parse(JsonElement root)
    {
        var sampleRate = 22050;
        if (root.TryGetProperty("audio", out var audio) &&
            audio.TryGetProperty("sample_rate", out var sr) &&
            sr.TryGetInt32(out var srv))
            sampleRate = srv;

        float noise = 0.667f, length = 1.0f, noiseW = 0.8f;
        if (root.TryGetProperty("inference", out var inf))
        {
            noise  = ReadFloat(inf, "noise_scale",  noise);
            length = ReadFloat(inf, "length_scale", length);
            noiseW = ReadFloat(inf, "noise_w",      noiseW);
        }

        var phonemeType = root.TryGetProperty("phoneme_type", out var pt) && pt.ValueKind == JsonValueKind.String
            ? pt.GetString() ?? "espeak"
            : "espeak";

        var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("phoneme_id_map", out var pim) && pim.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in pim.EnumerateObject())
            {
                var ids = new List<long>();
                foreach (var idEl in prop.Value.EnumerateArray())
                    if (idEl.TryGetInt64(out var id)) ids.Add(id);
                map[prop.Name] = ids.ToArray();
            }
        }

        return new PiperVoiceConfig(map, sampleRate, noise, length, noiseW, phonemeType);
    }

    private static float ReadFloat(JsonElement obj, string name, float fallback)
        => obj.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? (float)d : fallback;

    /// <summary>
    /// Turns a phoneme sequence into model token ids, in piper-phonemize's exact
    /// layout with <c>interspersePad</c> on (Piper's default):
    /// <c>[BOS, PAD, id(p1), PAD, id(p2), PAD, …, id(pN), PAD, EOS]</c>.
    /// Phonemes absent from the map are skipped (and counted), matching Piper —
    /// a single unknown symbol must not abort the whole utterance.
    /// </summary>
    public long[] PhonemesToIds(IEnumerable<string> phonemes, out int skipped)
        => PhonemesToIds(phonemes, out skipped, out _);

    /// <summary>
    /// As <see cref="PhonemesToIds(IEnumerable{string}, out int)"/>, additionally
    /// reporting WHICH symbols were dropped. Callers should surface these: a
    /// dropped symbol is inaudible, so the only evidence of a broken front-end is
    /// this list.
    /// </summary>
    public long[] PhonemesToIds(
        IEnumerable<string> phonemes, out int skipped, out IReadOnlyList<string> skippedSymbols)
        => PhonemesToIds(phonemes, out skipped, out skippedSymbols, out _);

    /// <summary>
    /// As above, also reporting symbols that were APPROXIMATED rather than spoken
    /// exactly — a diacritic the voice lacks, folded to its base letter.
    /// </summary>
    /// <remarks>
    /// An approximation is a compromise, not a success, and it must be visible.
    /// Sepedi's <c>š</c> and Tshivenda's <c>ṱ ḓ ṋ</c> are absent from this voice's
    /// vocabulary; folding them to <c>s t d n</c> keeps the word audible instead of
    /// deleting a consonant mid-word, but it is not the language's true sound and
    /// a native speaker will hear the difference.
    /// </remarks>
    public long[] PhonemesToIds(
        IEnumerable<string> phonemes, out int skipped,
        out IReadOnlyList<string> skippedSymbols, out IReadOnlyList<string> approximatedSymbols)
    {
        ArgumentNullException.ThrowIfNull(phonemes);
        skipped = 0;
        var dropped = new List<string>();
        var approximated = new List<string>();

        var ids = new List<long>(64);

        if (_phonemeIdMap.TryGetValue(Bos, out var bos)) ids.AddRange(bos);
        if (_phonemeIdMap.TryGetValue(Pad, out var padAfterBos)) ids.AddRange(padAfterBos);

        _phonemeIdMap.TryGetValue(Pad, out var pad);

        foreach (var p in phonemes)
        {
            if (!TryMapSymbol(p, out var mapped, out var wasApproximated))
            {
                skipped++;
                if (!dropped.Contains(p)) dropped.Add(p);
                continue;
            }
            if (wasApproximated && !approximated.Contains(p)) approximated.Add(p);
            ids.AddRange(mapped);
            if (pad is not null) ids.AddRange(pad);
        }

        if (_phonemeIdMap.TryGetValue(Eos, out var eos)) ids.AddRange(eos);

        skippedSymbols = dropped;
        approximatedSymbols = approximated;
        return ids.ToArray();
    }

    /// <summary>
    /// Looks up one symbol, falling back to its lower-case form.
    /// </summary>
    /// <remarks>
    /// A grapheme voice's vocabulary is built AFTER the training text has been
    /// through the model's own cleaner, and every cleaner in use here lower-cases
    /// (Coqui's <c>multilingual_cleaners</c>, MMS's uroman path). Such a vocab
    /// therefore contains no capitals at all, so matching on the raw character
    /// silently discarded every sentence-initial letter and every proper noun —
    /// the model received "awubona" for "Sawubona". Exact match is tried first,
    /// so a genuinely case-bearing vocabulary is unaffected.
    /// </remarks>
    private bool TryMapSymbol(string symbol, out long[] ids, out bool approximated)
    {
        approximated = false;

        if (_phonemeIdMap.TryGetValue(symbol, out ids!)) return true;

        var lower = symbol.ToLowerInvariant();
        if (!string.Equals(lower, symbol, StringComparison.Ordinal)
            && _phonemeIdMap.TryGetValue(lower, out ids!)) return true;

        // A GRAPHEME CLUSTER the vocabulary stores as separate codepoints.
        //
        // Text is split into clusters, so Burmese "ကြို" (consonant + medial +
        // two vowel signs) arrives as ONE symbol while the voice's vocabulary
        // holds each codepoint on its own. Measured on the P30: Burmese lost
        // twelve distinct clusters outright, and Thai — whose vowels are also
        // combining marks — was mangled far more quietly. Its clusters fell
        // through to the diacritic fold below, which threw the vowel sign away
        // and kept the bare consonant, turning a 15 s paragraph into 4.3 s of
        // consonants that reported no error whatsoever.
        //
        // Splitting the cluster back into codepoints keeps every mark, so this
        // must be tried BEFORE any approximation.
        if (symbol.Length > 1)
        {
            var parts = new List<long>();
            var whole = true;
            foreach (var rune in symbol.EnumerateRunes())
            {
                // Zero-width formatting characters shape how text is DRAWN and say
                // nothing about how it sounds. Persian writes them constantly
                // (زبان‌های), as do Hindi, Bengali, Urdu and most Indic scripts —
                // and because they bind into the grapheme cluster, one invisible
                // character was failing the whole cluster. Measured on the P30:
                // Persian and Telugu each lost real consonants to a mark nobody
                // can even see.
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;

                var s = rune.ToString();
                if (_phonemeIdMap.TryGetValue(s, out var part) ||
                    _phonemeIdMap.TryGetValue(s.ToLowerInvariant(), out part))
                    parts.AddRange(part);
                else { whole = false; break; }
            }
            if (whole && parts.Count > 0)
            {
                ids = parts.ToArray();
                return true;   // exact, not an approximation — nothing was lost
            }
        }

        // A letter the voice never learned. Dropping it deletes a consonant from
        // the middle of a word — measured on this voice, Sepedi lost every 'š' and
        // Tshivenda every 'ṱ ḓ ṋ', which is most of what makes those two languages
        // sound like themselves. An approximation is worth more than a hole, so
        // long as it is declared rather than passed off as correct.
        foreach (var candidate in Approximations(symbol))
        {
            if (_phonemeIdMap.TryGetValue(candidate, out ids!)
                || _phonemeIdMap.TryGetValue(candidate.ToLowerInvariant(), out ids!))
            {
                approximated = true;
                return true;
            }
        }

        ids = Array.Empty<long>();
        return false;
    }

    /// <summary>
    /// Nearest stand-ins for <paramref name="symbol"/>, best first: an exact
    /// phonetic equivalent where one exists, then the base letter with its
    /// diacritics stripped.
    /// </summary>
    private static IEnumerable<string> Approximations(string symbol)
    {
        // Where the vocabulary happens to carry the true phoneme under a different
        // spelling, use it — Tshivenda's 'ṅ' IS /ŋ/, and 'ŋ' is in this map, so
        // that substitution loses nothing at all.
        if (symbol is "ṅ" or "Ṅ") yield return "ŋ";
        if (symbol is "š" or "Š") yield return "ʃ";

        // Folding a diacritic away is only defensible where the mark modifies a
        // letter that still carries most of the sound without it — Latin 'š'→'s',
        // 'ṱ'→'t'. In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks
        // ARE the vowels and tones; dropping them does not approximate the word,
        // it deletes it. Thai measured 4.3 s instead of ~15 s because every vowel
        // sign was folded off a consonant and the result was filed as a harmless
        // approximation.
        var stripped = StripDiacritics(symbol);
        if (stripped.Length == 0 || string.Equals(stripped, symbol, StringComparison.Ordinal))
            yield break;

        // Judge the BASE that remains, not the composed character: Tshivenda 'ṱ'
        // lives in Latin Extended Additional (U+1E71), far above the Latin block,
        // yet strips to a plain 't'. Thai 'วั' strips to 'ว', which is not Latin
        // at all — and that is the case that must be refused.
        if (!IsLatinBase(stripped)) yield break;

        yield return stripped;
    }

    /// <summary>
    /// True when the symbol's base character is Latin, i.e. when stripping its
    /// marks leaves a letter that still approximates the original sound.
    /// </summary>
    private static bool IsLatinBase(string stripped)
    {
        foreach (var rune in stripped.EnumerateRunes())
            if (rune.Value > 0x024F) return false;   // beyond Latin Extended-B
        return stripped.Length > 0;
    }

    /// <summary>Decomposes and removes combining marks: <c>ṱ</c> → <c>t</c>.</summary>
    private static string StripDiacritics(string s)
    {
        string decomposed;
        try { decomposed = s.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { return s; }   // not normalisable — leave it alone

        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Splits a phoneme STRING into individual phoneme symbols by Unicode
    /// codepoint (rune) — how espeak/Piper enumerate phonemes. Callers that
    /// already have a symbol list should use <see cref="PhonemesToIds"/> directly.
    /// </summary>
    public static IReadOnlyList<string> SplitPhonemeString(string phonemeString)
    {
        var list = new List<string>(phonemeString.Length);
        var e = StringInfo.GetTextElementEnumerator(phonemeString);
        while (e.MoveNext())
            list.Add((string)e.Current);
        return list;
    }
}
