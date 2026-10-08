// RasterImage.cs
//
// The pixels the vision models need, without ImageSharp.
//
// WHY THIS EXISTS. SixLabors.ImageSharp 3.1.12 carries three high-severity
// advisories (NU1903: GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q,
// GHSA-jjfr-hcj7-qf5w) plus one moderate, 3.1.12 is the last 3.x, and the fix is
// a major bump to 4.x. Both 3.x and 4.x are under the Six Labors Split License:
// Apache-2.0 only below 1M USD annual gross revenue or as a TRANSITIVE
// dependency, and CircleAI referenced it DIRECTLY. Licence first - so it goes.
//
// WHAT WAS ACTUALLY BEING USED, across four files and seven API shapes:
// Image.Load x3, .Resize x3, .Mutate x2, Rgb24 x12, Rgba32 x37, PngEncoder x5,
// JpegEncoder x1. Decode bytes, letterbox or crop and resize, walk pixels into an
// ONNX tensor, and in one place encode a PNG. No drawing, no filters, no colour
// management, no format zoo. A 2D graphics library was carrying four calls.
//
// WHAT REPLACES IT. Microsoft.Maui.Graphics - MIT, version 10.0.20, ALREADY in
// this project's restore graph through Microsoft.Maui.Essentials, so the licence
// cost is zero and the dependency count does not move. It decodes through the
// PLATFORM codec: Android's BitmapFactory (AOSP - no GMS, so degoogled stays
// true), ImageIO on Apple, WIC on Windows. A phone's own JPEG decoder is hardware
// accelerated and already in memory; ImageSharp was decoding in managed code
// beside it.
//
// AND THE PIXEL WORK IS PLAIN C#. Letterbox, crop and nearest/bilinear sampling
// are a dozen lines each over a byte[], and doing them here rather than through a
// graphics pipeline means the tensor fill reads the same buffer it resized - no
// intermediate Image, no Mutate, no DrawImage compositing a canvas.
//
// THE ONE PLATFORM HOLE, STATED RATHER THAN HIDDEN. PlatformImage has no
// implementation on a bare net9.0/net10.0 host - it is a MAUI type and needs a
// platform head. Decode therefore throws PlatformNotSupportedException there with
// that sentence in the message, instead of returning an empty bitmap that would
// make every vision model quietly score noise. Everything that does NOT decode -
// Letterbox, Crop, Resize, ToTensor, EncodePng - is pure managed code and works
// on every leg, which is why the tests can cover the arithmetic without a device.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace CircleAI.Vision;

/// <summary>
/// A tightly packed 8-bit RGB raster: width * height * 3 bytes, row-major, no
/// stride padding, no alpha.
/// </summary>
/// <remarks>
/// RGB and not RGBA because every consumer here feeds an ONNX vision model and
/// every one of those models takes three channels. The 37 Rgba32 references this
/// replaces were all <c>Rgba32</c> only because that is ImageSharp's default
/// pixel type, not because anything read the alpha.
/// </remarks>
public sealed class RasterImage
{
    /// <summary>Bytes per pixel. Three: R, G, B.</summary>
    public const int Channels = 3;

    /// <summary>Row-major RGB bytes, exactly <see cref="Width"/> * <see cref="Height"/> * 3 long.</summary>
    public byte[] Pixels { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Wraps an existing buffer. The buffer is NOT copied.</summary>
    /// <exception cref="ArgumentException">The buffer is not width * height * 3 bytes.</exception>
    public RasterImage(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Dimensions must be positive; got {width}x{height}.");

        var expected = (long)width * height * Channels;
        if (pixels.LongLength != expected)
            throw new ArgumentException(
                $"Buffer is {pixels.LongLength} bytes; a {width}x{height} RGB raster needs {expected}.");

        Pixels = pixels;
        Width = width;
        Height = height;
    }

    /// <summary>A new raster filled with one colour.</summary>
    /// <remarks>
    /// The fill matters: the detector and the plate recogniser both letterbox onto
    /// a 114/114/114 grey canvas, which is what their models were trained to see
    /// in the padding. Black padding shifts the score distribution.
    /// </remarks>
    public static RasterImage Filled(int width, int height, byte r, byte g, byte b)
    {
        var buf = new byte[(long)width * height * Channels];
        for (long i = 0; i < buf.LongLength; i += Channels)
        {
            buf[i] = r;
            buf[i + 1] = g;
            buf[i + 2] = b;
        }
        return new RasterImage(buf, width, height);
    }

    /// <summary>Byte offset of a pixel's red component.</summary>
    public int Offset(int x, int y) => (y * Width + x) * Channels;

    /// <summary>Reads one pixel.</summary>
    public (byte R, byte G, byte B) this[int x, int y]
    {
        get
        {
            var o = Offset(x, y);
            return (Pixels[o], Pixels[o + 1], Pixels[o + 2]);
        }
    }

    /// <summary>Writes one pixel.</summary>
    public void Set(int x, int y, byte r, byte g, byte b)
    {
        var o = Offset(x, y);
        Pixels[o] = r;
        Pixels[o + 1] = g;
        Pixels[o + 2] = b;
    }

    /// <summary>
    /// Scales to an exact size with bilinear sampling.
    /// </summary>
    /// <remarks>
    /// Bilinear and not nearest-neighbour, because this feeds a model: nearest
    /// aliases hard edges and a face detector trained on smooth downscales reads
    /// the aliasing as structure. Bilinear is also what ImageSharp's default
    /// Resize did, so swapping it keeps the scores comparable to every previous
    /// measurement - a change in interpolation would look exactly like a change
    /// in model quality.
    /// </remarks>
    public RasterImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;

        var dst = new byte[(long)width * height * Channels];

        // Map destination centres back into the source, the half-pixel convention.
        // Without the 0.5 offsets the image shifts by half a pixel per axis, which
        // is invisible to a person and moves every bounding box the model returns.
        var sx = (double)Width / width;
        var sy = (double)Height / height;

        for (var y = 0; y < height; y++)
        {
            var fy = (y + 0.5) * sy - 0.5;
            var y0 = (int)Math.Floor(fy);
            var wy = fy - y0;
            var y1 = Math.Min(Math.Max(y0 + 1, 0), Height - 1);
            y0 = Math.Min(Math.Max(y0, 0), Height - 1);

            for (var x = 0; x < width; x++)
            {
                var fx = (x + 0.5) * sx - 0.5;
                var x0 = (int)Math.Floor(fx);
                var wx = fx - x0;
                var x1 = Math.Min(Math.Max(x0 + 1, 0), Width - 1);
                x0 = Math.Min(Math.Max(x0, 0), Width - 1);

                var o00 = Offset(x0, y0);
                var o10 = Offset(x1, y0);
                var o01 = Offset(x0, y1);
                var o11 = Offset(x1, y1);
                var o = (y * width + x) * Channels;

                for (var c = 0; c < Channels; c++)
                {
                    var top = Pixels[o00 + c] * (1 - wx) + Pixels[o10 + c] * wx;
                    var bot = Pixels[o01 + c] * (1 - wx) + Pixels[o11 + c] * wx;
                    dst[o + c] = (byte)Math.Clamp(Math.Round(top * (1 - wy) + bot * wy), 0, 255);
                }
            }
        }

        return new RasterImage(dst, width, height);
    }

    /// <summary>Copies a rectangle out, clamped to the image.</summary>
    /// <remarks>
    /// Clamped rather than validated, because the caller is a face detector
    /// handing back a box it inferred: a box that runs one pixel off the edge is
    /// ordinary, and throwing there would drop a real face. An empty intersection
    /// is a different thing and does throw.
    /// </remarks>
    public RasterImage Crop(int x, int y, int width, int height)
    {
        var x0 = Math.Clamp(x, 0, Width);
        var y0 = Math.Clamp(y, 0, Height);
        var x1 = Math.Clamp(x + width, 0, Width);
        var y1 = Math.Clamp(y + height, 0, Height);

        var w = x1 - x0;
        var h = y1 - y0;
        if (w <= 0 || h <= 0)
            throw new ArgumentException(
                $"Crop ({x},{y},{width},{height}) does not intersect a {Width}x{Height} image.");

        var dst = new byte[(long)w * h * Channels];
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(Pixels, Offset(x0, y0 + row), dst, row * w * Channels, w * Channels);
        }
        return new RasterImage(dst, w, h);
    }

    /// <summary>Draws <paramref name="src"/> onto this image at the given origin, clipped.</summary>
    /// <remarks>Opaque copy, no blending: the two callers composite a resized photo onto a grey canvas.</remarks>
    public void Draw(RasterImage src, int atX, int atY)
    {
        ArgumentNullException.ThrowIfNull(src);

        for (var row = 0; row < src.Height; row++)
        {
            var dy = atY + row;
            if (dy < 0 || dy >= Height) continue;

            var dx0 = Math.Max(atX, 0);
            var dx1 = Math.Min(atX + src.Width, Width);
            var count = dx1 - dx0;
            if (count <= 0) continue;

            Buffer.BlockCopy(
                src.Pixels, src.Offset(dx0 - atX, row),
                Pixels, Offset(dx0, dy),
                count * Channels);
        }
    }

    /// <summary>
    /// Encodes a PNG: 8-bit truecolour, non-interlaced, one IDAT.
    /// </summary>
    /// <remarks>
    /// Hand-rolled because the alternative was keeping a 2D graphics library for
    /// ONE call - OnnxImageGenerator's SaveAsPng - and because PNG's compressor is
    /// already in the BCL: <see cref="ZLibStream"/> is the zlib wrapper the format
    /// specifies, so this is a header, a filter byte per row, and a CRC.
    ///
    /// Filter type 0 (None) on every row. Paeth would compress better; it would
    /// also be the only part of this file where a bug is invisible, because a
    /// wrong prediction still decodes - into subtly wrong pixels. The output is a
    /// generated image handed straight to a caller, not something stored at scale.
    /// </remarks>
    public byte[] EncodePng()
    {
        using var ms = new MemoryStream();

        // Signature.
        ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR: width, height, bit depth 8, colour type 2 (truecolour), deflate,
        // adaptive filtering, no interlace.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), Height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT: each row prefixed with its filter type.
        byte[] deflated;
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var filter = new byte[1];
                for (var y = 0; y < Height; y++)
                {
                    z.Write(filter);
                    z.Write(Pixels, Offset(0, y), Width * Channels);
                }
            }
            deflated = raw.ToArray();
        }
        WriteChunk(ms, "IDAT", deflated);
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    private static void WriteChunk(Stream to, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        to.Write(len);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        to.Write(typeBytes);
        to.Write(data);

        // CRC covers the type and the data, not the length.
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        to.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
