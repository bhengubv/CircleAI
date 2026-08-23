// EmbeddedVoiceConfigsTests.cs
//
// The registry pins every voice sidecar by SHA-256. These tests hold the
// embedded copies to those pins.
//
// WHY THIS TEST EARNS ITS PLACE. The sidecars were generated once by a script
// nobody committed, their hashes were written into the registry, and the bytes
// were then lost — so 43 of the 47 addresses 404'd and the failure was invisible
// until a device tried to download one. Nothing in the build noticed, because a
// registry entry is just JSON until someone fetches it. This test is what makes
// the next such drift fail on a laptop instead of on a phone in someone's hand.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class EmbeddedVoiceConfigsTests
{
    private sealed record Pin(string Model, string FileName, string Sha256, long SizeBytes);

    /// <summary>
    /// Every <c>model.onnx.json</c> the embedded registry pins, read from the
    /// same resource the product reads rather than from a path on disk.
    /// </summary>
    private static IReadOnlyList<Pin> SidecarPins()
    {
        var asm = typeof(EmbeddedVoiceConfigs).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("Models.embedded_registry.json", StringComparison.Ordinal));

        using var stream = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(stream);

        var pins = new List<Pin>();
        foreach (var model in doc.RootElement.GetProperty("Models").EnumerateArray())
        {
            if (!model.TryGetProperty("BundleFiles", out var files)) continue;
            var modelName = model.GetProperty("Name").GetString() ?? "?";

            foreach (var f in files.EnumerateArray())
            {
                var fileName = f.GetProperty("Name").GetString() ?? "";
                if (!fileName.EndsWith("model.onnx.json", StringComparison.Ordinal)
                    && !fileName.EndsWith("language_ids.json", StringComparison.Ordinal)) continue;

                pins.Add(new Pin(
                    modelName,
                    fileName,
                    f.GetProperty("Sha256").GetString() ?? "",
                    f.GetProperty("SizeBytes").GetInt64()));
            }
        }
        return pins;
    }

    [Fact]
    public void The_registry_pins_sidecars_at_all()
    {
        // A guard on the guard: if the registry stops pinning sidecars the rest
        // of this class would pass vacuously.
        Assert.NotEmpty(SidecarPins());
    }

    [Fact]
    public void Every_embedded_sidecar_matches_its_registry_pin()
    {
        var mismatches = new List<string>();

        foreach (var pin in SidecarPins())
        {
            var bytes = EmbeddedVoiceConfigs.TryGet(pin.FileName);
            if (bytes is null) continue;   // covered by the coverage test below

            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (sha != pin.Sha256)
                mismatches.Add($"{pin.Model} {pin.FileName}: sha {sha[..12]}… pinned {pin.Sha256[..12]}…");
            if (bytes.LongLength != pin.SizeBytes)
                mismatches.Add($"{pin.Model} {pin.FileName}: {bytes.LongLength} bytes, pinned {pin.SizeBytes}");
        }

        Assert.True(mismatches.Count == 0,
            "Regenerating the sidecars without recalibrating the registry leaves the "
            + "download step rejecting its own embedded copy:\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_small_companion_file_is_embedded()
    {
        // THE RULE: the model downloads, everything small beside it ships. A
        // 114 MB model has to come over the network; a 2 KB sidecar has no
        // business doing so, and when it did, 43 voices broke on a dead address.
        var missing = SidecarPins()
            .Where(p => EmbeddedVoiceConfigs.TryGet(p.FileName) is null)
            .Select(p => $"{p.Model} {p.FileName} ({p.SizeBytes} bytes)")
            .ToList();

        Assert.True(missing.Count == 0,
            "These would have to fetch a small companion file over the network, which "
            + "is exactly what was 404-ing:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_int8_variant_shares_the_full_models_companions()
    {
        // vits-11za-int8 was published as model.onnx alone — both its companions
        // 404. They are byte-identical to the full model's, and the registry
        // already pins the same SHA for both, so one copy answers for each name.
        foreach (var file in new[] { "model.onnx.json", "language_ids.json" })
        {
            var full = EmbeddedVoiceConfigs.TryGet($"vits-11za/{file}");
            var int8 = EmbeddedVoiceConfigs.TryGet($"vits-11za-int8/{file}");
            Assert.NotNull(full);
            Assert.NotNull(int8);
            Assert.Equal(full, int8);
        }
    }

    [Fact]
    public void The_multilingual_voice_knows_all_eleven_languages()
    {
        // Without language_ids.json a multilingual voice is not told which
        // language to speak, and every South African language comes out as
        // Afrikaans — audible, wrong, and not an error.
        var bytes = EmbeddedVoiceConfigs.TryGet("vits-11za/language_ids.json");
        Assert.NotNull(bytes);

        using var doc = JsonDocument.Parse(bytes!);
        var ids = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(11, ids.Count);
        Assert.Contains("zul", ids);
        Assert.Contains("afr", ids);
        Assert.Contains("ven", ids);
    }

    [Theory]
    [InlineData("mms-swh/model.onnx.json")]
    [InlineData("mms-hau/model.onnx.json")]
    [InlineData("mms-yor/model.onnx.json")]
    public void A_sidecar_declares_16k_and_puts_the_blank_at_zero(string fileName)
    {
        // THE PAD RULE and the sample rate are the two fields that were wrong in
        // the published sidecars, and both fail silently: a wrong blank makes the
        // voice speak fluent nonsense, a wrong rate plays correct audio at the
        // wrong speed. Neither shows up as an error.
        var bytes = EmbeddedVoiceConfigs.TryGet(fileName);
        Assert.NotNull(bytes);

        using var doc = JsonDocument.Parse(bytes!);
        Assert.Equal(16000, doc.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32());

        var map = doc.RootElement.GetProperty("phoneme_id_map");
        var blank = map.GetProperty("_");
        Assert.Equal(0, blank[0].GetInt32());
    }

    [Fact]
    public void An_unknown_name_returns_null_rather_than_throwing()
    {
        Assert.Null(EmbeddedVoiceConfigs.TryGet("no-such-voice/model.onnx.json"));
        Assert.Null(EmbeddedVoiceConfigs.TryGet(""));
        Assert.Null(EmbeddedVoiceConfigs.TryGet(null));
    }

    [Fact]
    public void A_platform_separated_name_resolves_too()
    {
        // A caller that has already been through Path.Combine hands over
        // backslashes on Windows; the registry spells it with a forward slash.
        Assert.NotNull(EmbeddedVoiceConfigs.TryGet(@"mms-swh\model.onnx.json"));
    }
}
