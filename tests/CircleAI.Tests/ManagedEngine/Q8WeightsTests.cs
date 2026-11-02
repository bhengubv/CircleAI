// Q8WeightsTests.cs
//
// The Q8_0 weight kernel the MTP head runs on: its vector path equals its scalar reference to the
// last bit (exact int32 sums, the fork's float order); the product shared across threads equals
// the single-thread product; and the product is the dequantised weights times the dequantised
// activations.

using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class Q8WeightsTests
{
    private static byte[] RandomRows(int rows, int cols, int seed)
    {
        var rng = new Random(seed);
        var blocks = cols / Q8Weights.BlockWeights * rows;
        var data = new byte[blocks * Q8Weights.BlockBytes];
        rng.NextBytes(data);
        for (var i = 0; i < blocks; i++)
            BitConverter.TryWriteBytes(data.AsSpan(i * Q8Weights.BlockBytes, 2), (Half)(0.0005f + (float)rng.NextDouble() * 0.002f));
        return data;
    }

    private static Q8Activations Activations(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(rng.NextDouble() * 6 - 3);
        var q = new Q8Activations(n);
        q.Quantize(x);
        return q;
    }

    [Fact]
    public void The_vector_product_equals_the_scalar_product_to_the_bit()
    {
        const int cols = 5120; // the 4B MTP head's eh_proj input
        for (var seed = 0; seed < 6; seed++)
        {
            var row = RandomRows(1, cols, seed);
            var x = Activations(cols, seed + 50);
            var expected = Q8Weights.DotScalar(row, x);
            var actual = Q8Weights.Dot(row, Q8Weights.BlockScales(row), x);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void The_product_over_rows_and_threads_equals_each_rows_own(int threads)
    {
        const int cols = 384, rows = 301;
        var matrix = RandomRows(rows, cols, 9);
        var scales = Q8Weights.BlockScales(matrix);
        var x = Activations(cols, 10);
        var output = new float[rows];
        using var pool = new WorkerPool(threads);
        Q8Weights.MatVec(matrix, scales, x, output, pool);
        var rowBytes = cols / Q8Weights.BlockWeights * Q8Weights.BlockBytes;
        for (var r = 0; r < rows; r++)
        {
            var expected = Q8Weights.DotScalar(matrix.AsSpan(r * rowBytes, rowBytes), x);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(output[r]));
        }
    }

    [Fact]
    public void The_product_is_the_dequantised_weights_times_the_dequantised_activations()
    {
        const int cols = 1024;
        var row = RandomRows(1, cols, 3);
        var x = Activations(cols, 4);
        double expected = 0;
        for (var i = 0; i < cols; i++)
        {
            var block = row.AsSpan(i / 32 * Q8Weights.BlockBytes, Q8Weights.BlockBytes);
            expected += (double)(sbyte)block[2 + i % 32] * Q8Weights.Scale(block) * x.Quants[i] * x.Scales[i / 32];
        }
        Assert.InRange(Math.Abs(Q8Weights.DotScalar(row, x) - expected), 0, 1e-5 * Math.Max(1, Math.Abs(expected)));
    }
}
