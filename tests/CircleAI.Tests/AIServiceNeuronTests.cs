// AIServiceNeuronTests.cs — the two-slot Neuron behaviour inside AIService:
// router-gated slot selection, specialist hot-load, evict-first under pressure,
// and generalist-floor session persistence. Backward-compat: Router == null
// keeps the single-slot path.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Hosting;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class AIServiceNeuronTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string Temp()
    {
        var p = Path.GetTempFileName();
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { File.Delete(p); } catch { /* best-effort */ }
    }

    private sealed class FixedRouter : INeuronRouter
    {
        private readonly RouteDecision _decision;
        public FixedRouter(RouteDecision decision) => _decision = decision;
        public RouteDecision Route(RouteContext context) => _decision;
    }

    [Fact]
    public async Task RouterNull_UsesGeneralist_SingleSlot()
    {
        var generalist = new FakeChatGenerator("GEN");
        var opts = new AIOptions { ModelPath = Temp(), WarmOnStart = false }; // Router null
        await using var svc = new AIService(opts, generatorFactory: _ => generalist);
        await svc.StartAsync();

        // "solve this" is a reasoning cue, but with no router it stays generalist.
        Assert.Equal("GEN", await svc.AskAsync("solve this equation"));
    }

    [Fact]
    public async Task Router_HotLoadsSpecialist_AndAnswersFromIt()
    {
        var genPath = Temp();
        var specPath = Temp();
        var generalist = new FakeChatGenerator("GEN");
        var specialist = new FakeChatGenerator("SPEC");
        IChatGenerator Factory(string path) => path == specPath ? specialist : generalist;

        var loader = new FakeModelLoader(new Dictionary<string, string> { ["spec-model"] = specPath });
        var selector = new FakeModelSelector(new ModelSelection("spec-model", false, 10_000, DeviceTier.Desktop));
        var opts = new AIOptions
        {
            ModelPath = genPath,
            WarmOnStart = false,
            Router = new FixedRouter(RouteDecision.Specialist(ChatCapability.Reasoning, "test")),
        };
        await using var svc = new AIService(opts, loader, Factory, selector);
        await svc.StartAsync();

        Assert.Equal("SPEC", await svc.AskAsync("anything"));
        Assert.Equal(ChatCapability.Reasoning, selector.LastRequested);
    }

    [Fact]
    public async Task Router_Generalist_NeverConsultsSelector()
    {
        var generalist = new FakeChatGenerator("GEN");
        var loader = new FakeModelLoader(new Dictionary<string, string>());
        var selector = new FakeModelSelector(new ModelSelection("spec-model", false, 10_000, DeviceTier.Desktop));
        var opts = new AIOptions
        {
            ModelPath = Temp(),
            WarmOnStart = false,
            Router = new FixedRouter(RouteDecision.Generalist("stay")),
        };
        await using var svc = new AIService(opts, loader, _ => generalist, selector);
        await svc.StartAsync();

        Assert.Equal("GEN", await svc.AskAsync("hi"));
        Assert.Equal(0, selector.BestFitCallCount);
    }

    [Fact]
    public async Task EvictFirstUnderPressure_GeneralistSurvives_SpecialistRebuilds()
    {
        var genPath = Temp();
        var specPath = Temp();
        var generalist = new FakeChatGenerator("GEN");
        var specBuilds = 0;
        IChatGenerator Factory(string path)
        {
            if (path == specPath) { specBuilds++; return new FakeChatGenerator("SPEC"); }
            return generalist;
        }

        var loader = new FakeModelLoader(new Dictionary<string, string> { ["spec-model"] = specPath });
        var selector = new FakeModelSelector(new ModelSelection("spec-model", false, 10_000, DeviceTier.Desktop));
        var pressure = new ManualMemoryPressureSource();
        var opts = new AIOptions
        {
            ModelPath = genPath,
            WarmOnStart = false,
            Router = new FixedRouter(RouteDecision.Specialist(ChatCapability.Reasoning, "t")),
        };
        await using var svc = new AIService(
            opts, loader, Factory, selector, modelRegistry: null, memoryPressureSource: pressure);
        await svc.StartAsync();

        Assert.Equal("SPEC", await svc.AskAsync("q1"));
        Assert.Equal(1, specBuilds);
        Assert.Equal("SPEC", await svc.AskAsync("q2")); // already resident — no rebuild
        Assert.Equal(1, specBuilds);

        await pressure.Raise(MemoryPressureLevel.Critical); // evict specialist first

        Assert.True(svc.IsReady);                            // generalist floor intact
        Assert.Equal("SPEC", await svc.AskAsync("q3"));      // specialist rebuilt on demand
        Assert.Equal(2, specBuilds);
    }

    [Fact]
    public async Task SessionRoundTrip_GeneralistFloor()
    {
        var generalist = new FakeChatGenerator("GEN");
        var opts = new AIOptions { ModelPath = Temp(), WarmOnStart = false };
        await using var svc = new AIService(opts, generatorFactory: _ => generalist);
        await svc.StartAsync();

        var snapshot = Temp();
        Assert.True(await svc.SaveSessionAsync(snapshot));
        Assert.True(await svc.LoadSessionAsync(snapshot));
    }
}
