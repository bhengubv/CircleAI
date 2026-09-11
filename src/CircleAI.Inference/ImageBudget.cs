// ImageBudget.cs
//
// How big an image may be before it is handed to a vision model.
//
// THE FIRST PHOTO ANYBODY TAKES IS THE PROBLEM. A phone camera produces twelve
// megapixels; a vision encoder wants a few hundred pixels a side. Nothing in
// between was bounding it, so the path was: 4 MB of JPEG into the bridge, MNN
// decodes it to roughly 36 MB of RGB, the VL preprocessor turns that area into
// vision tokens - thousands of them - and the model prefills every one before it
// emits a single word. On a P30 that is minutes of apparently-frozen phone, or
// an out-of-memory kill, for a question that takes seconds from a small image.
//
// Downscaled to a thousand pixels on the long edge, the same photo is a fraction
// of the tokens and loses nothing a model that size could have resolved anyway.
//
// THIS FILE DOES NOT DECODE OR RESIZE ANYTHING, and that is the point. The
// dimensions of a JPEG, PNG, WebP, GIF or BMP are in the first few dozen bytes of
// the file, so deciding whether an image is affordable costs no pixels at all.
// The actual resize belongs to the platform - BitmapFactory on Android, which
// subsamples while decoding and so never materialises the full bitmap either.
// A pure-managed JPEG decoder is explicitly out of scope in this repo, and
// adding one to enforce a limit would cost more memory than the limit saves.

using System.Buffers.Binary;

namespace CircleAI.Inference;

/// <summary>Decides whether an image is small enough for a vision model.</summary>
public static class ImageBudget
{
    /// <summary>
    /// Longest edge, in pixels, that goes to a vision model unshrunk.
    /// </summary>
    /// <remarks>
    /// WHY A LENGTH RATHER THAN A TOKEN COUNT. The honest unit is vision tokens,
    /// but every encoder counts them differently - Qwen2.5-VL tiles 28-pixel
    /// blocks, SmolVLM splits into fixed squares - so a token budget here would
    /// be right for one catalogued model and quietly wrong for the next one
    /// added. A cap on the long edge is a bound on area, which bounds the token
    /// count under every one of those schemes without pretending to know which
    /// is in use.
    /// <para>
    /// A thousand and twenty-four because it is comfortably above what the
    /// catalogued models resolve to internally and well below what a phone
    /// camera produces.
    /// </para>
    /// <para>
    /// IT SHRINKS SCREENSHOTS TOO, and an earlier version of this comment
    /// claimed otherwise. A phone screenshot is 1080 wide and 1920 or more
    /// tall, so its long edge is over the budget and it is halved like anything
    /// else. That is the right answer for a photograph and a real cost for a
    /// screenshot of small text, where halving can put the text below what the
    /// encoder resolves - so a caller that knows it is looking at text can pass
    /// a larger budget and pay the prefill.
    /// </para>
    /// </remarks>
    public const int MaxEdge = 1024;

    /// <summary>
    /// The pixel dimensions of an encoded image, or <c>null</c> when the format
    /// is not recognised.
    /// </summary>
    /// <remarks>
    /// NULL MEANS "DO NOT KNOW", WHICH IS NOT "TOO BIG". An unrecognised format
    /// is passed through to the decoder rather than rejected here: MNN reads
    /// more formats than this reader does, and refusing an image because this
    /// file could not measure it would break a working path to enforce a limit.
    /// </remarks>
    public static (int Width, int Height)? Measure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) return null;

        // PNG: 8-byte signature, then IHDR with width and height big-endian.
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            if (bytes.Length < 24) return null;
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        }

        // GIF: "GIF87a"/"GIF89a", then width and height little-endian.
        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));

        // BMP: "BM", then a DIB header whose width and height are signed
        // little-endian. Height is negative for a top-down bitmap.
        if (bytes[0] == 'B' && bytes[1] == 'M' && bytes.Length >= 26)
            return (Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[18..])),
                    Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..])));

        // WebP: "RIFF" .... "WEBP", then one of three chunk layouts.
        if (bytes.Length >= 30 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return WebP(bytes);

        // JPEG: walk the markers to a start-of-frame, which carries the size.
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return Jpeg(bytes);

        return null;
    }

    /// <summary>
    /// Whether an image must be shrunk before a vision model sees it.
    /// </summary>
    /// <remarks>
    /// False for an image this cannot measure - see <see cref="Measure"/>.
    /// </remarks>
    public static bool NeedsShrinking(ReadOnlySpan<byte> bytes, int maxEdge = MaxEdge)
    {
        var size = Measure(bytes);
        return size is not null && Math.Max(size.Value.Width, size.Value.Height) > maxEdge;
    }

    /// <summary>
    /// The power-of-two subsample factor that brings an image within budget.
    /// </summary>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="maxEdge">Longest edge wanted.</param>
    /// <returns>1 when no subsampling is needed, otherwise 2, 4, 8, …</returns>
    /// <remarks>
    /// A POWER OF TWO BECAUSE THAT IS WHAT A DECODER CAN DO CHEAPLY. Android's
    /// BitmapFactory.Options.inSampleSize takes exactly this, and honouring it
    /// means the full-size bitmap is never allocated at all - the decoder reads
    /// every nth pixel on the way through. Asking for an arbitrary target would
    /// force a full decode and then a resize, which is the allocation this is
    /// trying to avoid.
    /// <para>
    /// It deliberately UNDERSHOOTS: the factor chosen is the largest that leaves
    /// the long edge at or above <paramref name="maxEdge"/>, so the result is
    /// never smaller than asked for. Halving once more to get nearer the target
    /// would throw away detail the model could have used.
    /// </para>
    /// </remarks>
    public static int SampleFor(int width, int height, int maxEdge = MaxEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdge);
        var edge = Math.Max(width, height);
        if (edge <= maxEdge) return 1;

        var sample = 1;
        while (edge / (sample * 2) >= maxEdge) sample *= 2;
        return sample;
    }

    /// <summary>The subsample factor for an encoded image, or 1 if unmeasurable.</summary>
    public static int SampleFor(ReadOnlySpan<byte> bytes, int maxEdge = MaxEdge)
    {
        var size = Measure(bytes);
        return size is null ? 1 : SampleFor(size.Value.Width, size.Value.Height, maxEdge);
    }

    /// <summary>A sentence about why an image was shrunk, for the trace.</summary>
    public static string Describe(ReadOnlySpan<byte> bytes, int maxEdge = MaxEdge)
    {
        var size = Measure(bytes);
        if (size is null) return $"{bytes.Length / 1024} KB, dimensions unknown - passed through";

        var (w, h) = size.Value;
        var sample = SampleFor(w, h, maxEdge);
        return sample == 1
            ? $"{w}x{h}, within {maxEdge} - unchanged"
            : $"{w}x{h} -> about {w / sample}x{h / sample} (1/{sample}) for a {maxEdge} budget";
    }

    /// <summary>Width and height from a JPEG's first start-of-frame marker.</summary>
    /// <remarks>
    /// The markers must be walked; the size is not at a fixed offset. Phone
    /// photos carry a multi-kilobyte EXIF block - often with a full JPEG
    /// thumbnail inside it - before the frame header, so reading the first
    /// 0xFFC0 found by scanning would return the THUMBNAIL's dimensions and
    /// conclude a twelve-megapixel photo is 160x120.
    /// </remarks>
    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }        // resynchronise on padding

            var marker = b[i + 1];
            if (marker is 0xFF or 0x01 || (marker >= 0xD0 && marker <= 0xD9)) { i += 2; continue; }

            if (i + 3 >= b.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            if (length < 2) return null;

            // SOF0..SOF15, excluding the DHT/JPG/DAC markers that share the run.
            if (marker >= 0xC0 && marker <= 0xCF &&
                marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 9 >= b.Length) return null;
                // [marker][len:2][precision:1][height:2][width:2]
                var height = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]);
                var width  = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]);
                return (width, height);
            }

            i += 2 + length;
        }
        return null;
    }

    /// <summary>Width and height from a WebP's VP8, VP8L or VP8X chunk.</summary>
    private static (int, int)? WebP(ReadOnlySpan<byte> b)
    {
        // Chunk fourcc at 12, payload from 20.
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
        {
            // Lossy: 3-byte frame tag, 3-byte sync code, then 14-bit dimensions.
            if (b.Length < 30) return null;
            var w = BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF;
            var h = BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF;
            return (w, h);
        }

        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
        {
            // Lossless: signature byte then 14 bits of width and 14 of height,
            // both stored one less than the real value.
            if (b.Length < 25) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
        {
            // Extended: 4 flag bytes, then 24-bit canvas width and height, each
            // stored one less than the real value.
            if (b.Length < 30) return null;
            var w = b[24] | (b[25] << 8) | (b[26] << 16);
            var h = b[27] | (b[28] << 8) | (b[29] << 16);
            return (w + 1, h + 1);
        }

        return null;
    }
}
