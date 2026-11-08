// UntiedHeadTests.cs
//
// What the 27B keeps that the 2B does not: an output head that is a table of its own (output.weight, untied) and
// DeltaNet gates (ssm_beta, ssm_alpha) in BF16. An untied head holding the embedding's rows scores as the tied head, to
// the bit, in both packings and paired; one with rows of its own scores with them; its store on flash holds its own
// rows, and a model reading it from there computes what the copy did. A BF16 value is rounded as the fork rounds it,
// and a model with BF16 gates runs close to the same model's F32 gates.

using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

public sealed class UntiedHeadTests
{
    [Theory]
    [InlineData(GgmlType.PTQ1_0, false)]
    [InlineData(GgmlType.PTQ1_0, true)]
    [InlineData(GgmlType.PQ2_0, false)]
    public void An_untied_head_holding_the_embeddings_rows_scores_as_the_tied_head(GgmlType packing, bool paired)
    {
        With(SyntheticQwen35.Build(packing, 41), tiedFile =>
            With(SyntheticQwen35.Build(packing, 41, head: SyntheticQwen35.Head.EmbeddingsCopy), untiedFile =>
            {
                var tied = Qwen35Model.Load(tiedFile, null, headFromStore: false, ffnFromStore: false, pairedOverride: paired);
                var untied = Qwen35Model.Load(untiedFile, null, headFromStore: false, ffnFromStore: false, pairedOverride: paired);
                Assert.False(tied.UntiedHead);
                Assert.True(untied.UntiedHead);
                Same(tied, untied);
            }));
    }

    // The trunk is the tied model's (the head is written last), so a head with rows of its own is all that differs.
    [Fact]
    public void An_untied_head_with_rows_of_its_own_scores_with_them()
    {
        With(SyntheticQwen35.Build(GgmlType.PTQ1_0, 43), tiedFile =>
            With(SyntheticQwen35.Build(GgmlType.PTQ1_0, 43, head: SyntheticQwen35.Head.Own), untiedFile =>
            {
                var tied = Qwen35Model.Load(tiedFile, null, headFromStore: false, ffnFromStore: false, pairedOverride: false);
                var untied = Qwen35Model.Load(untiedFile, null, headFromStore: false, ffnFromStore: false, pairedOverride: false);
                var x = new float[SyntheticQwen35.Vocabulary];
                var y = new float[SyntheticQwen35.Vocabulary];
                tied.Forward(5, tied.NewState(16), x);
                untied.Forward(5, untied.NewState(16), y);
                Assert.All(y, v => Assert.True(float.IsFinite(v)));
                Assert.NotEqual(x, y);
            }));
    }

    [Fact]
    public void An_untied_heads_store_holds_its_own_rows_and_a_model_reading_it_from_flash_computes_what_the_copy_did()
    {
        var directory = Directory.CreateTempSubdirectory("untied-head-").FullName;
        try
        {
            With(SyntheticQwen35.Build(GgmlType.PTQ1_0, 47, head: SyntheticQwen35.Head.Own), file =>
            {
                Assert.Null(HeadStore.Unreadable(file));
                var heads = HeadStore.Write(file, Path.Combine(directory, "model.head"), t => Script.Latin, chunkRows: 16, layout: HeadLayout.Paired);
                Assert.Equal(0, heads.Verify(file));
                var ffn = FfnStore.Write(file, Path.Combine(directory, "model.ffn"));
                var copy = Qwen35Model.Load(file, null, headFromStore: false, ffnFromStore: false, pairedOverride: true);
                var served = Qwen35Model.Load(file, null, headFromStore: true, ffnFromStore: true, pairedOverride: true);
                using var surface = heads.Load(heads.Groups, channels: 2);
                using var warm = ffn.Load(channels: 2);
                served.Head = surface;
                served.ArmFfn(warm);
                Same(copy, served);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ggml_compute_fp32_to_bf16: to nearest, ties to even; infinity stays infinity; a NaN stays a NaN, made quiet.
    [Theory]
    [InlineData(0x3F80_0000u, (ushort)0x3F80)] // 1
    [InlineData(0x3F80_8000u, (ushort)0x3F80)] // 1 + 2^-8: a tie, to the even 0x3F80
    [InlineData(0x3F81_8000u, (ushort)0x3F82)] // a tie above an odd one, up to the even 0x3F82
    [InlineData(0x3F80_8001u, (ushort)0x3F81)] // just past a tie: up
    [InlineData(0x3F80_7FFFu, (ushort)0x3F80)] // just short of a tie: down
    [InlineData(0xBF81_8000u, (ushort)0xBF82)] // negative, the same rule
    [InlineData(0x7F80_0000u, (ushort)0x7F80)] // infinity
    [InlineData(0x7F80_0001u, (ushort)0x7FC0)] // a signalling NaN comes back quiet
    [InlineData(0x7FC0_0000u, (ushort)0x7FC0)] // a quiet NaN
    public void A_value_is_rounded_to_bf16_as_the_fork_rounds_it(uint bits, ushort expected)
    {
        Assert.Equal(expected, Bf16.Round(BitConverter.UInt32BitsToSingle(bits)));
    }

    [Fact]
    public void A_bf16_value_widens_exactly_and_rounds_back_to_itself()
    {
        for (var bits = 0; bits <= ushort.MaxValue; bits += 7)
        {
            var value = Bf16.Widen((ushort)bits);
            if (float.IsNaN(value)) continue;
            Assert.Equal((ushort)bits, Bf16.Round(value));
        }
    }

    // Each product of two BF16 values is exact in single precision, so summing them in double in order is the fork's
    // scalar loop to the bit.
    [Fact]
    public void A_bf16_product_sums_exact_single_precision_products_in_double()
    {
        var rng = new Random(3);
        const int rows = 5, n = 300;
        var weights = new ushort[rows * n];
        for (var i = 0; i < weights.Length; i++) weights[i] = Bf16.Round((float)(rng.NextDouble() * 2 - 1));
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 4 - 2);
        var rounded = new float[n];
        Bf16.Round(x, rounded);
        var y = new float[rows];

        Bf16.MatVec(weights, rounded, y);

        for (var r = 0; r < rows; r++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++) sum += (double)Bf16.Widen(weights[r * n + i]) * Bf16.Widen(Bf16.Round(x[i]));
            Assert.Equal((float)sum, y[r]);
        }
    }

    // The gates' weights rounded to BF16 and their input rounded too: every logit within 2% of the F32 gates' largest.
    [Fact]
    public void A_model_with_bf16_gates_runs_close_to_the_same_models_f32_gates()
    {
        With(SyntheticQwen35.Build(GgmlType.PTQ1_0, 53), f32File =>
            With(SyntheticQwen35.Build(GgmlType.PTQ1_0, 53, bf16Gates: true), bf16File =>
            {
                var f32 = Qwen35Model.Load(f32File, null, headFromStore: false, ffnFromStore: false, pairedOverride: false);
                var bf16 = Qwen35Model.Load(bf16File, null, headFromStore: false, ffnFromStore: false, pairedOverride: false);
                var a = f32.NewState(16);
                var b = bf16.NewState(16);
                var x = new float[SyntheticQwen35.Vocabulary];
                var y = new float[SyntheticQwen35.Vocabulary];
                foreach (var token in new[] { 4, 19, 7 })
                {
                    f32.Forward(token, a, x);
                    bf16.Forward(token, b, y);
                    var top = x.Max(MathF.Abs);
                    for (var i = 0; i < x.Length; i++)
                    {
                        Assert.True(float.IsFinite(y[i]));
                        Assert.True(MathF.Abs(x[i] - y[i]) <= 0.02f * top, $"token {token}, logit {i}: {y[i]} against {x[i]}");
                    }
                }
            }));
    }

    // A token at a time, then a prompt's batch of eight: every logit the same, to the bit.
    private static void Same(Qwen35Model a, Qwen35Model b)
    {
        var sa = a.NewState(32);
        var sb = b.NewState(32);
        var x = new float[SyntheticQwen35.Vocabulary];
        var y = new float[SyntheticQwen35.Vocabulary];
        foreach (var token in new[] { 5, 9, 30, 2, 17 })
        {
            a.Forward(token, sa, x);
            b.Forward(token, sb, y);
            Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
        }
        var tokens = new[] { 3, 8, 12, 20, 6, 11, 14, 27 };
        a.ForwardBatch(tokens, sa, [null, null, null, null, null, null, null, x]);
        b.ForwardBatch(tokens, sb, [null, null, null, null, null, null, null, y]);
        Assert.Equal(x.Select(BitConverter.SingleToInt32Bits), y.Select(BitConverter.SingleToInt32Bits));
    }

    private static void With(byte[] model, Action<GgufFile> use) =>
        GgufBuilder.WithFile(model, path =>
        {
            using var file = GgufFile.Open(path);
            use(file);
            return 0;
        });
}
