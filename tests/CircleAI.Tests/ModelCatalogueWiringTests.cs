// ModelCatalogueWiringTests.cs
//
// The DI composition (AddModelCatalogue) and the startup bootstrap. The whole
// subsystem resolves from one call; the signature verifier defaults to the
// embedded trust key; and the Wolverine bridge is wired only when the host has a
// self-healer — a no-op otherwise. The bootstrap seeds + assesses and surfaces
// only on a probe it can trust.

using System;
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
}
