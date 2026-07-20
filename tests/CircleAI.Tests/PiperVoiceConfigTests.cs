// PiperVoiceConfigTests.cs
//
// The token layout is the whole reason OnnxTtsEngine could not speak: it fed the
// model codepoint+1 instead of phoneme ids in Piper's BOS/pad/EOS layout. These
// tests pin the layout against piper-phonemize's reference algorithm
// (interspersePad on): [BOS, PAD, id, PAD, id, PAD, ..., id, PAD, EOS].
//
// No model needed — pure token math, so it runs in milliseconds and cannot
// regress silently.

using System.Text.Json;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class PiperVoiceConfigTests
{
    // A tiny synthetic Piper config: real special-token ids, a few phonemes.
    private const string Json = """
    {
      "audio": { "sample_rate": 22050 },
      "inference": { "noise_scale": 0.667, "length_scale": 1.0, "noise_w": 0.8 },
      "phoneme_type": "espeak",
      "phoneme_id_map": {
        "_": [0], "^": [1], "$": [2], " ": [3],
        "h": [20], "a": [14], "i": [21]
      }
    }
    """;

    private static PiperVoiceConfig Config()
    {
        using var doc = JsonDocument.Parse(Json);
        return PiperVoiceConfig.Parse(doc.RootElement);
    }

    [Fact]
    public void ParsesSampleRateAndScales()
    {
        var c = Config();
        Assert.Equal(22050, c.SampleRate);
        Assert.Equal(0.667f, c.NoiseScale, 3);
        Assert.Equal(1.0f, c.LengthScale, 3);
        Assert.Equal(0.8f, c.NoiseW, 3);
        Assert.Equal("espeak", c.PhonemeType);
        Assert.True(c.HasPhonemeMap);
    }

    [Fact]
    public void PhonemesToIds_MatchesPiperLayout()
    {
        var c = Config();
        var ids = c.PhonemesToIds(new[] { "h", "a", "i" }, out var skipped);

        // BOS, PAD, then each phoneme followed by PAD, then EOS.
        Assert.Equal(new long[] { 1, 0, 20, 0, 14, 0, 21, 0, 2 }, ids);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void UnknownPhonemes_AreSkipped_NotCrashed()
    {
        // A single symbol espeak emits that the voice lacks must not abort the
        // whole utterance — Piper skips it and keeps going.
        var c = Config();
        var ids = c.PhonemesToIds(new[] { "h", "Z", "i" }, out var skipped);

        Assert.Equal(new long[] { 1, 0, 20, 0, 21, 0, 2 }, ids);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void SplitPhonemeString_SplitsByCodepoint_IncludingIpa()
    {
        // The passthrough/espeak phonemizers hand the engine a string; it must
        // split into individual symbols the map can look up — including IPA.
        var parts = PiperVoiceConfig.SplitPhonemeString("həl");
        Assert.Equal(new[] { "h", "ə", "l" }, parts);
    }

    [Fact]
    public void PassthroughPhonemizer_TreatsInputAsPhonemes()
    {
        var p = new PassthroughPhonemizer().Phonemize("hai");
        Assert.Equal(new[] { "h", "a", "i" }, p);
    }

    [Fact]
    public void SidecarPath_IsModelPlusJson()
    {
        Assert.Equal(
            "/models/en_US-lessac-medium.onnx.json",
            PiperVoiceConfig.SidecarPathFor("/models/en_US-lessac-medium.onnx"));
    }
}
