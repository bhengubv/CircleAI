// CatalogueUpdaterTests.cs
//
// The sink that makes the catalogue update from ANY connection. ApplyRegistry is
// the unified entry (side-load / discovery result); the CatalogueArrived path is
// how an internet refresh and an aethernet Offer both flow in once Attached.
// Tests use unique model names + Get-based asserts so the process-static
// ModelCatalogue event cannot cause cross-test flake, and clean up the statics.

using System;
using System.Collections.Generic;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueUpdaterTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    static DeviceProbe Probe(double ramGb = 8)
        => new((long)(ramGb * 1_000_000_000), 100_000_000_000, GpuKind.None, 8,
               ThermalClass.Active, Connectivity.Online)
        { RamTotalBytes = (long)(ramGb * 1_000_000_000) };

    static ModelEntry Row(string name)
        => new(name, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat, TotalBytes = 100_000_000,
            MinRamGb = 0.6, MinStorageGb = 0.1, QualityRank = 8, License = "Apache-2.0",
            Capabilities = new[] { "Default" },
        };

    static ModelRegistry Registry(params string[] names)
    {
        var models = new List<ModelEntry>();
        foreach (var n in names) models.Add(Row(n));
        return new ModelRegistry("test://catalogue", DateTime.UtcNow, models);
    }

    static CatalogueUpdater Updater(IModelCatalog cat, DeviceProbe probe)
        => new(new CatalogueFeedWriter(cat, DeviceModelAssessor.MnnOnly(cat)), () => probe);

    [Fact]
    public void ApplyRegistry_applies_rows_and_assesses()
    {
        using var cat = NewCatalog();
        var updater = Updater(cat, Probe());

        var r = updater.ApplyRegistry(Registry("updater-direct-a", "updater-direct-b"), "side-load");

        Assert.True(r.Accepted);
        Assert.Equal(2, r.Upserted);
        Assert.NotNull(cat.Get("updater-direct-a"));
        Assert.NotNull(cat.Best(ModelModality.Chat));   // assessed → selectable
    }

    [Fact]
    public void An_aethernet_offer_flows_in_when_attached()
    {
        using var cat = NewCatalog();
        using var updater = Updater(cat, Probe());
        try
        {
            updater.Attach();
            ModelCatalogue.Offer(Registry("updater-offer-model"));   // the aethernet / side-load door
            Assert.NotNull(cat.Get("updater-offer-model"));          // reached the SQLite catalogue
        }
        finally { ModelCatalogue.Forget(); }
    }

    [Fact]
    public void Detach_stops_the_flow()
    {
        using var cat = NewCatalog();
        var updater = Updater(cat, Probe());
        try
        {
            updater.Attach();
            updater.Detach();
            ModelCatalogue.Offer(Registry("updater-after-detach"));
            Assert.Null(cat.Get("updater-after-detach"));
        }
        finally { ModelCatalogue.Forget(); updater.Dispose(); }
    }
}
