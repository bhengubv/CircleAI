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
    {
        ArgumentNullException.ThrowIfNull(phonemes);
        skipped = 0;

        var ids = new List<long>(64);

        if (_phonemeIdMap.TryGetValue(Bos, out var bos)) ids.AddRange(bos);
        if (_phonemeIdMap.TryGetValue(Pad, out var padAfterBos)) ids.AddRange(padAfterBos);

        _phonemeIdMap.TryGetValue(Pad, out var pad);

        foreach (var p in phonemes)
        {
            if (!_phonemeIdMap.TryGetValue(p, out var mapped))
            {
                skipped++;
                continue;
            }
            ids.AddRange(mapped);
            if (pad is not null) ids.AddRange(pad);
        }

        if (_phonemeIdMap.TryGetValue(Eos, out var eos)) ids.AddRange(eos);

        return ids.ToArray();
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
