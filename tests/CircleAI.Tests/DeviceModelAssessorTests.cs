// DeviceModelAssessorTests.cs
//
// The assessment that fills `compatible`/`rank`. `compatible` is the AND of four
// gates — engine shipped, RAM fits, storage fits, licence free — and each gate
// is exercised here on its own. `rank` stays quality-dominant. A row that fails
// any gate is not returned by Best(); a row that passes is, best quality first.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class DeviceModelAssessorTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    static DeviceProbe Probe(double ramGb, double storageGb = 100, double? vramGb = null)
        => new(
            RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  (long)(storageGb * 1_000_000_000),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online)
        { VramGb = vramGb, RamTotalBytes = (long)(ramGb * 1_000_000_000) };

    static ModelEntry Entry(
        string name,
        ModelEngine engine = ModelEngine.Mnn,
        int qualityRank = 6,
        double minRamGb = 0.6,
        double minStorageGb = 0.5,
        ModelModality modality = ModelModality.Chat)
        => new(name, "1.0", "MNN-Q4")
        {
            Engine       = engine,
            Modality     = modality,
            TotalBytes   = 100_000_000,
            MinRamGb     = minRamGb,
            MinStorageGb = minStorageGb,
            QualityRank  = qualityRank,
        };

    [Fact]
    public void An_MNN_model_that_fits_is_compatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("qwen"));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("qwen", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void An_engine_the_device_does_not_ship_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("bonsai-gguf", engine: ModelEngine.LlamaCpp));  // GGUF, no engine shipped
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Null(cat.Best(ModelModality.Chat));   // the bit tells the truth
    }

    [Fact]
    public void A_model_that_needs_more_RAM_than_the_device_has_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("big", minRamGb: 4.0));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 1.0));   // usable ~0.85 GB
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void A_model_that_needs_more_storage_than_is_free_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("m", minStorageGb: 50));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, storageGb: 1));
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void A_VRAM_requirement_gates_when_the_device_reports_none()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("needs-gpu") with { MinVramGb = 6.0 });

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, vramGb: null));
        Assert.Null(cat.Best(ModelModality.Chat));

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, vramGb: 8));
        Assert.NotNull(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void Rank_tracks_measured_quality()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("small", qualityRank: 6, minRamGb: 0.6));
        cat.Upsert(Entry("big",   qualityRank: 10, minRamGb: 1.5));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("big", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void Assess_returns_the_number_of_rows_scored()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("a"));
        cat.Upsert(Entry("b"));
        cat.Upsert(Entry("c"));
        var result = DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal(3, result.Assessed);
        Assert.Equal(3, result.Compatible);
    }

    // An MoE bundle (Qwen3-30B-A3B: 18 GB of weights on disk, but only ~3B active
    // experts resident) advertises a low MinRamGb that is only truthful when the
    // runtime memory-maps the weights. The compatible bit must follow what the
    // runtime will actually do: recommend it when mmap pages the rest off disk,
    // refuse it when the whole file would have to live in RAM and OOM the phone.
    [Fact]
    public void An_MoE_bundle_is_compatible_only_when_mmap_pages_its_weights()
    {
        static ModelEntry Moe() => new("qwen3-30b-a3b", "3.0", "MNN-Q4")
        {
            Engine       = ModelEngine.Mnn,
            Modality     = ModelModality.Chat,
            TotalBytes   = 18_000_000_000,   // 18 GB on disk
            MinRamGb     = 2.5,              // only the active experts, IF mmap'd
            MinStorageGb = 18.0,
            QualityRank  = 15,
        };

        var phone = Probe(ramGb: 5, storageGb: 64);   // usable ~4.25 GB: 2.5 fits, 18 does not

        using (var cat = NewCatalog())
        {
            cat.Upsert(Moe());
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false).Assess(phone);
            Assert.Null(cat.Best(ModelModality.Chat));   // honest: 18 GB can't be resident
        }

        using (var cat = NewCatalog())
        {
            cat.Upsert(Moe());
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true).Assess(phone);
            Assert.Equal("qwen3-30b-a3b", cat.Best(ModelModality.Chat)!.Name);   // mmap pages the rest
        }
    }

    // A giant that must page tens of GB off disk before its first token stays
    // COMPATIBLE (a person can choose it) but must never be the silent default — a
    // model that answers quickly outranks it, whatever the quality gap.
    [Fact]
    public void A_giant_that_must_mmap_is_not_the_default_when_a_fast_model_fits()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("moe-35b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat,
            TotalBytes = 22_000_000_000, MinRamGb = 2.5, MinStorageGb = 22.0, QualityRank = 16,
        });
        cat.Upsert(Entry("fast-2b", qualityRank: 9, minRamGb: 1.9, minStorageGb: 1.5));

        // mmap on, ample RAM + storage so BOTH are compatible.
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
            .Assess(Probe(ramGb: 12, storageGb: 128));

        Assert.Equal(2, cat.Compatible(ModelModality.Chat).Count);        // both available
        Assert.Equal("fast-2b", cat.Best(ModelModality.Chat)!.Name);      // fast one is the default
    }

    // A dense bundle's MinRamGb already includes its full weights, so the mmap
    // flag must not change its verdict — guards the Max() in EffectiveMinRamGb.
    [Fact]
    public void A_dense_bundle_is_unaffected_by_the_mmap_flag()
    {
        var phone = Probe(ramGb: 5, storageGb: 64);

        foreach (var mmap in new[] { false, true })
        {
            using var cat = NewCatalog();
            cat.Upsert(Entry("qwen3-1.7b", minRamGb: 1.7, minStorageGb: 1.2));  // TotalBytes 0.1 GB « 1.7
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: mmap).Assess(phone);
            Assert.Equal("qwen3-1.7b", cat.Best(ModelModality.Chat)!.Name);
        }
    }

    // ---- vision bundles -----------------------------------------------------

    // A VLM carries a vision encoder alongside the LLM, so it needs MORE
    // resident than it weighs on disk. Qwen2.5-VL-3B is 2.74 GB with MinRamGb
    // 3.9 — above a 3.6 GB phone — so it sat permanently incompatible even
    // though mmap is exactly what such a model needs. It weighs less than the
    // 8 GB giant threshold, so the old gate never offered it mmap.
    [Fact]
    public void A_vision_bundle_that_needs_more_resident_than_it_weighs_gets_mmap()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-vl-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 2_736_200_899, MinRamGb = 3.9, MinStorageGb = 2.6, QualityRank = 9,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
            .Assess(Probe(ramGb: 4.3));   // ~3.6 GB usable after headroom

        Assert.NotNull(cat.Best(ModelModality.Vision));
    }

    // With mmap off it must stay out: eagerly it needs its full weight resident
    // plus the encoder, and loading it would OOM.
    [Fact]
    public void The_same_vision_bundle_stays_out_when_mmap_is_off()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-vl-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 2_736_200_899, MinRamGb = 3.9, MinStorageGb = 2.6, QualityRank = 9,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false)
            .Assess(Probe(ramGb: 4.3));

        Assert.Null(cat.Best(ModelModality.Vision));
    }

    // A small VLM whose MinRamGb already covers its weights must NOT be pushed
    // onto the slow mmap path — SmolVLM-256M is 311 MB and fits eagerly.
    [Fact]
    public void A_small_vision_bundle_that_fits_eagerly_is_not_mmapped()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("smolvlm-256m", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 311_428_914, MinRamGb = 0.5, MinStorageGb = 0.4, QualityRank = 4,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
            .Assess(Probe(ramGb: 4.3));

        Assert.NotNull(cat.Best(ModelModality.Vision));
    }

    // ---- GGUF / engine honesty ---------------------------------------------

    // Bonsai 2 27B is catalogued so the app can SAY it exists, and gated so it
    // never offers a download that cannot load. Measured from the GGUF header
    // 2026-09-25: architecture "qwen35", 5.95 GB, PTQ1_0 (1.75 bits/weight,
    // 32 weights per 7-byte block). The engine is llama.cpp and this device
    // ships MNN, so the honest verdict is incompatible — not absent.
    [Fact]
    public void Bonsai_is_catalogued_but_gated_off_an_MNN_only_device()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("Ternary-Bonsai-2-27B", "1.0", "PTQ1_0")
        {
            Engine = ModelEngine.LlamaCpp, Modality = ModelModality.Chat,
            TotalBytes = 5_946_648_928, MinRamGb = 7.5, MinStorageGb = 6.0, QualityRank = 16,
        });

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 32, storageGb: 200));

        // Present in the catalogue...
        Assert.Contains(cat.All(), m => m.Name == "Ternary-Bonsai-2-27B");
        // ...and never selected, however much RAM the device has.
        Assert.Null(cat.Best(ModelModality.Chat));
    }
}
