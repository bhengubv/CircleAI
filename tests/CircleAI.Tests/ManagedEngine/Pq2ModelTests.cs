// Pq2ModelTests.cs
//
// The qwen35 model reading PQ2_0, on a synthetic model written in both packings with the same trits
// and scales (SyntheticQwen35), so every place the packing reaches is exercised without the real
// files: the embedding row, every product, the tied head, and the feed-forward's 384-wide input,
// which is not whole Q8_K blocks and takes the fork's Q8_0 fallback.
//   - The two packings hold the same weights; they differ only in how the activations are
//     quantised (Q8_0 for PTQ1_0, Q8_K for PQ2_0), so their logits agree closely and their
//     confident picks are the same tokens.
//   - A PQ2_0 product sums exact integers, so the model gives the same logits to the bit on one
//     thread and on several.
//   - A file that mixes packings is refused with its name: one packing throughout is read.
// The real PQ2_0 files are held to the pinned fork by the parity tests (Bonsai2ParityTests).

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class Pq2ModelTests
{
    private static readonly int[] Tokens = [1, 5, 9, 3, 30, 12];

    private static List<float[]> Run(byte[] model, WorkerPool? pool, out GgmlType packing)
    {
        var steps = new List<float[]>();
        GgmlType read = default;
        GgufBuilder.WithFile(model, path =>
        {
            using var file = GgufFile.Open(path);
            var m = Qwen35Model.Load(file, pool);
            read = m.Packing;
            var state = m.NewState(16);
            foreach (var token in Tokens)
            {
                var logits = new float[m.Config.Vocabulary];
                m.Forward(token, state, logits);
                steps.Add(logits);
            }
            return 0;
        });
        packing = read;
        return steps;
    }

    [Fact]
    public void A_PQ2_0_model_and_its_PTQ1_0_twin_give_the_same_answers()
    {
        var pq2 = Run(SyntheticQwen35.Build(GgmlType.PQ2_0), null, out var pq2Packing);
        var ptq1 = Run(SyntheticQwen35.Build(GgmlType.PTQ1_0), null, out var ptq1Packing);
        Assert.Equal(GgmlType.PQ2_0, pq2Packing);
        Assert.Equal(GgmlType.PTQ1_0, ptq1Packing);

        var confident = 0;
        for (var s = 0; s < Tokens.Length; s++)
        {
            var a = pq2[s];
            var b = ptq1[s];
            Assert.All(a, v => Assert.True(float.IsFinite(v)));

            // Close: the cosine of the two logit vectors, and every logit within a small share of their spread.
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            Assert.True(dot / Math.Sqrt(na * nb) > 0.999, $"step {s}: cosine {dot / Math.Sqrt(na * nb)}");
            var spread = a.Max() - a.Min();
            for (var i = 0; i < a.Length; i++) Assert.InRange(MathF.Abs(a[i] - b[i]), 0f, 0.05f * spread);

            // The same pick wherever the PTQ1_0 twin was not torn between two tokens.
            var order = Enumerable.Range(0, b.Length).OrderByDescending(i => b[i]).ToArray();
            if (b[order[0]] - b[order[1]] < 0.1f * spread) continue;
            Assert.Equal(order[0], Array.IndexOf(a, a.Max()));
            confident++;
        }
        Assert.True(confident > 0, "no step had a clear top token to compare");
    }

    [Fact]
    public void A_PQ2_0_model_gives_the_same_logits_to_the_bit_on_any_number_of_threads()
    {
        var model = SyntheticQwen35.Build(GgmlType.PQ2_0, seed: 11);
        var one = Run(model, null, out _);
        using var pool = new WorkerPool(3);
        var three = Run(model, pool, out _);
        for (var s = 0; s < Tokens.Length; s++)
            for (var i = 0; i < one[s].Length; i++)
                Assert.Equal(BitConverter.SingleToInt32Bits(one[s][i]), BitConverter.SingleToInt32Bits(three[s][i]));
    }

    [Theory]
    [InlineData(GgmlType.PQ2_0)]
    [InlineData(GgmlType.PTQ1_0)]
    public void A_file_that_mixes_packings_is_refused_by_name(GgmlType packing)
    {
        var model = SyntheticQwen35.Build(packing, mixedPacking: true);
        var error = Assert.Throws<NotSupportedException>(() => Run(model, null, out _));
        Assert.Contains("blk.1.", error.Message);
        Assert.Contains("one packing throughout", error.Message);
    }

    [Fact]
    public void The_packing_is_read_off_the_embedding_table()
    {
        GgufBuilder.Read(SyntheticQwen35.Build(GgmlType.PQ2_0), file => Assert.Equal(GgmlType.PQ2_0, Qwen35Model.TernaryPackingOf(file)));
        GgufBuilder.Read(SyntheticQwen35.Build(GgmlType.PTQ1_0), file => Assert.Equal(GgmlType.PTQ1_0, Qwen35Model.TernaryPackingOf(file)));
    }

    [Theory]
    [InlineData(GgmlType.PQ2_0)]
    [InlineData(GgmlType.PTQ1_0)]
    public void The_brain_factory_hands_a_file_in_either_packing_to_the_CSharp_engine(GgmlType packing)
    {
        // The factory's own reading of a file no catalogue row names (every build site asks it).
        GgufBuilder.WithFile(SyntheticQwen35.Build(packing), path =>
        {
            Assert.Equal(CircleAI.Core.ModelEngine.Managed, CircleAI.Inference.BrainFactory.EngineOf(path));
            return 0;
        });
    }
}
