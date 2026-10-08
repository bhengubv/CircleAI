// PngReader.cs
//
// Reading back the PNG that Microsoft.Maui.Graphics just wrote.
//
// WHY A DECODER AT ALL, when the whole point was to stop shipping one.
// Maui.Graphics is a DRAWING API: IImage has Width, Height, Save and Downsize,
// and no pixel buffer. On Apple and Windows the only portable way from an IImage
// to bytes is to ask it to encode losslessly and read the result. So this parses
// exactly one thing - the PNG we ourselves asked for, one call earlier, in the
// same process.
//
// THAT NARROWS IT TO ALMOST NOTHING, and the narrowing is deliberate rather than
// lazy: 8-bit, non-interlaced, colour type 2 (RGB) or 6 (RGBA). Anything else
// throws by name. A general PNG reader would have to handle 1/2/4/16-bit depths,
// palettes, greyscale, Adam7 interlacing and tRNS - every one of them a branch
// that would never execute here and could never be tested here. The input is our
// own output; pretending otherwise would be inventing surface area.
//
// On Android this file is never reached: BitmapFactory gives us pixels directly.
//
// All five row filters ARE implemented, because the encoder on the other side is
// the platform's and we do not get to choose what it picks. Paeth in particular
// decodes to subtly wrong pixels rather than failing if it is wrong, so it is
// written straight from the spec's predictor and tested against a round trip.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace CircleAI.Vision;

/// <summary>Minimal PNG reader for 8-bit truecolour images. Returns packed RGB.</summary>
internal static class PngReader
{
    /// <summary>Reads a PNG stream into tightly packed RGB bytes.</summary>
    /// <exception cref="InvalidDataException">Not a PNG, or not an 8-bit non-interlaced truecolour PNG.</exception>
    public static byte[] Read(Stream stream, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        Span<byte> sig = stackalloc byte[8];
        if (r.Read(sig) != 8 ||
            sig[0] != 0x89 || sig[1] != 'P' || sig[2] != 'N' || sig[3] != 'G' ||
            sig[4] != 0x0D || sig[5] != 0x0A || sig[6] != 0x1A || sig[7] != 0x0A)
        {
            throw new InvalidDataException("Not a PNG: signature mismatch.");
        }

        var w = 0;
        var h = 0;
        var srcChannels = 0;
        var sawIhdr = false;

        // IDAT may be split across any number of chunks and the zlib stream spans
        // them, so they are concatenated before inflating. Splitting is common for
        // large images and a reader that assumes one IDAT fails only on big input.
        using var idat = new MemoryStream();

        while (true)
        {
            Span<byte> lenBytes = stackalloc byte[4];
            if (r.Read(lenBytes) != 4) throw new InvalidDataException("PNG ended before IEND.");
            var len = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
            if (len < 0) throw new InvalidDataException($"PNG chunk length {len} is negative.");

            var typeBytes = r.ReadBytes(4);
            if (typeBytes.Length != 4) throw new InvalidDataException("PNG ended inside a chunk type.");
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);

            var data = len == 0 ? Array.Empty<byte>() : r.ReadBytes(len);
            if (data.Length != len) throw new InvalidDataException($"PNG {type} chunk is truncated.");

            _ = r.ReadBytes(4); // CRC: we wrote this file a call ago; not re-verified.

            switch (type)
            {
                case "IHDR":
                    if (data.Length < 13) throw new InvalidDataException("PNG IHDR is too short.");
                    w = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0));
                    h = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    var depth = data[8];
                    var colour = data[9];
                    var interlace = data[12];

                    if (w <= 0 || h <= 0) throw new InvalidDataException($"PNG has no area: {w}x{h}.");
                    if (depth != 8)
                        throw new InvalidDataException($"PNG bit depth {depth} is not supported; only 8.");
                    if (interlace != 0)
                        throw new InvalidDataException("Interlaced PNG is not supported.");
                    srcChannels = colour switch
                    {
                        2 => 3,  // truecolour
                        6 => 4,  // truecolour + alpha
                        _ => throw new InvalidDataException(
                                $"PNG colour type {colour} is not supported; only 2 (RGB) and 6 (RGBA)."),
                    };
                    sawIhdr = true;
                    break;

                case "IDAT":
                    if (!sawIhdr) throw new InvalidDataException("PNG IDAT before IHDR.");
                    idat.Write(data);
                    break;

                case "IEND":
                    if (!sawIhdr) throw new InvalidDataException("PNG IEND before IHDR.");
                    width = w;
                    height = h;
                    return Unfilter(idat.ToArray(), w, h, srcChannels);

                default:
                    break; // ancillary chunk; nothing here needs it
            }
        }
    }

    /// <summary>Inflates the pixel data and reverses the per-row filters.</summary>
    private static byte[] Unfilter(byte[] zlib, int w, int h, int srcChannels)
    {
        var stride = w * srcChannels;
        var raw = new byte[(long)stride * h];

        using (var src = new MemoryStream(zlib, writable: false))
        using (var z = new ZLibStream(src, CompressionMode.Decompress))
        {
            // Filter byte, then one row, h times. Read exactly - a partial read
            // from a decompression stream is normal and a loop that assumes
            // otherwise corrupts the bottom of the image.
            var prev = new byte[stride];
            var cur = new byte[stride];

            for (var y = 0; y < h; y++)
            {
                var filter = z.ReadByte();
                if (filter < 0) throw new InvalidDataException($"PNG data ended at row {y} of {h}.");

                ReadExactly(z, cur, stride, y, h);
                Reverse(filter, cur, prev, srcChannels, y);

                Buffer.BlockCopy(cur, 0, raw, y * stride, stride);
                (prev, cur) = (cur, prev);
            }
        }

        if (srcChannels == 3) return raw;

        // Drop alpha. Every consumer is a three-channel model, and compositing
        // against an assumed background would invent pixels the model then measures.
        var rgb = new byte[(long)w * h * 3];
        for (long i = 0, o = 0; i < raw.LongLength; i += 4, o += 3)
        {
            rgb[o] = raw[i];
            rgb[o + 1] = raw[i + 1];
            rgb[o + 2] = raw[i + 2];
        }
        return rgb;
    }

    private static void ReadExactly(Stream s, byte[] buf, int count, int row, int rows)
    {
        var got = 0;
        while (got < count)
        {
            var n = s.Read(buf, got, count - got);
            if (n <= 0)
                throw new InvalidDataException($"PNG row {row} of {rows} is truncated ({got}/{count} bytes).");
            got += n;
        }
    }

    /// <summary>Reverses one row's filter in place, per RFC 2083 section 6.</summary>
    private static void Reverse(int filter, byte[] cur, byte[] prev, int bpp, int row)
    {
        switch (filter)
        {
            case 0: // None
                break;

            case 1: // Sub — left neighbour
                for (var i = bpp; i < cur.Length; i++)
                    cur[i] = (byte)(cur[i] + cur[i - bpp]);
                break;

            case 2: // Up — the row above
                for (var i = 0; i < cur.Length; i++)
                    cur[i] = (byte)(cur[i] + prev[i]);
                break;

            case 3: // Average — of left and above, floored
                for (var i = 0; i < cur.Length; i++)
                {
                    var left = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                }
                break;

            case 4: // Paeth — whichever of left/above/upper-left is closest to their linear estimate
                for (var i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;      // left
                    int b = prev[i];                           // above
                    int c = i >= bpp ? prev[i - bpp] : 0;      // upper-left

                    var p = a + b - c;
                    var pa = Math.Abs(p - a);
                    var pb = Math.Abs(p - b);
                    var pc = Math.Abs(p - c);

                    // Ties go to a, then b — the order is normative, not arbitrary.
                    var pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                    cur[i] = (byte)(cur[i] + pred);
                }
                break;

            default:
                throw new InvalidDataException($"PNG row {row} uses unknown filter {filter}.");
        }
    }
}
