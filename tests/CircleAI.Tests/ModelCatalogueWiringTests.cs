// ModelCatalogueWiringTests.cs
//
// The DI composition (AddModelCatalogue) and the startup bootstrap. The whole
// subsystem resolves from one call; the signature verifier defaults to the
// embedded trust key; and the Wolverine bridge is wired only when the host has a
// self-healer — a no-op otherwise. The bootstrap seeds + assesses and surfaces
// only on a probe it can trust.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class ModelCatalogueWiringTests
{
    sealed class FakeHealer : ISelfHealer
    {
        public Task<HealingRecord> HealAsync(FailureContext failure, CancellationToken ct = default)
            => Task.FromResult(new HealingRecord(
                "i", DateTimeOffset.UtcNow, "s", "src", "m", "defer", "c", "m",
                null, "a", HealingOutcome.Escalated, true, false));
        public void Heal(Exception exception, string? source = null) { }
        public event Action? HealingChanged { add { } remove { } }
    }

    [Fact]
    public void AddModelCatalogue_resolves_the_whole_subsystem()
    {
        var services = new ServiceCollection();
        services.AddModelCatalogue(":memory:");
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IModelCatalog>());
        Assert.NotNull(sp.GetService<IModelAssessor>());
        Assert.NotNull(sp.GetService<CatalogueFeedWriter>());
        Assert.NotNull(sp.GetService<CatalogModelSelector>());
    }

    [Fact]
    public void Without_a_healer_the_observer_is_a_no_op()
    {
        var services = new ServiceCollection();
        services.AddModelCatalogue(":memory:");
        using var sp = services.BuildServiceProvider();
        Assert.IsType<NullModelCatalogObserver>(sp.GetService<IModelCatalogObserver>());
    }

    [Fact]
    public void With_a_healer_the_observer_bridges_to_wolverine()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISelfHealer>(new FakeHealer());
        services.AddModelCatalogue(":memory:");
        using var sp = services.BuildServiceProvider();
        Assert.IsType<SelfHealingCatalogObserver>(sp.GetService<IModelCatalogObserver>());
    }

    [Fact]
    public void Does_not_replace_the_live_selector_by_default()
    {
        var services = new ServiceCollection();
        services.AddModelCatalogue(":memory:");
        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<IModelSelector>());   // not swapped unless asked
    }

    [Fact]
    public void Can_opt_into_the_catalogue_as_the_primary_selector()
    {
        var services = new ServiceCollection();
        services.AddModelCatalogue(":memory:", useAsPrimarySelector: true);
        using var sp = services.BuildServiceProvider();
        Assert.IsType<CatalogModelSelector>(sp.GetService<IModelSelector>());
    }
}

public class CatalogueBootstrapTests
{
    sealed class Recorder : IModelCatalogObserver
    {
        public int Assessed, NoCompatible;
        public void OnFeedRejected(CatalogFeedRejection r, string s, string d) { }
        public void OnFeedApplied(int u, string s) { }
        public void OnCatalogueAssessed(int c, int t, DeviceProbe p) => Assessed++;
        public void OnNoCompatibleModel(ModelModality m, int c, DeviceProbe p) => NoCompatible++;
    }

    static DeviceProbe Probe(double ramGb)
        => new((long)(ramGb * 1_000_000_000), 100_000_000_000, GpuKind.None, 8,
               ThermalClass.Active, Connectivity.Online)
        { RamTotalBytes = (long)(ramGb * 1_000_000_000) };   // RamSource defaults to Explicit

    [Fact]
    public void Seeds_assesses_and_surfaces_on_a_measured_probe()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        var rec = new Recorder();

        var result = CatalogueBootstrap.Run(cat, DeviceModelAssessor.MnnOnly(cat), Probe(8), rec);

        Assert.True(cat.Count() > 0);                       // seeded from embedded
        Assert.True(result.Assessed > 0);
        Assert.Equal(1, rec.Assessed);                      // surfaced (probe is trustworthy)
        Assert.NotNull(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void Stays_quiet_on_a_heuristic_probe()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        var rec = new Recorder();
        var heuristic = Probe(8) with { RamSource = DeviceProbe.RamMeasurement.Heuristic };

        CatalogueBootstrap.Run(cat, DeviceModelAssessor.MnnOnly(cat), heuristic, rec);

        Assert.True(cat.Count() > 0);                       // still seeded + assessed
        Assert.Equal(0, rec.Assessed);                      // but not surfaced off a guess
        Assert.Equal(0, rec.NoCompatible);
    }

    [Fact]
    public void Reconciles_the_installed_flag_with_what_is_on_disk()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        // Pre-populate so SeedFromEmbedded is skipped and the ids are ours.
        static ModelEntry Row(string id) => new(id, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat,
            MinRamGb = 0.6, MinStorageGb = 0.5, QualityRank = 6, TotalBytes = 100_000_000,
        };
        cat.Upsert(Row("present-on-disk"));
        cat.Upsert(Row("gone-from-disk"));
        cat.SetInstalled("gone-from-disk", true);   // a stale flag, no folder behind it

        // A models dir where only present-on-disk has finished (installed.json marker).
        var dir = Path.Combine(Path.GetTempPath(), "circleai-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "present-on-disk"));
        File.WriteAllText(Path.Combine(dir, "present-on-disk", "installed.json"), "{}");
        // A partial download (no marker) must NOT count as installed.
        Directory.CreateDirectory(Path.Combine(dir, "gone-from-disk"));   // folder exists, no installed.json
        try
        {
            CatalogueBootstrap.Run(cat, DeviceModelAssessor.MnnOnly(cat), Probe(8), modelsDirectory: dir);

            Assert.True(cat.IsInstalled("present-on-disk"));    // marker present -> set true
            Assert.False(cat.IsInstalled("gone-from-disk"));    // no marker -> stale flag cleared
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
