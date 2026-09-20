// HarnessExposureTests.cs
//
// The EXPOSE half of the plan, proven as a consumer sees it: a program that references
// the SDK and calls AddCircleAI gets Circle AI's self-healing sense and its capability
// catalogue out of the container — no model, no server. This is what a real harness
// (later, Butler/Wolverine) does to consume Circle AI.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public sealed class HarnessExposureTests
{
    [Fact]
    public async Task AddCircleAI_exposes_the_catalogue_and_the_self_healing_sense()
    {
        var services = new ServiceCollection();
        services.AddCircleAI(new AIOptions());
        // AIService is IAsyncDisposable, so the provider must be async-disposed.
        await using var sp = services.BuildServiceProvider();

        var catalog = sp.GetService<ICapabilityCatalog>();
        Assert.NotNull(catalog);
        Assert.NotNull(catalog!.Find("self.healing"));   // discovery works end to end

        Assert.NotNull(sp.GetService<IFailureAnalyst>()); // the sense is wired to the brain
    }

    [Fact]
    public async Task A_host_can_substitute_its_own_self_healing_sense()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFailureAnalyst>(new StubAnalyst());   // registered BEFORE AddCircleAI
        services.AddCircleAI(new AIOptions());
        await using var sp = services.BuildServiceProvider();

        // TryAdd inside AddCircleAI keeps the host's registration (see the addsingleton-
        // last-wins / tryadd-first-wins rule this repo learned the hard way).
        Assert.IsType<StubAnalyst>(sp.GetService<IFailureAnalyst>());
    }

    private sealed class StubAnalyst : IFailureAnalyst
    {
        public Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default)
            => Task.FromResult(HealingVerdict.NeedsAHuman("stub"));
    }
}
