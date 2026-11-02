// WeightMatrixTests.cs
//
// The vision tower's products (WeightMatrix) on weights written field by field into a GGUF, so the
// layout is the format's and not the reader's: Q8_0 blocks, F16 and F32 rows, a convolution kernel
// read flattened. Each output must be its row's dot product in the fork's rounding - Q8_0 against
// the activations quantised as Q8Activations quantises them, F16 against fp16-rounded activations,
// F32 as it is - summed exactly here in double; the AVX2 path must equal the scalar path (to the
// bit for Q8_0, whose blocks it sums exactly); and any number of threads must give one thread's
// answer, to the bit. GELU is the fork's fp16 table.

using System.Runtime.Intrinsics.X86;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.Inference.Managed.Runtime;
using Xunit;

namespace CircleAI.Tests;

public sealed class WeightMatrixTests
{
    private const int Columns = 96;
    private const int Rows = 37; // not a whole number of tiles
    private const int Tokens = 5;

    [Theory]
    [InlineData(GgmlType.Q8_0)]
    [InlineData(GgmlType.F16)]
    [InlineData(GgmlType.F32)]
    public void Each_output_is_its_rows_dot_product_in_the_forks_rounding_on_every_path_and_any_threads(GgmlType type)
    {
        var rng = new Random(20261102 + (int)type);
        var (data, rows) = Weights(type, rng);
        var x = Values(rng, Tokens * Columns, 2.0);
        var bias = Values(rng, Rows, 0.5);
        var bytes = new GgufBuilder().String("general.architecture", "test").Tensor("w", [Columns, Rows], (uint)type, data).Build();

        GgufBuilder.Read(bytes, file =>
        {
            var matrix = WeightMatrix.Bind(file, "w", Columns, Rows);
            Assert.Equal(type, matrix.Type);
            var scalar = new float[Tokens * Rows];
            matrix.Multiply(x, Tokens, scalar, bias, new ProductScratch(), pool: null, KernelPath.Scalar);

            for (var t = 0; t < Tokens; t++)
            {
                var input = Input(type, x.AsSpan(t * Columns, Columns));
                for (var r = 0; r < Rows; r++)
                {
                    var expected = Reference(type, rows[r], input) + bias[r];
                    var got = scalar[t * Rows + r];
                    Assert.True(Math.Abs(got - expected) <= 1e-5 * Math.Max(1, Math.Abs(expected)), $"{type} token {t} row {r}: {got}, exactly {expected}");
                }
            }

            if (Avx2.IsSupported)
            {
                var avx = new float[Tokens * Rows];
                matrix.Multiply(x, Tokens, avx, bias, new ProductScratch(), pool: null, KernelPath.Avx2);
                if (type == GgmlType.Q8_0) Assert.Equal(scalar, avx);
                else for (var i = 0; i < avx.Length; i++) Assert.True(Math.Abs(avx[i] - scalar[i]) <= 1e-6f * Math.Max(1f, Math.Abs(scalar[i])), $"{type} AVX2 {i}");
            }

            using var pool = new WorkerPool(4);
            var threaded = new float[Tokens * Rows];
            matrix.Multiply(x, Tokens, threaded, bias, new ProductScratch(), pool, KernelPath.Scalar);
            Assert.Equal(scalar, threaded);
        });
    }

    [Fact]
    public void A_convolution_kernel_is_read_as_one_row_per_output_and_wrong_shapes_and_types_are_refused()
    {
        // [kw 4, kh 4, channels 3, outputs 5] in F32: output o's row is its 48 values (c, y, x), x fastest.
        var kernel = new float[4 * 4 * 3 * 5];
        for (var i = 0; i < kernel.Length; i++) kernel[i] = i * 0.01f;
        var bytes = new GgufBuilder().String("general.architecture", "test")
            .Tensor("conv", [4, 4, 3, 5], (uint)GgmlType.F32, Bytes(kernel))
            .Tensor("bf16", [32, 2], (uint)GgmlType.BF16, new byte[32 * 2 * 2])
            .Build();
        GgufBuilder.Read(bytes, file =>
        {
            var matrix = WeightMatrix.Bind(file, "conv", 48, 5);
            var x = new float[48];
            x[47] = 1f; // picks each row's last value
            var y = new float[5];
            matrix.Multiply(x, 1, y, null, new ProductScratch(), null);
            for (var o = 0; o < 5; o++) Assert.Equal(kernel[o * 48 + 47], y[o]);

            Assert.Throws<InvalidDataException>(() => WeightMatrix.Bind(file, "conv", 50, 5)); // not its size
            Assert.Throws<InvalidDataException>(() => WeightMatrix.Bind(file, "conv", 30, 8)); // rows cut across its first dimension
            Assert.Throws<NotSupportedException>(() => WeightMatrix.Bind(file, "bf16", 32, 2));
            Assert.Throws<InvalidDataException>(() => WeightMatrix.Bind(file, "missing", 32, 2));
        });
    }

    [Fact]
    public void Gelu_is_the_forks_fp16_table_and_close_to_gelu()
    {
        float[] xs = [-12f, -10f, -9.99f, -3.5f, -1f, -0.1f, 0f, 0.05f, 0.7f, 1f, 2.5f, 9.99f, 10f, 12.5f];
        var ys = xs.ToArray();
        Gelu.Apply(ys);
        for (var i = 0; i < xs.Length; i++)
        {
            var x = xs[i];
            var expected = x <= -10f ? 0f : x >= 10f ? x : (float)(Half)Gelu.Exact((float)(Half)x);
            Assert.Equal(expected, ys[i]);
        }

        for (var x = -9f; x <= 9f; x += 0.37f)
        {
            Span<float> y = [x];
            Gelu.Apply(y);
            var exact = 0.5 * x * (1 + Math.Tanh(Math.Sqrt(2 / Math.PI) * (x + 0.044715 * x * x * x)));
            Assert.True(Math.Abs(y[0] - exact) <= 0.01 * Math.Max(1, Math.Abs(x)), $"GELU({x}) = {y[0]}, about {exact}");
        }
    }

    // ---- weights written as the file holds them, and what the product reads ----

    private static (byte[] Data, float[][] Rows) Weights(GgmlType type, Random rng)
    {
        var rows = new float[Rows][];
        using var data = new MemoryStream();
        using var w = new BinaryWriter(data);
        for (var r = 0; r < Rows; r++)
        {
            rows[r] = new float[Columns];
            if (type == GgmlType.Q8_0)
            {
                for (var b = 0; b < Columns / 32; b++)
                {
                    var scale = (Half)(0.001 + rng.NextDouble() * 0.02);
                    w.Write(scale);
                    for (var i = 0; i < 32; i++)
                    {
                        var q = (sbyte)rng.Next(-127, 128);
                        w.Write(q);
                        rows[r][b * 32 + i] = q * (float)scale;
                    }
                }
            }
            else
            {
                for (var i = 0; i < Columns; i++)
                {
                    var v = (float)(rng.NextDouble() * 2 - 1);
                    if (type == GgmlType.F16)
                    {
                        w.Write((Half)v);
                        rows[r][i] = (float)(Half)v;
                    }
                    else
                    {
                        w.Write(v);
                        rows[r][i] = v;
                    }
                }
            }
        }
        w.Flush();
        return (data.ToArray(), rows);
    }

    // The activations as the weight type's dot product reads them.
    private static float[] Input(GgmlType type, ReadOnlySpan<float> x)
    {
        var input = x.ToArray();
        if (type == GgmlType.F16)
        {
            for (var i = 0; i < input.Length; i++) input[i] = (float)(Half)input[i];
        }
        else if (type == GgmlType.Q8_0)
        {
            var q8 = new Q8Activations(input.Length);
            q8.Quantize(x);
            for (var i = 0; i < input.Length; i++) input[i] = q8.Quants[i] * q8.Scales[i / 32];
        }
        return input;
    }

    // Exactly, in double: the Q8_0 weight's dequantised values against the dequantised activations
    // are the per-block integer sums times both scales.
    private static double Reference(GgmlType type, float[] row, float[] input)
    {
        double sum = 0;
        for (var i = 0; i < row.Length; i++) sum += (double)row[i] * input[i];
        return sum;
    }

    private static float[] Values(Random rng, int count, double range)
    {
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = (float)((rng.NextDouble() * 2 - 1) * range);
        return values;
    }

    private static byte[] Bytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
