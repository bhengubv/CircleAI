// TurboVecHnswStoreTests.cs
//
// (RT-09b) Tests for the turbovec-backed HnswEmbeddingStore + low-level
// TurboVecEmbeddingIndex. Verifies native-library load, add/search
// round-trip, persistence, and ABI version surface.
//
// All tests are offline; no network. Native lib (turbovecbridge.dll)
// must be present in the test bin/ directory — the csproj's
// CopyToOutputDirectory pattern places it there automatically.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Embeddings.Local;
using Xunit;

namespace CircleAI.Tests;

public sealed class TurboVecEmbeddingIndexTests
{
    [Fact]
    public void NativeAbiVersion_Resolves()
    {
        // Native lib must load — if this throws, the dll is missing or unreadable.
        var v = TurboVecEmbeddingIndex.NativeAbiVersion();
        Assert.True(v >= 1, $"ABI version reported as {v} — native lib load problem.");
    }

    [Fact]
    public async Task AddAndSearch_ExactQueryReturnsItself()
    {
        using var idx = new TurboVecEmbeddingIndex(dimension: 64, bitWidth: 4);
        var rng = new Random(12345);
        var vectors = new float[64 * 10];
        for (var i = 0; i < vectors.Length; i++) vectors[i] = (float)(rng.NextDouble() * 2 - 1);

        // Add 10 vectors one by one.
        for (var i = 0; i < 10; i++)
        {
            var slice = new float[64];
            Array.Copy(vectors, i * 64, slice, 0, 64);
            await idx.AddAsync(slice);
        }
        Assert.Equal(10, idx.Count);

        // Query with vector 5; expect it to come back as a hit.
        var query = new float[64];
        Array.Copy(vectors, 5 * 64, query, 0, 64);
        var hits = await idx.SearchAsync(query, topK: 3);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.InternalId == 5);
    }

    [Fact]
    public async Task SaveLoad_PreservesContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tvb-test-{Guid.NewGuid():N}.tvi");
        try
        {
            using (var src = new TurboVecEmbeddingIndex(dimension: 32, bitWidth: 4))
            {
                var rng = new Random(99);
                for (var i = 0; i < 25; i++)
                {
                    var v = new float[32];
                    for (var j = 0; j < 32; j++) v[j] = (float)(rng.NextDouble() * 2 - 1);
                    await src.AddAsync(v);
                }
                await src.SaveAsync(path);
                Assert.Equal(25, src.Count);
            }

            using var dst = new TurboVecEmbeddingIndex(dimension: 32, bitWidth: 4);
            await dst.LoadAsync(path);
            Assert.Equal(25, dst.Count);

            // Search should yield something.
            var q = new float[32];
            for (var j = 0; j < 32; j++) q[j] = 0.1f;
            var hits = await dst.SearchAsync(q, topK: 5);
            Assert.NotEmpty(hits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(7, 4)]    // not multiple of 8
    [InlineData(64, 1)]
    [InlineData(64, 5)]
    public void Constructor_RejectsInvalidArgs(int dim, int bitWidth)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TurboVecEmbeddingIndex(dim, bitWidth));
    }
}

public sealed class HnswEmbeddingStoreTests
{
    private sealed class HashEncoder : IEmbeddingEncoder
    {
        public int Dimension { get; }
        public HashEncoder(int d) => Dimension = d;
        public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
        {
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
        await using var store = new HnswEmbeddingStore(new HashEncoder(64));
        await store.AddAsync(new EmbeddingDocument("a", "the quick brown fox"));
        await store.AddAsync(new EmbeddingDocument("b", "lorem ipsum dolor sit amet"));
        await store.AddAsync(new EmbeddingDocument("c", "completely unrelated text here"));

        var hits = await store.SearchAsync("the quick brown fox", topK: 1);
        Assert.NotEmpty(hits);
        Assert.Equal("a", hits[0].Document.Id);
    }

    [Fact]
    public async Task Remove_HidesDocFromSearch()
    {
        await using var store = new HnswEmbeddingStore(new HashEncoder(32));
        await store.AddAsync(new EmbeddingDocument("x", "delete me"));
        await store.AddAsync(new EmbeddingDocument("y", "keep me"));

        Assert.True(await store.RemoveAsync("x"));
        var hits = await store.SearchAsync("delete me", topK: 5);
        Assert.DoesNotContain(hits, h => h.Document.Id == "x");
    }

    [Fact]
    public async Task SaveLoad_RoundTripsDocsAndIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hnsw-test-{Guid.NewGuid():N}.tvi");
        try
        {
            await using (var src = new HnswEmbeddingStore(new HashEncoder(48)))
            {
                await src.AddAsync(new EmbeddingDocument("k1", "alpha bravo charlie",
                    new Dictionary<string, string> { ["lang"] = "en" }));
                await src.AddAsync(new EmbeddingDocument("k2", "delta echo foxtrot"));
                await src.SaveAsync(path);
                Assert.Equal(2, src.Count);
            }

            await using var loaded = new HnswEmbeddingStore(new HashEncoder(48));
            await loaded.LoadAsync(path);
            Assert.Equal(2, loaded.Count);
            var hits = await loaded.SearchAsync("alpha bravo charlie", topK: 1);
            Assert.NotEmpty(hits);
            Assert.Equal("k1", hits[0].Document.Id);
            Assert.Equal("en", hits[0].Document.Metadata!["lang"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".docs")) File.Delete(path + ".docs");
        }
    }

    [Fact]
    public void Constructor_RejectsNon8Dimension()
    {
        Assert.Throws<ArgumentException>(() => new HnswEmbeddingStore(new HashEncoder(63)));
    }
}
