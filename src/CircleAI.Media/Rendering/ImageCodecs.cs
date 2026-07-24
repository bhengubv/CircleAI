// ImageCodecs.cs
//
// (Rendering 1.0) Real, pure-managed still codecs — no native libraries.
//   * PNG encode (RGBA, 8-bit, filter 0)  — for stills and APNG frames.
//   * PNG decode (8-bit, colour types 0/2/4/6, non-interlaced).
//   * BMP encode/decode (24/32-bit, BI_RGB).
// Deflate is handled by the BCL's ZLibStream (zlib header + Adler-32 for
// free); only the PNG chunk CRC-32 is hand-rolled. JPEG is deliberately not
// decoded here — a full JPEG decoder is out of scope; hosts wire a platform
// decoder behind IImageDecoder.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace CircleAI.Media.Rendering;

/// <summary>Pure-managed PNG/BMP encode and decode.</summary>
public static class ImageCodecs
{
    internal static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // ---- PNG encode ------------------------------------------------------

    /// <summary>Encode an RGBA buffer as a PNG (8-bit, colour type 6).</summary>
    public static byte[] EncodePng(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var ms = new MemoryStream();
        ms.Write(PngSignature, 0, PngSignature.Length);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), image.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // colour type: RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter method
        ihdr[12] = 0;  // interlace
        WriteChunk(ms, 'I', 'H', 'D', 'R', ihdr);

        byte[] idat = ZlibCompress(BuildFilteredScanlines(image));
        WriteChunk(ms, 'I', 'D', 'A', 'T', idat);
        WriteChunk(ms, 'I', 'E', 'N', 'D', ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    /// <summary>Prepend a filter-0 byte to each RGBA scanline (input to zlib).</summary>
    internal static byte[] BuildFilteredScanlines(PixelBuffer image)
    {
        int w = image.Width, h = image.Height, stride = w * 4;
        byte[] outp = new byte[h * (stride + 1)];
        byte[] px = image.Pixels;
        int si = 0, di = 0;
        for (int y = 0; y < h; y++)
        {
            outp[di++] = 0; // filter type: None
            System.Buffer.BlockCopy(px, si, outp, di, stride);
            si += stride;
            di += stride;
        }
        return outp;
    }

    // ---- PNG decode ------------------------------------------------------

    /// <summary>Decode an 8-bit, non-interlaced PNG (grey/greyA/RGB/RGBA) to RGBA.</summary>
    public static PixelBuffer DecodePng(ReadOnlyMemory<byte> bytes)
    {
        var data = bytes.Span;
        if (data.Length < 8 || !data[..8].SequenceEqual(PngSignature))
            throw new NotSupportedException("Not a PNG stream.");

        int pos = 8;
        int width = 0, height = 0, colorType = -1, bitDepth = 0, interlace = 0;
        bool haveHeader = false;
        using var idat = new MemoryStream();

        while (pos + 12 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
            if (len < 0 || (long)pos + 4 + len + 4 > data.Length)
                throw new NotSupportedException("Corrupt PNG chunk.");
            var type = data.Slice(pos, 4);
            pos += 4;
            var chunk = data.Slice(pos, len);
            pos += len + 4; // data + CRC (CRC not validated)

            if (Eq(type, 'I', 'H', 'D', 'R'))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(chunk[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(4, 4));
                bitDepth = chunk[8];
                colorType = chunk[9];
                interlace = chunk[12];
                haveHeader = true;
                if (width <= 0 || height <= 0) throw new NotSupportedException("Invalid PNG dimensions.");
                if (bitDepth != 8) throw new NotSupportedException($"Unsupported PNG bit depth {bitDepth} (managed decoder handles 8-bit only).");
                if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not supported in managed code.");
                if (colorType is not (0 or 2 or 4 or 6)) throw new NotSupportedException($"Unsupported PNG colour type {colorType}.");
            }
            else if (Eq(type, 'I', 'D', 'A', 'T'))
            {
                idat.Write(chunk);
            }
            else if (Eq(type, 'I', 'E', 'N', 'D'))
            {
                break;
            }
        }

        if (!haveHeader) throw new NotSupportedException("PNG missing IHDR.");

        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        byte[] raw = ZlibDecompress(idat.ToArray());
        int stride = width * channels;
        if ((long)raw.Length < (long)height * (stride + 1))
            throw new NotSupportedException("PNG scanline data underflow.");

        byte[] cur = new byte[stride];
        byte[] prev = new byte[stride];
        var outBuf = new PixelBuffer(width, height);
        byte[] outPx = outBuf.Pixels;

        int ri = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[ri++];
            for (int x = 0; x < stride; x++)
            {
                int rawv = raw[ri++];
                int a = x >= channels ? cur[x - channels] : 0;
                int b = prev[x];
                int c = x >= channels ? prev[x - channels] : 0;
                int val = filter switch
                {
                    0 => rawv,
                    1 => rawv + a,
                    2 => rawv + b,
                    3 => rawv + ((a + b) >> 1),
                    4 => rawv + Paeth(a, b, c),
                    _ => throw new NotSupportedException($"Unknown PNG filter {filter}.")
                };
                cur[x] = (byte)(val & 0xFF);
            }

            int di = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int sidx = x * channels;
                byte r8, g8, b8, a8;
                switch (colorType)
                {
                    case 0: r8 = g8 = b8 = cur[sidx]; a8 = 255; break;
                    case 2: r8 = cur[sidx]; g8 = cur[sidx + 1]; b8 = cur[sidx + 2]; a8 = 255; break;
                    case 4: r8 = g8 = b8 = cur[sidx]; a8 = cur[sidx + 1]; break;
                    default: r8 = cur[sidx]; g8 = cur[sidx + 1]; b8 = cur[sidx + 2]; a8 = cur[sidx + 3]; break;
                }
                outPx[di++] = r8;
                outPx[di++] = g8;
                outPx[di++] = b8;
                outPx[di++] = a8;
            }

            (prev, cur) = (cur, prev);
        }

        return outBuf;
    }

    // ---- BMP -------------------------------------------------------------

    /// <summary>Encode an RGBA buffer as a 24-bit bottom-up BMP (BI_RGB).</summary>
    public static byte[] EncodeBmp(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        int rowStride = (w * 3 + 3) / 4 * 4;
        int imageSize = rowStride * h;
        int fileSize = 54 + imageSize;
        byte[] o = new byte[fileSize];

        o[0] = (byte)'B'; o[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(2), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(22), h); // positive => bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(38), 2835);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(42), 2835);

        byte[] px = image.Pixels;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * w * 4;
            int dst = 54 + y * rowStride;
            for (int x = 0; x < w; x++)
            {
                int s = srcRow + x * 4;
                o[dst++] = px[s + 2]; // B
                o[dst++] = px[s + 1]; // G
                o[dst++] = px[s];     // R
            }
        }
        return o;
    }

    /// <summary>Decode an uncompressed 24- or 32-bit BMP to RGBA.</summary>
    public static PixelBuffer DecodeBmp(ReadOnlyMemory<byte> bytes)
    {
        var d = bytes.Span;
        if (d.Length < 54 || d[0] != (byte)'B' || d[1] != (byte)'M')
            throw new NotSupportedException("Not a BMP stream.");

        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(10, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4));
        int bpp = BinaryPrimitives.ReadInt16LittleEndian(d.Slice(28, 2));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(30, 4));

        if (compression != 0) throw new NotSupportedException("Only uncompressed BMP (BI_RGB) is supported.");
        if (bpp != 24 && bpp != 32) throw new NotSupportedException($"Unsupported BMP bit depth {bpp}.");
        if (width <= 0) throw new NotSupportedException("Invalid BMP width.");

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        if (height == 0) throw new NotSupportedException("Invalid BMP height.");

        int bytesPP = bpp / 8;
        int rowStride = (width * bytesPP + 3) / 4 * 4;
        if ((long)d.Length < (long)dataOffset + (long)rowStride * height)
            throw new NotSupportedException("BMP pixel data underflow.");

        var outBuf = new PixelBuffer(width, height);
        byte[] outPx = outBuf.Pixels;
        for (int y = 0; y < height; y++)
        {
            int srcRowIndex = topDown ? y : (height - 1 - y);
            int src = dataOffset + srcRowIndex * rowStride;
            int dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = src + x * bytesPP;
                byte bb = d[s], gg = d[s + 1], rr = d[s + 2];
                byte aa = bytesPP == 4 ? d[s + 3] : (byte)255;
                outPx[dst++] = rr;
                outPx[dst++] = gg;
                outPx[dst++] = bb;
                outPx[dst++] = aa;
            }
        }
        return outBuf;
    }

    // ---- shared helpers --------------------------------------------------

    internal static void WriteChunk(Stream s, char t0, char t1, char t2, char t3, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], data.Length);
        header[4] = (byte)t0; header[5] = (byte)t1; header[6] = (byte)t2; header[7] = (byte)t3;
        s.Write(header);
        if (!data.IsEmpty) s.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = Crc32.Update(crc, header.Slice(4, 4));
        crc = Crc32.Update(crc, data);
        crc ^= 0xFFFFFFFFu;
        Span<byte> crcb = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcb, crc);
        s.Write(crcb);
    }

    internal static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static byte[] ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static bool Eq(ReadOnlySpan<byte> t, char a, char b, char c, char d)
        => t[0] == (byte)a && t[1] == (byte)b && t[2] == (byte)c && t[3] == (byte)d;

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}

/// <summary>Managed PNG/BMP image decoder (JPEG delegated to a platform backend).</summary>
public sealed class ManagedImageDecoder : IImageDecoder
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly ManagedImageDecoder Instance = new();

    public string BackendId => "managed-png-bmp";

    public PixelBuffer Decode(ReadOnlyMemory<byte> bytes, string? mimeHint = null)
    {
        var s = bytes.Span;
        if (LooksPng(s)) return ImageCodecs.DecodePng(bytes);
        if (LooksBmp(s)) return ImageCodecs.DecodeBmp(bytes);
        if (LooksJpeg(s))
            throw new NotSupportedException("JPEG decoding needs a platform decoder (Android BitmapFactory / SkiaSharp) wired through IImageDecoder.");
        throw new NotSupportedException("Unrecognised image format; managed decoder supports PNG and BMP.");
    }

    public bool TryDecode(ReadOnlyMemory<byte> bytes, string? mimeHint, out PixelBuffer? image)
    {
        try
        {
            image = Decode(bytes, mimeHint);
            return true;
        }
        catch (Exception)
        {
            image = null;
            return false;
        }
    }

    private static bool LooksPng(ReadOnlySpan<byte> s)
        => s.Length >= 8 && s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47;

    private static bool LooksBmp(ReadOnlySpan<byte> s)
        => s.Length >= 2 && s[0] == (byte)'B' && s[1] == (byte)'M';

    private static bool LooksJpeg(ReadOnlySpan<byte> s)
        => s.Length >= 2 && s[0] == 0xFF && s[1] == 0xD8;
}

internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
