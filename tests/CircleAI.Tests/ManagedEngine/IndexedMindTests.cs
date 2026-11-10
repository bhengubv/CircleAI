// IndexedMindTests.cs
//
// THE INDEXED MIND (2026-11-09): a core index on flash, a portion of it stored as a cached index, the warm part in RAM -
// a word's thought finds its group, and only that group's neurons are read and computed.
//
// Watch records every word's thought and strongest neurons at every layer. The core index groups the thoughts and lists
// each group's neurons, in whole tiles, and reads back as it was written. An index whose list holds every neuron computes
// what every neuron does, to the bit, whichever tier serves the piece - RAM, the cached index, the model's own file - a
// word at a time and a batch alike, and with the layers' stream bringing ffn_down and the mixing alone.

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
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            Assert.Equal(Layers, cached.Count);

            // From RAM (every piece warm), from the cached index (none warm), from the model's own file (no cache).
            foreach (var (tier, cache, warm) in new[] { ("warm", cached, long.MaxValue), ("cached", cached, 0L), ("core", (CachedIndex?)null, 0L) })
            {
                using var serving = new IndexedFeedForward(core, cache, file, warm, channels: 2, readBytes: FlashFile.Page);
                indexed.Index = serving;
                Same(copy, indexed);
                var (fromRam, fromCache, _, fromCore, _) = serving.TakeCounts();
                Assert.True(tier switch { "warm" => fromRam > 0 && fromCache == 0 && fromCore == 0, "cached" => fromCache > 0 && fromRam == 0 && fromCore == 0, _ => fromCore > 0 && fromRam == 0 && fromCache == 0 },
                    $"{tier}: {fromRam} from RAM, {fromCache} from the cache, {fromCore} from the core");
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
            var cached = CachedIndex.Build(core, file, Path.Combine(directory, "model.cached"), budget: long.MaxValue, channels: 2, readBytes: FlashFile.Page);
            var down = TileStore.OverModel(file, TileSet.FeedForward, [2]);
            Assert.All(down.Entries, e => Assert.Equal(2, e.Kind));
            using var stream = new LayerStream(TileStore.OverModel(file, TileSet.Mixing), down, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            using var serving = new IndexedFeedForward(core, cached, file, warmBudget: 0, channels: 2, readBytes: FlashFile.Page);
            streamed.Index = serving;
            streamed.Stream = stream;
            Same(copy, streamed);
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
