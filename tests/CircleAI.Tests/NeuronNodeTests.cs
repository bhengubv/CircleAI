// NeuronNodeTests.cs — the host-neutral facade + AddNeuron DI wiring.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Hosting.Chat;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public sealed class NeuronNodeTests : IDisposable
{
    private readonly string _modelPath = Path.GetTempFileName();

    public void Dispose()
    {
        try { File.Delete(_modelPath); } catch { /* best-effort */ }
    }

    private AIService BuildBrain(string reply = "hi", string[]? chunks = null, string? modelId = null)
    {
        var gen = new FakeChatGenerator(reply, chunks);
        var opts = new AIOptions { ModelPath = _modelPath, ModelId = modelId, WarmOnStart = false };
        return new AIService(opts, generatorFactory: _ => gen);
    }

    [Fact]
    public async Task Stream_MapsTurns_AndYieldsBrainChunks()
    {
        await using var brain = BuildBrain(chunks: new[] { "Hel", "lo" });
        var node = new NeuronNode(brain);
        await brain.StartAsync();

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", "hi") }))
            sb.Append(chunk);

        Assert.Equal("Hello", sb.ToString());
    }

    [Fact]
    public async Task IsReady_And_Status_ReflectBrain()
    {
        await using var brain = BuildBrain();
        var node = new NeuronNode(brain);

        Assert.False(node.IsReady);
        Assert.Equal("loading model…", node.StatusMessage);

        await brain.StartAsync();

        Assert.True(node.IsReady);
        Assert.Equal("ready", node.StatusMessage);
    }

    [Fact]
    public async Task EngineLabel_ReflectsResolvedModel()
    {
        await using var brain = BuildBrain(modelId: "qwen3-test");
        var node = new NeuronNode(brain);
        await brain.StartAsync();

        Assert.Contains("qwen3-test", node.EngineLabel);
    }

    [Fact]
    public async Task SaveLoad_RoundTrips_ThroughBrain()
    {
        var snapshot = Path.GetTempFileName();
        try
        {
            await using var brain = BuildBrain();
            var node = new NeuronNode(brain);
            await brain.StartAsync();

            Assert.True(await node.SaveSessionAsync(snapshot));
            Assert.True(await node.LoadSessionAsync(snapshot));
            Assert.False(string.IsNullOrWhiteSpace(node.SessionSnapshotPath));
        }
        finally { try { File.Delete(snapshot); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task NullChatRuntime_YieldsOfflineStatus()
    {
        var node = new NullChatRuntime();
        Assert.False(node.IsReady);

        var sb = new StringBuilder();
        await foreach (var chunk in node.StreamAsync(new[] { new ChatTurn("user", "hi") }))
            sb.Append(chunk);

        Assert.Contains("No chat engine", sb.ToString());
    }

    [Fact]
    public async Task AddNeuron_RegistersHostNeutralRuntime()
    {
        var services = new ServiceCollection();
        services.AddNeuron(
            () => new AIOptions { ModelPath = _modelPath, WarmOnStart = false },
            warmOnStart: false);

        // AIService is IAsyncDisposable-only, so the container must dispose async.
        await using var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<IChatRuntime>();

        Assert.IsType<NeuronNode>(runtime);
        Assert.Equal("circleai-neuron", runtime.Id);
        Assert.False(runtime.IsReady); // resolving must not load a native model
    }
}
