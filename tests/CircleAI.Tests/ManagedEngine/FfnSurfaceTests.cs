// FfnSurfaceTests.cs
//
// Step 3 of the streaming engine: a word that computes only some of each layer's feed-forward neuron groups. The
// vector kernel's subset products are its full products row for row (a range of tiles), and skipping blocks whose
// input is zero changes no bit; a surface of every group is the full feed-forward exactly; a surface keeps the
// groups that fired most; and the model adds up what fired as it runs.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class FfnSurfaceTests
{
    private const int Rows = 64;   // four tiles
    private const int Cols = 512;  // four blocks

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_range_of_tiles_is_its_full_product_row_for_row_and_leaves_the_rest_alone(bool pooled)
    {
        var matrix = new Ptq1PairMatrix(Matrix(3), Rows, Cols);
        var x = Input(5, zeroBlocks: []);
        using var pool = pooled ? new WorkerPool(2) : null;
        var full = new float[Rows];
        matrix.MatVec(x, full, pool);

        var some = Enumerable.Repeat(123f, Rows).ToArray();
        matrix.MatVecRanges(x, some, [(0, 1), (2, 4)], pool);
        for (var r = 0; r < Rows; r++)
        {
            if (r / 16 == 1) Assert.Equal(123f, some[r]);
            else Assert.Equal(BitConverter.SingleToInt32Bits(full[r]), BitConverter.SingleToInt32Bits(some[r]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skipping_blocks_whose_input_is_zero_changes_no_bit(bool pooled)
    {
        var matrix = new Ptq1PairMatrix(Matrix(7), Rows, Cols);
        var x = Input(11, zeroBlocks: [1, 3]);
        using var pool = pooled ? new WorkerPool(2) : null;
        var full = new float[Rows];
        matrix.MatVec(x, full, pool);

        var masked = new float[Rows];
        matrix.MatVecMasked(x, masked, [1, 0, 1, 0], pool);
        Assert.Equal(full.Select(BitConverter.SingleToInt32Bits), masked.Select(BitConverter.SingleToInt32Bits));

        var all = new float[Rows];
        matrix.MatVecMasked(x, all, [1, 1, 1, 1], pool);
        Assert.Equal(full.Select(BitConverter.SingleToInt32Bits), all.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void A_surface_of_every_group_is_the_whole_feed_forward_and_a_smaller_one_still_answers()
    {
        GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, 41), path =>
        {
            using var file = GgufFile.Open(path);
            var model = Qwen35Model.Load(file, null, headFromStore: false, pairedOverride: true);
            var groups = SyntheticQwen35.FeedForward / model.FfnGroupSize;
            Assert.Equal(3, groups);
            var every = FfnSurface.Of(Enumerable.Range(0, SyntheticQwen35.Layers).Select(_ => Enumerable.Repeat(true, groups).ToArray()).ToArray(),
                model.FfnGroupSize, SyntheticQwen35.FeedForward);
            Assert.Equal(1.0, every.Fraction);

            var a = new float[SyntheticQwen35.Vocabulary];
            var b = new float[SyntheticQwen35.Vocabulary];
            var plain = model.NewState(16);
            var surfaced = model.NewState(16);
            foreach (var token in new[] { 4, 9, 30, 2 })
            {
                model.FfnGroups = null;
                model.Forward(token, plain, a);
                model.FfnGroups = every;
                model.Forward(token, surfaced, b);
                Assert.Equal(a.Select(BitConverter.SingleToInt32Bits), b.Select(BitConverter.SingleToInt32Bits));
            }

            // One group of three: still a distribution, and not the same one.
            model.FfnGroups = FfnSurface.Of(Enumerable.Range(0, SyntheticQwen35.Layers).Select(_ => new[] { true, false, false }).ToArray(),
                model.FfnGroupSize, SyntheticQwen35.FeedForward);
            model.Forward(7, model.NewState(4), b);
            model.FfnGroups = null;
            model.Forward(7, model.NewState(4), a);
            Assert.All(b, v => Assert.True(float.IsFinite(v)));
            Assert.NotEqual(a.Select(BitConverter.SingleToInt32Bits), b.Select(BitConverter.SingleToInt32Bits));
            return 0;
        });
    }

    [Fact]
    public void The_model_adds_up_what_each_group_fired_and_a_surface_keeps_the_strongest()
    {
        GgufBuilder.WithFile(SyntheticQwen35.Build(GgmlType.PTQ1_0, 43), path =>
        {
            using var file = GgufFile.Open(path);
            var model = Qwen35Model.Load(file, null, headFromStore: false, pairedOverride: true);
            model.RecordFfn = true;
            var state = model.NewState(16);
            foreach (var token in new[] { 4, 9, 30 }) model.Forward(token, state);
            model.ForwardBatch(new[] { 1, 2, 3, 5, 6, 7, 8, 10 }, state, new float[]?[8]);
            var (activity, tokens) = model.TakeFfnActivity();
            Assert.Equal(11, tokens);
            Assert.Equal(SyntheticQwen35.Layers * 3, activity.Length);
            Assert.All(activity, v => Assert.True(v > 0));
            Assert.Equal(0, model.TakeFfnActivity().Tokens); // the count starts again

            var surface = FfnSurface.Strongest(activity, 0.34, SyntheticQwen35.Layers, model.FfnGroupSize, SyntheticQwen35.FeedForward);
            for (var il = 0; il < SyntheticQwen35.Layers; il++)
            {
                var layer = activity.AsSpan(il * 3, 3).ToArray();
                var strongest = Array.IndexOf(layer, layer.Max());
                Assert.Equal(2, surface.Layer(il).Kept); // ceil(3 * 0.34)
                Assert.True(surface.Layer(il).Mask[strongest]);
            }
            return 0;
        });
    }

    [Fact]
    public void Groups_side_by_side_are_one_range_of_tiles_and_their_down_blocks_are_marked()
    {
        var surface = FfnSurface.Of([[true, true, false, true]], 512, 2048);
        var layer = surface.Layer(0);
        Assert.Equal(new[] { (0, 64), (96, 128) }, layer.Tiles);
        Assert.Equal(16, layer.Blocks.Length);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1 }, layer.Blocks);
        Assert.Equal(0.75, surface.Fraction);
    }

    // Random trits and a scale per block, packed as the model packs PTQ1_0.
    private static byte[] Matrix(int seed)
    {
        var rng = new Random(seed);
        var blocks = Cols / 128 * Rows;
        var data = new byte[blocks * Ptq1.BlockBytes];
        var trits = new sbyte[128];
        for (var i = 0; i < blocks; i++)
        {
            for (var j = 0; j < 128; j++) trits[j] = (sbyte)(rng.Next(3) - 1);
            Ptq1KernelTests.PackReference(trits, (float)(Half)(0.05f * (0.5f + (float)rng.NextDouble()))).CopyTo(data, i * Ptq1.BlockBytes);
        }
        return data;
    }

    // An activation with the given 128-wide blocks all zero, quantised and spread for the vector kernel.
    private static Ptq1PairInput Input(int seed, int[] zeroBlocks)
    {
        var rng = new Random(seed);
        var values = new float[Cols];
        for (var i = 0; i < Cols; i++) values[i] = zeroBlocks.Contains(i / 128) ? 0f : (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(Cols);
        q.Quantize(values);
        var x = new Ptq1PairInput(Cols);
        x.Set(q);
        return x;
    }
}
