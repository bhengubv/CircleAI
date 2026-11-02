// VisionImage.cs
//
// An image made ready for the vision tower exactly as the pinned fork makes it (mtmd-image.cpp,
// mtmd_image_preprocessor_dyn_size and img_tool), so the tower reads the pixels the fork reads:
//
//   size    VisionConfig.Fit - whole 32-pixel tokens a side, 8 to 4096 of them, aspect kept;
//   fit     PAD_CEIL: scaled to fit inside that size (ceil of each side at the smaller ratio, in
//           fp32), centred, the rest black;
//   resize  Pillow's separable resampling, bicubic with a = -0.5: per output pixel the input
//           range and the filter weights, normalised, in 22-bit fixed point; the horizontal pass
//           first, then the vertical, each rounded and clamped to a byte;
//   values  (byte / 255 - mean) / std per channel, in fp32.
//
// All of it is integer arithmetic or fp32 that matches the fork's, so the same picture gives the
// same bytes here as there.

using CircleAI.Vision;

namespace CircleAI.Inference.Managed.Vision;

/// <summary>Sizing, resampling and normalising an image as the fork prepares it for its tower.</summary>
public static class VisionImage
{
    // 32 bits - 8 for a byte - 2 of headroom for the sums, as the fork (and Pillow) keep them.
    private const int PrecisionBits = 32 - 8 - 2;

    /// <summary>
    /// <paramref name="image"/> fitted inside <paramref name="width"/> x <paramref name="height"/>
    /// with its aspect kept, centred on black: the fork's resize with PAD_CEIL.
    /// </summary>
    public static RasterImage Fit(RasterImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (image.Width == width && image.Height == height) return image;

        var scale = MathF.Min((float)width / image.Width, (float)height / image.Height);
        var w = Math.Min((int)MathF.Ceiling(image.Width * scale), width);
        var h = Math.Min((int)MathF.Ceiling(image.Height * scale), height);
        var resized = Resize(image, w, h);

        var canvas = new byte[(long)width * height * RasterImage.Channels];
        var x0 = (width - w) / 2;
        var y0 = (height - h) / 2;
        for (var y = 0; y < h; y++)
        {
            resized.Pixels.AsSpan(y * w * RasterImage.Channels, w * RasterImage.Channels)
                .CopyTo(canvas.AsSpan(((y0 + y) * width + x0) * RasterImage.Channels));
        }
        return new RasterImage(canvas, width, height);
    }

    /// <summary>
    /// <paramref name="image"/> resampled to <paramref name="width"/> x <paramref name="height"/>,
    /// bicubic, as Pillow (and the fork, which follows it) resamples.
    /// </summary>
    public static RasterImage Resize(RasterImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var pixels = image.Pixels;
        var w = image.Width;
        if (width != image.Width)
        {
            var (bounds, weights, k) = Weights(image.Width, width);
            pixels = Horizontal(pixels, image.Width, image.Height, width, k, bounds, weights);
            w = width;
        }
        if (height != image.Height)
        {
            var (bounds, weights, k) = Weights(image.Height, height);
            pixels = Vertical(pixels, w, height, k, bounds, weights);
        }
        return ReferenceEquals(pixels, image.Pixels) ? new RasterImage((byte[])pixels.Clone(), width, height) : new RasterImage(pixels, width, height);
    }

    /// <summary>
    /// The tower's input: every pixel's channels as (byte / 255 - mean) / std, interleaved RGB,
    /// row by row.
    /// </summary>
    public static float[] Normalize(RasterImage image, float[] mean, float[] std)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pixels = image.Pixels;
        var values = new float[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = i % RasterImage.Channels;
            values[i] = (pixels[i] / 255.0f - mean[c]) / std[c];
        }
        return values;
    }

    // Pillow's bicubic kernel with a = -0.5 (ggml and PyTorch use -0.75; the fork follows Pillow).
    private static double Bicubic(double x)
    {
        const double a = -0.5;
        if (x < 0.0) x = -x;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    // For each output pixel: the first input pixel, how many contribute, and their fixed-point
    // weights - precompute_weights in the fork, line for line.
    private static (int[] Bounds, int[] Weights, int K) Weights(int inSize, int outSize)
    {
        const double filterSupport = 2.0;
        var scale = (double)inSize / outSize;
        var filterScale = scale < 1.0 ? 1.0 : scale;
        var support = filterSupport * filterScale;
        var k = (int)Math.Ceiling(support) * 2 + 1;

        var pre = new double[outSize * k];
        var bounds = new int[outSize * 2];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ww = 0.0;
            var ss = 1.0 / filterScale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            var x = 0;
            for (; x < xmax; x++)
            {
                var w = Bicubic((x + xmin - center + 0.5) * ss);
                pre[xx * k + x] = w;
                ww += w;
            }
            for (x = 0; x < xmax; x++)
            {
                if (ww != 0.0) pre[xx * k + x] /= ww;
            }
            for (; x < k; x++) pre[xx * k + x] = 0;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        var weights = new int[outSize * k];
        var fixedPoint = Math.ScaleB(1.0, PrecisionBits);
        for (var i = 0; i < weights.Length; i++)
        {
            // Pillow adds or takes a half and truncates toward zero.
            weights[i] = (int)(pre[i] * fixedPoint + (pre[i] < 0 ? -0.5 : 0.5));
        }
        return (bounds, weights, k);
    }

    private static byte Clip8(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    private static byte[] Horizontal(byte[] src, int inWidth, int height, int outWidth, int k, int[] bounds, int[] weights)
    {
        var dst = new byte[(long)outWidth * height * 3];
        for (var yy = 0; yy < height; yy++)
        {
            var srcRow = yy * inWidth * 3;
            var dstRow = yy * outWidth * 3;
            for (var xx = 0; xx < outWidth; xx++)
            {
                var xmin = bounds[xx * 2];
                var count = bounds[xx * 2 + 1];
                var kw = xx * k;
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                var p = srcRow + xmin * 3;
                for (var x = 0; x < count; x++, p += 3)
                {
                    var w = weights[kw + x];
                    s0 += src[p] * w;
                    s1 += src[p + 1] * w;
                    s2 += src[p + 2] * w;
                }
                dst[dstRow + xx * 3] = Clip8(s0 >> PrecisionBits);
                dst[dstRow + xx * 3 + 1] = Clip8(s1 >> PrecisionBits);
                dst[dstRow + xx * 3 + 2] = Clip8(s2 >> PrecisionBits);
            }
        }
        return dst;
    }

    private static byte[] Vertical(byte[] src, int width, int outHeight, int k, int[] bounds, int[] weights)
    {
        var rowValues = width * 3;
        var dst = new byte[(long)rowValues * outHeight];
        var acc = new int[rowValues];
        for (var yy = 0; yy < outHeight; yy++)
        {
            var ymin = bounds[yy * 2];
            var count = bounds[yy * 2 + 1];
            Array.Fill(acc, 1 << (PrecisionBits - 1));
            for (var y = 0; y < count; y++)
            {
                var w = weights[yy * k + y];
                var row = (ymin + y) * rowValues;
                for (var i = 0; i < rowValues; i++) acc[i] += src[row + i] * w;
            }
            var dstRow = yy * rowValues;
            for (var i = 0; i < rowValues; i++) dst[dstRow + i] = Clip8(acc[i] >> PrecisionBits);
        }
        return dst;
    }
}
