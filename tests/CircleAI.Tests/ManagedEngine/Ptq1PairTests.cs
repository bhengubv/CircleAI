// Ptq1PairTests.cs
//
// The vector kernel's repack and sums, held to the scalar reference. The NEON loop itself runs only on
// ARM, so on this PC the layout is read back one weight at a time, by the same code path a phone
// checks against its vectors ("fast" in the soak receiver).

using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class Ptq1PairTests
{
    private static (byte[] Matrix, Q8Activations X) Random(int rows, int cols, int seed)
    {
        var rng = new Random(seed);
        var matrix = new byte[rows * (cols / Ptq1.BlockWeights) * Ptq1.BlockBytes];
        rng.NextBytes(matrix);
        for (var b = 26; b < matrix.Length; b += Ptq1.BlockBytes) BitConverter.TryWriteBytes(matrix.AsSpan(b, 2), (Half)(0.01f + (float)rng.NextDouble() * 0.05f));

        var x = new float[cols];
        for (var i = 0; i < cols; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var q = new Q8Activations(cols);
        q.Quantize(x);
        return (matrix, q);
    }

    private static float[] Reference(byte[] matrix, Q8Activations x, int rows)
    {
        var rowBytes = x.Length / Ptq1.BlockWeights * Ptq1.BlockBytes;
        var want = new float[rows];
        for (var r = 0; r < rows; r++) want[r] = Ptq1.Dot(matrix.AsSpan(r * rowBytes, rowBytes), x, KernelPath.Scalar);
        return want;
    }

    private static void Close(float[] want, float[] got)
    {
        var top = want.Max(MathF.Abs);
        for (var r = 0; r < want.Length; r++)
            Assert.True(MathF.Abs(want[r] - got[r]) <= 1e-4f * top, $"row {r}: {got[r]} against {want[r]}");
    }

    [Theory]
    [InlineData(16, 128)]
    [InlineData(48, 2048)]
    [InlineData(32, 6144)]
    public void Each_row_is_the_scalar_kernels_row(int rows, int cols)
    {
        var (matrix, x) = Random(rows, cols, rows + cols);
        var input = new Ptq1PairInput(cols);
        input.Set(x);
        var got = new float[rows];

        new Ptq1PairMatrix(matrix, rows, cols).MatVec(input, got);

        Close(Reference(matrix, x, rows), got);
    }

    [Fact]
    public void Threads_change_nothing()
    {
        const int rows = 160, cols = 2048;
        var (matrix, x) = Random(rows, cols, 5);
        var input = new Ptq1PairInput(cols);
        input.Set(x);
        var alone = new float[rows];
        var shared = new float[rows];
        using var pool = new WorkerPool(3);

        new Ptq1PairMatrix(matrix, rows, cols).MatVec(input, alone);
        new Ptq1PairMatrix(matrix, rows, cols, pool).MatVec(input, shared, pool);

        Assert.Equal(alone, shared);
    }

    [Fact]
    public void An_input_can_be_set_again()
    {
        const int rows = 16, cols = 256;
        var (matrix, first) = Random(rows, cols, 1);
        var (_, second) = Random(rows, cols, 2);
        var input = new Ptq1PairInput(cols);
        var product = new Ptq1PairMatrix(matrix, rows, cols);
        var got = new float[rows];

        input.Set(first);
        product.MatVec(input, got);
        input.Set(second);
        product.MatVec(input, got);

        Close(Reference(matrix, second, rows), got);
    }

    [Theory]
    [InlineData(1, 16, 128)]
    [InlineData(3, 48, 2048)]
    [InlineData(8, 160, 2048)]
    [InlineData(8, 32, 6144)]
    public void Inputs_side_by_side_are_each_their_own_product(int n, int rows, int cols)
    {
        var (matrix, _) = Random(rows, cols, rows + cols);
        var product = new Ptq1PairMatrix(matrix, rows, cols);
        var batch = new Ptq1PairBatch(cols);
        var inputs = Enumerable.Range(0, n).Select(i => Random(16, cols, 50 + i).X).ToArray();
        for (var i = 0; i < n; i++) batch.Set(i, inputs[i]);
        var got = Enumerable.Range(0, n).Select(_ => new float[rows]).ToArray();

        product.MatVec(batch, n, got);

        for (var i = 0; i < n; i++) Close(Reference(matrix, inputs[i], rows), got[i]);
    }

    [Fact]
    public void Outputs_can_start_part_way_along()
    {
        const int rows = 32, cols = 256;
        var (matrix, x) = Random(rows, cols, 9);
        var batch = new Ptq1PairBatch(cols);
        batch.Set(0, x);
        var got = new[] { new float[rows], new float[rows], new float[rows] };

        new Ptq1PairMatrix(matrix, rows, cols).MatVec(batch, 1, got, first: 2);

        Close(Reference(matrix, x, rows), got[2]);
        Assert.All(got[0], value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Threads_change_nothing_side_by_side()
    {
        const int rows = 160, cols = 2048;
        var (matrix, x) = Random(rows, cols, 5);
        var alone = new Ptq1PairBatch(cols);
        var shared = new Ptq1PairBatch(cols, workers: 3);
        alone.Set(0, x);
        shared.Set(0, x);
        var one = new[] { new float[rows] };
        var many = new[] { new float[rows] };
        using var pool = new WorkerPool(3);

        new Ptq1PairMatrix(matrix, rows, cols).MatVec(alone, 1, one);
        new Ptq1PairMatrix(matrix, rows, cols, pool).MatVec(shared, 1, many, pool);

        Assert.Equal(one[0], many[0]);
    }

    [Fact]
    public void A_batch_with_too_little_room_is_refused()
    {
        var (matrix, x) = Random(16, 128, 1);
        var product = new Ptq1PairMatrix(matrix, 16, 128);
        var batch = new Ptq1PairBatch(128);
        batch.Set(0, x);
        using var pool = new WorkerPool(2);

        Assert.Throws<ArgumentException>(() => product.MatVec(batch, 1, [new float[16]], pool));
        Assert.Throws<ArgumentOutOfRangeException>(() => product.MatVec(batch, 9, new float[9][]));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Set(8, x));
    }

    [Fact]
    public void A_matrix_that_is_not_whole_tiles_is_refused()
    {
        var (matrix, _) = Random(32, 128, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new Ptq1PairMatrix(matrix, 24, 128));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ptq1PairMatrix(matrix, 16, 100));
    }

    [Fact]
    public void Inputs_and_outputs_of_the_wrong_length_are_refused()
    {
        var (matrix, x) = Random(16, 128, 1);
        var product = new Ptq1PairMatrix(matrix, 16, 128);
        var input = new Ptq1PairInput(128);
        input.Set(x);

        Assert.Throws<ArgumentException>(() => product.MatVec(new Ptq1PairInput(256), new float[16]));
        Assert.Throws<ArgumentException>(() => product.MatVec(input, new float[15]));
        Assert.Throws<ArgumentException>(() => input.Set(new Q8Activations(256)));
    }

    [Fact]
    public void This_pc_does_not_take_the_vector_path_into_a_model()
    {
        // The engine's tests run the lookup kernels here; only ARM with its vectors takes the copy.
        Assert.Equal(System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported, Ptq1PairMatrix.Usable);
    }
}
