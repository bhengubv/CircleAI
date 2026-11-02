// VisionImageTests.cs
//
// A picture made ready for the vision tower as the pinned fork makes it (mtmd-image.cpp): sized to
// whole 32-pixel tokens between 8 and 4096 of them, its shape kept (calc_size_preserved_ratio,
// worked by hand below from the fork's fp32 arithmetic); fitted and centred on black (PAD_CEIL);
// resampled the way Pillow resamples, which leaves a flat picture flat. The fixture picture -
// 100x60 - is the case the vision fixture leans on: under the 8-token floor, so scaled up to
// 128x96 with 9 black rows above and 10 below.

using CircleAI.Inference.Managed.Vision;
using CircleAI.Vision;
using Xunit;

namespace CircleAI.Tests;

public sealed class VisionImageTests
{
    // The 2B tower's shape, as its GGUF gives it.
    private static readonly VisionConfig TwoB = new(1024, 16, 4096, 24, 16, 2, 2048, 1e-6f, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f], 48);

    private const int ForkMaximum = VisionConfig.MaxTokens * 32 * 32;

    [Theory]
    [InlineData(100, 60, ForkMaximum, 128, 96)]    // under 8 tokens: up, to 4x3
    [InlineData(10, 10, ForkMaximum, 96, 96)]      // tiny: up to 3x3
    [InlineData(64, 128, ForkMaximum, 64, 128)]    // exactly 8 tokens: kept
    [InlineData(1000, 700, ForkMaximum, 992, 704)] // sides rounded to whole tokens
    [InlineData(6400, 4000, 512 * 512, 640, 384)]  // a photo within a 512-edge budget: 20x12
    [InlineData(4000, 6400, 512 * 512, 384, 640)]  // the same, upright
    [InlineData(6400, 4000, ForkMaximum, 2560, 1600)] // the fork's own ceiling, 4000 tokens
    public void A_picture_is_sized_as_the_fork_sizes_it(int width, int height, int maxPixels, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), TwoB.Fit(width, height, maxPixels));
    }

    [Fact]
    public void A_budget_is_held_between_the_forks_floor_and_ceiling()
    {
        Assert.Equal(TwoB.Fit(3000, 3000, VisionConfig.MinTokens * 1024), TwoB.Fit(3000, 3000, 1));
        Assert.Equal(TwoB.Fit(30000, 30000, ForkMaximum), TwoB.Fit(30000, 30000, int.MaxValue));
        var (w, h) = TwoB.Fit(30000, 30000, int.MaxValue);
        Assert.True(w * h <= ForkMaximum, $"{w}x{h}");
    }

    [Fact]
    public void The_fixture_picture_is_fitted_centred_on_black_and_a_flat_picture_stays_flat()
    {
        var white = RasterImage.Filled(100, 60, 255, 255, 255);
        var fitted = VisionImage.Fit(white, 128, 96);
        Assert.Equal((128, 96), (fitted.Width, fitted.Height));
        for (var y = 0; y < 96; y++)
        {
            // 100x60 at 1.28 is 128x77 (77 = ceil(76.8)), centred: rows 9..85 hold it.
            var expected = y is >= 9 and < 86 ? (byte)255 : (byte)0;
            for (var x = 0; x < 128; x++) Assert.Equal((expected, expected, expected), fitted[x, y]);
        }

        var grey = VisionImage.Resize(RasterImage.Filled(37, 23, 90, 140, 200), 61, 17);
        for (var y = 0; y < grey.Height; y++)
        for (var x = 0; x < grey.Width; x++) Assert.Equal(((byte)90, (byte)140, (byte)200), grey[x, y]);
    }

    [Fact]
    public void Resampling_keeps_a_ramp_in_order_and_values_are_scaled_by_the_towers_mean_and_spread()
    {
        var ramp = new RasterImage(new byte[64 * 3], 64, 1);
        for (var x = 0; x < 64; x++) ramp.Set(x, 0, (byte)(x * 4), (byte)(x * 4), (byte)(x * 4));
        var wider = VisionImage.Resize(ramp, 150, 1);
        for (var x = 1; x < 150; x++) Assert.True(wider[x, 0].R >= wider[x - 1, 0].R, $"not in order at {x}");

        var values = VisionImage.Normalize(RasterImage.Filled(1, 1, 0, 255, 51), TwoB.ImageMean, TwoB.ImageStd);
        Assert.Equal(new[] { -1f, 1f, (51 / 255f - 0.5f) / 0.5f }, values);
    }
}
