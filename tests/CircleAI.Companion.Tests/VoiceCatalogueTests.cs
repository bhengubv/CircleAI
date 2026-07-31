using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace CircleAI.Companion.Tests;

/// <summary>
/// Guards the shipped voice catalogue.
/// </summary>
/// <remarks>
/// Every voice here was verified speaking on a Huawei P30 Lite, then catalogued so
/// a phone can fetch it on demand. The failure this protects against is subtle: an
/// entry with a wrong or missing hash downloads and then fails verification on the
/// user's phone, after they have spent the bandwidth. A malformed entry must fail
/// here, on a build machine, not there.
/// </remarks>
public class VoiceCatalogueTests
{
    private static JsonElement Registry()
    {
        // Walk up to the repo root — test binaries run from bin/<cfg>/<tfm>.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "CircleAI.Core", "Models", "embedded_registry.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("embedded_registry.json not found from " + AppContext.BaseDirectory);
    }

    private static List<JsonElement> Voices() =>
        Registry().GetProperty("Models").EnumerateArray()
            .Where(m => m.TryGetProperty("Modality", out var mo) && mo.GetString() == "Tts")
            .ToList();

    [Fact]
    public void Catalogues_every_voice_proven_on_the_device()
    {
        // 58 bundles covering 56 language tags, plus the two English voices that
        // predate this work. A regression that silently drops entries would leave
        // users of those languages with no voice and no error.
        Assert.True(Voices().Count >= 58, $"expected >= 58 TTS entries, found {Voices().Count}");
    }

    [Fact]
    public void Every_voice_carries_a_verifiable_hash_for_every_file()
    {
        // Downloading 110 MB over a cheap phone's data and THEN failing the hash
        // is the worst outcome for the person this is built for.
        foreach (var v in Voices())
        {
            var name = v.GetProperty("Name").GetString();
            Assert.True(v.TryGetProperty("BundleFiles", out var files),
                $"{name}: no BundleFiles — nothing to download");

            foreach (var f in files.EnumerateArray())
            {
                var sha = f.GetProperty("Sha256").GetString();
                Assert.False(string.IsNullOrWhiteSpace(sha), $"{name}: file without a hash");
                Assert.Equal(64, sha!.Length);                       // SHA-256 hex
                Assert.True(f.GetProperty("SizeBytes").GetInt64() > 0, $"{name}: zero-byte file");
            }
        }
    }

    [Fact]
    public void Every_voice_states_a_language_so_selection_cannot_guess()
    {
        // SpeechModelSelector refuses to hand back a voice whose language does not
        // match the request — an untagged entry is simply never selected, so a
        // missing tag makes a voice invisible rather than wrong. Both are bad.
        foreach (var v in Voices())
        {
            var name = v.GetProperty("Name").GetString();
            Assert.True(v.TryGetProperty("Language", out var lang), $"{name}: no Language tag");
            Assert.False(string.IsNullOrWhiteSpace(lang.GetString()), $"{name}: empty Language tag");
        }
    }

    [Theory]
    [InlineData("sw")]   // Swahili
    [InlineData("yo")]   // Yoruba
    [InlineData("ha")]   // Hausa
    [InlineData("ta")]   // Tamil
    [InlineData("zh")]   // Mandarin
    [InlineData("yue")]  // Cantonese
    [InlineData("ja")]   // Japanese
    [InlineData("ar")]   // Arabic
    [InlineData("ht")]   // Haitian Creole
    [InlineData("qu")]   // Quechua
    [InlineData("tpi")]  // Tok Pisin
    [InlineData("si")]   // Sinhala
    public void A_speaker_of_this_language_can_be_served(string tag)
    {
        var served = Voices().Any(v =>
            v.GetProperty("Language").GetString()!
             .Split(',').Any(t => t.Trim() == tag));

        Assert.True(served, $"nothing catalogued for '{tag}' — a speaker of it gets silence");
    }

    [Fact]
    public void No_single_voice_is_too_large_for_the_target_phone()
    {
        // The P30 Lite is the benchmark: ~1.5 GB free RAM. A voice that cannot load
        // there is not a voice we ship, however good it sounds elsewhere.
        foreach (var v in Voices())
        {
            var name = v.GetProperty("Name").GetString();
            var bytes = v.GetProperty("TotalBytes").GetInt64();
            Assert.True(bytes < 400L * 1024 * 1024,
                $"{name}: {bytes / 1048576} MB is too large for the target device");
        }
    }
}
