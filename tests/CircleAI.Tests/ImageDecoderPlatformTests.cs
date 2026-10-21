// ImageDecoderPlatformTests.cs
//
// Real JPEG and WebP bytes through each platform's own codec - WIC on Windows, ImageIO
// on a Mac, the distribution's libjpeg-turbo and libwebp on Linux - checked pixel by
// pixel, not just for "did not throw".
//
// THE FIXTURES ARE MADE, NOT DOWNLOADED. The JPEG was written once by Windows' own
// encoder and is inlined below. The WebP is written by TinyLosslessWebP at test time:
// enough of the VP8L format to produce a real lossless file, so the pixels that come
// back can be compared EXACTLY to the pixels that went in.
//
// FOUR QUADRANTS - red, green over blue, white - because each wrong answer a decoder
// can give lands somewhere visible: swapped channels turn red into blue, an upside-down
// buffer swaps the top and bottom rows, a mirrored one swaps left and right.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Vision;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class ImageDecoderPlatformTests
{
    // 64x64 baseline JPEG, 4:2:0, quality 95, written by Windows' JPEG encoder (GDI+).
    // Quadrants: red (255,0,0) top left, green (0,255,0) top right, blue (0,0,255) bottom
    // left, white bottom right. Each is 32x32, aligned to the 16-pixel blocks, so no
    // block straddles a colour edge and every quadrant's centre decodes clean.
    private const string QuadrantsJpeg =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAIBAQIBAQICAgICAgICAwUDAwMDAwYEBAMFBwYHBwcGBwcICQsJCAgKCAcHCg0KCgsMDAwMBwkODw0MDgsMDAz/2wBDAQICAgMDAwYDAwYMCAcIDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAz/wAARCABAAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4vooor+Uz/fwKKKKAP0oooor+Wz/hXCiiigD816KKK/qQ/wC6gKKKKAP0oooor+Wz/hXCiiigD8G6KKK/7TD/AFYCiiigD+4iiiiv8Az9UCiiigD+Heiiiv8Afw/KwooooA/uIooor/AM/VAooooA/9k=";

    private readonly ITestOutputHelper _out;

    public ImageDecoderPlatformTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Windows_and_a_Mac_decode_photos_with_nothing_installed()
    {
        // The Windows service runs plain net10.0 and has a vision model. Until 3.9.1 it
        // could not read a single photo, and this is the assertion that says so if it
        // ever stops being able to again.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        Assert.True(ImageDecoder.IsSupported, "Windows (WIC) and macOS (ImageIO) both ship a JPEG codec; IsSupported must say so.");
    }

    [Fact]
    public void A_JPEG_comes_back_the_right_size_upright_and_in_the_right_colours()
    {
        var jpeg = Convert.FromBase64String(QuadrantsJpeg);
        if (!ImageDecoder.IsSupported)
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => ImageDecoder.Decode(jpeg));
            _out.WriteLine($"no JPEG codec here, as IsSupported said: {ex.Message}");
            return;
        }

        var image = ImageDecoder.Decode(jpeg);
        _out.WriteLine($"decoded {jpeg.Length} byte(s) to {image.Width}x{image.Height}");

        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        // Lossy, so near rather than equal - but far nearer than any wrong answer.
        AssertNear((255, 0, 0), image[16, 16], 8, "top left");
        AssertNear((0, 255, 0), image[48, 16], 8, "top right");
        AssertNear((0, 0, 255), image[16, 48], 8, "bottom left");
        AssertNear((255, 255, 255), image[48, 48], 8, "bottom right");
    }

    [Fact]
    public void A_lossless_WebP_comes_back_pixel_for_pixel()
    {
        var source = Quadrants(8);
        var webp = TinyLosslessWebP.Encode(source);
        if (!ImageDecoder.IsWebPSupported)
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => ImageDecoder.Decode(webp));
            Assert.Contains("WebP", ex.Message);
            _out.WriteLine($"no WebP codec here, as IsWebPSupported said: {ex.Message}");
            return;
        }

        var image = ImageDecoder.Decode(webp);
        _out.WriteLine($"decoded a {webp.Length}-byte lossless WebP to {image.Width}x{image.Height}");

        Assert.Equal(source.Width, image.Width);
        Assert.Equal(source.Height, image.Height);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
            AssertNear(source[x, y], image[x, y], 0, $"pixel ({x},{y})");
    }

    [Fact]
    public void The_WebP_probe_is_exactly_what_the_writer_makes_for_one_white_pixel()
    {
        // The probe IsWebPSupported decodes is 32 bytes packed by hand into ImageDecoder.
        // The writer packs the same image by rule. If either has a bit out of place, this
        // is where it shows - before IsWebPSupported can quietly answer false.
        var written = TinyLosslessWebP.Encode(RasterImage.Filled(1, 1, 255, 255, 255));
        Assert.Equal(written, ImageDecoder.WebPProbe.ToArray());
    }

    [Fact]
    public void Bytes_that_are_no_image_are_bad_data_not_a_missing_codec()
    {
        // Two problems with two fixes: the caller sent the wrong bytes, or the machine
        // lacks a codec. A codec that says "unknown" means both, so the decoder tells
        // them apart by signature - this checks it says the first for words.
        if (!ImageDecoder.IsSupported) return;
        var words = "this is a sentence, not an image"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(words));
    }

    [Fact]
    public void A_JPEG_header_with_no_image_behind_it_is_bad_data()
    {
        if (!ImageDecoder.IsSupported) return;
        var jpegMagic = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F' };
        Assert.Throws<InvalidDataException>(() => ImageDecoder.Decode(jpegMagic));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, "Png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, "Jpeg")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "WebP")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "Gif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, "Bmp")]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0 }, "Tiff")]
    [InlineData(new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, "Heif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, "Unknown")]
    public void Formats_are_told_apart_by_their_first_bytes(byte[] head, string expected)
    {
        Assert.Equal(expected, EncodedImage.Sniff(head).ToString());
    }

    /// <summary>An n x n image of four solid quadrants: red, green over blue, white.</summary>
    private static RasterImage Quadrants(int n)
    {
        var pixels = new byte[n * n * RasterImage.Channels];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var (r, g, b) = (x < n / 2, y < n / 2) switch
            {
                (true, true) => ((byte)255, (byte)0, (byte)0),
                (false, true) => ((byte)0, (byte)255, (byte)0),
                (true, false) => ((byte)0, (byte)0, (byte)255),
                _ => ((byte)255, (byte)255, (byte)255),
            };
            var o = (y * n + x) * RasterImage.Channels;
            pixels[o] = r;
            pixels[o + 1] = g;
            pixels[o + 2] = b;
        }
        return new RasterImage(pixels, n, n);
    }

    private static void AssertNear((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance, string where)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance,
            $"{where}: expected {expected} within {tolerance}, got {actual}");
    }
}

/// <summary>A lossless WebP writer for test images whose every channel takes at most two values.</summary>
/// <remarks>
/// Enough of VP8L (RFC 9649) to make a real file with no encoder on the machine: no
/// transforms, no colour cache, one prefix-code group for the whole image, and "simple"
/// prefix codes of one or two symbols per channel. A one-symbol code costs no bits per
/// pixel; a two-symbol code costs one, the smaller value being code 0 because VP8L's
/// codes are canonical. Bits are packed least significant first, as VP8L reads them.
/// </remarks>
internal static class TinyLosslessWebP
{
    public static byte[] Encode(RasterImage image)
    {
        var bits = new BitWriter();
        bits.Write(0x2F, 8);                        // the VP8L signature byte
        bits.Write((uint)image.Width - 1, 14);
        bits.Write((uint)image.Height - 1, 14);
        bits.Write(0, 1);                           // alpha_is_used: no
        bits.Write(0, 3);                           // version 0
        bits.Write(0, 1);                           // no transform
        bits.Write(0, 1);                           // no colour cache
        bits.Write(0, 1);                           // no meta prefix codes: one group

        var red = Symbols(image, 0);
        var green = Symbols(image, 1);
        var blue = Symbols(image, 2);
        WriteSimpleCode(bits, green);               // VP8L's order: green, red, blue, alpha, distance
        WriteSimpleCode(bits, red);
        WriteSimpleCode(bits, blue);
        WriteSimpleCode(bits, [255]);               // alpha: opaque everywhere
        WriteSimpleCode(bits, [0]);                 // distance: unused, every pixel is a literal

        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var (r, g, b) = image[x, y];
            WriteSymbol(bits, green, g);            // a literal reads green, red, blue, then alpha (no bits)
            WriteSymbol(bits, red, r);
            WriteSymbol(bits, blue, b);
        }

        return Riff(bits.ToArray());
    }

    private static byte[] Symbols(RasterImage image, int channel)
    {
        var seen = new SortedSet<byte>();
        for (var i = channel; i < image.Pixels.Length; i += RasterImage.Channels) seen.Add(image.Pixels[i]);
        if (seen.Count > 2)
            throw new ArgumentException($"channel {channel} takes {seen.Count} values; this writer handles two at most.");
        return seen.ToArray();
    }

    private static void WriteSimpleCode(BitWriter bits, byte[] symbols)
    {
        bits.Write(1, 1);                           // simple code length code
        bits.Write((uint)symbols.Length - 1, 1);    // one symbol or two
        if (symbols[0] < 2)
        {
            bits.Write(0, 1);                       // is_first_8bits: no, one bit is enough
            bits.Write(symbols[0], 1);
        }
        else
        {
            bits.Write(1, 1);
            bits.Write(symbols[0], 8);
        }
        if (symbols.Length == 2) bits.Write(symbols[1], 8);
    }

    private static void WriteSymbol(BitWriter bits, byte[] symbols, byte value)
    {
        if (symbols.Length == 2) bits.Write(value == symbols[1] ? 1u : 0u, 1);
    }

    private static byte[] Riff(byte[] vp8l)
    {
        var pad = vp8l.Length % 2;
        using var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        WriteUInt32(ms, (uint)(4 + 8 + vp8l.Length + pad));
        ms.Write("WEBP"u8);
        ms.Write("VP8L"u8);
        WriteUInt32(ms, (uint)vp8l.Length);
        ms.Write(vp8l);
        if (pad == 1) ms.WriteByte(0);
        return ms.ToArray();
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _used = 8;                      // bits used in the last byte; 8 starts a new one

        public void Write(uint value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_used == 8)
                {
                    _bytes.Add(0);
                    _used = 0;
                }
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _used);
                _used++;
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
