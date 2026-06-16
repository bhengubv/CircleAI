// CircleAITwoZeroTests.cs
//
// Tests for the 2.0.0 features: RT-08 fallback chain, RT-04 brownout
// observer signal, RT-09 embeddings store round-trip.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core.Models;
using CircleAI.Embeddings.Local;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

// ────────────────────────────────────────────────────────────────────────
// RT-08 — IModelSelector.ChainFor
// ────────────────────────────────────────────────────────────────────────

public sealed class FallbackChainTests
{
    [Fact]
    public void ChainFor_WalksFallbackModelIdTransitively()
    {
        var big    = new ModelEntry("big",    "1.0", "Q4") { QualityRank = 30, FallbackModelId = "med" };
        var med    = new ModelEntry("med",    "1.0", "Q4") { QualityRank = 20, FallbackModelId = "small" };
        var small  = new ModelEntry("small",  "1.0", "Q4") { QualityRank = 10 };

        using var registry = new InMemoryRegistry(new[] { big, med, small });
        var selector       = new DeviceAwareModelSelector(registry);

        var chain = selector.ChainFor("big");
        Assert.Equal(new[] { "big", "med", "small" }, chain);
    }

    [Fact]
    public void ChainFor_TerminatesOnSelfReference()
    {
        var a = new ModelEntry("a", "1.0", "Q4") { QualityRank = 10, FallbackModelId = "a" };
        using var registry = new InMemoryRegistry(new[] { a });
        var selector       = new DeviceAwareModelSelector(registry);
        Assert.Equal(new[] { "a" }, selector.ChainFor("a"));
    }

    [Fact]
    public void ChainFor_BreaksCycle()
    {
        var a = new ModelEntry("a", "1.0", "Q4") { QualityRank = 10, FallbackModelId = "b" };
        var b = new ModelEntry("b", "1.0", "Q4") { QualityRank = 9,  FallbackModelId = "a" };
        using var registry = new InMemoryRegistry(new[] { a, b });
        var selector       = new DeviceAwareModelSelector(registry);
        Assert.Equal(new[] { "a", "b" }, selector.ChainFor("a"));
    }

    [Fact]
    public void ChainFor_UnknownHeadReturnsEmpty()
    {
        using var registry = new InMemoryRegistry(Array.Empty<ModelEntry>());
        var selector       = new DeviceAwareModelSelector(registry);
        Assert.Empty(selector.ChainFor("ghost"));
    }

    [Fact]
    public void ChainFor_EmbeddedRegistry_HasQwen3Chain()
    {
        // Sanity-check the catalog ships the qwen3 chain we just stamped.
        using var registry = new ModelRegistryService();
        var selector       = new DeviceAwareModelSelector(registry);
        var chain          = selector.ChainFor("Qwen3-14B-MNN");
        Assert.Contains("Qwen3-8B-MNN",   chain);
        Assert.Contains("Qwen3-4B-MNN",   chain);
        Assert.Contains("Qwen3-1.7B-MNN", chain);
        Assert.Contains("Qwen3-0.6B-MNN", chain);
        // Final entry must be terminal — no further fallback.
        Assert.Equal("Qwen3-0.6B-MNN", chain[^1]);
    }
}

// ────────────────────────────────────────────────────────────────────────
// RT-04 — IMemoryPressureSource
// ────────────────────────────────────────────────────────────────────────

public sealed class MemoryPressureSourceTests
{
    [Fact]
    public async Task ManualSource_RaisesOnceWithCorrectTransition()
    {
        var src = new ManualMemoryPressureSource();
        var transitions = new List<(MemoryPressureLevel, MemoryPressureLevel)>();
        using var sub = src.Subscribe((from, to) =>
        {
            transitions.Add((from, to));
            return ValueTask.CompletedTask;
        });

        await src.Raise(MemoryPressureLevel.Trim);
        await src.Raise(MemoryPressureLevel.Trim); // idempotent — no second event
        await src.Raise(MemoryPressureLevel.Critical);

        Assert.Equal(2, transitions.Count);
        Assert.Equal((MemoryPressureLevel.Normal,  MemoryPressureLevel.Trim),     transitions[0]);
        Assert.Equal((MemoryPressureLevel.Trim,    MemoryPressureLevel.Critical), transitions[1]);
    }

    [Fact]
    public void NullSource_NeverFires()
    {
        var fired = false;
        using var sub = NullMemoryPressureSource.Instance.Subscribe((_, _) =>
        {
            fired = true;
            return ValueTask.CompletedTask;
        });
        Assert.False(fired);
        Assert.Equal(MemoryPressureLevel.Normal, NullMemoryPressureSource.Instance.Current);
    }

    [Fact]
    public async Task ManualSource_UnsubscribedHandlerStopsFiring()
    {
        var src = new ManualMemoryPressureSource();
        var count = 0;
        var sub = src.Subscribe((_, _) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        });
        await src.Raise(MemoryPressureLevel.Trim);
        sub.Dispose();
        await src.Raise(MemoryPressureLevel.Critical);
        Assert.Equal(1, count);
    }
}

// ────────────────────────────────────────────────────────────────────────
// RT-09 — InMemoryEmbeddingStore round-trip
// ────────────────────────────────────────────────────────────────────────

public sealed class InMemoryEmbeddingStoreTests
{
    private sealed class HashEncoder : IEmbeddingEncoder
    {
        public int Dimension { get; }
        public HashEncoder(int d) => Dimension = d;
        public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
        {
            // Deterministic pseudo-random encoder so search has signal.
            var seed = unchecked((int)2166136261u);
            foreach (var c in text) seed = (seed ^ c) * 16777619;
            var rng = new Random(seed);
            var v = new float[Dimension];
            for (var i = 0; i < Dimension; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);
            return ValueTask.FromResult(v);
        }
    }

    [Fact]
    public async Task AddSearch_FindsExactMatch()
    {
        await using var store = new InMemoryEmbeddingStore(new HashEncoder(64));
        await store.AddAsync(new EmbeddingDocument("a", "the quick brown fox"));
        await store.AddAsync(new EmbeddingDocument("b", "lorem ipsum dolor sit amet"));
        await store.AddAsync(new EmbeddingDocument("c", "completely unrelated text here"));

        var hits = await store.SearchAsync("the quick brown fox", topK: 1);
        Assert.Single(hits);
        Assert.Equal("a", hits[0].Document.Id);
        Assert.True(hits[0].Score > 0.9f);
    }

    [Fact]
    public async Task TopK_BoundedAndOrdered()
    {
        await using var store = new InMemoryEmbeddingStore(new HashEncoder(32));
        for (var i = 0; i < 25; i++)
            await store.AddAsync(new EmbeddingDocument($"doc-{i}", $"text {i}"));

        var hits = await store.SearchAsync("text 7", topK: 5);
        Assert.Equal(5, hits.Count);
        // Must be sorted descending.
        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Score >= hits[i].Score);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllDocuments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"circleai-emb-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var store = new InMemoryEmbeddingStore(new HashEncoder(48)))
            {
                await store.AddAsync(new EmbeddingDocument("k1", "alpha bravo charlie",
                    new Dictionary<string, string> { ["lang"] = "en", ["tag"] = "phonetic" }));
                await store.AddAsync(new EmbeddingDocument("k2", "delta echo foxtrot"));
                await store.SaveAsync(path);
                Assert.Equal(2, store.Count);
            }

            await using var loaded = new InMemoryEmbeddingStore(new HashEncoder(48));
            await loaded.LoadAsync(path);
            Assert.Equal(2, loaded.Count);

            var hits = await loaded.SearchAsync("alpha bravo charlie", topK: 1);
            Assert.Single(hits);
            Assert.Equal("k1", hits[0].Document.Id);
            Assert.Equal("en", hits[0].Document.Metadata!["lang"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Remove_DeletesDocument()
    {
        await using var store = new InMemoryEmbeddingStore(new HashEncoder(16));
        await store.AddAsync(new EmbeddingDocument("x", "delete me"));
        Assert.Equal(1, store.Count);
        Assert.True(await store.RemoveAsync("x"));
        Assert.Equal(0, store.Count);
        Assert.False(await store.RemoveAsync("x"));
    }

    [Fact]
    public async Task AddAsync_VectorDimensionMismatch_Throws()
    {
        await using var store = new InMemoryEmbeddingStore(new HashEncoder(32));
        var doc = new EmbeddingDocument("z", "ignored");
        var badVec = new float[31];
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.AddAsync(doc, badVec));
    }
}
