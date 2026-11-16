// Q4KernelTests.cs
//
// Q4, the sixteen-level row the indexed mind keeps ffn_down's neurons in (CachedIndex). Three claims:
//   1. Fidelity - a row that is not ternary (a column of ffn_down with the file's fold undone: sums of signed trits) comes
//      back pointing the way it went in, where the ternary block it replaces there loses about a quarter of it.
//   2. Layout - the largest weight decodes to itself and zero to zero, whatever the row's size.
//   3. Agreement - the sum the pieces are projected with gives its own scalar formula's floats, to the bit.

using System.Globalization;
using CircleAI.Inference.Managed.Kernels;
using Xunit;

namespace CircleAI.Tests;

public sealed class Q4KernelTests
{
    // A column of ffn_down with the fold undone, as the index meets it: each weight a sum of 128 signed trits, scaled.
    private static float[] Unfolded(int seed, float scale = 0.02f)
    {
        var rng = new Random(seed);
        var weights = new float[Q4.BlockWeights];
        for (var i = 0; i < weights.Length; i++)
        {
            var sum = 0;
            for (var j = 0; j < 128; j++) sum += rng.Next(3) - 1;
            weights[i] = sum * scale / MathF.Sqrt(128);
        }
        return weights;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            aa += (double)a[i] * a[i];
            bb += (double)b[i] * b[i];
        }
        return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb);
    }

    [Fact]
    public void A_row_that_is_not_ternary_keeps_its_direction_at_sixteen_levels_where_three_lose_a_quarter()
    {
        const int rows = 200;
        double worst = 1, ternary = 0;
        var block = new byte[Q4.BlockBytes];
        var back = new float[Q4.BlockWeights];
        var three = new byte[Ptq1.BlockBytes];
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var seed = 0; seed < rows; seed++)
        {
            var weights = Unfolded(seed);
            Q4.Pack(weights, block);
            Q4.Unpack(block, back);
            worst = Math.Min(worst, Cosine(weights, back));

            Ptq1.Pack(weights, three);
            Ptq1.DecodeScalar(three, trits);
            var scale = Ptq1.Scale(three);
            ternary += Cosine(weights, [.. trits.Select(t => t * scale)]) / rows;
        }

        Assert.True(worst >= 0.98, string.Create(CultureInfo.InvariantCulture,
            $"the worst of {rows} rows came back {worst:F4} like itself at sixteen levels (1 is the same); the ternary block kept {ternary:F4} on average"));
        Assert.True(ternary < 0.9, string.Create(CultureInfo.InvariantCulture,
            $"setup: the ternary block kept {ternary:F4} of these rows, so they do not show what sixteen levels are for"));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.003f)]
    [InlineData(250f)]
    public void The_largest_weight_comes_back_as_itself_and_zero_as_zero(float size)
    {
        var weights = new float[Q4.BlockWeights];
        weights[5] = size;
        weights[77] = -size;
        weights[100] = size / 7;
        var block = new byte[Q4.BlockBytes];
        var back = new float[Q4.BlockWeights];
        Q4.Pack(weights, block);
        Q4.Unpack(block, back);

        var half = (float)(Half)size;   // the scale is kept as a half
        Assert.Equal(half, Q4.Scale(block));
        Assert.InRange(MathF.Abs(back[5] - half), 0f, half * 1e-6f);
        Assert.InRange(MathF.Abs(back[77] + half), 0f, half * 1e-6f);
        Assert.InRange(MathF.Abs(back[100] - half / 7), 0f, half * 1e-6f);
        for (var i = 0; i < back.Length; i++)
            if (i is not (5 or 77 or 100)) Assert.Equal(0f, back[i]);
    }

    [Fact]
    public void A_row_of_zeros_packs_to_zeros()
    {
        var block = new byte[Q4.BlockBytes];
        block.AsSpan().Fill(0xFF);
        var back = new float[Q4.BlockWeights];
        Q4.Pack(new float[Q4.BlockWeights], block);
        Q4.Unpack(block, back);
        Assert.Equal(0f, Q4.Scale(block));
        Assert.All(back, w => Assert.Equal(0f, w));
    }

    [Fact]
    public unsafe void The_sum_of_a_scaled_row_is_its_scalar_formula_to_the_bit()
    {
        var rng = new Random(11);
        var block = new byte[Q4.BlockBytes];
        for (var seed = 0; seed < 20; seed++)
        {
            Q4.Pack(Unfolded(100 + seed), block);
            var a = (float)(rng.NextDouble() * 6 - 3);
            var start = new float[Q4.BlockWeights];
            for (var i = 0; i < start.Length; i++) start[i] = (float)(rng.NextDouble() * 2 - 1);

            // The formula: output[j] += (a * scale / 7) * level, a level being the weight's half byte less 8.
            var c = a * (Q4.Scale(block) / Q4.Levels);
            var expected = (float[])start.Clone();
            for (var j = 0; j < Q4.BlockWeights / 2; j++)
            {
                expected[j] += c * ((block[j] & 15) - 8);
                expected[Q4.BlockWeights / 2 + j] += c * ((block[j] >> 4) - 8);
            }

            var got = (float[])start.Clone();
            fixed (byte* b = block)
            fixed (float* o = got)
                Q4.AddScaled(b, a, o);
            for (var i = 0; i < got.Length; i++)
                Assert.True(BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(got[i]),
                    string.Create(CultureInfo.InvariantCulture, $"row {seed}, weight {i}: the sum gave {got[i]:R}, the formula {expected[i]:R}"));
        }
    }
}
