// CatalogueSeedTests.cs
//
// The embedded registry demoted to a first-run seed. Seeding bootstraps an EMPTY
// catalogue and never clobbers one a feed has populated; each seeded row is
// stamped with a derived engine so the assessor's engine gate is honest from the
// first launch. After a seed + assess, the catalogue can select a chat model
// offline — the app is never model-less on a cold device.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueSeedTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    static DeviceProbe Probe(double ramGb = 8)
        => new(
            RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  100_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = (long)(ramGb * 1_000_000_000) };

    [Fact]
    public void Seeds_the_embedded_ladder_into_an_empty_catalogue()
    {
        using var cat = NewCatalog();
        var n = CatalogueSeed.SeedFromEmbedded(cat);

        Assert.True(n > 0);
        Assert.Equal(n, cat.Count());

        // A known curated chat model is present and stamped MNN / Chat.
        var qwen = cat.Get("Qwen3-0.6B-MNN");
        Assert.NotNull(qwen);
        Assert.Equal(ModelEngine.Mnn, qwen!.Engine);
        Assert.Equal(ModelModality.Chat, qwen.Modality);
    }

    [Fact]
    public void Does_not_reseed_a_catalogue_that_a_feed_already_populated()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("fed-model", "1.0", "MNN-Q4") { QualityRank = 9 });

        var n = CatalogueSeed.SeedFromEmbedded(cat);

        Assert.Equal(0, n);
        Assert.Equal(1, cat.Count());   // the feed row survives; the seed does not run
    }

    [Fact]
    public void After_seed_and_assess_a_chat_model_is_selectable_offline()
    {
        using var cat = NewCatalog();
        CatalogueSeed.SeedFromEmbedded(cat);
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));

        var best = cat.Best(ModelModality.Chat);
        Assert.NotNull(best);
        Assert.Equal(ModelEngine.Mnn, best!.Engine);
    }

    [Theory]
    [InlineData("MNN-Q4", null, ModelModality.Chat, ModelEngine.Mnn)]
    [InlineData("GGUF-Q4", "qwen3", ModelModality.Chat, ModelEngine.LlamaCpp)]
    [InlineData("ggml-q5", "whisper", ModelModality.Asr, ModelEngine.Ggml)]
    [InlineData("fp16", "vits", ModelModality.Tts, ModelEngine.Onnx)]
    [InlineData("int8", null, ModelModality.Tts, ModelEngine.Onnx)]   // speech modality → ONNX
    public void DeriveEngine_maps_quantisation_architecture_and_modality(
        string quant, string? arch, ModelModality modality, ModelEngine expected)
    {
        var e = new ModelEntry("m", "1.0", quant) { Architecture = arch, Modality = modality };
        Assert.Equal(expected, CatalogueSeed.DeriveEngine(e));
    }
}
