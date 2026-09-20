// SqliteModelCatalogTests.cs
//
// The runtime model catalogue — one SQLite table as the single source of truth
// for model selection. The load-bearing test is Re_pin_...: a signed feed
// replaces a shipped model's version and Best() returns the new one after
// re-assessment, WITHOUT an app release. That is the "outdated model" hostage
// being killed, expressed as an assertion.

using System;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class SqliteModelCatalogTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    static ModelEntry Entry(
        string name,
        string version = "1.0",
        ModelModality modality = ModelModality.Chat,
        ModelEngine engine = ModelEngine.Mnn,
        int qualityRank = 6,
        double minRamGb = 0.6,
        double minStorageGb = 0.5,
        string? license = null)
        => new(name, version, "MNN-Q4")
        {
            Repo            = $"taobao-mnn/{name}",
            Source          = ModelSource.HuggingFace,
            Modality        = modality,
            Engine          = engine,
            TotalBytes      = 454_470_710,
            BundleFiles     = new[] { new BundleFile("llm.mnn.weight", "abc123", 450_810_338) },
            MinRamGb        = minRamGb,
            MinStorageGb    = minStorageGb,
            Capabilities    = new[] { "Default", "Tools", "Reasoning" },
            QualityRank     = qualityRank,
            FallbackModelId = "smaller",
            MemoryHintBytes = 636_000_000,
            Architecture    = "qwen3",
            License         = license,
        };

    [Fact]
    public void Upsert_then_Get_round_trips_every_field()
    {
        using var cat = NewCatalog();
        var e = Entry("Qwen3-0.6B-MNN", version: "3.5", license: "Apache-2.0");
        cat.Upsert(e);

        var got = cat.Get("Qwen3-0.6B-MNN");
        Assert.NotNull(got);
        Assert.Equal(e.Name, got!.Name);
        Assert.Equal(e.Version, got.Version);
        Assert.Equal(e.Quantization, got.Quantization);
        Assert.Equal(e.Repo, got.Repo);
        Assert.Equal(e.Source, got.Source);
        Assert.Equal(e.Modality, got.Modality);
        Assert.Equal(e.Engine, got.Engine);
        Assert.Equal(e.TotalBytes, got.TotalBytes);
        Assert.Equal(e.MinRamGb, got.MinRamGb);
        Assert.Equal(e.MinStorageGb, got.MinStorageGb);
        Assert.Equal(e.QualityRank, got.QualityRank);
        Assert.Equal(e.FallbackModelId, got.FallbackModelId);
        Assert.Equal(e.MemoryHintBytes, got.MemoryHintBytes);
        Assert.Equal(e.Architecture, got.Architecture);
        Assert.Equal(e.License, got.License);

        Assert.NotNull(got.BundleFiles);
        Assert.Single(got.BundleFiles!);
        Assert.Equal("llm.mnn.weight", got.BundleFiles![0].Name);
        Assert.Equal("abc123", got.BundleFiles[0].Sha256);
        Assert.Equal(450_810_338, got.BundleFiles[0].SizeBytes);

        Assert.Equal(new[] { "Default", "Tools", "Reasoning" }, got.Capabilities!);
    }

    [Fact]
    public void A_fresh_upsert_lands_unassessed_and_is_not_offered()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("Qwen3-0.6B-MNN"));

        Assert.Equal(1, cat.Count());
        Assert.Null(cat.Best(ModelModality.Chat));          // compatible = 0 until assessed
        Assert.Empty(cat.Compatible(ModelModality.Chat));
    }

    [Fact]
    public void SetAssessment_makes_it_selectable_and_Best_returns_top_rank()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("small", qualityRank: 6));
        cat.Upsert(Entry("big", qualityRank: 10));

        cat.SetAssessment("small", compatible: true, rank: 6.0);
        cat.SetAssessment("big",   compatible: true, rank: 10.0);

        Assert.Equal("big", cat.Best(ModelModality.Chat)!.Name);
        Assert.Equal(new[] { "big", "small" },
            cat.Compatible(ModelModality.Chat).Select(m => m.Name));
    }

    [Fact]
    public void Re_pin_replaces_a_seeded_model_and_Best_returns_the_new_version()
    {
        using var cat = NewCatalog();

        // Seeded ladder: one model, assessed and selectable at v3.5.
        cat.Upsert(Entry("Qwen3-0.6B-MNN", version: "3.5"));
        cat.SetAssessment("Qwen3-0.6B-MNN", compatible: true, rank: 6.0);
        Assert.Equal("3.5", cat.Best(ModelModality.Chat)!.Version);

        // A signed feed re-pins the SAME id to a newer version.
        cat.Upsert(Entry("Qwen3-0.6B-MNN", version: "4.0"));

        // Replaced, not appended — no immutable spine.
        Assert.Equal(1, cat.Count());
        // A re-pin lands unassessed, so nothing is offered until re-assessed.
        Assert.Null(cat.Best(ModelModality.Chat));

        // The device re-assesses; Best now returns the NEW version — no app release.
        cat.SetAssessment("Qwen3-0.6B-MNN", compatible: true, rank: 6.0);
        Assert.Equal("4.0", cat.Best(ModelModality.Chat)!.Version);
    }

    [Fact]
    public void Compatible_filters_by_modality_and_the_bit()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("chat-ok", modality: ModelModality.Chat));
        cat.Upsert(Entry("chat-no", modality: ModelModality.Chat));
        cat.Upsert(Entry("tts-ok",  modality: ModelModality.Tts));

        cat.SetAssessment("chat-ok", compatible: true,  rank: 8);
        cat.SetAssessment("chat-no", compatible: false, rank: 0);   // e.g. engine not shipped
        cat.SetAssessment("tts-ok",  compatible: true,  rank: 5);

        Assert.Equal(new[] { "chat-ok" },
            cat.Compatible(ModelModality.Chat).Select(m => m.Name));
        Assert.Equal("tts-ok", cat.Best(ModelModality.Tts)!.Name);
    }

    [Fact]
    public void Best_is_null_when_nothing_compatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("x"));
        cat.SetAssessment("x", compatible: false, rank: 0);
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void Installed_survives_a_same_version_upsert_and_clears_on_re_pin()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("m", version: "1.0"));
        cat.SetInstalled("m", true);
        Assert.True(cat.IsInstalled("m"));

        // Same version re-upsert (a refreshed assessment) keeps it installed.
        cat.Upsert(Entry("m", version: "1.0"));
        Assert.True(cat.IsInstalled("m"));

        // A version re-pin makes the on-disk copy stale → not installed.
        cat.Upsert(Entry("m", version: "2.0"));
        Assert.False(cat.IsInstalled("m"));
    }

    [Fact]
    public void Unknown_ids_read_as_absent()
    {
        using var cat = NewCatalog();
        Assert.Null(cat.Get("nope"));
        Assert.False(cat.IsInstalled("nope"));
        Assert.Equal(0, cat.Count());
    }
}
