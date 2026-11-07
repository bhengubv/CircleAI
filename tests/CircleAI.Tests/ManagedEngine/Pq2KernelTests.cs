// Pq2KernelTests.cs
//
// The PQ2_0 kernels against their own scalar reference and against the fork's packing and
// quantising rules. Five claims:
//   1. Layout - weights packed by the fork's reference quantiser (ggml-quants.c,
//      quantize_row_pq2_0_ref, ported below) decode back to the same values in the same order.
//   2. Agreement - every path this machine can run gives the scalar path's answer to the last bit,
//      decode and dot product alike, with Q8_K and with Q8_0 activations. That includes the
//      lookup tables: unlike PTQ1_0's they hold exact integer sums.
//   3. Meaning - the scalar product is the sum of the dequantised weights times the dequantised
//      activations, (code - 1) * d_w * q * d_y, within float rounding.
//   4. Threads - a product shared across threads, and tables built across them, equal the
//      one-thread result to the last bit.
//   5. Rounding - Q8_K activations are the fork's reference quantiser: the largest magnitude lands
//      on -127, d = 1 / iscale, and halves round to even.

using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class Pq2KernelTests
{
    internal static byte[] RandomRow(int blocks, int seed)
    {
        var rng = new Random(seed);
        var row = new byte[blocks * Pq2.BlockBytes];
        rng.NextBytes(row);
        for (var i = 0; i < blocks; i++)
        {
            // A sane fp16 scale; random bytes there could be NaN or infinity.
            var scale = (Half)(0.001f + (float)rng.NextDouble() * 0.05f);
            BitConverter.TryWriteBytes(row.AsSpan(i * Pq2.BlockBytes, 2), scale);
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

    /// <summary>The fork's reference packer (quantize_row_pq2_0_ref), ported line for line.</summary>
    internal static byte[] PackReference(ReadOnlySpan<float> x)
    {
        var block = new byte[Pq2.BlockBytes];
        var amax = 0f;
        for (var j = 0; j < Pq2.BlockWeights; j++) amax = MathF.Max(amax, MathF.Abs(x[j]));
        var d = amax;
        var id = d > 0f ? 1f / d : 0f;
        BitConverter.TryWriteBytes(block.AsSpan(0, 2), (Half)d);
        for (var j = 0; j < Pq2.BlockWeights; j++)
        {
            var q = (int)MathF.Round(x[j] * id, MidpointRounding.AwayFromZero) + 1; // C's roundf
            q = Math.Clamp(q, 0, 3);
            block[Pq2.CodesOffset + j / 4] |= (byte)(q << (j % 4 * 2));
        }
        return block;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Weights_packed_by_the_fork_decode_back_in_order(int seed)
    {
        var rng = new Random(seed);
        var ternary = new sbyte[Pq2.BlockWeights];
        var x = new float[Pq2.BlockWeights];
        const float scale = 0.0234375f; // exact in fp16
        for (var i = 0; i < ternary.Length; i++)
        {
            ternary[i] = (sbyte)(rng.Next(3) - 1);
            x[i] = ternary[i] * scale;
        }
        ternary[0] = 1; // so the block's largest magnitude is the scale
        x[0] = scale;
        var block = PackReference(x);
        var scalar = new sbyte[Pq2.BlockWeights];
        var vector = new sbyte[Pq2.BlockWeights];
        Pq2.DecodeScalar(block, scalar);
        Pq2.DecodeVector(block, vector);

        Assert.Equal(ternary, scalar);
        Assert.Equal(ternary, vector);
        Assert.Equal(scale, Pq2.Scale(block));
    }

    [Fact]
    public void The_codes_sit_lowest_pair_first_from_byte_two()
    {
        // Weight j is bits 2*(j%4) of byte 2 + j/4: code 3 (+2) at weight 5 is byte 3's bits 2-3.
        var block = new byte[Pq2.BlockBytes];
        for (var i = Pq2.CodesOffset; i < Pq2.BlockBytes; i++) block[i] = 0b01_01_01_01; // every code 1: zero
        block[3] = 0b01_01_11_01;
        var values = new sbyte[Pq2.BlockWeights];
        Pq2.DecodeScalar(block, values);
        for (var j = 0; j < Pq2.BlockWeights; j++) Assert.Equal(j == 5 ? 2 : 0, values[j]);
    }

    [Fact]
    public void Vector_decode_equals_scalar_decode_for_every_byte_value()
    {
        // Every byte value in every position, so no lane of the vector decode is untested.
        for (var v = 0; v < 256; v++)
        {
            var block = new byte[Pq2.BlockBytes];
            for (var i = 0; i < Pq2.BlockBytes; i++) block[i] = (byte)((v + i * 37) & 0xFF);
            var scalar = new sbyte[Pq2.BlockWeights];
            var vector = new sbyte[Pq2.BlockWeights];
            Pq2.DecodeScalar(block, scalar);
            Pq2.DecodeVector(block, vector);
            Assert.Equal(scalar, vector);
        }
    }

    [Theory]
    [InlineData(KernelPath.Table)]
    [InlineData(KernelPath.Lut)]
    [InlineData(KernelPath.Vector128)]
    [InlineData(KernelPath.AdvSimd)]
    [InlineData(KernelPath.Dp)]
    [InlineData(KernelPath.Avx2)]
    [InlineData(KernelPath.Avx512)]
    [InlineData(KernelPath.AvxVnni)]
    public void Every_runnable_path_equals_the_scalar_path_exactly(KernelPath path)
    {
        if (!Pq2.CanRun(path)) return; // That instruction set is not on this machine.

        const int cols = 2560; // the 4B's width: ten Q8_K blocks
        var x = new Q8KActivations(cols);
        for (var seed = 0; seed < 8; seed++)
        {
            x.Quantize(RandomActivations(cols, seed));
            var row = RandomRow(cols / Pq2.BlockWeights, seed + 100);
            var expected = Pq2.Dot(row, x, KernelPath.Scalar);
            var actual = Pq2.Dot(row, x, path);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    [Theory]
    [InlineData(KernelPath.Table)]
    [InlineData(KernelPath.Lut)]
    [InlineData(KernelPath.Vector128)]
    [InlineData(KernelPath.AdvSimd)]
    [InlineData(KernelPath.Dp)]
    [InlineData(KernelPath.Avx2)]
    [InlineData(KernelPath.Avx512)]
    [InlineData(KernelPath.AvxVnni)]
    public void With_Q8_0_activations_every_runnable_path_equals_the_scalar_path_exactly(KernelPath path)
    {
        if (!Pq2.CanRun(path)) return;

        const int cols = 384; // three blocks: not whole Q8_K blocks, so the fork takes Q8_0 here
        var x = new Q8Activations(cols);
        for (var seed = 0; seed < 8; seed++)
        {
            x.Quantize(RandomActivations(cols, seed));
            var row = RandomRow(cols / Pq2.BlockWeights, seed + 400);
            var expected = Pq2.Dot(row, x, KernelPath.Scalar);
            var actual = Pq2.Dot(row, x, path);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    [Fact]
    public void The_product_is_the_dequantised_weights_times_the_dequantised_activations()
    {
        const int cols = 1024;
        var x = new Q8KActivations(cols);
        x.Quantize(RandomActivations(cols, 21));
        var row = RandomRow(cols / Pq2.BlockWeights, 22);
        var values = new sbyte[Pq2.BlockWeights];

        double expected = 0;
        for (var i = 0; i < cols / Pq2.BlockWeights; i++)
        {
            var block = row.AsSpan(i * Pq2.BlockBytes, Pq2.BlockBytes);
            Pq2.DecodeScalar(block, values);
            for (var j = 0; j < Pq2.BlockWeights; j++)
                expected += (double)values[j] * Pq2.Scale(block) * x.Quants[i * Pq2.BlockWeights + j] * x.Scales[(i * Pq2.BlockWeights + j) / Q8KActivations.BlockSize];
        }
        var actual = Pq2.Dot(row, x, KernelPath.Scalar);
        Assert.InRange(Math.Abs(actual - expected), 0, 1e-5 * Math.Max(1, Math.Abs(expected)));
    }

    [Fact]
    public void The_lookup_table_matrix_product_equals_its_row_by_row_product()
    {
        // MatVec walks block by block across the rows for the cache; that must not change a single
        // sum. The tables are built for another input first, so a rebuild is proven to leave nothing
        // of the old one behind - nor of an old Q8_0 build's per-sub-block scaling.
        const int cols = 2560, rows = 37;
        var blocks = cols / Pq2.BlockWeights;
        var rowBytes = blocks * Pq2.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 300 + r).CopyTo(matrix, r * rowBytes);
        var scales = Pq2.BlockScales(matrix);

        var lut = new Pq2Lut(cols);
        var older = new Q8Activations(cols);
        older.Quantize(RandomActivations(cols, 5));
        lut.Build(older);
        var x = new Q8KActivations(cols);
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
            var scalar = Pq2.Dot(matrix.AsSpan(r * rowBytes, rowBytes), x, KernelPath.Scalar);
            Assert.Equal(BitConverter.SingleToInt32Bits(scalar), BitConverter.SingleToInt32Bits(output[r]));
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
        var blocks = cols / Pq2.BlockWeights;
        var rowBytes = blocks * Pq2.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 500 + r).CopyTo(matrix, r * rowBytes);
        var scales = Pq2.BlockScales(matrix);

        var lut = new Pq2Lut(cols);
        var x = new Q8KActivations(cols);
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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Tables_built_across_threads_equal_the_one_thread_tables(int threads)
    {
        // 9216 is the 4B's widest input (72 blocks); the tables are rebuilt over an older input so
        // nothing of it may survive. The Q8_0 form is built across threads too.
        const int cols = 9216, rows = 300;
        var blocks = cols / Pq2.BlockWeights;
        var rowBytes = blocks * Pq2.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 900 + r).CopyTo(matrix, r * rowBytes);
        var scales = Pq2.BlockScales(matrix);
        var x = new Q8KActivations(cols);
        x.Quantize(RandomActivations(cols, 11));
        var x0 = new Q8Activations(cols);
        x0.Quantize(RandomActivations(cols, 13));

        var one = new Pq2Lut(cols);
        one.Build(x);
        var expected = new float[rows];
        one.MatVec(matrix, scales, expected);
        one.Build(x0);
        var expected0 = new float[rows];
        one.MatVec(matrix, scales, expected0);

        using var pool = new WorkerPool(threads);
        var shared = new Pq2Lut(cols);
        var older = new Q8KActivations(cols);
        older.Quantize(RandomActivations(cols, 12));
        shared.Build(older, pool);
        for (var round = 0; round < 3; round++)
        {
            shared.Build(x, pool);
            var actual = new float[rows];
            shared.MatVec(matrix, scales, actual);
            for (var r = 0; r < rows; r++)
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[r]), BitConverter.SingleToInt32Bits(actual[r]));

            shared.Build(x0, pool);
            shared.MatVec(matrix, scales, actual, pool);
            for (var r = 0; r < rows; r++)
                Assert.Equal(BitConverter.SingleToInt32Bits(expected0[r]), BitConverter.SingleToInt32Bits(actual[r]));
        }
    }

    [Theory]
    [InlineData(2560, 1)]
    [InlineData(2560, 3)]
    [InlineData(384, 2)] // three blocks: the Q8_0 fallback
    public void Several_inputs_at_once_give_each_its_own_product_to_the_bit(int cols, int threads)
    {
        // The verification pass of speculative decoding: one walk of the weights for every input.
        const int rows = 203, inputs = 3;
        var blocks = cols / Pq2.BlockWeights;
        var rowBytes = blocks * Pq2.BlockBytes;
        var matrix = new byte[rows * rowBytes];
        for (var r = 0; r < rows; r++) RandomRow(blocks, 700 + r).CopyTo(matrix, r * rowBytes);
        var scales = Pq2.BlockScales(matrix);

        var batch = new Pq2BatchLut(cols, inputs + 1);
        var outputs = new float[inputs][];
        var expected = new float[inputs][];
        for (var i = 0; i < inputs; i++)
        {
            var x = RandomActivations(cols, 40 + i);
            batch.Quantize(i, x);
            outputs[i] = new float[rows];
            expected[i] = new float[rows];
            var one = new Pq2Lut(cols);
            if (cols % Q8KActivations.BlockSize == 0)
            {
                var q = new Q8KActivations(cols);
                q.Quantize(x);
                one.Build(q);
            }
            else
            {
                var q = new Q8Activations(cols);
                q.Quantize(x);
                one.Build(q);
            }
            one.MatVec(matrix, scales, expected[i]);
        }

        using var pool = new WorkerPool(threads);
        batch.Build(inputs, pool);
        batch.MatVec(matrix, scales, inputs, outputs, pool);
        for (var i = 0; i < inputs; i++)
            for (var r = 0; r < rows; r++)
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[i][r]), BitConverter.SingleToInt32Bits(outputs[i][r]));
    }

    [Fact]
    public void Q8K_activations_follow_the_forks_reference_quantiser()
    {
        // The first value of largest magnitude is -127, so iscale = -127 / -127 = 1 exactly: it lands
        // on -127, as the largest always does, and every other value on its own midpoint, where
        // halves go to even. d = 1 / iscale = 1. The +127 after it ties the magnitude and does not
        // take over the sign: the comparison is strict.
        var x = new float[Q8KActivations.BlockSize];
        x[0] = -127f;
        x[1] = 2.5f;
        x[2] = 3.5f;
        x[3] = -2.5f;
        x[4] = 0.5f;
        x[5] = 127f;
        var q = new Q8KActivations(Q8KActivations.BlockSize * 2);
        var both = new float[Q8KActivations.BlockSize * 2];
        x.CopyTo(both, 0); // the second block stays zero
        q.Quantize(both);

        Assert.Equal(-127, q.Quants[0]);
        Assert.Equal(2, q.Quants[1]);
        Assert.Equal(4, q.Quants[2]);
        Assert.Equal(-2, q.Quants[3]);
        Assert.Equal(0, q.Quants[4]);
        Assert.Equal(127, q.Quants[5]);
        Assert.Equal(1f, q.Scales[0]);
        Assert.Equal(0f, q.Scales[1]);
        Assert.All(q.Quants.AsSpan(Q8KActivations.BlockSize).ToArray(), v => Assert.Equal(0, v));
    }

    [Fact]
    public void A_block_whose_largest_value_is_positive_has_a_negative_scale()
    {
        var x = new float[Q8KActivations.BlockSize];
        for (var i = 0; i < x.Length; i++) x[i] = (i % 7 - 3) * 0.25f;
        x[10] = 2f;
        var q = new Q8KActivations(Q8KActivations.BlockSize);
        q.Quantize(x);

        var iscale = -127f / 2f;
        Assert.Equal(1f / iscale, q.Scales[0]);
        Assert.Equal(-127, q.Quants[10]);
        for (var i = 0; i < x.Length; i++) Assert.InRange(MathF.Abs(q.Quants[i] * q.Scales[0] - x[i]), 0f, MathF.Abs(q.Scales[0]) / 2 + 1e-6f);
    }

    [Fact]
    public void The_best_path_is_one_this_machine_can_run()
    {
        Assert.True(Pq2.CanRun(Pq2.Best));
    }
}
