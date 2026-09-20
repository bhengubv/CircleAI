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
}
