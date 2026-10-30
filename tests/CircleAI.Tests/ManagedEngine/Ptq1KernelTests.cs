// Ptq1KernelTests.cs
//
// The PTQ1_0 kernels against their own scalar reference and against the fork's
// packing rules. Three claims:
//   1. Layout - weights packed by the fork's reference quantiser
//      (ggml-quants.c, quantize_row_ptq1_0_ref, ported below) decode back to the
//      same trits in the same order.
//   2. Agreement - every vector path this machine can run gives the scalar path's
//      answer to the last bit, decode and dot product alike.
//   3. Rounding - activations round half to even, as the fork's x86 path does.

using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class Ptq1KernelTests
{
    private static byte[] RandomRow(int blocks, int seed)
    {
        var rng = new Random(seed);
        var row = new byte[blocks * Ptq1.BlockBytes];
        rng.NextBytes(row);
        for (var i = 0; i < blocks; i++)
        {
            // A sane fp16 scale; random bytes there could be NaN or infinity.
            var scale = (Half)(0.001f + (float)rng.NextDouble() * 0.05f);
            BitConverter.TryWriteBytes(row.AsSpan(i * Ptq1.BlockBytes + 26, 2), scale);
        }
        return row;
    }

    private static float[] RandomActivations(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 8 - 4);
        return x;
    }

    // The fork's reference packer, ported line for line so the layout claim is
    // tested against the rule that wrote the model files, not against our decoder.
    private static byte[] PackReference(ReadOnlySpan<sbyte> trits, float scale)
    {
        var block = new byte[Ptq1.BlockBytes];
        var o = 0;
        var j = 0;
        foreach (var c in new[] { 32, 16, 8 })
        {
            for (; j + c <= 24; j += c)
            {
                for (var m = 0; m < c; m++)
                {
                    var q = 0;
                    for (var n = 0; n < 5; n++) q = q * 3 + trits[o + m + n * c] + 1;
                    block[j + m] = (byte)((q * 256 + 242) / 243);
                }
                o += 5 * c;
            }
        }
        for (var h = 0; h < 2; h++)
        {
            var q = 0;
            for (var m = 0; m < 4; m++) q = q * 3 + trits[o + h + m * 2] + 1;
            q *= 3;
            block[24 + h] = (byte)((q * 256 + 242) / 243);
        }
        BitConverter.TryWriteBytes(block.AsSpan(26, 2), (Half)scale);
        return block;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Weights_packed_by_the_fork_decode_back_in_order(int seed)
    {
        var rng = new Random(seed);
        var trits = new sbyte[Ptq1.BlockWeights];
        for (var i = 0; i < trits.Length; i++) trits[i] = (sbyte)(rng.Next(3) - 1);

        var block = PackReference(trits, 0.02f);
        var scalar = new sbyte[Ptq1.BlockWeights];
        var vector = new sbyte[Ptq1.BlockWeights];
        Ptq1.DecodeScalar(block, scalar);
        Ptq1.DecodeVector(block, vector);

        Assert.Equal(trits, scalar);
        Assert.Equal(trits, vector);
    }

    [Fact]
    public void Vector_decode_equals_scalar_decode_for_every_byte_value()
    {
        // Every byte value in every position, so no lane of the vector decode is untested.
        for (var v = 0; v < 256; v++)
        {
            var block = new byte[Ptq1.BlockBytes];
            for (var i = 0; i < 26; i++) block[i] = (byte)((v + i * 37) & 0xFF);
            var scalar = new sbyte[Ptq1.BlockWeights];
            var vector = new sbyte[Ptq1.BlockWeights];
            Ptq1.DecodeScalar(block, scalar);
            Ptq1.DecodeVector(block, vector);
            Assert.Equal(scalar, vector);
        }
    }

    [Theory]
    [InlineData(KernelPath.Table)]
    [InlineData(KernelPath.Vector128)]
    [InlineData(KernelPath.AdvSimd)]
    [InlineData(KernelPath.Dp)]
    [InlineData(KernelPath.Avx2)]
    public void Every_runnable_path_equals_the_scalar_path_exactly(KernelPath path)
    {
        if (!Ptq1.CanRun(path)) return; // That instruction set is not on this machine.

        const int cols = 2048; // the 2B's width
        var x = new Q8Activations(cols);
        for (var seed = 0; seed < 8; seed++)
        {
            x.Quantize(RandomActivations(cols, seed));
            var row = RandomRow(cols / Ptq1.BlockWeights, seed + 100);
            var expected = Ptq1.Dot(row, x, KernelPath.Scalar);
            var actual = Ptq1.Dot(row, x, path);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    [Fact]
    public void The_lookup_table_path_matches_the_scalar_path_within_float_rounding()
    {
        // Same sums, different association order: equal to within float rounding, which is
        // far inside the drift the reference shows between its own thread counts (S0.1).
        const int cols = 2048;
        var x = new Q8Activations(cols);
        for (var seed = 0; seed < 8; seed++)
        {
            x.Quantize(RandomActivations(cols, seed));
            var row = RandomRow(cols / Ptq1.BlockWeights, seed + 200);
            var expected = Ptq1.Dot(row, x, KernelPath.Scalar);
            var actual = Ptq1.Dot(row, x, KernelPath.Lut);
            Assert.InRange(MathF.Abs(actual - expected), 0f, 1e-4f * MathF.Max(1f, MathF.Abs(expected)));
        }
    }

    [Fact]
    public void The_lookup_table_matrix_product_equals_its_row_by_row_product()
    {
        // MatVec walks block by block across the rows for the cache; that must not change
        // a single sum. The tables are built for another input first, so a rebuild is
        // proven to leave nothing of the old one behind.
        const int cols = 2048, rows = 37;
        var blocks = cols / Ptq1.BlockWeights;
        var rowBytes = blocks * Ptq1.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 300 + r).CopyTo(matrix, r * rowBytes);
        var scales = Ptq1.BlockScales(matrix);

        var lut = new Ptq1Lut(cols);
        var x = new Q8Activations(cols);
        x.Quantize(RandomActivations(cols, 6));
        lut.Build(x);
        x.Quantize(RandomActivations(cols, 7));
        lut.Build(x);
        var output = new float[rows];
        lut.MatVec(matrix, scales, output);

        for (var r = 0; r < rows; r++)
        {
            var expected = lut.Dot(matrix.AsSpan(r * rowBytes, rowBytes), scales.AsSpan(r * blocks, blocks));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(output[r]));
            var scalar = Ptq1.Dot(matrix.AsSpan(r * rowBytes, rowBytes), x, KernelPath.Scalar);
            Assert.InRange(MathF.Abs(output[r] - scalar), 0f, 1e-4f * MathF.Max(1f, MathF.Abs(scalar)));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void The_lookup_table_product_split_across_threads_equals_the_one_thread_product(int threads)
    {
        // 1000 rows is no multiple of any chunk size, so the last chunk is a short one.
        const int cols = 2048, rows = 1000;
        var blocks = cols / Ptq1.BlockWeights;
        var rowBytes = blocks * Ptq1.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 500 + r).CopyTo(matrix, r * rowBytes);
        var scales = Ptq1.BlockScales(matrix);

        var lut = new Ptq1Lut(cols);
        var x = new Q8Activations(cols);
        x.Quantize(RandomActivations(cols, 8));
        lut.Build(x);
        var single = new float[rows];
        lut.MatVec(matrix, scales, single);

        using var pool = new WorkerPool(threads);
        for (var round = 0; round < 3; round++)
        {
            var split = new float[rows];
            lut.MatVec(matrix, scales, split, pool);
            for (var r = 0; r < rows; r++)
                Assert.Equal(BitConverter.SingleToInt32Bits(single[r]), BitConverter.SingleToInt32Bits(split[r]));
        }
    }

    [Fact]
    public void Activations_round_half_to_even_like_the_x86_reference()
    {
        // amax 127 makes id exactly 1, so each value lands on its own midpoint.
        var x = new float[Q8Activations.BlockSize];
        x[0] = 127f;
        x[1] = 2.5f;
        x[2] = 3.5f;
        x[3] = -2.5f;
        x[4] = 0.5f;
        var q = new Q8Activations(Q8Activations.BlockSize);
        q.Quantize(x);

        Assert.Equal(127, q.Quants[0]);
        Assert.Equal(2, q.Quants[1]);
        Assert.Equal(4, q.Quants[2]);
        Assert.Equal(-2, q.Quants[3]);
        Assert.Equal(0, q.Quants[4]);
        Assert.Equal((float)(Half)1f, q.Scales[0]);
    }

    [Fact]
    public void The_best_path_is_one_this_machine_can_run()
    {
        Assert.True(Ptq1.CanRun(Ptq1.Best));
    }
}
