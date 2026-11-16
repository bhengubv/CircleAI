// DownloadSplitStoreIndexReviseTests.cs
//
// DOWNLOAD, SPLIT, STORE, INDEX, REVISE (2026-11-12): the indexed mind's five steps, each on its own and then as one flow,
// on a small model. Written from the design's words for each step (CircleOS docs/INDEXED_MIND_DESIGN.md, "The idea") and
// the owner's of 2026-11-12, not from the engine's code. Each test quotes the words it checks and measures them with what
// the engine reports: the requests a download made, what verify found different, where a store's pieces lie on flash, the
// bytes a word read and where from, the pieces RAM served.
//
// THE FLOW'S TEST RUNS THE STEPS IN ORDER BY HAND, each from what the step before left on flash: it shows the steps fit
// end to end. The engine's own job for Store and Index - a queue of units worked through whenever the phone is idle - and
// the two lines the person is told - "Until indexing is complete AI is functioning at xx%." and "The more you use it the
// more it learns what the user needs" - are measured in IndexingQueueTests.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Rotation;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class DownloadSplitStoreIndexReviseTests
{
    private const int Layers = 4;
    private const int Ff = SyntheticQwen35.FeedForward; // 384 neurons a layer
    private const int Quarter = 96;                       // a quarter of them, six 16-neuron tiles

    // The words Watch reads, and the words the tests then ask about.
    private static readonly int[] Watched = [3, 8, 12, 20, 6, 11, 14, 27, 5, 9, 30, 2, 17, 25, 4, 22, 13, 7, 29, 16, 10, 31, 19, 26];
    private static readonly int[] Asked = [5, 9, 30, 2, 17, 21, 8, 12, 24, 15];

    // ---- 1. Download ----

    // DESIGN: "Download. The model arrives as a normal file." Through the engine's own download service, from a source that
    // drops the connection part way: what had arrived is kept, the next attempt carries on from it, and the file on flash is
    // the file that was published - a model the engine opens like any other.
    [Fact]
    public async Task Download_the_model_arrives_as_a_normal_file_and_one_that_stopped_carries_on()
    {
        var directory = Directory.CreateTempSubdirectory("five-steps-download-").FullName;
        try
        {
            var published = SyntheticQwen35.Build(GgmlType.PTQ1_0, 301, layers: Layers);
            var source = new Source(published) { DropsAfter = published.Length / 3 };
            using var http = new HttpClient(source);
            using var downloads = new ModelDownloadService(directory, http);
            var from = new Uri("https://example.invalid/the-model.gguf");

            string path;
            try
            {
                path = await downloads.EnsureModelAsync("the-model", from, Hash(published), null, default);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                // The connection went part way through. Asked again, it carries on.
                path = await downloads.EnsureModelAsync("the-model", from, Hash(published), null, default);
            }

            Assert.True(source.Dropped, "setup: the connection never dropped");
            var resumedAt = source.Asked[^1] ?? 0;
            Assert.True(resumedAt > 0 && resumedAt <= source.DropsAfter,
                $"after the connection dropped at byte {source.DropsAfter} the download asked again from byte {resumedAt}: it did not carry on from what had arrived");
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(published), "the file on flash is not the file that was published");
            using var file = GgufFile.Open(path);
            Assert.Equal(Layers, ModelConfig.FromGguf(file).Layers);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 2. Split ----

    // DESIGN: "Split. It is cut into pieces by layer and neuron group"; "each layout is proven byte-exact by verify". Every
    // layer's mixing and feed-forward, and the head, cut into the kernel's tiles on flash, and verify finds nothing different.
    [Fact]
    public void Split_cuts_the_file_into_the_kernels_pieces_layer_by_layer_and_verify_finds_nothing_different()
    {
        WithModel(303, (file, directory) =>
        {
            var mix = MixStore.Write(file, Path.Combine(directory, "model.mix"), channels: 2, readBytes: FlashFile.Page);
            var ffn = FfnStore.Write(file, Path.Combine(directory, "model.ffn"), channels: 2, readBytes: FlashFile.Page);
            var head = HeadStore.Write(file, Path.Combine(directory, "model.head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired);

            Assert.Equal(0, mix.Verify(file, channels: 2, readBytes: FlashFile.Page));
            Assert.Equal(0, ffn.Verify(file, channels: 2, readBytes: FlashFile.Page));
            Assert.Equal(0, head.Verify(file));
            Assert.Equal(Layers, mix.Layers);
            for (var il = 0; il < Layers; il++)
            {
                Assert.Equal(3, ffn.LayerEntries(il).Count());      // gate, up and down
                Assert.True(mix.LayerEntries(il).Any(), $"layer {il} has no mixing in the split");
            }
        });
    }

    // DESIGN: "Where a format folds a rotation into a tensor (Ternary-Bonsai-2's ffn_down), Split undoes it so each piece
    // holds true neurons." A piece's ffn_down row for a neuron is that neuron's own: fed that one neuron alone, the piece
    // answers the way the model's ffn_down does (ASSUMED: a cosine of at least 0.5; one neuron among the 128 a rotation
    // mixes scores about 0.09). Held of a plain file, and of one that folds a Hadamard into ffn_down's input as the real
    // files do.
    //
    // MEASURED AT THE WEIGHTS, NOT THE TOP WORD. The first form of this test asked for the model's own word nine times in
    // ten, and passed with the rotation ignored (2026-11-12): on this small model the knowledge layers move the top word too
    // little for it to see a wrong ffn_down. The failure message carries that count.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Split_a_pieces_ffn_down_row_is_the_neurons_own_whether_or_not_the_file_folds_a_rotation_in(bool folds)
    {
        WithModel(305, (file, directory) =>
        {
            var rotation = PrismHadamard.FromGguf(file)?.InputOf("blk.0.ffn_down.weight");
            Assert.True((rotation is not null) == folds, "setup: the model's fold is not the one asked for");
            var core = Core(file, directory, groups: 1, list: Ff);
            var coreStore = CachedIndex.Build(core, file, Path.Combine(directory, "core.store"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            using var serving = new IndexedFeedForward(core, coreStore, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            var piece = serving.Piece(0, Thought(new Random(305)));
            Assert.Equal(Ff, piece.Neurons.Length);

            var stored = Decode(file, "blk.0.ffn_down.weight"); // a row an output, a column an input, as the file holds them
            var alone = new float[Ff];
            var turned = new float[Ff];
            var fromPiece = new float[SyntheticQwen35.Embedding];
            var worst = 1.0;
            var worstNeuron = -1;
            foreach (var neuron in new[] { 0, 37, 127, 128, 200, 255, 256, 300, 383 })
            {
                Array.Clear(alone);
                alone[neuron] = 1;
                if (rotation is null) alone.CopyTo(turned, 0);
                else rotation.Apply(alone, turned);
                // The model's ffn_down for this neuron alone: its rows over the input as the model turns it.
                var own = new double[stored.Length];
                for (var row = 0; row < stored.Length; row++)
                {
                    double sum = 0;
                    for (var j = 0; j < Ff; j++) sum += stored[row][j] * turned[j];
                    own[row] = sum;
                }
                piece.Down.Project(alone, piece.Neurons, fromPiece);
                var cosine = Cosine(own, fromPiece);
                if (cosine >= worst) continue;
                worst = cosine;
                worstNeuron = neuron;
            }

            var model = Logits(Full(file), Asked);
            var pieces = Logits(FromTheIndex(file, serving), Asked);
            var same = Enumerable.Range(0, Asked.Length).Count(w => Top(model[w]) == Top(pieces[w]));
            Assert.True(worst >= 0.5, string.Create(CultureInfo.InvariantCulture,
                $"neuron {worstNeuron}'s row in the piece agrees {worst:F2} with that neuron's own ffn_down (1 is the same, 0 unrelated): the piece holds the file's columns, not true neurons - and the pieces still gave the model's top word {same} times in {Asked.Length}, so the top word cannot see it here"));
        }, folded: folds ? ["ffn_down"] : null);
    }

    // ---- 3. Store ----

    // DESIGN: "Store. The pieces go on the SSD, with those that fire together side by side." And of the core: "complete and
    // lasting". Every group Watch saw words fall in has its piece; a piece is one span on flash holding the group's neurons -
    // the ones that fired for the same thoughts - the pieces lie end to end, and one read brings a group's.
    [Fact]
    public void Store_puts_every_groups_neurons_side_by_side_in_one_piece_on_flash()
    {
        WithModel(307, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: 32);
            var path = Path.Combine(directory, "core.store");
            var store = CachedIndex.Build(core, file, path, budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);

            var seen = Enumerable.Range(0, Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                .Where(p => core.Size(p.Layer, p.Group) > 0).ToList();
            var pieces = Pieces(store, core).OrderBy(p => p.Offset).ToList();
            Assert.True(pieces.Count == seen.Count, $"the store holds {pieces.Count} pieces for the {seen.Count} groups Watch saw words fall in");
            for (var i = 0; i < pieces.Count; i++)
            {
                Assert.Equal(store.PieceStride, pieces[i].Bytes);
                if (i > 0) Assert.Equal(pieces[i - 1].Offset + pieces[i - 1].Bytes, pieces[i].Offset);
            }
            Assert.Equal(pieces[^1].Offset + pieces[^1].Bytes, new FileInfo(path).Length);

            using var serving = new IndexedFeedForward(core, store, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            var sketch = new ThoughtSketch(SyntheticQwen35.Embedding);
            var sketched = new float[ThoughtSketch.Width];
            var rng = new Random(307);
            for (var round = 0; round < 3; round++)
            {
                for (var layer = 0; layer < Layers; layer++)
                {
                    var thought = Thought(rng);
                    sketch.Sketch(thought, sketched);
                    var group = core.Nearest(layer, sketched);
                    var piece = serving.Piece(layer, thought);
                    var counts = serving.TakeCounts();
                    var (reads, bytes) = (counts.Asked, counts.AskedBytes);
                    Assert.Equal(core.List(layer, group).ToArray(), piece.Neurons);
                    Assert.True(reads == 1 && bytes == store.PieceStride, $"layer {layer}'s group {group} took {reads} reads of {bytes} bytes; its piece is one span of {store.PieceStride}");
                }
            }
        });
    }

    // ---- 4. Index ----

    // DESIGN: "Index (the core). A map of what relates to what, and each word fetches only its pieces." THE OWNER: "RAM IS THE
    // TURBO NOT THE ENGINE". As the engine serves a model too big for RAM - every layer read from flash, no layer's weights
    // held - a word's knowledge layers come as its pieces, one a layer, and the layers' stream brings the mixing alone: no
    // whole ffn_gate, ffn_up or ffn_down is read for a word.
    [Fact]
    public void Index_with_every_layer_read_from_flash_a_word_fetches_its_pieces_and_no_whole_knowledge_tensor()
    {
        WithModel(309, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var coreStore = CachedIndex.Build(core, file, Path.Combine(directory, "core.store"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var mix = MixStore.Write(file, Path.Combine(directory, "model.mix"), channels: 2, readBytes: FlashFile.Page);

            // The layers' stream is given the mixing alone: the index serves the knowledge layers.
            var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            using var serving = new IndexedFeedForward(core, coreStore, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            using var stream = new LayerStream(mix, null, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            model.Stream = stream;
            model.Index = serving;
            var state = model.NewState(32);
            long pieces = 0, pieceBytes = 0;
            foreach (var token in Asked)
            {
                model.Forward(token, state);
                var counts = serving.TakeCounts();
                pieces += counts.Warm + counts.Ahead + counts.Asked + counts.Again;
                pieceBytes += counts.Bytes;
            }
            var (_, streamed, _) = stream.TakeCounts();
            model.Index = null;
            model.Stream = null;

            Assert.Equal((long)Asked.Length * Layers, pieces);
            Assert.Equal(pieces * coreStore.PieceStride, pieceBytes);
            var beyondMixing = streamed - Asked.Length * mix.DataBytes;
            var readAhead = stream.Slots * stream.SlotBytes;
            Assert.True(beyondMixing <= readAhead,
                $"over {Asked.Length} words the layers' stream read {beyondMixing} bytes beyond the mixing - ffn_down, whole, every word - on top of the {pieceBytes} bytes of pieces the index fetched; its read-ahead alone is at most {readAhead}");
        });
    }

    // ---- 5. Revise ----

    // DESIGN: "Revise (nightly) ... the index is brought up to date each night". THE OWNER, 2026-11-12: "The more you use it
    // the more it learns what the user needs" - what to keep in RAM, step 4 (2026-11-15: "Select coherent portions from 3
    // into RAM for 4) warm index"). A day spent on one question whose pieces the morning's step 4 - chosen by what Watch
    // saw - did not wholly hold; the night revises step 2's heat from that use, every piece staying where it lies; the next
    // morning, with RAM for the day's groups and no more, the question's first word finds all its pieces in step 4.
    [Fact]
    public void Revise_after_a_day_of_use_the_next_morning_RAM_holds_what_the_day_used()
    {
        WithModel(311, (file, directory) =>
        {
            var core = Core(file, directory, groups: 4, list: Quarter);
            var coreStore = CachedIndex.Build(core, file, Path.Combine(directory, "core.store"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);

            // The day: one word asked twelve times, each a new conversation - a word the morning's step 4 did not wholly hold.
            var word = -1;
            long[] dayUse = [];
            List<(int Layer, int Group)> dayGroups = [];
            for (var candidate = 2; candidate < SyntheticQwen35.Vocabulary && word < 0; candidate++)
            {
                using var day = new IndexedFeedForward(core, coreStore, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
                var asked = FromTheIndex(file, day);
                for (var time = 0; time < 12; time++) asked.Forward(candidate, asked.NewState(8));
                var use = day.TakeUsage();
                var groups = Enumerable.Range(0, Layers).SelectMany(il => Enumerable.Range(0, core.Groups).Select(g => (Layer: il, Group: g)))
                    .Where(p => use[p.Layer * core.Groups + p.Group] > 0).ToList();
                using var morning = new IndexedFeedForward(core, coreStore, file, warmBudget: groups.Count * coreStore.PieceStride, channels: 2, readBytes: FlashFile.Page);
                if (groups.All(g => morning.HoldsWarm(g.Layer, g.Group))) continue;
                (word, dayUse, dayGroups) = (candidate, use, groups);
            }
            Assert.True(word >= 0, "setup: the morning's step 4 already holds the groups of every word tried");

            // The night: Revise - step 2's heat written from the day's use; its pieces stay where they lie.
            var offsets = Pieces(coreStore, core).Select(p => p.Offset).ToList();
            var night = CachedIndex.Revise(coreStore, dayUse);
            Assert.Equal(offsets, Pieces(night, core).Select(p => p.Offset).ToList());
            Assert.True(Pieces(night, core).All(p => night.Heat(p.Layer, p.Group) == dayUse[p.Layer * core.Groups + p.Group]), "the night's heat is not the day's use");
            Assert.True(dayGroups.All(g => CachedIndex.Open(coreStore.FilePath, file, core)!.Heat(g.Layer, g.Group) > 0), "the night's heat did not reach flash");

            // The next morning: RAM for the day's groups and no more. The word again, a new conversation.
            using var next = new IndexedFeedForward(core, CachedIndex.Open(coreStore.FilePath, file, core), file, warmBudget: dayGroups.Count * night.PieceStride, channels: 2, readBytes: FlashFile.Page);
            var again = FromTheIndex(file, next);
            again.Forward(word, again.NewState(8));
            var counts = next.TakeCounts();
            Assert.True(counts.Warm == Layers && counts.Bytes == 0,
                $"the morning after a day of word {word}, it found {counts.Warm} of its {Layers} pieces in step 4 and read {counts.Bytes} bytes from flash: step 4 holds the groups Watch saw most, not the ones the day used");
        });
    }

    // ---- the five as one flow ----

    // PROMPT: "Do this as one continuous flow". DESIGN: "The first four steps happen once per model ... The fifth happens every
    // night." Download, Split, Store, Index and Revise in order, each working from what the step before left on flash and
    // nothing else; the model then answers with no layer and no head held in RAM as a copy, and its words are the model's own
    // (ASSUMED: nine in ten). Asked for again, every step finds its work on flash: nothing is fetched and nothing written.
    [Fact]
    public async Task The_five_steps_run_in_order_each_from_what_the_step_before_left_on_flash()
    {
        var directory = Directory.CreateTempSubdirectory("five-steps-flow-").FullName;
        try
        {
            var published = SyntheticQwen35.Build(GgmlType.PTQ1_0, 313, layers: Layers);
            var source = new Source(published);
            using var http = new HttpClient(source);
            using var downloads = new ModelDownloadService(Path.Combine(directory, "models"), http);
            var from = new Uri("https://example.invalid/the-model.gguf");
            var stores = Directory.CreateDirectory(Path.Combine(directory, "stores")).FullName;
            string At(string kind) => Path.Combine(stores, "the-model." + kind);

            // 1. DOWNLOAD.
            var modelPath = await downloads.EnsureModelAsync("the-model", from, Hash(published), null, default);

            // What the flow's answers are held to: the model's own next words, every weight in RAM.
            int[] own;
            using (var file = GgufFile.Open(modelPath)) own = [.. Logits(Full(file), Asked).Select(Top)];

            // 2. SPLIT: the file that arrived, cut into the kernel's pieces, proven byte-exact.
            using (var file = GgufFile.Open(modelPath))
            {
                Assert.Equal(0, MixStore.Write(file, At("mix"), channels: 2, readBytes: FlashFile.Page).Verify(file, channels: 2, readBytes: FlashFile.Page));
                Assert.Equal(0, FfnStore.Write(file, At("ffn"), channels: 2, readBytes: FlashFile.Page).Verify(file, channels: 2, readBytes: FlashFile.Page));
                Assert.Equal(0, HeadStore.Write(file, At("head"), ScriptOf, chunkRows: 16, layout: HeadLayout.Paired).Verify(file));
            }

            // 3 AND 4. STORE AND INDEX: Watch runs on the model read from the split's stores, then the core index and its
            // pieces go to flash.
            using (var file = GgufFile.Open(modelPath))
            {
                var mix = MixStore.Open(file, At("mix"));
                var ffn = FfnStore.Open(file, At("ffn"));
                Assert.True(mix is not null && ffn is not null, "the split's stores do not open as this model's");
                var streamed = LanguageModel.Load(file, null, headFromStore: true, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
                using (var stream = new LayerStream(mix!, ffn!, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2))
                using (var watch = new ThoughtWatch(At("watch"), Layers, SyntheticQwen35.Embedding, Ff, kept: Ff))
                {
                    streamed.Stream = stream;
                    streamed.Watch = watch;
                    streamed.ForwardBatch(Watched, streamed.NewState(64), new float[Watched.Length][]);
                    streamed.Watch = null;
                    streamed.Stream = null;
                }
                var core = ThoughtIndex.Build(ThoughtWatch.Read(At("watch")), At("index"), groups: 2, listNeurons: Ff);
                CachedIndex.Build(core, file, At("core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            }

            // THE ANSWER, from flash.
            int[] first;
            long[] use;
            using (var file = GgufFile.Open(modelPath))
            {
                first = Answer(file, At, out use);
                var same = first.Zip(own).Count(p => p.First == p.Second);
                Assert.True(same * 10 >= Asked.Length * 9, $"read back from flash, the flow gave the model's own word {same} times in {Asked.Length}");
            }

            // 5. REVISE: the pieces' heat brought up to date from that use - step 4, with room for half of them, then warms
            // ones used at least as much as the ones it leaves on flash.
            using (var file = GgufFile.Open(modelPath))
            {
                var core = ThoughtIndex.Open(At("index"));
                Assert.NotNull(core);
                var coreStore = CachedIndex.Open(At("core"), file, core);
                Assert.NotNull(coreStore);
                var all = Pieces(coreStore, core).ToList();
                var night = CachedIndex.Revise(coreStore, use);
                using var warm = new IndexedFeedForward(core, night, file, warmBudget: all.Count / 2 * night.PieceStride, channels: 2, readBytes: FlashFile.Page);
                var kept = all.Where(p => warm.HoldsWarm(p.Layer, p.Group)).Select(p => use[p.Layer * core.Groups + p.Group]).ToList();
                var left = all.Where(p => !warm.HoldsWarm(p.Layer, p.Group)).Select(p => use[p.Layer * core.Groups + p.Group]).ToList();
                Assert.True(kept.Count > 0 && left.Count > 0, $"setup: step 4 holds {kept.Count} of step 2's {all.Count} pieces, so there is no portion to choose");
                Assert.True(kept.Min() >= left.Max(), $"after the answers the night's step 4 keeps a group used {kept.Min()} times and leaves out one used {left.Max()} times");
            }

            // ONCE A MODEL: asked for again, nothing is fetched, every step's work opens from flash, the answers are the same
            // and no file is written.
            var before = Stamps(directory);
            var requests = source.Requests;
            Assert.Equal(modelPath, await downloads.EnsureModelAsync("the-model", from, Hash(published), null, default));
            Assert.Equal(requests, source.Requests);
            using (var file = GgufFile.Open(modelPath))
            {
                Assert.NotNull(FfnStore.Open(file, At("ffn")));
                Assert.Equal(first, Answer(file, At, out _));
            }
            Assert.Equal(before, Stamps(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The model answering from what the steps left on flash: the mixing through the layers' stream, the knowledge layers
    // through the index (step 4 in RAM, step 2's pieces), the head from its store. The words it gave, and what it used.
    private static int[] Answer(GgufFile file, Func<string, string> at, out long[] use)
    {
        var mix = MixStore.Open(file, at("mix"));
        var heads = HeadStore.Open(file, at("head"), HeadLayout.Paired);
        var core = ThoughtIndex.Open(at("index"));
        Assert.True(mix is not null && heads is not null && core is not null, "a step's work on flash does not open as this model's");
        var coreStore = CachedIndex.Open(at("core"), file, core!);
        Assert.True(coreStore is not null, "the index's pieces on flash do not open as this model's");

        var model = LanguageModel.Load(file, null, headFromStore: true, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
        using var stream = new LayerStream(mix!, null, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
        using var serving = new IndexedFeedForward(core!, coreStore, file, warmBudget: coreStore!.PieceStride, channels: 2, readBytes: FlashFile.Page);
        using var head = heads!.Load(heads.Surface([Script.Latin]), channels: 2);
        model.Stream = stream;
        model.Index = serving;
        model.Head = head;
        var state = model.NewState(32);
        var logits = new float[SyntheticQwen35.Vocabulary];
        var tops = new int[Asked.Length];
        for (var i = 0; i < Asked.Length; i++)
        {
            model.Forward(Asked[i], state, logits);
            tops[i] = Top(logits);
        }
        model.Index = null;
        model.Stream = null;
        model.Head = null;
        use = serving.TakeUsage();
        return tops;
    }

    // Every file under a directory: its name, its length and when it was last written.
    private static List<(string File, long Bytes, DateTime Written)> Stamps(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetRelativePath(directory, p), new FileInfo(p).Length, File.GetLastWriteTimeUtc(p))).ToList();

    // Every token in a Latin speaker's surface, in three scripts: the head store's rows are the whole vocabulary.
    private static Script ScriptOf(int token) => token < 2 ? Script.Special : token % 2 == 0 ? Script.Latin : Script.Neutral;

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // The model with every weight in RAM: the full read the others are measured against.
    private static LanguageModel Full(GgufFile file) => LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);

    // The model with its knowledge layers from the index alone: no copy of ffn_gate, ffn_up or ffn_down in RAM, and no stream.
    private static LanguageModel FromTheIndex(GgufFile file, IndexedFeedForward serving)
    {
        var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
        model.Index = serving;
        return model;
    }

    // Watch over the words, each record keeping its strongest neurons; then the core index from what it saw.
    private static ThoughtIndex Core(GgufFile file, string directory, int groups, int list)
    {
        var model = Full(file);
        var path = Path.Combine(directory, "model.watch");
        using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, Ff, kept: list))
        {
            model.Watch = watch;
            model.ForwardBatch(Watched, model.NewState(64), new float[Watched.Length][]);
            model.Watch = null;
        }
        return ThoughtIndex.Build(ThoughtWatch.Read(path), Path.Combine(directory, "model.index"), groups, list);
    }

    private static List<float[]> Logits(LanguageModel model, int[] words)
    {
        var state = model.NewState(32);
        var all = new List<float[]>();
        foreach (var token in words)
        {
            var logits = new float[SyntheticQwen35.Vocabulary];
            model.Forward(token, state, logits);
            all.Add(logits);
        }
        return all;
    }

    private static IEnumerable<(int Layer, int Group, long Offset, long Bytes)> Pieces(CachedIndex store, ThoughtIndex core)
    {
        for (var layer = 0; layer < core.Layers; layer++)
        {
            for (var group = 0; group < core.Groups; group++)
            {
                if (store.Entry(layer, group) is { } entry) yield return (layer, group, entry.Offset, entry.Bytes);
            }
        }
    }

    private static int Top(float[] logits)
    {
        var best = 0;
        for (var i = 1; i < logits.Length; i++)
        {
            if (logits[i] > logits[best]) best = i;
        }
        return best;
    }

    private static float[] Thought(Random rng)
    {
        var thought = new float[SyntheticQwen35.Embedding];
        for (var i = 0; i < thought.Length; i++) thought[i] = (float)(rng.NextDouble() * 2 - 1);
        return thought;
    }

    // A PTQ1_0 tensor's weights as the file holds them, decoded block by block: one array a row.
    private static float[][] Decode(GgufFile file, string name)
    {
        var tensor = file.Tensor(name) ?? throw new InvalidDataException($"The model has no {name}.");
        var columns = (int)tensor.RowLength;
        var rows = (int)tensor.Rows;
        var blocks = columns / Ptq1.BlockWeights;
        var bytes = file.Bytes(tensor);
        var weights = new float[rows][];
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var r = 0; r < rows; r++)
        {
            weights[r] = new float[columns];
            for (var b = 0; b < blocks; b++)
            {
                var block = bytes.Slice((r * blocks + b) * Ptq1.BlockBytes, Ptq1.BlockBytes);
                var scale = Ptq1.Scale(block);
                Ptq1.DecodeScalar(block, trits);
                for (var j = 0; j < Ptq1.BlockWeights; j++) weights[r][b * Ptq1.BlockWeights + j] = trits[j] * scale;
            }
        }
        return weights;
    }

    // How alike two directions are: 1 the same, 0 unrelated.
    private static double Cosine(double[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += (double)b[i] * b[i];
        }
        return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb);
    }

    private static void WithModel(int seed, Action<GgufFile, string> use, IReadOnlyList<string>? folded = null)
    {
        var directory = Directory.CreateTempSubdirectory("five-steps-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed, layers: Layers, folded: folded), path =>
            {
                using var file = GgufFile.Open(path);
                use(file, directory);
                return 0;
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Where the model is published: serves the file whole or from a byte onward, as a CDN does, and can drop one
    // connection part way through the body.
    private sealed class Source(byte[] published) : HttpMessageHandler
    {
        private int _dropped;
        private int _requests;

        /// <summary>The first request for the file's body is cut after this many bytes; never when negative.</summary>
        public int DropsAfter { get; init; } = -1;

        /// <summary>Where each request for the body asked to start: null for the whole file. The one-byte probe is not among them.</summary>
        public List<long?> Asked { get; } = [];

        /// <summary>Whether a connection was dropped.</summary>
        public bool Dropped => Volatile.Read(ref _dropped) != 0;

        /// <summary>Every request made, the probe too.</summary>
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = range?.From ?? 0;
            var to = Math.Min(range?.To ?? published.Length - 1, published.Length - 1);
            var probe = range is { From: 0, To: 0 };
            if (!probe)
            {
                lock (Asked) Asked.Add(range?.From);
            }
            var drop = !probe && DropsAfter >= 0 && Interlocked.CompareExchange(ref _dropped, 1, 0) == 0;
            Stream body = drop
                ? new Dropping(published, (int)from, DropsAfter)
                : new MemoryStream(published, (int)from, (int)(to - from + 1), writable: false);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new StreamContent(body) };
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, published.Length);
            response.Content.Headers.ContentLength = to - from + 1;
            return Task.FromResult(response);
        }
    }

    // A body that serves so many bytes and then loses its connection.
    private sealed class Dropping(byte[] published, int from, int dropsAfter) : Stream
    {
        private int _served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served >= dropsAfter) throw new IOException("the connection went away");
            var n = Math.Min(Math.Min(count, dropsAfter - _served), published.Length - from - _served);
            if (n <= 0) return 0;
            Array.Copy(published, from + _served, buffer, offset, n);
            _served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
