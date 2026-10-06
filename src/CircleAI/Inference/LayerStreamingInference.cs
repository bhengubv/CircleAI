// LayerStreamingInference.cs
//
// (3.3.0) Layer-by-layer streaming inference — pattern-port of the
// AirLLM idea: load one transformer layer's weights at a time from
// disk into RAM/VRAM, run forward, save the activations, evict the
// layer, load the next. Lets a 70B model fit on a 4 GB device at the
// cost of disk bandwidth per token.
//
// The actual MNN/CUDA glue is host-supplied via ILayerStreamingRunner.
// This file defines the contract + a null default + simple orchestrator.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>(3.3.0) One layer's weights packed for streaming.</summary>
/// <param name="LayerIndex">0-based transformer layer index.</param>
/// <param name="WeightShardPath">Path on disk to this layer's tensor shard.</param>
/// <param name="ApproxBytes">Size of the shard, for memory accounting.</param>
public sealed record LayerWeightShard(int LayerIndex, string WeightShardPath, long ApproxBytes);

/// <summary>(3.3.0) Layer-streaming model plan.</summary>
public sealed record LayerStreamingPlan(
    string                        ModelId,
    int                           TotalLayers,
    IReadOnlyList<LayerWeightShard> Shards,
    long                          ApproxParameterBytes);

/// <summary>(3.3.0) One layer's hidden-state output after forward.</summary>
public sealed record LayerActivations(int LayerIndex, ReadOnlyMemory<float> Hidden);

/// <summary>(3.3.0) Host-supplied per-layer runner (load + forward + evict).</summary>
public interface ILayerStreamingRunner
{
    string BackendId { get; }
    bool   IsAvailable { get; }

    /// <summary>Forward one layer; returns hidden states.</summary>
    ValueTask<LayerActivations> RunLayerAsync(
        LayerWeightShard      shard,
        ReadOnlyMemory<float> inputHidden,
        CancellationToken     ct = default);

    /// <summary>Drop the layer from RAM after forward.</summary>
    ValueTask EvictAsync(int layerIndex, CancellationToken ct = default);
}

/// <summary>(3.3.0) Null runner that throws on use — drop-in default.</summary>
public sealed class NullLayerStreamingRunner : ILayerStreamingRunner
{
    public static readonly NullLayerStreamingRunner Instance = new();
    public string BackendId   => "null";
    public bool   IsAvailable => false;

    public ValueTask<LayerActivations> RunLayerAsync(
        LayerWeightShard      shard,
        ReadOnlyMemory<float> inputHidden,
        CancellationToken     ct = default)
        => throw new InvalidOperationException(
            "No ILayerStreamingRunner is wired. Register one (CircleAI.Inference.Native.AirLlm) to enable layer-streaming.");

    public ValueTask EvictAsync(int layerIndex, CancellationToken ct = default) => ValueTask.CompletedTask;
}

/// <summary>(3.3.0) Drives a full forward pass layer by layer.</summary>
public sealed class LayerStreamingOrchestrator
{
    private readonly ILayerStreamingRunner _runner;

    public LayerStreamingOrchestrator(ILayerStreamingRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>
    /// (3.3.0) Stream every layer in <paramref name="plan"/>, eviciting after each.
    /// Returns the final hidden state. <paramref name="onLayerComplete"/> fires
    /// after each layer so callers can update progress / cancel mid-pass.
    /// </summary>
    public async Task<LayerActivations> ForwardAsync(
        LayerStreamingPlan          plan,
        ReadOnlyMemory<float>       initialHidden,
        Action<LayerActivations>?   onLayerComplete = null,
        CancellationToken           ct              = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Shards.Count == 0)
        {
            throw new ArgumentException("Plan has no layer shards.", nameof(plan));
        }

        var hidden = initialHidden;
        LayerActivations? last = null;
        foreach (var shard in plan.Shards)
        {
            ct.ThrowIfCancellationRequested();
            last   = await _runner.RunLayerAsync(shard, hidden, ct).ConfigureAwait(false);
            hidden = last.Hidden;
            onLayerComplete?.Invoke(last);
            await _runner.EvictAsync(shard.LayerIndex, ct).ConfigureAwait(false);
        }
        return last!;
    }
}

/// <summary>(3.3.0) Discover layer shards on disk from a manifest directory.</summary>
public static class LayerShardDiscovery
{
    /// <summary>
    /// Scan <paramref name="modelDirectory"/> for files named
    /// <c>layer_NNN.safetensors</c> (or any "layer_NNN.*" extension) and
    /// build a <see cref="LayerStreamingPlan"/>.
    /// </summary>
    public static LayerStreamingPlan Discover(string modelId, string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required", nameof(modelId));
        if (!Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException($"Model directory not found: {modelDirectory}");
        }

        var shards = new List<LayerWeightShard>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(modelDirectory, "layer_*.*"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // "layer_NNN" → NNN
            var underscoreIdx = name.IndexOf('_');
            if (underscoreIdx < 0) continue;
            if (!int.TryParse(name[(underscoreIdx + 1)..], out var index)) continue;
            var size = new FileInfo(path).Length;
            shards.Add(new LayerWeightShard(index, path, size));
            total += size;
        }

        shards.Sort((a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
        return new LayerStreamingPlan(modelId, shards.Count, shards, total);
    }
}
