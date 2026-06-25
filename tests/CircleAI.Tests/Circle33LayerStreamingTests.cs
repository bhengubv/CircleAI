// Circle33LayerStreamingTests.cs
//
// (3.3.0) Tests for layer-streaming inference contracts and orchestrator.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class Circle33LayerStreamingTests
{
    [Fact]
    public void NullRunner_IsAvailable_False()
    {
        var r = NullLayerStreamingRunner.Instance;
        Assert.False(r.IsAvailable);
        Assert.Equal("null", r.BackendId);
    }

    [Fact]
    public async Task NullRunner_RunLayer_Throws()
    {
        var r = NullLayerStreamingRunner.Instance;
        var shard = new LayerWeightShard(0, "/tmp/x", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await r.RunLayerAsync(shard, ReadOnlyMemory<float>.Empty));
    }

    [Fact]
    public async Task Orchestrator_RunsEveryLayerInOrder_EvictsAfterEach()
    {
        var runner = new FakeRunner();
        var orchestrator = new LayerStreamingOrchestrator(runner);

        var shards = new List<LayerWeightShard>
        {
            new(0, "/tmp/0", 1),
            new(1, "/tmp/1", 1),
            new(2, "/tmp/2", 1),
        };
        var plan = new LayerStreamingPlan("Qwen-x", 3, shards, 3);
        var initial = new float[] { 1, 1, 1 };

        var result = await orchestrator.ForwardAsync(plan, initial);

        Assert.Equal(new[] { 0, 1, 2 }, runner.RunSequence);
        Assert.Equal(new[] { 0, 1, 2 }, runner.EvictSequence);
        Assert.Equal(2, result.LayerIndex);
    }

    [Fact]
    public async Task Orchestrator_EmptyPlan_Throws()
    {
        var runner = new FakeRunner();
        var orchestrator = new LayerStreamingOrchestrator(runner);
        var emptyPlan = new LayerStreamingPlan("Q", 0, Array.Empty<LayerWeightShard>(), 0);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await orchestrator.ForwardAsync(emptyPlan, ReadOnlyMemory<float>.Empty));
    }

    [Fact]
    public async Task Orchestrator_FiresProgressCallbackAfterEachLayer()
    {
        var runner = new FakeRunner();
        var orchestrator = new LayerStreamingOrchestrator(runner);
        var plan = new LayerStreamingPlan("Q", 2,
            new[] { new LayerWeightShard(0, "/tmp/0", 1), new LayerWeightShard(1, "/tmp/1", 1) }, 2);

        var seen = new List<int>();
        await orchestrator.ForwardAsync(plan, new float[] { 0 }, onLayerComplete: a => seen.Add(a.LayerIndex));

        Assert.Equal(new[] { 0, 1 }, seen);
    }

    [Fact]
    public async Task Orchestrator_CancelMidPass_StopsRunner()
    {
        var runner = new FakeRunner { DelayPerLayerMs = 50 };
        var orchestrator = new LayerStreamingOrchestrator(runner);
        var plan = new LayerStreamingPlan("Q", 4,
            new[]
            {
                new LayerWeightShard(0, "/tmp/0", 1),
                new LayerWeightShard(1, "/tmp/1", 1),
                new LayerWeightShard(2, "/tmp/2", 1),
                new LayerWeightShard(3, "/tmp/3", 1),
            }, 4);

        using var cts = new CancellationTokenSource(75);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await orchestrator.ForwardAsync(plan, new float[] { 0 }, ct: cts.Token));
    }

    [Fact]
    public void Discover_ReadsLayerFilesInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"circleai-layers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "layer_002.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "layer_000.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "layer_001.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "not_a_layer.bin"), "x");

            var plan = LayerShardDiscovery.Discover("model-x", dir);

            Assert.Equal(3, plan.TotalLayers);
            Assert.Equal(new[] { 0, 1, 2 }, plan.Shards.Select(s => s.LayerIndex));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Discover_MissingDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            LayerShardDiscovery.Discover("m", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    private sealed class FakeRunner : ILayerStreamingRunner
    {
        public List<int> RunSequence    { get; } = new();
        public List<int> EvictSequence  { get; } = new();
        public int       DelayPerLayerMs { get; set; }

        public string BackendId   => "fake";
        public bool   IsAvailable => true;

        public async ValueTask<LayerActivations> RunLayerAsync(
            LayerWeightShard      shard,
            ReadOnlyMemory<float> inputHidden,
            CancellationToken     ct = default)
        {
            if (DelayPerLayerMs > 0) await Task.Delay(DelayPerLayerMs, ct).ConfigureAwait(false);
            RunSequence.Add(shard.LayerIndex);
            return new LayerActivations(shard.LayerIndex, inputHidden);
        }

        public ValueTask EvictAsync(int layerIndex, CancellationToken ct = default)
        {
            EvictSequence.Add(layerIndex);
            return ValueTask.CompletedTask;
        }
    }
}
