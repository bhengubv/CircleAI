// RasterImageTests.cs
//
// The pixel arithmetic that replaced ImageSharp.
//
// WHY THESE EXIST AND WHAT THEY ARE FOR. Four Vision files used ImageSharp for
// decode, letterbox, crop, resize and one PNG encode. ImageSharp 3.1.12 carries
// three high-severity advisories and is the last 3.x, and both 3.x and 4.x are
// under the Six Labors Split License - Apache-2.0 only below 1M USD annual
// revenue, on a DIRECT reference. Replacing it means we now own the arithmetic,
// and every one of these failures is silent on a device:
//
//   a half-pixel shift         -> every bounding box moves, scores look fine
//   nearest instead of bilinear-> aliasing reads as structure to a detector
//   a swapped channel          -> an embedding that matches nothing, no error
//   wrong letterbox padding    -> the model measures black bars it was trained to ignore
//   a bad PNG filter           -> the file still decodes, into wrong pixels
//
// So these assert VALUES, not that a call returned. They run on net9.0 and
// net10.0 with no device, because everything except JPEG decode is managed code.

using System;
using System.IO;
using CircleAI.Vision;
using Xunit;

namespace CircleAI.Tests;

public class RasterImageTests
{
    [Fact]
    public void A_raster_is_three_bytes_per_pixel_and_says_so_when_it_is_not()
    {
        var ok = new RasterImage(new byte[2 * 3 * 3], 2, 3);
        Assert.Equal(2, ok.Width);
        Assert.Equal(3, ok.Height);

        // The message names both numbers, because "invalid buffer" on a 4K frame
        // tells whoever reads the log nothing about which side was wrong.
        var ex = Assert.Throws<ArgumentException>(() => new RasterImage(new byte[10], 2, 3));
        Assert.Contains("18", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public void Filled_puts_the_same_colour_in_every_pixel()
    {
        // 114/114/114 is the letterbox grey the YOLO models were trained to ignore.
        var img = RasterImage.Filled(3, 2, 114, 114, 114);

        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
                Assert.Equal((114, 114, 114), img[x, y]);
    }

    [Fact]
    public void Set_and_read_round_trip_in_RGB_order()
    {
        // Order matters more than it looks: the embedder deliberately reads this
        // buffer BGR into its tensor, and that swap is only correct if the buffer
        // itself is unambiguously R,G,B.
        var img = RasterImage.Filled(2, 2, 0, 0, 0);
        img.Set(1, 0, 10, 20, 30);

        Assert.Equal((10, 20, 30), img[1, 0]);
        Assert.Equal(10, img.Pixels[img.Offset(1, 0)]);
        Assert.Equal(20, img.Pixels[img.Offset(1, 0) + 1]);
        Assert.Equal(30, img.Pixels[img.Offset(1, 0) + 2]);
    }

    [Fact]
    public void Resizing_to_the_same_size_is_the_same_object()
    {
        var img = RasterImage.Filled(8, 8, 1, 2, 3);
        Assert.Same(img, img.Resize(8, 8));
    }

    [Fact]
    public void Upscaling_a_flat_image_changes_no_colour()
    {
        // The guard on the interpolation: bilinear over identical neighbours must
        // return the neighbour exactly. If rounding is wrong this drifts by one,
        // which no eye would catch and every normalised tensor would carry.
        var img = RasterImage.Filled(2, 2, 200, 100, 50);
        var big = img.Resize(7, 5);

        Assert.Equal(7, big.Width);
        Assert.Equal(5, big.Height);
        for (var y = 0; y < big.Height; y++)
            for (var x = 0; x < big.Width; x++)
                Assert.Equal((200, 100, 50), big[x, y]);
    }

    [Fact]
    public void Downscaling_two_by_two_to_one_averages_the_four_pixels()
    {
        // Half-pixel convention, checked by hand: destination pixel 0 maps to
        // source 0.5, so all four source pixels weigh 1/4. 0+90+180+255 = 525,
        // /4 = 131.25, rounds to 131.
        var img = RasterImage.Filled(2, 2, 0, 0, 0);
        img.Set(0, 0, 0, 0, 0);
        img.Set(1, 0, 90, 90, 90);
        img.Set(0, 1, 180, 180, 180);
        img.Set(1, 1, 255, 255, 255);

        var one = img.Resize(1, 1);

        Assert.Equal(1, one.Width);
        Assert.Equal(1, one.Height);
        Assert.Equal((131, 131, 131), one[0, 0]);
    }

    [Fact]
    public void A_horizontal_ramp_stays_monotonic_through_a_resize()
    {
        // Catches a transposed index, which a flat-colour test cannot: swap x and
        // y in the sampler and a ramp comes back constant.
        var src = RasterImage.Filled(4, 1, 0, 0, 0);
        for (var x = 0; x < 4; x++) src.Set(x, 0, (byte)(x * 60), 0, 0);

        var wide = src.Resize(16, 1);

        for (var x = 1; x < wide.Width; x++)
            Assert.True(wide[x, 0].R >= wide[x - 1, 0].R,
                $"ramp went backwards at x={x}: {wide[x - 1, 0].R} then {wide[x, 0].R}");
    }

    [Fact]
    public void Crop_takes_the_rectangle_asked_for()
    {
        var src = RasterImage.Filled(4, 4, 0, 0, 0);
        src.Set(2, 1, 7, 8, 9);

        var crop = src.Crop(2, 1, 2, 2);

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal((7, 8, 9), crop[0, 0]);
    }

    [Fact]
    public void Crop_clamps_a_box_that_runs_off_the_edge()
    {
        // Not an error case: the caller is a face detector handing back a box it
        // inferred, and a face at the frame edge produces exactly this. Throwing
        // would drop a real detection.
        var src = RasterImage.Filled(4, 4, 5, 5, 5);

        var crop = src.Crop(3, 3, 10, 10);

        Assert.Equal(1, crop.Width);
        Assert.Equal(1, crop.Height);
        Assert.Equal((5, 5, 5), crop[0, 0]);
    }

    [Fact]
    public void Crop_with_no_overlap_is_an_error_rather_than_an_empty_image()
    {
        // A zero-area raster would flow into a tensor and the model would score
        // it. Better to stop here, named.
        var src = RasterImage.Filled(4, 4, 0, 0, 0);
        var ex = Assert.Throws<ArgumentException>(() => src.Crop(10, 10, 2, 2));
        Assert.Contains("4x4", ex.Message);
    }

    [Fact]
    public void Draw_composites_at_the_origin_given_and_clips_the_rest()
    {
        var canvas = RasterImage.Filled(5, 5, 114, 114, 114);
        var patch = RasterImage.Filled(2, 2, 1, 2, 3);

        canvas.Draw(patch, 4, 4);   // only its top-left pixel lands

        Assert.Equal((1, 2, 3), canvas[4, 4]);
        Assert.Equal((114, 114, 114), canvas[3, 4]);
        Assert.Equal((114, 114, 114), canvas[0, 0]);
    }

    [Fact]
    public void Draw_at_a_negative_origin_clips_instead_of_throwing()
    {
        var canvas = RasterImage.Filled(4, 4, 0, 0, 0);
        var patch = RasterImage.Filled(3, 3, 9, 9, 9);

        canvas.Draw(patch, -1, -1);

        Assert.Equal((9, 9, 9), canvas[0, 0]);
        Assert.Equal((9, 9, 9), canvas[1, 1]);
        Assert.Equal((0, 0, 0), canvas[3, 3]);
    }

    [Fact]
    public void A_PNG_this_writes_is_a_PNG_this_reads_back_exactly()
    {
        // THE ENCODER'S ONLY REAL PROOF. OnnxImageGenerator's output goes straight
        // to a caller; a wrong CRC, a wrong IHDR field or a mis-sized IDAT makes a
        // file that some decoders accept and others reject, and we would hear
        // about it from whoever opened it, not from a build.
        var src = RasterImage.Filled(9, 7, 0, 0, 0);
        var rnd = new Random(1234);
        for (var y = 0; y < src.Height; y++)
            for (var x = 0; x < src.Width; x++)
                src.Set(x, y, (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));

        var png = src.EncodePng();

        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);

        using var ms = new MemoryStream(png);
        var back = PngReader.Read(ms, out var w, out var h);

        Assert.Equal(src.Width, w);
        Assert.Equal(src.Height, h);
        Assert.Equal(src.Pixels, back);
    }

    [Fact]
    public void A_single_pixel_PNG_round_trips()
    {
        // The degenerate size that off-by-one stride maths gets wrong.
        var one = RasterImage.Filled(1, 1, 3, 2, 1);

        using var ms = new MemoryStream(one.EncodePng());
        var back = PngReader.Read(ms, out var w, out var h);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
        Assert.Equal(new byte[] { 3, 2, 1 }, back);
    }

    [Fact]
    public void PngReader_refuses_what_it_cannot_actually_read()
    {
        var notPng = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var ex = Assert.Throws<InvalidDataException>(() => PngReader.Read(notPng, out _, out _));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_decoder_reads_a_PNG_on_every_framework_and_is_honest_about_the_rest()
    {
        // PNG needs no platform codec, so it works on net9.0/net10.0 too - and
        // taking that path rather than refusing is the difference between a
        // library that works here and one that only works on a phone.
        var src = RasterImage.Filled(4, 3, 20, 40, 60);
        var decoded = ImageDecoder.Decode(src.EncodePng());

        Assert.Equal(4, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal((20, 40, 60), decoded[2, 1]);
    }

    [Fact]
    public void Decoding_nothing_is_an_argument_error_not_an_empty_image()
    {
        Assert.Throws<ArgumentException>(() => ImageDecoder.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_JPEG_off_device_says_what_is_missing_rather_than_returning_grey()
    {
        // The whole point of the exception. IsSupported is false on net9.0/net10.0
        // because there is no platform codec, and a decoder that returned a blank
        // raster instead would let a model score noise and report a confident
        // nonsense answer.
        if (ImageDecoder.IsSupported) return;   // on an android head this is the wrong assertion

        var jpegMagic = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F' };

        var ex = Assert.Throws<PlatformNotSupportedException>(() => ImageDecoder.Decode(jpegMagic));
        Assert.Contains("platform codec", ex.Message);
        Assert.Contains("RasterImage", ex.Message);
    }
}
