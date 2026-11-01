// TurboQuantVersion2Tests.cs
//
// TurboQuant's version-2 payloads (S3 of the Bonsai 2 plan): the Hadamard rotation - O(d log d),
// the same transform Bonsai 2's weights are folded with - and the paper's two forms, and the reason
// both are kept: at equal bits the MSE form shrinks every inner product toward zero by about its
// distortion, and the Prod form does not, at the price of more noise. Version-1 payloads are not
// touched here; TurboQuantCodecTests and TurboQuantParityTests hold them to what they always were.

using CircleAI.Core.Compression;
using CircleAI.Inference.Managed.Rotation;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class TurboQuantVersion2Tests(ITestOutputHelper output)
{
    private static float[] Gaussian(int dim, int seed)
    {
        var random = new Random(seed);
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            var u = 1.0 - random.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * random.NextDouble()));
        }
        return v;
    }

    private static float[] Unit(int dim, int seed)
    {
        var v = Gaussian(dim, seed);
        var norm = Norm(v);
        for (var i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }

    private static float Norm(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        return (float)Math.Sqrt(sum);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return (float)sum;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(256)]
    [InlineData(384)]
    [InlineData(1536)]
    public void The_rotation_keeps_length_and_turns_back(int dim)
    {
        var x = Gaussian(dim, dim);
        var y = new float[dim];
        HadamardRotation.Rotate(x, y);
        Assert.InRange(Norm(y) / Norm(x), 0.9999f, 1.0001f);

        var back = new float[dim];
        HadamardRotation.Unrotate(y, back);
        for (var i = 0; i < dim; i++) Assert.InRange(back[i] - x[i], -1e-4f, 1e-4f);

        var again = new float[dim];
        HadamardRotation.Rotate(x, again);
        Assert.Equal(y, again); // the same signs and order every time
    }

    [Fact]
    public void Widths_that_are_not_a_power_of_two_rotate_in_power_of_two_blocks()
    {
        Assert.Equal(new[] { 256 }, HadamardRotation.Blocks(256));
        Assert.Equal(new[] { 256, 128 }, HadamardRotation.Blocks(384));
        Assert.Equal(new[] { 512, 256 }, HadamardRotation.Blocks(768));
        Assert.Equal(new[] { 1024, 512 }, HadamardRotation.Blocks(1536));
    }

    [Fact]
    public void A_vector_on_one_axis_is_spread_over_its_whole_block()
    {
        var x = new float[256];
        x[17] = 1f;
        var y = new float[256];
        HadamardRotation.Rotate(x, y);
        Assert.All(y, v => Assert.InRange(MathF.Abs(v), 1f / 16f - 1e-6f, 1f / 16f + 1e-6f));
    }

    [Fact]
    public void Bonsai_2s_folding_and_TurboQuant_run_one_transform()
    {
        var folded = Gaussian(512, 5);
        var turbo = (float[])folded.Clone();
        PrismHadamard.Transform(folded);
        WalshHadamard.Transform(turbo);
        Assert.Equal(folded, turbo);

        WalshHadamard.Transform(turbo); // its own inverse
        var original = Gaussian(512, 5);
        for (var i = 0; i < turbo.Length; i++) Assert.InRange(turbo[i] - original[i], -1e-5f, 1e-5f);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Version_2_reconstructs_as_closely_as_version_1(int bits)
    {
        const int dim = 256, count = 150;
        double denseError = 0, hadamardError = 0;
        for (var n = 0; n < count; n++)
        {
            var v = Unit(dim, 100 + n);
            var dense = TurboQuantCodec.Decode(TurboQuantCodec.Encode(v, bits), dim, bits);
            var hadamard = TurboQuantCodec.Decode(TurboQuantCodec.Encode(v, bits, TurboQuantForm.Mse), dim, bits);
            for (var i = 0; i < dim; i++)
            {
                denseError += (dense[i] - v[i]) * (dense[i] - v[i]);
                hadamardError += (hadamard[i] - v[i]) * (hadamard[i] - v[i]);
            }
        }
        output.WriteLine($"{bits} bits, mean squared error a vector: dense {denseError / count:F4}, Hadamard {hadamardError / count:F4}");
        Assert.True(hadamardError <= denseError * 1.15, $"Hadamard {hadamardError / count:F4} against dense {denseError / count:F4}");
    }

    [Fact]
    public void At_equal_bits_Prod_leaves_inner_products_unbiased_where_Mse_shrinks_them()
    {
        // Each vector against itself: the true inner product is 1. The MSE form returns about one
        // minus its distortion every time; the Prod form returns 1 on average, scattered wider.
        const int dim = 256, count = 400, bits = 3;
        double mseSum = 0, mseSq = 0, prodSum = 0, prodSq = 0;
        for (var n = 0; n < count; n++)
        {
            var v = Unit(dim, 1000 + n);
            double mse = TurboQuantCodec.InnerProduct(v, TurboQuantCodec.Encode(v, bits, TurboQuantForm.Mse), bits);
            double prod = TurboQuantCodec.InnerProduct(v, TurboQuantCodec.Encode(v, bits, TurboQuantForm.Prod), bits);
            mseSum += mse;
            mseSq += mse * mse;
            prodSum += prod;
            prodSq += prod * prod;
        }
        var mseMean = mseSum / count;
        var prodMean = prodSum / count;
        var mseSpread = Math.Sqrt(mseSq / count - mseMean * mseMean);
        var prodSpread = Math.Sqrt(prodSq / count - prodMean * prodMean);
        output.WriteLine($"{bits} bits, <v, v> = 1: Mse {mseMean:F4} ± {mseSpread:F4}, Prod {prodMean:F4} ± {prodSpread:F4}");

        Assert.InRange(prodMean, 0.99, 1.01);
        Assert.True(mseMean < 0.985, $"Mse should shrink: {mseMean:F4}");
        Assert.True(prodSpread > mseSpread, "the second stage's price is noise");
    }

    [Theory]
    [InlineData(TurboQuantForm.Mse, 3)]
    [InlineData(TurboQuantForm.Prod, 3)]
    [InlineData(TurboQuantForm.Prod, 4)]
    public void An_inner_product_on_the_codes_is_the_inner_product_with_the_decoded_vector(TurboQuantForm form, int bits)
    {
        const int dim = 384;
        var v = Gaussian(dim, 7);
        var q = Gaussian(dim, 8);
        var payload = TurboQuantCodec.Encode(v, bits, form);
        var onCodes = TurboQuantCodec.InnerProduct(q, payload, bits);
        var decoded = Dot(q, TurboQuantCodec.Decode(payload, dim, bits));
        Assert.InRange(onCodes - decoded, -1e-3f * MathF.Abs(decoded) - 1e-3f, 1e-3f * MathF.Abs(decoded) + 1e-3f);
    }

    [Fact]
    public void A_Prod_payload_needs_two_bits_and_carries_its_signs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TurboQuantCodec.Encode(Unit(64, 1), 1, TurboQuantForm.Prod));

        var payload = TurboQuantCodec.Encode(Unit(64, 1), 3, TurboQuantForm.Prod);
        Assert.Equal(2, payload.Version);
        Assert.Equal(TurboQuantForm.Prod, payload.Form);
        Assert.Equal(16, payload.PackedIndices.Length); // 64 coordinates at 2 bits
        Assert.Equal(8, payload.Signs!.Length);         // and one sign each
        Assert.True(payload.ResidualNorm > 0f);

        Assert.Equal(1, TurboQuantCodec.Encode(Unit(64, 1), 3).Version); // the old call writes version 1, as ever
    }

    [Theory]
    [InlineData(TurboQuantForm.Mse)]
    [InlineData(TurboQuantForm.Prod)]
    public void A_zero_vector_stays_zero(TurboQuantForm form)
    {
        var payload = TurboQuantCodec.Encode(new float[128], 3, form);
        Assert.All(TurboQuantCodec.Decode(payload, 128, 3), v => Assert.Equal(0f, v));
        Assert.Equal(0f, TurboQuantCodec.InnerProduct(Gaussian(128, 2), payload, 3));
    }
}
