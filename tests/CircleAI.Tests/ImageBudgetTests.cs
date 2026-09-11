// ImageBudgetTests.cs
//
// How big a picture is, read from its header, and what that means for a phone.
//
// These build real file headers rather than shipping fixtures, because the thing
// being pinned IS the header layout - a fixture would only prove the reader
// agrees with whatever produced the fixture.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class ImageBudgetTests
{
    // ── Measuring ───────────────────────────────────────────────────────

    [Fact]
    public void A_png_reports_its_size_from_the_ihdr()
    {
        Assert.Equal((4032, 3024), ImageBudget.Measure(Png(4032, 3024)));
    }

    [Fact]
    public void A_jpeg_reports_the_size_in_its_start_of_frame()
    {
        Assert.Equal((1920, 1080), ImageBudget.Measure(Jpeg(1920, 1080)));
    }

    [Fact]
    public void A_jpeg_with_exif_before_the_frame_is_not_measured_as_its_thumbnail()
    {
        // THE ONE THAT WOULD HAVE LOOKED FINE. Every phone photo carries a
        // multi-kilobyte EXIF block, and phones put a JPEG THUMBNAIL inside it.
        // A reader that scans for the first 0xFFC0 finds the thumbnail's frame
        // and concludes a twelve-megapixel photo is 160x120 - so it is judged
        // within budget, handed over whole, and the phone freezes.
        var withExif = Jpeg(4032, 3024, exifThumbnail: (160, 120));

        Assert.Equal((4032, 3024), ImageBudget.Measure(withExif));
    }

    [Fact]
    public void A_gif_and_a_bmp_report_their_sizes()
    {
        Assert.Equal((320, 200), ImageBudget.Measure(Gif(320, 200)));
        Assert.Equal((640, 480), ImageBudget.Measure(Bmp(640, 480)));
    }

    [Fact]
    public void A_bottom_up_bitmap_does_not_report_a_negative_height()
    {
        // BMP stores a negative height for top-down rows. Passed through, the
        // long edge comes out negative and every such image is "within budget".
        Assert.Equal((640, 480), ImageBudget.Measure(Bmp(640, -480)));
    }

    [Fact]
    public void A_lossy_webp_reports_its_size()
    {
        Assert.Equal((3000, 2000), ImageBudget.Measure(WebPLossy(3000, 2000)));
    }

    [Fact]
    public void Something_unrecognised_measures_to_nothing_rather_than_guessing()
    {
        Assert.Null(ImageBudget.Measure(new byte[64]));
        Assert.Null(ImageBudget.Measure([1, 2, 3]));
        Assert.Null(ImageBudget.Measure([]));
    }

    [Fact]
    public void An_unmeasurable_image_is_not_treated_as_too_big()
    {
        // NULL MEANS "DO NOT KNOW", NOT "REJECT". MNN reads more formats than
        // this header reader does; refusing one because we could not measure it
        // would break a working path in order to enforce a limit.
        Assert.False(ImageBudget.NeedsShrinking(new byte[64]));
        Assert.Equal(1, ImageBudget.SampleFor(new byte[64]));
    }

    // ── The budget ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(800, 600, 1)]           // already small
    [InlineData(1024, 768, 1)]          // exactly the budget
    [InlineData(1025, 768, 1)]          // over, but halving would undershoot
    [InlineData(2048, 1536, 2)]
    [InlineData(4032, 3024, 2)]         // a 12 MP phone photo
    [InlineData(8000, 6000, 4)]
    public void The_subsample_factor_is_the_largest_that_does_not_undershoot(
        int w, int h, int expected)
    {
        Assert.Equal(expected, ImageBudget.SampleFor(w, h));
    }

    [Fact]
    public void Subsampling_never_produces_an_image_smaller_than_the_budget()
    {
        // The direction that matters. Overshooting throws away detail the model
        // could have used, and it is silent.
        for (var edge = 1025; edge < 12_000; edge += 37)
        {
            var sample = ImageBudget.SampleFor(edge, edge / 2);
            Assert.True(edge / sample >= ImageBudget.MaxEdge,
                $"{edge} at 1/{sample} is {edge / sample}, under the {ImageBudget.MaxEdge} budget");
        }
    }

    [Fact]
    public void The_factor_is_always_a_power_of_two()
    {
        // BitmapFactory.inSampleSize rounds anything else DOWN to one, so a
        // factor of 3 silently becomes 2 and the image stays half again too big.
        for (var edge = 1; edge < 20_000; edge += 91)
        {
            var sample = ImageBudget.SampleFor(edge, edge);
            Assert.True((sample & (sample - 1)) == 0, $"{sample} is not a power of two");
        }
    }

    [Fact]
    public void The_long_edge_decides_regardless_of_orientation()
    {
        Assert.Equal(ImageBudget.SampleFor(4032, 3024), ImageBudget.SampleFor(3024, 4032));
    }

    [Fact]
    public void A_phone_photo_and_a_phone_screenshot_both_need_shrinking()
    {
        // THE SCREENSHOT IS OVER THE BUDGET TOO, which is not what I assumed
        // when I wrote this. 1080x1920 has a long edge of 1920, so it is halved
        // like a photograph. Worth pinning because the tempting assumption -
        // "screenshots are already small" - is how a budget ends up with an
        // exception nobody can justify.
        Assert.True(ImageBudget.NeedsShrinking(Jpeg(4032, 3024)));
        Assert.True(ImageBudget.NeedsShrinking(Png(1080, 1920)));

        // What genuinely passes through: anything whose long edge fits.
        Assert.False(ImageBudget.NeedsShrinking(Png(1024, 768)));
        Assert.False(ImageBudget.NeedsShrinking(Jpeg(800, 600)));
    }

    [Fact]
    public void A_zero_budget_is_refused_rather_than_looping_forever()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBudget.SampleFor(100, 100, 0));
    }

    [Fact]
    public void The_description_says_what_happened_to_the_image()
    {
        Assert.Contains("4032x3024", ImageBudget.Describe(Jpeg(4032, 3024)));
        Assert.Contains("1/2", ImageBudget.Describe(Jpeg(4032, 3024)));
        Assert.Contains("unchanged", ImageBudget.Describe(Png(800, 600)));
        Assert.Contains("unknown", ImageBudget.Describe(new byte[64]));
    }

    // ── Header builders ─────────────────────────────────────────────────

    private static byte[] Png(int w, int h)
    {
        var b = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), 13);          // IHDR length
        "IHDR"u8.ToArray().CopyTo(b, 12);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    private static byte[] Gif(int w, int h)
    {
        var b = new byte[16];
        "GIF89a"u8.ToArray().CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)h);
        return b;
    }

    private static byte[] Bmp(int w, int h)
    {
        var b = new byte[30];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), h);
        return b;
    }

    private static byte[] WebPLossy(int w, int h)
    {
        var b = new byte[32];
        "RIFF"u8.ToArray().CopyTo(b, 0);
        "WEBP"u8.ToArray().CopyTo(b, 8);
        "VP8 "u8.ToArray().CopyTo(b, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(26), (ushort)(w & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(28), (ushort)(h & 0x3FFF));
        return b;
    }

    /// <summary>
    /// A JPEG header: SOI, an optional APP1/EXIF block carrying a thumbnail's
    /// own frame, then the real SOF0.
    /// </summary>
    private static byte[] Jpeg(int w, int h, (int W, int H)? exifThumbnail = null)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        if (exifThumbnail is { } thumb)
        {
            // An APP1 segment whose payload contains a complete little JPEG,
            // exactly as a phone writes it.
            var payload = new List<byte>();
            payload.AddRange("Exif\0\0"u8.ToArray());
            payload.AddRange([0xFF, 0xD8]);                       // thumbnail SOI
            payload.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);     // thumbnail SOF0
            payload.AddRange([(byte)(thumb.H >> 8), (byte)thumb.H]);
            payload.AddRange([(byte)(thumb.W >> 8), (byte)thumb.W]);
            payload.AddRange(new byte[6]);
            payload.AddRange([0xFF, 0xD9]);                       // thumbnail EOI

            var length = payload.Count + 2;
            bytes.AddRange([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
            bytes.AddRange(payload);
        }

        // The real frame.
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(h >> 8), (byte)h]);
        bytes.AddRange([(byte)(w >> 8), (byte)w]);
        bytes.AddRange(new byte[6]);

        return [.. bytes];
    }
}
