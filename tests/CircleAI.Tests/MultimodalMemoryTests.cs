// MultimodalMemoryTests.cs
//
// Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
// InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester
// (dedup + caption + persist). No external dependencies; bytes are
// synthesised inline so the tests run identically on every box.

using System.Text;
using CircleAI.Memory.Multimodal;
using Xunit;

namespace CircleAI.Tests;

public sealed class MultimodalMemoryTests
{
    // ── Test helpers ──────────────────────────────────────────────────────

    private static byte[] FakeJpeg(int extraBytes = 100)
    {
        var buf = new byte[2 + extraBytes];
        buf[0] = 0xFF; buf[1] = 0xD8;
        for (int i = 2; i < buf.Length; i++) buf[i] = (byte)(i % 251);
        return buf;
    }

    private static byte[] FakePng(int extraBytes = 100)
    {
        var buf = new byte[4 + extraBytes];
        buf[0] = 0x89; buf[1] = 0x50; buf[2] = 0x4E; buf[3] = 0x47;
        for (int i = 4; i < buf.Length; i++) buf[i] = (byte)(i % 251);
        return buf;
    }

    private static MultimodalMemoryIngester WireIngester(
        out InMemoryMultimodalMemoryStore store,
        IMultimodalCaptioner? customCaptioner = null)
    {
        store = new InMemoryMultimodalMemoryStore();
        var captioners = customCaptioner is null
            ? new IMultimodalCaptioner[] { new HeuristicMultimodalCaptioner() }
            : new[] { customCaptioner, (IMultimodalCaptioner)new HeuristicMultimodalCaptioner() };
        return new MultimodalMemoryIngester(captioners, store);
    }

    // ══════════════════════════════════════════════════════════════════════
    // HeuristicMultimodalCaptioner
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Heuristic_AlwaysCanCaption_AnyModality()
    {
        var c = new HeuristicMultimodalCaptioner();
        Assert.True(c.CanCaption(MediaModality.Image, "image/jpeg"));
        Assert.True(c.CanCaption(MediaModality.Audio, null));
        Assert.True(c.CanCaption(MediaModality.Video, "video/mp4"));
        Assert.True(c.CanCaption(MediaModality.TextDocument, "application/pdf"));
    }

    [Fact]
    public async Task Heuristic_Caption_DetectsJpegMagic()
    {
        var c = new HeuristicMultimodalCaptioner();
        var r = await c.CaptionAsync(MediaModality.Image, FakeJpeg(), mimeType: null);
        Assert.Contains("image/jpeg", r.Caption);
        Assert.Null(r.Embedding); // honest: heuristic produces no embedding
    }

    [Fact]
    public async Task Heuristic_Caption_UsesDeclaredMime_WhenProvided()
    {
        var c = new HeuristicMultimodalCaptioner();
        var r = await c.CaptionAsync(MediaModality.Image, FakePng(), mimeType: "image/heic");
        Assert.Contains("image/heic", r.Caption);
    }

    [Fact]
    public async Task Heuristic_Caption_MarksItselfAsFallback()
    {
        var c = new HeuristicMultimodalCaptioner();
        var r = await c.CaptionAsync(MediaModality.Image, FakeJpeg(), mimeType: null);
        Assert.Contains("no captioner wired", r.Caption);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Ingester — happy path
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ingest_FirstTime_AddsEntryAndReportsNotDeduplicated()
    {
        var ing = WireIngester(out var store);
        var bytes = FakeJpeg();
        var r = await ing.IngestAsync(MediaModality.Image, bytes, mimeType: "image/jpeg");

        Assert.False(r.WasDeduplicated);
        Assert.Equal(1, await store.CountAsync());
        Assert.NotNull(r.Entry);
        Assert.Equal(bytes.Length, r.Entry.SourceByteCount);
        Assert.Equal("image/jpeg", r.Entry.SourceMimeType);
        Assert.False(string.IsNullOrWhiteSpace(r.Entry.SourceSha256));
    }

    [Fact]
    public async Task Ingest_SecondTimeSameBytes_DeduplicatesAndReinforces()
    {
        var ing = WireIngester(out var store);
        var bytes = FakeJpeg();
        var first = await ing.IngestAsync(MediaModality.Image, bytes, mimeType: "image/jpeg");
        var second = await ing.IngestAsync(MediaModality.Image, bytes, mimeType: "image/jpeg");

        Assert.False(first.WasDeduplicated);
        Assert.True(second.WasDeduplicated);
        Assert.Equal(1, await store.CountAsync());
        Assert.Equal(first.Entry.SourceSha256, second.Entry.SourceSha256);
        Assert.Equal(2, second.Entry.ReferenceCount);
    }

    [Fact]
    public async Task Ingest_DifferentBytes_ProducesDistinctEntries()
    {
        var ing = WireIngester(out var store);
        var a = FakeJpeg(50);
        var b = FakeJpeg(60); // different length → different bytes → different hash
        var ra = await ing.IngestAsync(MediaModality.Image, a);
        var rb = await ing.IngestAsync(MediaModality.Image, b);
        Assert.NotEqual(ra.Entry.SourceSha256, rb.Entry.SourceSha256);
        Assert.Equal(2, await store.CountAsync());
    }

    [Fact]
    public async Task Ingest_EmptyBytes_Throws()
    {
        var ing = WireIngester(out _);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ing.IngestAsync(MediaModality.Image, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task Ingest_RecordsSourceUriAndTags_WhenProvided()
    {
        var ing = WireIngester(out _);
        var bytes = FakePng();
        var tags = new Dictionary<string, string> { ["location"] = "home", ["person"] = "alex" };
        var r = await ing.IngestAsync(
            MediaModality.Image, bytes, mimeType: "image/png",
            sourceUri: "file:///photos/IMG_001.png", tags: tags);

        Assert.Equal("file:///photos/IMG_001.png", r.Entry.SourceUri);
        Assert.NotNull(r.Entry.Tags);
        Assert.Equal("home", r.Entry.Tags!["location"]);
        Assert.Equal("alex", r.Entry.Tags!["person"]);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Custom captioner is preferred over heuristic
    // ══════════════════════════════════════════════════════════════════════

    private sealed class FakeRichCaptioner : IMultimodalCaptioner
    {
        public bool CanCaption(MediaModality modality, string? mimeType) =>
            modality == MediaModality.Image;

        public Task<CaptionResult> CaptionAsync(
            MediaModality modality, ReadOnlyMemory<byte> sourceBytes, string? mimeType,
            CancellationToken ct = default) =>
            Task.FromResult(new CaptionResult(
                Caption: "A blue sky with two clouds.",
                Embedding: new float[] { 0.1f, 0.2f, 0.3f },
                WidthPx: 1920,
                HeightPx: 1080));
    }

    [Fact]
    public async Task Ingest_PrefersRichCaptioner_OverHeuristic()
    {
        var ing = WireIngester(out _, new FakeRichCaptioner());
        var bytes = FakeJpeg();
        var r = await ing.IngestAsync(MediaModality.Image, bytes, mimeType: "image/jpeg");

        Assert.Equal("A blue sky with two clouds.", r.Entry.Caption);
        Assert.NotNull(r.Entry.Embedding);
        Assert.Equal(1920, r.Entry.WidthPx);
        Assert.Equal(1080, r.Entry.HeightPx);
    }

    [Fact]
    public async Task Ingest_FallsBackToHeuristic_WhenRichCaptionerDeclines()
    {
        var ing = WireIngester(out _, new FakeRichCaptioner());
        var bytes = FakePng();
        // Audio modality — FakeRichCaptioner only handles Image
        var r = await ing.IngestAsync(MediaModality.Audio, bytes, mimeType: "audio/wav");

        Assert.Contains("no captioner wired", r.Entry.Caption);
        Assert.Null(r.Entry.Embedding);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Store: search, prune, recent
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Store_SearchByEmbedding_RanksByCosine()
    {
        var store = new InMemoryMultimodalMemoryStore();
        var nearMatch = new MultimodalMemoryEntry
        {
            SourceSha256 = "near", Caption = "near",
            Embedding = new float[] { 1f, 0.1f, 0.0f },
        };
        var farMatch = new MultimodalMemoryEntry
        {
            SourceSha256 = "far", Caption = "far",
            Embedding = new float[] { 0f, 0f, 1f },
        };
        await store.AddAsync(nearMatch);
        await store.AddAsync(farMatch);

        var ranked = await store.SearchAsync(new float[] { 1f, 0f, 0f }, topK: 2);
        Assert.Equal("near", ranked[0].SourceSha256);
        Assert.Equal("far", ranked[1].SourceSha256);
    }

    [Fact]
    public async Task Store_SearchWithNullQuery_ReturnsMostRecent()
    {
        var store = new InMemoryMultimodalMemoryStore();
        var older = new MultimodalMemoryEntry
        {
            SourceSha256 = "older", Caption = "older",
            RecordedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
        };
        var newer = new MultimodalMemoryEntry
        {
            SourceSha256 = "newer", Caption = "newer",
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(older);
        await store.AddAsync(newer);
        var recent = await store.SearchAsync(queryEmbedding: null, topK: 2);
        Assert.Equal("newer", recent[0].SourceSha256);
    }

    [Fact]
    public async Task Store_Prune_RemovesEntriesOlderThanCutoff()
    {
        var store = new InMemoryMultimodalMemoryStore();
        await store.AddAsync(new MultimodalMemoryEntry
        {
            SourceSha256 = "old", Caption = "old",
            RecordedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
        });
        await store.AddAsync(new MultimodalMemoryEntry
        {
            SourceSha256 = "new", Caption = "new",
            RecordedAtUtc = DateTimeOffset.UtcNow,
        });

        var removed = await store.PruneOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-5));
        Assert.Equal(1, removed);
        Assert.Equal(1, await store.CountAsync());
        Assert.NotNull(await store.GetByHashAsync("new"));
        Assert.Null(await store.GetByHashAsync("old"));
    }

    [Fact]
    public async Task Store_Reinforce_IncrementsReferenceCount()
    {
        var store = new InMemoryMultimodalMemoryStore();
        var entry = new MultimodalMemoryEntry { SourceSha256 = "x", Caption = "x" };
        await store.AddAsync(entry);
        await store.ReinforceAsync("x");
        await store.ReinforceAsync("x");

        var got = await store.GetByHashAsync("x");
        Assert.NotNull(got);
        Assert.Equal(3, got!.ReferenceCount); // initial 1 + 2 reinforce
    }

    [Fact]
    public async Task Store_AddWithoutHash_Throws()
    {
        var store = new InMemoryMultimodalMemoryStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync(new MultimodalMemoryEntry { Caption = "x" }));
    }
}
