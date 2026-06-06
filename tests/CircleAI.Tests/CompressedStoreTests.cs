// CompressedStoreTests.cs
//
// Item 5 audit follow-up — applies the TurboQuant codec to the two
// embedding-bearing stores via decorators. Verifies:
//   • Round-trip through Add → Get/Search preserves geometry (cosine)
//   • Embedding field is NULL on the inner store (the whole point —
//     no FP32 duplication)
//   • Search ranking still works against compressed entries
//   • Non-embedding entries pass through untouched

using CircleAI.Memory;
using CircleAI.Memory.Compression;
using CircleAI.Memory.Multimodal;
using Xunit;

namespace CircleAI.Tests;

public sealed class CompressedStoreTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static float[] RandomUnit(int dim, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        double sumSq = 0;
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)(rng.NextDouble() * 2 - 1);
            sumSq += v[i] * v[i];
        }
        var inv = (float)(1.0 / Math.Sqrt(sumSq));
        for (int i = 0; i < dim; i++) v[i] *= inv;
        return v;
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; magA += a[i] * a[i]; magB += b[i] * b[i]; }
        var d = Math.Sqrt(magA) * Math.Sqrt(magB);
        return d < 1e-30 ? 0f : (float)(dot / d);
    }

    // ══════════════════════════════════════════════════════════════════════
    // EmbeddingPayloadCodec
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Codec_RoundTrip_PreservesCosine()
    {
        var v = RandomUnit(128, seed: 42);
        var encoded = EmbeddingPayloadCodec.Encode(v, bitsPerDim: 4);
        var decoded = EmbeddingPayloadCodec.Decode(encoded);
        Assert.True(Cosine(v, decoded) >= 0.99f, "4-bit cosine should be ≥ 0.99");
    }

    [Fact]
    public void Codec_DetectsHeader()
    {
        var encoded = EmbeddingPayloadCodec.Encode(RandomUnit(64, 1), 2);
        Assert.True(EmbeddingPayloadCodec.IsEncoded(encoded));
        Assert.False(EmbeddingPayloadCodec.IsEncoded(new byte[] { 0x00, 0x01, 0x02 }));
    }

    [Fact]
    public void Codec_RejectsTooShortPayload()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingPayloadCodec.Decode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Codec_Base64_RoundTrip()
    {
        var v = RandomUnit(64, seed: 7);
        var b64 = EmbeddingPayloadCodec.EncodeBase64(v, 3);
        var back = EmbeddingPayloadCodec.DecodeBase64(b64);
        Assert.True(Cosine(v, back) >= 0.96f);
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedEpisodicMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EpisodicStore_AddStoresEmbeddingAsCompressedTag_NotFloat()
    {
        var inner = new InMemoryEpisodicStore();
        var outer = new CompressedEpisodicMemoryStore(inner, bitsPerDim: 2);

        var entry = new EpisodicMemoryEntry
        {
            UserText = "hello",
            AssistantText = "hi",
            Embedding = RandomUnit(128, 1),
        };
        await outer.AddAsync(entry);

        // Inner store sees a NULL embedding.
        var rawRecent = await inner.GetRecentAsync(1);
        Assert.Single(rawRecent);
        Assert.Null(rawRecent[0].Embedding);
        Assert.NotNull(rawRecent[0].Tags);
        Assert.True(rawRecent[0].Tags!.ContainsKey(CompressedEpisodicMemoryStore.CompressedTagKey));
    }

    [Fact]
    public async Task EpisodicStore_GetRecentRehydratesEmbedding()
    {
        var inner = new InMemoryEpisodicStore();
        var outer = new CompressedEpisodicMemoryStore(inner, bitsPerDim: 4);

        var original = RandomUnit(64, 1);
        await outer.AddAsync(new EpisodicMemoryEntry
        {
            UserText = "u", AssistantText = "a", Embedding = original,
        });

        var got = await outer.GetRecentAsync(1);
        Assert.Single(got);
        Assert.NotNull(got[0].Embedding);
        Assert.True(Cosine(original, got[0].Embedding!) >= 0.99f);
    }

    [Fact]
    public async Task EpisodicStore_SearchRanksByCosineThroughCompression()
    {
        var inner = new InMemoryEpisodicStore();
        var outer = new CompressedEpisodicMemoryStore(inner, bitsPerDim: 4);

        var v1 = RandomUnit(64, seed: 1);
        var v2 = RandomUnit(64, seed: 2); // unrelated
        await outer.AddAsync(new EpisodicMemoryEntry { UserText = "near", AssistantText = "n", Embedding = v1 });
        await outer.AddAsync(new EpisodicMemoryEntry { UserText = "far", AssistantText = "f", Embedding = v2 });

        // Query close to v1 should put "near" first.
        var results = await outer.SearchAsync(v1, topK: 2);
        Assert.Equal(2, results.Count);
        Assert.Equal("near", results[0].UserText);
    }

    [Fact]
    public async Task EpisodicStore_AddWithoutEmbedding_PassesThroughUnchanged()
    {
        var inner = new InMemoryEpisodicStore();
        var outer = new CompressedEpisodicMemoryStore(inner);

        await outer.AddAsync(new EpisodicMemoryEntry { UserText = "u", AssistantText = "a" });
        var raw = await inner.GetRecentAsync(1);
        Assert.Single(raw);
        Assert.Null(raw[0].Embedding);
        // No compressed tag should appear when there was no embedding to begin with.
        Assert.True(raw[0].Tags is null || !raw[0].Tags!.ContainsKey(CompressedEpisodicMemoryStore.CompressedTagKey));
    }

    [Fact]
    public void EpisodicStore_InvalidBitWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompressedEpisodicMemoryStore(new InMemoryEpisodicStore(), bitsPerDim: 9));
    }

    // ══════════════════════════════════════════════════════════════════════
    // CompressedMultimodalMemoryStore
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MultimodalStore_RoundTrip_PreservesEmbeddingAndMetadata()
    {
        var inner = new InMemoryMultimodalMemoryStore();
        var outer = new CompressedMultimodalMemoryStore(inner, bitsPerDim: 4);

        var emb = RandomUnit(128, 9);
        await outer.AddAsync(new MultimodalMemoryEntry
        {
            SourceSha256 = "deadbeef",
            Modality = MediaModality.Image,
            Caption = "a sunny beach",
            Embedding = emb,
            WidthPx = 1920,
            HeightPx = 1080,
        });

        var got = await outer.GetByHashAsync("deadbeef");
        Assert.NotNull(got);
        Assert.Equal("a sunny beach", got!.Caption);
        Assert.Equal(1920, got.WidthPx);
        Assert.Equal(1080, got.HeightPx);
        Assert.NotNull(got.Embedding);
        Assert.True(Cosine(emb, got.Embedding!) >= 0.99f);
    }

    [Fact]
    public async Task MultimodalStore_InnerStoreSeesNullEmbedding()
    {
        var inner = new InMemoryMultimodalMemoryStore();
        var outer = new CompressedMultimodalMemoryStore(inner);

        await outer.AddAsync(new MultimodalMemoryEntry
        {
            SourceSha256 = "abc",
            Caption = "x",
            Embedding = RandomUnit(64, 1),
        });

        var raw = await inner.GetByHashAsync("abc");
        Assert.NotNull(raw);
        Assert.Null(raw!.Embedding);
        Assert.True(raw.Tags!.ContainsKey(CompressedMultimodalMemoryStore.CompressedTagKey));
    }

    [Fact]
    public async Task MultimodalStore_SearchRanksByCosineThroughCompression()
    {
        var inner = new InMemoryMultimodalMemoryStore();
        var outer = new CompressedMultimodalMemoryStore(inner, bitsPerDim: 4);

        var v1 = RandomUnit(64, 1);
        var v2 = RandomUnit(64, 2);
        await outer.AddAsync(new MultimodalMemoryEntry { SourceSha256 = "a", Caption = "near", Embedding = v1 });
        await outer.AddAsync(new MultimodalMemoryEntry { SourceSha256 = "b", Caption = "far",  Embedding = v2 });

        var results = await outer.SearchAsync(v1, topK: 2);
        Assert.Equal(2, results.Count);
        Assert.Equal("near", results[0].Caption);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Storage size shrinkage proof
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Codec_PayloadAtTwoBits_IsAround16TimesSmallerThanFp32()
    {
        var v = RandomUnit(1536, seed: 42);
        var encoded = EmbeddingPayloadCodec.Encode(v, bitsPerDim: 2);
        var rawSize = v.Length * 4; // FP32 bytes
        var ratio = (double)rawSize / encoded.Length;
        // Header overhead means we don't hit a clean 16× for short vectors,
        // but at 1536-dim the header is amortised; expect > 12×.
        Assert.True(ratio > 12.0, $"Expected > 12× shrink at 1536-dim/2-bit; got {ratio:F2}×");
    }
}
