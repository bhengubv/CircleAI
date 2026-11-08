// LayerStreamTests.cs
//
// Every layer read from flash (the streaming engine's next step, for a model bigger than the phone's RAM - the 27B on
// the P30). The mixing's store holds every layer's projections as the vector kernel packs them; a model that keeps no
// copy of any layer computes what the copy did, to the bit, a token at a time and a prompt's batch alike, whether every
// layer is warm, some are, or none, through a ring of one slot or several; a pass that does not go in order starts the
// ring again; and no token runs without the layers.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class LayerStreamTests
{
    private const int Layers = 5;

    [Fact]
    public void The_mixing_store_holds_every_layers_projections_as_the_vector_kernel_packs_them()
    {
        WithModel(61, (file, directory) =>
        {
            var store = MixStore.Write(file, Path.Combine(directory, "model.mix"));

            // DeltaNet layers (even) hold qkv, gate and out; full-attention layers (odd) q, k, v and output.
            var expected = Enumerable.Range(0, Layers).Sum(il => il % 2 == 0 ? 3 : 4);
            Assert.Equal(expected, store.Entries.Count);
            Assert.Equal(0, store.Verify(file));
            Assert.All(store.Entries, e => Assert.Equal(0, e.WeightsOffset % FlashFile.Page));
            for (var il = 0; il < Layers; il++)
            {
                var (start, length) = store.LayerRange(il);
                var entries = store.LayerEntries(il).ToList();
                Assert.Equal(entries[0].WeightsOffset, start);
                Assert.Equal(entries[^1].End - start, length);
            }
            var again = MixStore.Open(file, store.FilePath);
            Assert.NotNull(again);
            Assert.Equal(store.Entries, again.Entries);
            Assert.Null(FfnStore.Open(file, store.FilePath)); // another set's file is not this one's
        });
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(0L, 2)]
    [InlineData(0L, 3)]
    [InlineData(1L, 2)]          // the budget of one byte: no layer fits, every one streams
    [InlineData(-1L, 1)]         // -1: room for two layers warm, the rest through the ring
    [InlineData(long.MaxValue, 2)] // every layer warm: nothing streams
    public void A_model_whose_layers_come_from_flash_computes_what_the_copy_did(long warm, int slots)
    {
        WithModel(67, (file, directory) =>
        {
            var mix = MixStore.Write(file, Path.Combine(directory, "model.mix"));
            var ffn = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
            if (warm == -1)
            {
                var (_, a) = mix.LayerRange(0);
                var (_, b) = ffn.LayerRange(0);
                var (_, c) = mix.LayerRange(1);
                var (_, d) = ffn.LayerRange(1);
                warm = a + b + c + d;
            }
            var copy = Qwen35Model.Load(file, null, headFromStore: false, ffnFromStore: false, pairedOverride: true);
            var streamed = Qwen35Model.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            Assert.True(streamed.MixFromStore);
            using var stream = new LayerStream(mix, ffn, channels: 2, readBytes: FlashFile.Page, warmBudget: warm, slots: slots);
            Assert.Equal(Layers, stream.Layers);
            if (warm == long.MaxValue) Assert.Equal(0, stream.Slots);
            streamed.Stream = stream;
            Same(copy, streamed);
            Same(copy, streamed); // and again, the ring going round
        });
    }

    [Fact]
    public void A_pass_that_does_not_go_in_order_starts_the_ring_again()
    {
        WithModel(71, (file, directory) =>
        {
            var mix = MixStore.Write(file, Path.Combine(directory, "model.mix"));
            var ffn = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
            using var warm = ffn.Load(channels: 1);
            using var stream = new LayerStream(mix, ffn, channels: 2, readBytes: FlashFile.Page, warmBudget: 0, slots: 2);
            var x = new float[SyntheticQwen35.FeedForward];
            var rng = new Random(9);
            for (var i = 0; i < x.Length; i++) x[i] = rng.NextSingle() - 0.5f;

            // Out of order - 3, 1, 4, then 0 - each read where the store holds it.
            foreach (var layer in new[] { 3, 1, 4, 0 })
            {
                var tiles = stream.Acquire(layer);
                Assert.Equal(Down(warm.View(layer, 2), x), Down(tiles.FeedForward[2]!, x));
                stream.Release(layer);
            }
        });
    }

    [Fact]
    public void No_token_runs_on_layers_that_are_not_there()
    {
        WithModel(73, (file, directory) =>
        {
            MixStore.Write(file, Path.Combine(directory, "model.mix"));
            FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
            var streamed = Qwen35Model.Load(file, null, headFromStore: false, ffnFromStore: true, pairedOverride: true, mixFromStore: true);
            Assert.Throws<InvalidOperationException>(() => streamed.Forward(3, streamed.NewState(16)));
        });
    }

    // ffn_down's product with x, through the vector kernel.
    private static float[] Down(Ptq1PairMatrix matrix, float[] x)
    {
        var q = new Q8Activations(x.Length);
        q.Quantize(x);
        var input = new Ptq1PairInput(x.Length);
        input.Set(q);
        var y = new float[SyntheticQwen35.Embedding];
        matrix.MatVec(input, y);
        return y;
    }

    // A token at a time, then a prompt's batch of eight: every logit the same, to the bit.
    private static void Same(Qwen35Model copy, Qwen35Model streamed)
    {
        var a = copy.NewState(32);
        var b = streamed.NewState(32);
        var x = new float[SyntheticQwen35.Vocabulary];
        var y = new float[SyntheticQwen35.Vocabulary];
        foreach (var token in new[] { 5, 9, 30, 2, 17 })
        {
            copy.Forward(token, a, x);
            streamed.Forward(token, b, y);
            Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
        }
        var tokens = new[] { 3, 8, 12, 20, 6, 11, 14, 27 };
        copy.ForwardBatch(tokens, a, [null, null, null, null, null, null, null, x]);
        streamed.ForwardBatch(tokens, b, [null, null, null, null, null, null, null, y]);
        Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
    }

    private static void WithModel(int seed, Action<GgufFile, string> use)
    {
        var directory = Directory.CreateTempSubdirectory("layer-stream-").FullName;
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
