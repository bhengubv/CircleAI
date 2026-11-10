// FfnStoreTests.cs
//
// The feed-forward's store (step 2 of the streaming engine): every layer's gate, up and down in the vector
// kernel's tiles, the same bytes the model's own repack makes; a model that keeps no copy of its feed-forward
// computing exactly what the copy did once a warm one is armed - a token at a time and a prompt's batch alike -
// and refusing to run a token while it is cold.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class FfnStoreTests
{
    [Fact]
    public void Every_layers_feed_forward_is_in_the_store_as_the_vector_kernel_packs_it()
    {
        WithModel(29, (file, directory) =>
        {
            var store = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));

            Assert.Equal(SyntheticQwen35.Layers * 3, store.Entries.Count);
            Assert.Equal(0, store.Verify(file));
            Assert.All(store.Entries, e => Assert.Equal(0, e.WeightsOffset % FlashFile.Page));
            Assert.Equal(store.Entries.Sum(e => FlashFile.Pages(e.WeightsBytes) + FlashFile.Pages(e.ScaleCount * sizeof(float))), store.DataBytes);
            Assert.False(File.Exists(Path.Combine(directory, "model.ffn.partial")));

            var again = FfnStore.Open(file, store.FilePath);
            Assert.NotNull(again);
            Assert.Equal(store.Entries, again.Entries);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_model_that_keeps_no_copy_of_its_feed_forward_computes_what_the_copy_did_once_armed(bool pooled)
    {
        WithModel(31, (file, directory) =>
        {
            var store = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
            using var pool = pooled ? new WorkerPool(2) : null;
            var copy = LanguageModel.Load(file, pool, headFromStore: false, ffnFromStore: false, pairedOverride: true);
            var served = LanguageModel.Load(file, pool, headFromStore: false, ffnFromStore: true, pairedOverride: true);
            Assert.True(served.FfnFromStore);
            Assert.False(served.FfnArmed);

            // Cold, no token runs: there is no feed-forward to run it on.
            Assert.Throws<InvalidOperationException>(() => served.Forward(3, served.NewState(16)));

            using (var warm = store.Load(channels: 3, readBytes: FlashFile.Page))
            {
                served.ArmFfn(warm);
                Same(copy, served);
                served.ReleaseFfn();
            }
            Assert.False(served.FfnArmed);
            Assert.Throws<InvalidOperationException>(() => served.Forward(3, served.NewState(16)));

            // Warm again from flash, the same once more.
            using var again = store.Load(channels: 1);
            served.ArmFfn(again);
            Same(copy, served);
        });
    }

    [Fact]
    public void The_head_and_the_feed_forward_both_read_from_flash_compute_what_the_copies_did()
    {
        WithModel(37, (file, directory) =>
        {
            var heads = HeadStore.Write(file, Path.Combine(directory, "model.head"), t => Script.Latin, chunkRows: 16, layout: HeadLayout.Paired);
            var ffn = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
            var copy = LanguageModel.Load(file, null, headFromStore: false, ffnFromStore: false, pairedOverride: true);
            var served = LanguageModel.Load(file, null, headFromStore: true, ffnFromStore: true, pairedOverride: true);
            using var surface = heads.Load(heads.Groups, channels: 2);
            using var warm = ffn.Load(channels: 2);
            served.Head = surface;
            served.ArmFfn(warm);
            Same(copy, served);
        });
    }

    // A token at a time, then a prompt's batch of eight (the vector kernel's lanes): every logit the same, to the bit.
    private static void Same(LanguageModel copy, LanguageModel served)
    {
        var a = copy.NewState(32);
        var b = served.NewState(32);
        var x = new float[SyntheticQwen35.Vocabulary];
        var y = new float[SyntheticQwen35.Vocabulary];
        foreach (var token in new[] { 5, 9, 30, 2, 17 })
        {
            copy.Forward(token, a, x);
            served.Forward(token, b, y);
            Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
        }
        var tokens = new[] { 3, 8, 12, 20, 6, 11, 14, 27 };
        copy.ForwardBatch(tokens, a, [null, null, null, null, null, null, null, x]);
        served.ForwardBatch(tokens, b, [null, null, null, null, null, null, null, y]);
        Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
    }

    private static void WithModel(int seed, Action<GgufFile, string> use)
    {
        var directory = Directory.CreateTempSubdirectory("ffn-store-").FullName;
        try
        {
            GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, seed), path =>
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
