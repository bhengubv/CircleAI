// IndexedMindTests.cs
//
// THE INDEXED MIND, IN THE OWNER'S STEPS (2026-11-15: "1) Download llm, 2) split, organise by thoughts, 3) create CORE
// index. Select coherent portions from 3 into RAM for 4) warm index. Only 4 ever reaches into 3, only 3 reaches into 2 and
// only 2 reaches into 1") - a word's thought finds its group, and only that group's neurons are read and computed.
//
// Watch records every word's thought and strongest neurons at every layer. The core index (step 3) groups the thoughts and
// lists each group's neurons, in whole tiles, and reads back as it was written. An index whose list holds every neuron
// computes what every neuron does, to the bit, whatever serves the piece - step 4 in RAM, step 2 read ahead or read when
// asked, step 1 through step 2 - a word at a time and a batch alike, and with the layers' stream bringing ffn_down and the
// mixing alone, from the model's own file or tiled in step 2.

using System.Numerics;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class IndexedMindTests
{
    private const int Layers = 3;
    private static readonly int[] Words = [3, 8, 12, 20, 6, 11, 14, 27, 5, 9, 30, 2];

    [Fact]
    public void Watch_records_every_words_thought_and_strongest_neurons_at_every_layer()
    {
        WithModel(91, (file, directory) =>
        {
            var model = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var path = Path.Combine(directory, "model.watch");
            using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, kept: 48))
            {
                model.Watch = watch;
                var state = model.NewState(32);
                model.ForwardBatch(Words, state, new float[Words.Length][]);
                model.Forward(17, state);
                model.Watch = null;
                Assert.Equal(Layers * (Words.Length + 1), watch.Records);
            }

            var read = ThoughtWatch.Read(path);
            Assert.Equal(Layers * (Words.Length + 1), read.Count);
            Assert.Equal((Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, 48), (read.Layers, read.Embedding, read.FeedForward, read.Kept));
            var perLayer = new int[Layers];
            foreach (var (layer, sketch, strong) in read.Each())
            {
                perLayer[layer]++;
                Assert.Equal(48, strong.Sum(b => BitOperations.PopCount(b)));
                Assert.Equal(1.0, Math.Sqrt(sketch.Sum(v => (double)v * v)), 2);
            }
            Assert.All(perLayer, count => Assert.Equal(Words.Length + 1, count));
        });
    }

    [Fact]
    public void The_core_index_groups_the_thoughts_lists_whole_tiles_and_reads_back_as_written()
    {
        WithModel(93, (file, directory) =>
        {
            var watch = Watch(file, directory, kept: 64);
            var core = ThoughtIndex.Build(watch, Path.Combine(directory, "model.index"), groups: 3, listNeurons: 70);
            Assert.Equal(80, core.ListNeurons); // 70 rounded up to whole 16-neuron tiles
            Assert.Equal(watch.Count, core.Records);
            for (var il = 0; il < Layers; il++)
            {
                Assert.Equal(Words.Length, Enumerable.Range(0, core.Groups).Sum(g => core.Size(il, g)));
                for (var g = 0; g < core.Groups; g++)
                {
                    var list = core.List(il, g).ToArray();
                    Assert.Equal(core.ListNeurons, list.Length);
                    Assert.Equal(list.OrderBy(n => n), list);
                    Assert.Equal(list.Length, list.Distinct().Count());
                }
            }

            var again = ThoughtIndex.Open(core.FilePath);
            Assert.NotNull(again);
            Assert.Equal((core.Layers, core.Groups, core.ListNeurons, core.Records), (again.Layers, again.Groups, again.ListNeurons, again.Records));
            var sketch = new float[ThoughtSketch.Width];
            sketch[0] = 1;
            for (var il = 0; il < Layers; il++)
            {
                Assert.Equal(core.Nearest(il, sketch), again.Nearest(il, sketch));
                for (var g = 0; g < core.Groups; g++) Assert.Equal(core.List(il, g).ToArray(), again.List(il, g).ToArray());
            }
        });
    }

    [Fact]
    public void An_index_of_every_neuron_computes_what_every_neuron_does_from_each_tier()
    {
        WithModel(97, (file, directory) =>
        {
            var copy = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var indexed = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var core = ThoughtIndex.Build(Watch(file, directory, kept: SyntheticQwen35.FeedForward), Path.Combine(directory, "model.index"),
                groups: 1, listNeurons: SyntheticQwen35.FeedForward);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(Layers, pieces.Count);

            // From step 4 (every piece warm), from step 2 (none warm), from step 1 through step 2 (no pieces).
            foreach (var (tier, split, warm) in new[] { ("step 4", pieces, long.MaxValue), ("step 2", pieces, 0L), ("step 1", (CachedIndex?)null, 0L) })
            {
                using var serving = new IndexedFeedForward(core, split, file, warm, channels: 2, readBytes: FlashFile.Page);
                indexed.Index = serving;
                Same(copy, indexed);
                var counts = serving.TakeCounts();
                var fromStep2 = counts.Ahead + counts.Asked - counts.FromModel;
                Assert.True(tier switch
                {
                    "step 4" => counts.Warm > 0 && fromStep2 == 0 && counts.FromModel == 0,
                    "step 2" => fromStep2 > 0 && counts.Warm == 0 && counts.FromModel == 0,
                    _ => counts.FromModel > 0 && counts.Warm == 0 && fromStep2 == 0,
                }, $"{tier}: {counts.Warm} from step 4, {fromStep2} from step 2, {counts.FromModel} from step 1");
                indexed.Index = null;
            }
        });
    }

    [Fact]
    public void A_stream_bringing_ffn_down_alone_with_the_index_serving_gate_and_up_computes_what_the_copy_did()
    {
        WithModel(101, (file, directory) =>
        {
            var copy = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var streamed = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            var core = ThoughtIndex.Build(Watch(file, directory, kept: SyntheticQwen35.FeedForward), Path.Combine(directory, "model.index"),
                groups: 2, listNeurons: SyntheticQwen35.FeedForward);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var down = TileStore.OverModel(file, TileSet.FeedForward, SplitByThoughts.DownKinds);
            Assert.All(down.Entries, e => Assert.Equal(2, e.Kind));
            using var stream = new LayerStream(TileStore.OverModel(file, TileSet.Mixing), down, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            using (var serving = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page))
            {
                streamed.Index = serving;
                streamed.Stream = stream;
                Same(copy, streamed);
                streamed.Index = null;
            }

            // AND WITH EACH PIECE'S GATE AND UP ALONE (2026-11-15): the layers bring their own ffn_down, so a piece is read
            // and held without its rounded copy - from step 4, from step 2, from step 1 through step 2 - fewer bytes a
            // piece, and still what the copy computed, to the bit.
            foreach (var (tier, split, warm) in new[] { ("step 4", pieces, long.MaxValue), ("step 2", pieces, 0L), ("step 1", (CachedIndex?)null, 0L) })
            {
                using var without = new IndexedFeedForward(core, split, file, warm, channels: 2, readBytes: FlashFile.Page, withDown: false);
                streamed.Index = without;
                Same(copy, streamed);
                var counts = without.TakeCounts();
                var fromStep2 = counts.Ahead + counts.Asked - counts.FromModel;
                Assert.True(tier switch
                {
                    "step 4" => counts.Warm > 0 && fromStep2 == 0 && counts.FromModel == 0,
                    "step 2" => fromStep2 > 0 && counts.Warm == 0 && counts.FromModel == 0,
                    _ => counts.FromModel > 0 && counts.Warm == 0 && fromStep2 == 0,
                }, $"{tier}: {counts.Warm} from step 4, {fromStep2} from step 2, {counts.FromModel} from step 1");
                if (tier == "step 2")
                    Assert.True(counts.AskedBytes / counts.Asked < pieces.PieceStride, $"a piece without its down read {counts.AskedBytes / counts.Asked} bytes, a whole one is {pieces.PieceStride}");
                if (tier == "step 4")
                    Assert.True(without.WarmBytes < (long)without.WarmPieces * pieces.PieceStride, $"step 4 holds {without.WarmBytes} bytes for {without.WarmPieces} pieces of {pieces.PieceStride}");
                streamed.Index = null;
            }
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var without = new IndexedFeedForward(core, pieces, file, 0, channels: 2, readBytes: FlashFile.Page, withDown: false);
                without.Piece(0, new float[SyntheticQwen35.Embedding], down: true);
            });

            // STEP 2 WHOLE (the owner, 2026-11-15: "only 2 reaches into 1"): pieces written without their down, and every
            // layer's attention and ffn_down tiled once - the stream reads them as they lie, repacks nothing, and computes
            // what the copy did, to the bit.
            var noDown = CachedIndex.Build(core, file, Path.Combine(directory, "nodown.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page, withDown: false);
            Assert.False(noDown.HoldsDown);
            Assert.True(noDown.PieceStride < pieces.PieceStride, $"a piece without its down takes {noDown.PieceStride} bytes, one with it {pieces.PieceStride}");
            Assert.False(CachedIndex.Open(noDown.FilePath, file, core)!.HoldsDown);
            Assert.Throws<ArgumentException>(() => new IndexedFeedForward(core, noDown, file, 0, withDown: true));
            var mix = TileStore.Write(file, Path.Combine(directory, "model.mix"), TileSet.Mixing, channels: 2, readBytes: FlashFile.Page);
            var downTiled = TileStore.Write(file, Path.Combine(directory, "model.down"), TileSet.FeedForward, channels: 2, readBytes: FlashFile.Page, kinds: SplitByThoughts.DownKinds);
            Assert.False(mix.InPlace);
            Assert.All(downTiled.Entries, e => Assert.Equal(2, e.Kind));
            Assert.NotNull(TileStore.Open(file, downTiled.FilePath, TileSet.FeedForward, SplitByThoughts.DownKinds));
            Assert.Null(TileStore.Open(file, downTiled.FilePath, TileSet.FeedForward));   // it is not the whole feed-forward
            using var tiled = new LayerStream(mix, downTiled, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            Assert.False(tiled.InPlace);
            using var whole = new IndexedFeedForward(core, noDown, file, 0, channels: 2, readBytes: FlashFile.Page, withDown: false);
            streamed.Index = whole;
            streamed.Stream = tiled;
            tiled.TakeParts();
            Same(copy, streamed);
            Assert.Equal(TimeSpan.Zero, tiled.TakeParts().Repacking);
            Assert.Equal(0, whole.TakeCounts().FromModel);
            streamed.Index = null;
        });
    }

    [Fact]
    public void A_model_that_does_not_pair_takes_no_index()
    {
        WithModel(103, (file, directory) =>
        {
            var core = ThoughtIndex.Build(Watch(file, directory, kept: 32), Path.Combine(directory, "model.index"), groups: 1, listNeurons: 32);
            using var serving = new IndexedFeedForward(core, null, file, 0);
            var unpaired = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: false);
            Assert.Throws<InvalidOperationException>(() => unpaired.Index = serving);
        });
    }

    // COHERENT THOUGHTS (the owner, 2026-11-15: "We index thoughts so what we keep warm have to be coherent"): a word's
    // thought is found once, at the first layer, and kept through every layer - each layer's list for a thought is the
    // neurons its own words fired most there, whatever those words look like by then. Four thoughts, two words each, over
    // two passages as Watch writes them (each word at a layer, then each at the next); past the first layer every word
    // looks like noise, so only a word's first-layer thought says which list its neurons belong to.
    [Fact]
    public void A_coherent_index_keeps_each_words_first_layer_thought_through_every_layer()
    {
        WithModel(105, (file, directory) =>
        {
            const int kept = 16;
            var rng = new Random(105);
            var thoughts = Enumerable.Range(0, 4).Select(_ => Noise(rng)).ToArray();
            int[][] passages = [[0, 0, 1, 1], [2, 2, 3, 3]];   // each word's thought, in its passage's order
            var path = Path.Combine(directory, "model.watch");
            using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, kept))
            {
                foreach (var passage in passages)
                    for (var il = 0; il < Layers; il++)
                        foreach (var t in passage)
                            watch.Record(il, il == 0 ? thoughts[t] : Noise(rng), Fired(NeuronsOf(t, il, kept)));
            }
            var records = ThoughtWatch.Read(path);
            var core = ThoughtIndex.Build(records, Path.Combine(directory, "coherent.index"), groups: 4, listNeurons: kept, coherent: true);
            Assert.True(core.Coherent);
            Assert.True(ThoughtIndex.Open(core.FilePath) is { Coherent: true }, "read back, the index is not of coherent thoughts");

            // Each thought its own group, found at the first layer - and the same group at every layer after it.
            var sketch = new ThoughtSketch(SyntheticQwen35.Embedding);
            var groups = thoughts.Select(t => core.Nearest(0, Sketched(sketch, t))).ToArray();
            Assert.Equal(4, groups.Distinct().Count());
            for (var il = 0; il < Layers; il++)
            {
                for (var t = 0; t < thoughts.Length; t++)
                {
                    Assert.Equal(groups[t], core.Nearest(il, Sketched(sketch, thoughts[t])));
                    Assert.Equal(2, core.Size(il, groups[t]));
                    Assert.True(core.List(il, groups[t]).ToArray().SequenceEqual(NeuronsOf(t, il, kept)),
                        $"layer {il}'s list for thought {t} is not the neurons its words fired there");
                }
            }

            // Grouped a layer at a time, the same words cannot be followed past the first layer: there they look like noise.
            var perLayer = ThoughtIndex.Build(records, Path.Combine(directory, "per-layer.index"), groups: 4, listNeurons: kept);
            Assert.False(perLayer.Coherent);
            var followed = Enumerable.Range(1, Layers - 1).All(il => Enumerable.Range(0, thoughts.Length)
                .All(t => Enumerable.Range(0, perLayer.Groups).Any(g => perLayer.List(il, g).ToArray().SequenceEqual(NeuronsOf(t, il, kept)))));
            Assert.False(followed, "grouped a layer at a time, every thought's list was found at every layer");
        });
    }

    // Served coherent, a word's thought is found where a pass brings it to the first layer and kept to the last, whatever
    // the word looks like on the way; the next pass finds it afresh. Each word of a prompt keeps its own.
    [Fact]
    public void Served_coherent_a_words_thought_is_found_at_the_first_layer_and_kept_to_the_last()
    {
        WithModel(107, (file, directory) =>
        {
            const int kept = 16;
            var rng = new Random(107);
            var thoughts = Enumerable.Range(0, 3).Select(_ => Noise(rng)).ToArray();
            var path = Path.Combine(directory, "model.watch");
            using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, kept))
            {
                for (var il = 0; il < Layers; il++)
                    for (var t = 0; t < thoughts.Length; t++)
                        watch.Record(il, il == 0 ? thoughts[t] : Noise(rng), Fired(NeuronsOf(t, il, kept)));
            }
            var core = ThoughtIndex.Build(ThoughtWatch.Read(path), Path.Combine(directory, "model.index"), groups: 3, listNeurons: kept, coherent: true);
            var sketch = new ThoughtSketch(SyntheticQwen35.Embedding);
            var group = thoughts.Select(t => core.Nearest(0, Sketched(sketch, t))).ToArray();
            Assert.Equal(3, group.Distinct().Count());

            using var serving = new IndexedFeedForward(core, null, file, 0, channels: 2, readBytes: FlashFile.Page);
            for (var il = 0; il < Layers; il++)
            {
                // Word 0 began as thought 0, word 1 as thought 1; past the first layer each looks like another thought.
                Assert.Equal(group[0], serving.GroupOf(il, thoughts[il == 0 ? 0 : 2], word: 0));
                Assert.Equal(group[1], serving.GroupOf(il, thoughts[il == 0 ? 1 : 0], word: 1));
            }
            Assert.Equal(group[2], serving.GroupOf(0, thoughts[2], word: 0));   // the next pass: found afresh
            Assert.Equal(group[2], serving.GroupOf(1, thoughts[1], word: 0));
            for (var il = 0; il < Layers; il++)
                Assert.True(serving.Piece(il, thoughts[il == 0 ? 1 : 2], word: 1).Neurons.SequenceEqual(core.List(il, group[1]).ToArray()),
                    $"layer {il}: the piece served is not thought 1's");
        });
    }

    // An index of every neuron is every neuron, coherent or not: the same logits to the bit, a word at a time and a prompt's
    // batch alike, from every tier - and a batch's words taken a thought at a time compute what each computed on its own.
    [Fact]
    public void A_coherent_index_of_every_neuron_computes_what_every_neuron_does_from_each_tier()
    {
        WithModel(109, (file, directory) =>
        {
            var copy = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var indexed = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
            var core = ThoughtIndex.Build(Watch(file, directory, kept: SyntheticQwen35.FeedForward), Path.Combine(directory, "model.index"),
                groups: 3, listNeurons: SyntheticQwen35.FeedForward, coherent: true);
            Assert.True(core.Coherent);
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            foreach (var (cache, warm) in new[] { (cached, long.MaxValue), (cached, 0L), ((CachedIndex?)null, 0L) })
            {
                using var serving = new IndexedFeedForward(core, cache, file, warm, channels: 2, readBytes: FlashFile.Page);
                indexed.Index = serving;
                Same(copy, indexed);
                indexed.Index = null;
            }
        });
    }

    // ONE READ A PROMPT (2026-11-15; on the P30 one list's pieces were read again for each of 32 words, 20.7 GB): a prompt's
    // words are served a group at a time, so a piece is read from flash once for every word of the batch that wants it -
    // per-layer groups and coherent thoughts alike.
    [Fact]
    public void A_prompt_reads_each_piece_it_needs_from_flash_once_not_once_a_word()
    {
        WithModel(111, (file, directory) =>
        {
            foreach (var coherent in new[] { false, true })
            {
                var core = ThoughtIndex.Build(Watch(file, directory, kept: 64), Path.Combine(directory, $"model-{coherent}.index"), groups: 2, listNeurons: 64, coherent);
                var pieces = CachedIndex.Build(core, file, Path.Combine(directory, $"model-{coherent}.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
                using var serving = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
                var model = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: false);
                model.Index = serving;
                model.ForwardBatch(Words, model.NewState(32), new float[Words.Length][]);
                var counts = serving.TakeCounts();
                var reads = counts.Ahead + counts.Asked;
                Assert.Equal(Layers * Words.Length, counts.Warm + counts.Again + reads);
                Assert.True(reads <= Layers * core.Groups,
                    $"{(coherent ? "coherent" : "per-layer")}: {counts.Ahead} pieces read ahead and {counts.Asked} read when asked for {Words.Length} words over {Layers} layers of {core.Groups} groups");
                Assert.Equal(Layers * Words.Length - reads, counts.Again);
                // A coherent prompt's thoughts are known at its first layer: step 2 reads every piece it wants ahead.
                if (coherent) Assert.Equal((reads, 0L), (counts.Ahead, counts.Asked));
            }
        });
    }

    // WHOLE THOUGHTS WARM (the owner, 2026-11-15: "what we keep warm have to be coherent"): with room in RAM for one thought,
    // every layer of the thought most of Watch's words fell in is warm, and none of the other; the other, wanted again and
    // again, is warmed whole, and the first let go whole. Step 2, given room on flash for one thought, holds every layer of
    // that one.
    [Fact]
    public void What_a_coherent_index_keeps_warm_and_lets_go_is_whole_thoughts()
    {
        WithModel(113, (file, directory) =>
        {
            var core = ThoughtIndex.Build(Watch(file, directory, kept: 64), Path.Combine(directory, "model.index"), groups: 2, listNeurons: 64, coherent: true);
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var hot = core.Size(0, 0) >= core.Size(0, 1) ? 0 : 1;
            var cold = 1 - hot;
            Assert.True(core.Size(0, cold) > 0, "setup: every word fell in one thought");

            using var serving = new IndexedFeedForward(core, cached, file, warmBudget: Layers * cached.PieceStride, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(Layers, serving.WarmPieces);
            Assert.Equal((Layers, 0), Served(serving, hot));
            Assert.Equal((0, Layers), Served(serving, cold));
            for (var il = 0; il < Layers; il++)
            {
                serving.Piece(il, cold);
                serving.Piece(il, cold);
            }
            Assert.Equal(Layers, serving.WarmPieces);
            Assert.Equal((Layers, 0), Served(serving, cold));
            Assert.Equal((0, Layers), Served(serving, hot));

            var portion = CachedIndex.Build(core, file, Path.Combine(directory, "portion.core"), budget: Layers * cached.PieceStride, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(Layers, portion.Count);
            for (var il = 0; il < Layers; il++) Assert.True(portion.Entry(il, hot) is not null, $"step 2 holds no piece of thought {hot} at layer {il}");
        });
    }

    // STEP 2, ORGANISED BY THOUGHTS (the owner, 2026-11-15: "we need to know what to load when"): a coherent thought's pieces
    // lie one after another, its layers in the order a pass runs them - once a word's thought is known, its whole pass is one
    // run of flash. Per-layer groups are found afresh at every layer, so theirs lie a layer at a time.
    [Fact]
    public void Step_2_lays_a_coherent_thoughts_pieces_one_after_another_in_layer_order()
    {
        WithModel(115, (file, directory) =>
        {
            foreach (var coherent in new[] { true, false })
            {
                var core = ThoughtIndex.Build(Watch(file, directory, kept: 64), Path.Combine(directory, $"model-{coherent}.index"), groups: 2, listNeurons: 64, coherent);
                var pieces = CachedIndex.Build(core, file, Path.Combine(directory, $"model-{coherent}.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
                for (var g = 0; g < core.Groups; g++)
                {
                    for (var il = 0; il + 1 < Layers; il++)
                    {
                        if (pieces.Entry(il, g) is not { } here || pieces.Entry(il + 1, g) is not { } next) continue;
                        if (coherent)
                            Assert.Equal(here.Offset + pieces.PieceStride, next.Offset);
                        else if (pieces.Entry(il, 1 - g) is not null)
                            Assert.NotEqual(here.Offset + pieces.PieceStride, next.Offset);
                    }
                }
                Assert.Equal(pieces.Count, CachedIndex.Open(pieces.FilePath, file, core)!.Count);
            }
        });
    }

    // KNOW WHAT TO LOAD WHEN (the owner, 2026-11-15): once a word's thought is found at the first layer, step 2 reads the
    // pass's later layers ahead - the word waits on no read a layer asks for - and the next pass, finding its thought
    // afresh, is planned again. A prompt's words with two thoughts have both thoughts' pieces read ahead, each once.
    [Fact]
    public void Once_a_words_thought_is_found_step_2_reads_its_later_layers_ahead()
    {
        WithModel(117, (file, directory) =>
        {
            var core = ThoughtIndex.Build(Watch(file, directory, kept: 64), Path.Combine(directory, "model.index"), groups: 2, listNeurons: 64, coherent: true);
            var pieces = CachedIndex.Build(core, file, Path.Combine(directory, "model.core"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            using var serving = new IndexedFeedForward(core, pieces, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(IndexedFeedForward.DefaultAhead, serving.AheadSlots);
            var rng = new Random(117);
            var thought = Noise(rng);
            var group = core.Nearest(0, Sketched(new ThoughtSketch(SyntheticQwen35.Embedding), thought));

            foreach (var pass in new[] { 1, 2 })
            {
                for (var il = 0; il < Layers; il++)
                    Assert.True(serving.Piece(il, il == 0 ? thought : Noise(rng), word: 0).Neurons.SequenceEqual(core.List(il, group).ToArray()),
                        $"pass {pass}, layer {il}: the piece read ahead is not the word's thought's");
                var counts = serving.TakeCounts();
                Assert.Equal((Layers, 0L, 0L), (counts.Ahead, counts.Asked, counts.Warm));
                Assert.True(counts.AheadBytes >= Layers * IndexedFeedForward.HeldBytes(core, pieces, withDown: true),
                    $"pass {pass}: step 2 read {counts.AheadBytes} bytes ahead for {Layers} pieces");
            }

            // Two words, two thoughts, as a prompt's batch asks: every word's thought at the first layer, then each layer's
            // pieces a thought at a time.
            var other = Enumerable.Range(0, 50).Select(_ => Noise(rng)).First(t => core.Nearest(0, Sketched(new ThoughtSketch(SyntheticQwen35.Embedding), t)) != group);
            var groups = new[] { thought, other }.Select(t => core.Nearest(0, Sketched(new ThoughtSketch(SyntheticQwen35.Embedding), t))).ToArray();
            for (var il = 0; il < Layers; il++)
            {
                var found = new[] { serving.GroupOf(il, il == 0 ? thought : Noise(rng), word: 0), serving.GroupOf(il, il == 0 ? other : Noise(rng), word: 1) };
                Assert.Equal(groups, found);
                foreach (var g in found.Order()) serving.Piece(il, g);
            }
            var both = serving.TakeCounts();
            Assert.Equal((2L * Layers, 0L), (both.Ahead, both.Asked));
        });
    }

    // Every layer's piece of one thought, one word each: how many came from step 4 and how many were read from flash.
    private static (int Warm, int Read) Served(IndexedFeedForward serving, int thought)
    {
        serving.TakeCounts();
        for (var il = 0; il < Layers; il++) serving.Piece(il, thought);
        var counts = serving.TakeCounts();
        return ((int)counts.Warm, (int)(counts.Ahead + counts.Asked));
    }

    // `kept` neurons of their own for thought `t` at layer `il`, sorted.
    private static ushort[] NeuronsOf(int t, int il, int kept) =>
        [.. Enumerable.Range((t * Layers + il) * kept, kept).Select(n => (ushort)n)];

    // A layer's activations with `neurons` the strongest.
    private static float[] Fired(ushort[] neurons)
    {
        var activations = new float[SyntheticQwen35.FeedForward];
        foreach (var n in neurons) activations[n] = 1;
        return activations;
    }

    private static float[] Noise(Random rng)
    {
        var thought = new float[SyntheticQwen35.Embedding];
        for (var i = 0; i < thought.Length; i++) thought[i] = (float)(rng.NextDouble() * 2 - 1);
        return thought;
    }

    private static float[] Sketched(ThoughtSketch sketch, float[] thought)
    {
        var into = new float[ThoughtSketch.Width];
        sketch.Sketch(thought, into);
        return into;
    }

    // A watch over the words, every neuron's strength kept as asked.
    private static WatchRecords Watch(GgufFile file, string directory, int kept)
    {
        var model = LanguageModel.Load(file, null, headFromStore: false, pairedOverride: true);
        var path = Path.Combine(directory, "model.watch");
        using (var watch = new ThoughtWatch(path, Layers, SyntheticQwen35.Embedding, SyntheticQwen35.FeedForward, kept))
        {
            model.Watch = watch;
            model.ForwardBatch(Words, model.NewState(32), new float[Words.Length][]);
            model.Watch = null;
        }
        return ThoughtWatch.Read(path);
    }

    // A word at a time, then a prompt's batch of eight: every logit the same, to the bit.
    private static void Same(LanguageModel copy, LanguageModel other)
    {
        var a = copy.NewState(32);
        var b = other.NewState(32);
        var x = new float[SyntheticQwen35.Vocabulary];
        var y = new float[SyntheticQwen35.Vocabulary];
        foreach (var token in new[] { 5, 9, 30, 2, 17 })
        {
            copy.Forward(token, a, x);
            other.Forward(token, b, y);
            Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
        }
        var tokens = new[] { 3, 8, 12, 20, 6, 11, 14, 27 };
        copy.ForwardBatch(tokens, a, [null, null, null, null, null, null, null, x]);
        other.ForwardBatch(tokens, b, [null, null, null, null, null, null, null, y]);
        Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
    }

    private static void WithModel(int seed, Action<GgufFile, string> use)
    {
        var directory = Directory.CreateTempSubdirectory("indexed-mind-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed, layers: Layers), path =>
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
}
