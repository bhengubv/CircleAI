// CatalogueFeedWriterTests.cs
//
// The applier — rows from a trusted source into the catalogue, then re-assess.
// No central signature: a batch is just rows the caller already trusts (a mesh
// peer, self-discovery, a side-loaded file). Integrity of the eventual download
// is the per-row SHA-256 (verified elsewhere, by the downloader); this covers
// what the applier owns: the anti-hostage re-pin end to end, a GGUF row that is
// catalogued but never offered, the licence-at-ingest gate, and a malformed batch.

using System;
using System.Text;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueFeedWriterTests
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

    static ModelEntry Entry(
        string name,
        string version = "1.0",
        int qualityRank = 6,
        ModelEngine engine = ModelEngine.Mnn,
        double minRamGb = 0.6,
        string? license = "Apache-2.0")
        => new(name, version, "MNN-Q4")
        {
            Engine       = engine,
            Modality     = ModelModality.Chat,
            TotalBytes   = 100_000_000,
            MinRamGb     = minRamGb,
            MinStorageGb = 0.1,
            QualityRank  = qualityRank,
            License      = license,
            Capabilities = new[] { "Default", "Tools" },
        };

    static CatalogueFeed Batch(params ModelEntry[] models)
        => new(1, DateTimeOffset.UtcNow, models);

    sealed class RecordingObserver : IModelCatalogObserver
    {
        public int Applied, Assessed, NoCompatible;
        public CatalogFeedRejection? LastRejection;
        public void OnFeedRejected(CatalogFeedRejection reason, string source, string detail) => LastRejection = reason;
        public void OnFeedApplied(int upserted, string source) => Applied++;
        public void OnCatalogueAssessed(int compatible, int total, DeviceProbe probe) => Assessed++;
        public void OnNoCompatibleModel(ModelModality modality, int catalogued, DeviceProbe probe) => NoCompatible++;
    }

    static CatalogueFeedWriter Writer(IModelCatalog cat, out RecordingObserver obs)
    {
        obs = new RecordingObserver();
        return new CatalogueFeedWriter(cat, DeviceModelAssessor.MnnOnly(cat), obs);
    }

    [Fact]
    public void A_batch_is_applied_assessed_and_selectable()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out var obs);

        var r = writer.Apply(Batch(Entry("m", qualityRank: 8)), Probe(), "mesh");

        Assert.True(r.Accepted);
        Assert.Equal(1, r.Upserted);
        Assert.Equal(1, r.Compatible);
        Assert.Equal("m", cat.Best(ModelModality.Chat)!.Name);
        Assert.Equal(1, obs.Applied);
        Assert.Equal(0, obs.NoCompatible);
    }

    [Fact]
    public void The_anti_hostage_update_end_to_end()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out _);

        // A shipped model, assessed and selectable at v3.5.
        cat.Upsert(Entry("Qwen3-0.6B-MNN", version: "3.5"));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe());
        Assert.Equal("3.5", cat.Best(ModelModality.Chat)!.Version);

        // An update (from any source) re-pins the SAME id to v4.0.
        var r = writer.Apply(Batch(Entry("Qwen3-0.6B-MNN", version: "4.0")), Probe(), "self-discovery");

        Assert.True(r.Accepted);
        Assert.Equal(1, cat.Count());                               // replaced, not appended
        Assert.Equal("4.0", cat.Best(ModelModality.Chat)!.Version); // NEW model — no app release
    }

    [Fact]
    public void A_GGUF_row_is_catalogued_but_never_offered_on_an_MNN_device()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out var obs);

        var bonsai = new ModelEntry("Ternary-Bonsai-2-27B", "2", "PTQ1_0")
        {
            Repo         = "prism-ml/Ternary-Bonsai-2-27B-gguf",
            Modality     = ModelModality.Chat,
            Engine       = ModelEngine.LlamaCpp,
            TotalBytes   = 6_000_000_000,
            MinRamGb     = 6.0,
            License      = "Apache-2.0",
            QualityRank  = 14,
            Capabilities = new[] { "Default", "Tools" },
        };

        var r = writer.Apply(Batch(bonsai), Probe(ramGb: 8), "mesh");

        Assert.True(r.Accepted);
        Assert.Equal(1, r.Upserted);
        Assert.Equal(0, r.Compatible);                         // engine not shipped
        Assert.Null(cat.Best(ModelModality.Chat));             // never offered
        Assert.NotNull(cat.Get("Ternary-Bonsai-2-27B"));       // but IS catalogued, ready for the engine
        Assert.Equal(ModelEngine.LlamaCpp, cat.Get("Ternary-Bonsai-2-27B")!.Engine);
        Assert.Equal(1, obs.NoCompatible);                     // Wolverine hears "can run nothing"
    }

    [Fact]
    public void A_row_with_a_non_free_licence_is_dropped_at_ingest()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out _);

        var r = writer.Apply(
            Batch(Entry("free-model", license: "Apache-2.0"),
                  Entry("gemma-model", license: "gemma-terms-of-use")),
            Probe(), "mesh");

        Assert.True(r.Accepted);
        Assert.Equal(1, r.Upserted);
        Assert.Equal(1, r.LicenceRejected);
        Assert.NotNull(cat.Get("free-model"));
        Assert.Null(cat.Get("gemma-model"));      // never catalogued
    }

    [Fact]
    public void A_row_with_no_stated_licence_is_dropped()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out _);

        var r = writer.Apply(Batch(Entry("mystery", license: null)), Probe(), "mesh");

        Assert.True(r.Accepted);
        Assert.Equal(0, r.Upserted);
        Assert.Equal(1, r.LicenceRejected);
        Assert.Null(cat.Get("mystery"));
    }

    [Fact]
    public void A_malformed_batch_is_rejected()
    {
        using var cat = NewCatalog();
        var writer = Writer(cat, out var obs);

        var bytes = Encoding.UTF8.GetBytes("{\"SchemaVersion\":99,\"Models\":[]}");
        var r = writer.Apply(bytes, Probe(), "side-load");

        Assert.False(r.Accepted);
        Assert.Equal(CatalogFeedRejection.Malformed, r.Rejection);
        Assert.Equal(0, cat.Count());
        Assert.Equal(CatalogFeedRejection.Malformed, obs.LastRejection);
    }
}
