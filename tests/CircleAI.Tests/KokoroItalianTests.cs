// KokoroItalianTests.cs
//
// OPEN-GAPS A8: KokoroTtsEngine was written for the voices no VITS voice covers and had no
// model and no caller. Italian is the one of its languages the catalogue had no voice for, so
// that is the row it gets: Apache-2.0, picked for an Italian phone by the same selector as every
// voice, and opened by the catalogued-voice path with espeak's Italian folded into the symbols
// Kokoro was trained on.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Assistant;
using CircleAI.Assistant.Voice;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class KokoroItalianTests
{
    private const string Model = "Kokoro-82M-Italian";

    private static DeviceProbe Phone() => new(
        RamAvailableBytes: 1_400L * 1024 * 1024,
        StorageFreeBytes:  20L * 1024 * 1024 * 1024,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online);

    [Fact]
    public void The_italian_voice_is_a_kokoro_bundle_with_a_licence_that_ships()
    {
        using var registry = new ModelRegistryService();
        var row = Assert.Single(registry.AllModels, m => KokoroTtsEngine.Reads(m.Architecture));

        Assert.Equal(Model, row.Name);
        Assert.Equal(ModelModality.Tts, row.Modality);
        Assert.Equal("Apache-2.0", row.License);
        Assert.Equal("it", row.Language);

        // Everything FromDirectory reads: the graph, the IPA vocabulary and one voice pack.
        var files = row.BundleFiles!.Select(f => f.Name).ToList();
        Assert.Single(files, f => f.EndsWith(".onnx", StringComparison.Ordinal));
        Assert.Contains("tokenizer.json", files);
        Assert.Single(files, f => f.StartsWith("voices/", StringComparison.Ordinal) && f.EndsWith(".bin", StringComparison.Ordinal));
        Assert.Equal(row.BundleFiles!.Sum(f => f.SizeBytes), row.TotalBytes);
    }

    [Fact]
    public void An_italian_phone_is_given_it_by_the_selector_and_by_setup()
    {
        using var registry = new ModelRegistryService();
        ISpeechModelSelector selector = new SpeechModelSelector(registry);
        Assert.Equal(Model, selector.PlanFor(Phone(), ModelModality.Tts, "it").Model?.ModelId);

        var want = Assert.Single(FirstRun.WantedFor(speech: true, "it"), w => w.Speaks == "it");
        Assert.Equal(ModelModality.Tts, want.Modality);

        var store = Path.Combine(Path.GetTempPath(), "circleai-kokoro-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var loader = new BundleModelLoader(store, registry);
            var plan = FirstRun.Plan(registry, loader, Phone(), speech: true, language: "it");
            Assert.Contains(plan, s => s.Title == want.Title && s.Model.Name == Model);
        }
        finally { if (Directory.Exists(store)) Directory.Delete(store, recursive: true); }
    }

    [Fact]
    public void Espeak_reads_each_language_in_the_voice_kokoros_own_g2p_uses()
    {
        Assert.Equal("it", CircleAITtsProbe.KokoroEspeakVoice("it-IT"));
        Assert.Equal("fr-fr", CircleAITtsProbe.KokoroEspeakVoice("fr"));
        Assert.Equal("pt-br", CircleAITtsProbe.KokoroEspeakVoice("pt_BR"));
    }

    // ── Folding espeak's symbols into Kokoro's ─────────────────────────

    [Fact]
    public void An_untied_affricate_becomes_the_one_symbol_kokoro_was_trained_on()
    {
        // "ciao", as espeak spells it one code point at a time.
        Assert.Equal(["ʧ", "ˈ", "a", "o"], KokoroTtsEngine.Fold(["t", "ʃ", "ˈ", "a", "o"]));
        Assert.Equal(["ʦ"], KokoroTtsEngine.Fold(["t", "s"]));
        Assert.Equal(["ʣ"], KokoroTtsEngine.Fold(["d", "z"]));
        Assert.Equal(["ʤ"], KokoroTtsEngine.Fold(["d", "ʒ"]));
    }

    [Fact]
    public void A_tie_folds_a_pair_and_is_never_spoken()
    {
        Assert.Equal(["W"], KokoroTtsEngine.Fold(["a", "͡", "ʊ"]));
        Assert.Equal(["ʧ"], KokoroTtsEngine.Fold(["t͡ʃ"]));
        Assert.Equal(["I"], KokoroTtsEngine.Fold(["a", "^", "ɪ"]));

        // A tie over a pair Kokoro has no symbol for is dropped; the two sounds stay.
        Assert.Equal(["k", "s"], KokoroTtsEngine.Fold(["k", "͡", "s"]));
    }

    [Fact]
    public void Nothing_is_folded_across_a_stress_mark_or_a_space_or_between_untied_vowels()
    {
        Assert.Equal(["t", "ˈ", "ʃ"], KokoroTtsEngine.Fold(["t", "ˈ", "ʃ"]));
        Assert.Equal(["t", " ", "s"], KokoroTtsEngine.Fold(["t", " ", "s"]));
        // Whether two vowels are one diphthong is what a tie says; without one, they stay two.
        Assert.Equal(["a", "ʊ"], KokoroTtsEngine.Fold(["a", "ʊ"]));
    }

    [Fact]
    public void The_token_ids_are_kokoros_symbols_not_espeaks()
    {
        var vocab = new Dictionary<string, long> { ["ʧ"] = 1, ["ˈ"] = 2, ["a"] = 3, ["o"] = 4, ["t"] = 5, ["ʃ"] = 6 };
        using var engine = new KokoroTtsEngine("never-opened.onnx", new Fixed(["t", "ʃ", "ˈ", "a", "o"]), vocab, new float[510 * 256]);

        Assert.Equal([1L, 2L, 3L, 4L], engine.TokenIds("ciao"));
        Assert.Equal(0, engine.LastSkippedCount);
    }

    private sealed class Fixed(IReadOnlyList<string> symbols) : IPhonemizer
    {
        public IReadOnlyList<string> Phonemize(string text) => symbols;
    }
}
