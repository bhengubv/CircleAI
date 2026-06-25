// Circle34RtFeaturesTests.cs
//
// (3.4.0) Unit tests for the pure-managed RT-* feature implementations
// in MnnInteropRtFeatures.cs — RT-05 speculative decoding and RT-12
// mesh offload. (RT-03 mmap and RT-10 LoRA need the native bridge to be
// rebuilt; not unit-testable here.)

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class Circle34RtFeaturesTests
{
    // RT-12 MeshOffloadStrategy ────────────────────────────────────────

    [Fact]
    public void MeshOffload_LocalCanFit_NoOffload()
    {
        var strat = new MeshOffloadStrategy(
            peers:           () => Array.Empty<MeshPeer>(),
            localRamBytes:   8L * 1024 * 1024 * 1024,
            localLoadAvg:    0.3);
        var verdict = strat.Decide("qwen-7b", requiredRamBytes: 4L * 1024 * 1024 * 1024, expectedSecondsLocal: 2);
        Assert.False(verdict.ShouldOffload);
    }

    [Fact]
    public void MeshOffload_LocalCantFit_PicksEligiblePeer()
    {
        var peers = new[]
        {
            new MeshPeer("peer-a", LatencyMs: 30, RamBytes: 16L * 1024 * 1024 * 1024, LoadAvg: 0.2,
                SupportedModels: new[] { "qwen-7b" }),
        };
        var strat = new MeshOffloadStrategy(
            peers:           () => peers,
            localRamBytes:   2L * 1024 * 1024 * 1024,
            localLoadAvg:    0.3);
        var verdict = strat.Decide("qwen-7b", requiredRamBytes: 8L * 1024 * 1024 * 1024, expectedSecondsLocal: 5);
        Assert.True(verdict.ShouldOffload);
        Assert.Equal("peer-a", verdict.TargetPeerId);
    }

    [Fact]
    public void MeshOffload_LocalCantFit_NoEligiblePeer_NoOffload()
    {
        var strat = new MeshOffloadStrategy(
            peers:           () => Array.Empty<MeshPeer>(),
            localRamBytes:   2L * 1024 * 1024 * 1024,
            localLoadAvg:    0.3);
        var verdict = strat.Decide("qwen-7b", requiredRamBytes: 8L * 1024 * 1024 * 1024, expectedSecondsLocal: 5);
        Assert.False(verdict.ShouldOffload);
    }

    [Fact]
    public void MeshOffload_LocalOverloaded_PrefersFasterPeer()
    {
        var peers = new[]
        {
            new MeshPeer("peer-a", LatencyMs: 20, RamBytes: 16L * 1024 * 1024 * 1024, LoadAvg: 0.1,
                SupportedModels: new[] { "qwen-7b" }),
        };
        var strat = new MeshOffloadStrategy(
            peers:           () => peers,
            localRamBytes:   16L * 1024 * 1024 * 1024,
            localLoadAvg:    0.95);
        var verdict = strat.Decide("qwen-7b", requiredRamBytes: 4L * 1024 * 1024 * 1024, expectedSecondsLocal: 5);
        Assert.True(verdict.ShouldOffload);
    }

    // RT-05 SpeculativeDecodingPipeline ────────────────────────────────

    [Fact]
    public async Task SpeculativeDecoding_AcceptsAgreedWords()
    {
        var draft  = new StaticGen("hello world foo bar");
        var target = new StaticGen("hello world baz qux");
        var pipe = new SpeculativeDecodingPipeline(draft, target, draftLen: 4);
        var output = new System.Text.StringBuilder();
        await pipe.GenerateAsync(
            new[] { new ChatMessage("user", "hi") },
            txt => output.Append(txt),
            maxChars: 30);
        // The agreed prefix is "hello world ".
        Assert.Contains("hello world", output.ToString());
    }

    [Fact]
    public async Task SpeculativeDecoding_StopsWhenGeneratorsDryUp()
    {
        var draft  = new StaticGen("");
        var target = new StaticGen("");
        var pipe = new SpeculativeDecodingPipeline(draft, target, draftLen: 4);
        var output = new System.Text.StringBuilder();
        var n = await pipe.GenerateAsync(
            new[] { new ChatMessage("user", "hi") },
            txt => output.Append(txt),
            maxChars: 20);
        Assert.Equal(0, n);
    }

    // Test helper — a real IChatGenerator that always streams the same text.
    private sealed class StaticGen : IChatGenerator
    {
        private readonly string _text;
        public StaticGen(string text) => _text = text;

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(_text);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (!string.IsNullOrEmpty(_text)) yield return _text;
        }

        public void Dispose() { }
    }
}
