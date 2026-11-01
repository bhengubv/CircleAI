// SearchByMeaningTests.cs
//
// OPEN-GAPS A7 and A12: an embedder with no caller, an index constructed nowhere, and a store
// that decoded every entry to score it. These pin the pieces that close them - the model's own
// SentencePiece file read in C#, XLM-R's ids, scoring on the codes, the meaning index that
// embeds as it is searched, rank fusion with BM25, and the row that carries a vector across
// the link.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Core.Models;
using CircleAI.Embeddings;
using CircleAI.Embeddings.Local;
using CircleAI.Inference;
using CircleAI.Linking;
using CircleAI.Search;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class SearchByMeaningTests
{
    // ── The model's own SentencePiece file ─────────────────────────────

    [Fact]
    public void A_sentencepiece_model_is_read_with_its_scores_and_its_unknown_piece()
    {
        var path = Temp(".model");
        try
        {
            File.WriteAllBytes(path, Proto(
                ("<unk>", 0f, 2), ("<s>", 0f, 3), ("</s>", 0f, 3),
                ("▁hello", -1f, null), ("▁he", -2f, null), ("llo", -2f, null), ("▁", -3f, null),
                ("h", -5f, null), ("e", -5f, null), ("l", -5f, null), ("o", -5f, null)));
            var pieces = SentencePieceUnigram.FromModelProto(path);

            // The best-scoring path, not the longest match: one piece at -1 beats two at -4.
            Assert.Equal([3], pieces.Encode("hello"));
            // A character no piece covers becomes the unknown piece, not nothing.
            Assert.Equal([3, 6, 0], pieces.Encode("hello ☃"));
            // A control piece is never matched in text.
            Assert.DoesNotContain(1, pieces.Encode("<s>"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ids_are_numbered_as_xlm_roberta_numbers_them()
    {
        // The fairseq offset of one, the unknown piece as 3, wrapped in <s> (0) and </s> (2).
        Assert.Equal([0L, 3L, 6L, 10L, 2L], OnnxTextEmbedder.Ids([0, 5, 9]));
        Assert.Equal(OnnxTextEmbedder.MaxTokens, OnnxTextEmbedder.Ids(Enumerable.Repeat(7, 2_000).ToList()).Length);
        Assert.Equal("a b", OnnxTextEmbedder.Squash("  a \t b\n"));
    }

    // ── Scoring on the codes (A12) ──────────────────────────────────────

    [Fact]
    public async Task The_store_finds_the_nearest_on_its_codes_without_decoding()
    {
        var rng = new Random(7);
        var vectors = Enumerable.Range(0, 200).ToDictionary(i => $"v{i}", _ => Unit(rng, 384));
        await using var store = new InMemoryEmbeddingStore(new Table(vectors));
        foreach (var (id, v) in vectors) await store.AddAsync(new EmbeddingDocument(id, id), v);

        var found = 0;
        for (var i = 0; i < 20; i++)
        {
            var near = vectors[$"v{i}"].Select(x => x + 0.02f * (float)(rng.NextDouble() - 0.5)).ToArray();
            var hits = await store.SearchAsync(near, 1);
            if (hits[0].Document.Id == $"v{i}") found++;
            Assert.InRange(hits[0].Score, 0.9f, 1.05f);
        }
        Assert.True(found >= 19, $"the nearest vector came first {found} times in 20");
    }

    [Fact]
    public async Task A_store_written_before_the_codes_were_scored_still_loads_and_searches()
    {
        // The version-1 file layout, entries in the version-1 payload, written by hand.
        var rng = new Random(3);
        var vectors = new Dictionary<string, float[]> { ["a"] = Unit(rng, 64), ["b"] = Unit(rng, 64) };
        var path = Temp(".celq");
        try
        {
            using (var bw = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                bw.Write(0x4C455143); bw.Write((ushort)1); bw.Write((ushort)4); bw.Write(64); bw.Write(2);
                foreach (var (id, v) in vectors)
                {
                    var payload = TurboQuantCodec.Encode(v, 4);
                    Assert.Equal(1, payload.Version);
                    bw.Write(id); bw.Write(id); bw.Write(0);
                    bw.Write(payload.Norm); bw.Write(payload.PackedIndices.Length); bw.Write(payload.PackedIndices);
                }
            }

            await using var store = new InMemoryEmbeddingStore(new Table(vectors));
            await store.LoadAsync(path);
            Assert.Equal("b", (await store.SearchAsync(vectors["b"], 1))[0].Document.Id);

            // Saved again, it is a version-2 file - and reads back the same.
            await store.SaveAsync(path);
            await using var again = new InMemoryEmbeddingStore(new Table(vectors));
            await again.LoadAsync(path);
            Assert.Equal("a", (await again.SearchAsync(vectors["a"], 1))[0].Document.Id);
        }
        finally { File.Delete(path); }
    }

    // ── The meaning index ───────────────────────────────────────────────

    [Fact]
    public async Task Lines_are_embedded_once_kept_on_disk_and_again_only_when_they_change()
    {
        var path = Temp(".celq");
        try
        {
            var encoder = new Topics();
            await using (var index = new MeaningIndex(encoder, path))
            {
                Assert.Equal(0, await index.IndexAsync([("t#0", "the car would not start"), ("t#1", "we ate lunch by the river")]));
                Assert.Equal(2, encoder.Calls);
                Assert.Equal(0, await index.IndexAsync([("t#0", "the car would not start"), ("t#1", "we ate lunch by the river")]));
                Assert.Equal(2, encoder.Calls);          // nothing new, nothing embedded

                // An edited line is embedded again; a line no longer kept is let go.
                Assert.Equal(0, await index.IndexAsync([("t#0", "the car started at last")]));
                Assert.Equal(3, encoder.Calls);
                Assert.Equal(["t#0"], await index.SearchAsync("lunch", 5));
            }

            // Another run reads them back from disk instead of embedding them again.
            var fresh = new Topics();
            await using var reopened = new MeaningIndex(fresh, path);
            Assert.Equal(0, await reopened.IndexAsync([("t#0", "the car started at last")]));
            Assert.Equal(0, fresh.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_search_spends_its_budget_and_leaves_the_rest_for_the_next()
    {
        await using var index = new MeaningIndex(new Topics()) { Budget = TimeSpan.Zero };
        Assert.Equal(2, await index.IndexAsync([("a", "car"), ("b", "lunch")]));
    }

    // ── Searching transcripts ───────────────────────────────────────────

    [Fact]
    public async Task A_line_with_no_word_in_common_is_found_by_meaning()
    {
        var kept = new Kept(
            Saved("car", "Morning", "the car would not start this morning"),
            Saved("food", "Noon", "we ate lunch by the river"));

        // No word of the query is in either line, so words alone find nothing.
        Assert.Empty(await TranscriptSearch.SearchAsync(kept, "vehicle trouble", 5));

        await using var meaning = new MeaningIndex(new Topics());
        var found = await TranscriptSearch.SearchAsync(kept, "vehicle trouble", 5, meaning);
        Assert.Equal("car", found[0].Id);
        Assert.Equal("the car would not start this morning", found[0].Text);
    }

    [Fact]
    public async Task A_meaning_leg_that_fails_leaves_the_words()
    {
        var kept = new Kept(Saved("food", "Noon", "we ate lunch by the river"));
        await using var broken = new MeaningIndex(new Broken());
        var found = await TranscriptSearch.SearchAsync(kept, "lunch", 5, broken);
        Assert.Equal("food", Assert.Single(found).Id);
    }

    [Fact]
    public void Rank_fusion_lifts_what_both_lists_found_and_keeps_what_one_did()
    {
        var fused = TranscriptSearch.Fuse(["a", "b", "c"], ["c", "d"], 4);
        Assert.Equal("c", fused[0]);                       // third in one list, first in the other
        // b and d tie, second in their lists: the first list's wins.
        Assert.Equal(["c", "a", "b", "d"], fused);
        Assert.Equal(["a", "b"], TranscriptSearch.Fuse(["a", "b"], [], 5));
    }

    // ── Across the link ─────────────────────────────────────────────────

    [Fact]
    public void A_vector_crosses_the_link_intact_or_not_at_all()
    {
        var v = new[] { 0.25f, -1.5f, 3e-7f, float.Epsilon };
        Assert.Equal(v, LinkEmbedRows.Vector(LinkEmbedRows.Row(v)));
        Assert.Equal(LinkScope.Chat, LinkVerbs.RequiredScope(LinkVerb.Embed));

        var row = LinkEmbedRows.Row(v);
        Assert.Throws<FormatException>(() => LinkEmbedRows.Vector(["5", row[1]]));
    }

    // ── The catalogue and setup ─────────────────────────────────────────

    [Fact]
    public void The_embedding_model_is_one_whose_data_is_free_and_it_waits_for_room()
    {
        using var registry = new ModelRegistryService();
        var row = Assert.Single(registry.AllModels, m => m.Modality == ModelModality.Embedding);
        Assert.Equal("Granite-Embedding-107M-Multilingual", row.Name);
        Assert.Equal("Apache-2.0", row.License);
        Assert.Contains(row.BundleFiles!, f => f.Name.EndsWith(".onnx", StringComparison.Ordinal));
        Assert.Contains(row.BundleFiles!, f => f.Name.EndsWith(".model", StringComparison.Ordinal));

        // Only with room to spare: 2 GB free is not enough, 20 GB is.
        Assert.False(ModelFit.FitsStorage(row, storageFreeGb: 2.0));
        Assert.True(ModelFit.FitsStorage(row, storageFreeGb: 20.0));

        Assert.Contains(FirstRun.WantedFor(speech: false), w => w.Modality == ModelModality.Embedding && w.Title == "search by meaning");
    }

    [Fact]
    public void With_nothing_installed_there_is_no_meaning_model_and_nothing_is_fetched()
    {
        var store = Path.Combine(Path.GetTempPath(), "circleai-meaning-" + Guid.NewGuid().ToString("N")[..8]);
        try { Assert.Null(MeaningModel.Installed(store)); }
        finally { if (Directory.Exists(store)) Directory.Delete(store, recursive: true); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static float[] Unit(Random rng, int dim)
    {
        var v = Enumerable.Range(0, dim).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var n = (float)Math.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / n)];
    }

    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), "circleai-meaning-" + Guid.NewGuid().ToString("N")[..8] + extension);

    private static SavedTranscript Saved(string id, string title, string line) =>
        new(id, title, DateTimeOffset.UnixEpoch,
            new Transcript(line, [new TranscriptLine(line, TimeSpan.Zero, TimeSpan.FromSeconds(2))]));

    /// <summary>A SentencePiece ModelProto: field 1 a piece each (piece, score, type), field 2 an ignored trainer spec.</summary>
    private static byte[] Proto(params (string Piece, float Score, int? Type)[] pieces)
    {
        var model = new List<byte>();
        foreach (var (piece, score, type) in pieces)
        {
            var body = new List<byte>();
            var text = Encoding.UTF8.GetBytes(piece);
            body.Add(0x0A); Varint(body, text.Length); body.AddRange(text);
            body.Add(0x15); body.AddRange(BitConverter.GetBytes(score));
            if (type is { } t) { body.Add(0x18); Varint(body, t); }
            model.Add(0x0A); Varint(model, body.Count); model.AddRange(body);
        }
        model.Add(0x12); Varint(model, 3); model.AddRange(new byte[] { 0x08, 0x01, 0x10 });   // skipped
        return [.. model];
    }

    private static void Varint(List<byte> to, int value)
    {
        var v = (uint)value;
        while (v >= 0x80) { to.Add((byte)(v | 0x80)); v >>= 7; }
        to.Add((byte)v);
    }

    /// <summary>A fixed vector per text.</summary>
    private sealed class Table(IReadOnlyDictionary<string, float[]> vectors) : IEmbeddingEncoder
    {
        public int Dimension => vectors.Values.First().Length;
        public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default) => new(vectors[text]);
    }

    /// <summary>Two topics - getting about, and eating - so meaning can be told from shared words.</summary>
    private sealed class Topics : IEmbeddingEncoder
    {
        private static readonly string[] Moving = ["car", "vehicle", "start", "started", "trouble", "drive"];
        private static readonly string[] Eating = ["lunch", "ate", "river", "food", "meal"];

        public int Calls { get; private set; }

        // Half the coordinates for each topic: wide enough for the codec's rotation to spread.
        public int Dimension => 64;

        public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var moving = words.Count(Moving.Contains);
            var eating = words.Count(Eating.Contains);
            var v = Enumerable.Range(0, Dimension).Select(i => (i < 32 ? moving : eating) + 0.01f).ToArray();
            var n = (float)Math.Sqrt(v.Sum(x => x * x));
            return new(v.Select(x => x / n).ToArray());
        }
    }

    private sealed class Broken : IEmbeddingEncoder
    {
        public int Dimension => throw new InvalidOperationException("no embedding model on this phone");
        public ValueTask<float[]> EncodeAsync(string text, CancellationToken ct = default)
            => throw new InvalidOperationException("no embedding model on this phone");
    }

    private sealed class Kept(params SavedTranscript[] saved) : IKeepsTranscripts
    {
        public Task<string> KeepAsync(string title, Transcript transcript, CancellationToken ct = default) => Task.FromResult("");
        public Task<IReadOnlyList<SavedTranscript>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SavedTranscript>>(saved);
        public Task<SavedTranscript?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(saved.FirstOrDefault(s => s.Id == id));
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
