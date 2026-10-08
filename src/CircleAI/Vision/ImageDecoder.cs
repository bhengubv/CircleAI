// ImageDecoder.cs
//
// Turning JPEG/PNG/WebP bytes into pixels, using the codec the device already has.
//
// THIS IS THE ONLY PART OF THE IMAGESHARP REMOVAL THAT NEEDS A PLATFORM.
// Everything else - letterbox, crop, bilinear resize, tensor fill, PNG encode and
// PNG decode - is arithmetic over a byte[] in RasterImage and PngReader, and runs
// on every target framework. Decode of a JPEG is different: it is a thousand lines
// of Huffman and inverse DCT that every operating system already ships, usually
// with hardware behind it.
//
// WHY ANDROID'S OWN DECODER AND NOT A LIBRARY. BitmapFactory is AOSP, so
// degoogled-by-default still holds; it is already resident in every process; and
// it costs zero bytes of APK. SixLabors.ImageSharp was decoding in managed code
// beside a hardware decoder, for four call sites, while carrying three
// high-severity advisories (GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q,
// GHSA-jjfr-hcj7-qf5w) and the Six Labors Split License - Apache-2.0 only below
// 1M USD annual revenue, and CircleAI referenced it DIRECTLY.
//
// WHY THERE IS NO Microsoft.Maui.Graphics PATH HERE, having considered one.
// Maui.Graphics is MIT and already in this project's restore graph, and its
// PlatformImage would decode on iOS/macOS/Windows. But PlatformImage only exists
// in the PLATFORM assemblies, and this project's frameworks are net9.0, net10.0
// and net10.0-android - there is no ios or windows head to compile it into. A
// branch for it would be code that cannot build here and cannot be tested here.
// If an iOS or Windows head is ever added, this is the file that gains a case, and
// PngReader is already the half of that work that needed writing.
//
// WHY IT THROWS ON net9.0/net10.0 INSTEAD OF COPING. A decoder that returned an
// empty or grey raster would let every vision model score noise and report
// confident nonsense - the plausible zero that has cost this repo more than any
// crash. So it says what is missing, in one sentence, and names what would fix it.

using System;
using System.IO;

namespace CircleAI.Vision;

/// <summary>Decodes encoded image bytes into an RGB <see cref="RasterImage"/>.</summary>
public static class ImageDecoder
{
    /// <summary>True when this build can decode encoded images on this platform.</summary>
    /// <remarks>
    /// A capability a caller can ASK about rather than discover through an
    /// exception, so a setup screen can say "vision needs a device" instead of
    /// offering a model that will throw on first use.
    /// </remarks>
    public static bool IsSupported =>
#if ANDROID
        true;
#else
        false;
#endif

    /// <summary>
    /// Decodes JPEG, PNG, WebP, GIF or BMP bytes to tightly packed RGB.
    /// </summary>
    /// <remarks>
    /// The format list is Android's, not ours: BitmapFactory sniffs the header, so
    /// anything the platform decodes, this decodes. A PNG specifically can also be
    /// read anywhere by <see cref="PngReader"/>, which is what the tests use.
    /// </remarks>
    /// <exception cref="ArgumentException">The buffer is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">No platform codec on this target framework.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
            throw new ArgumentException("No image bytes to decode.", nameof(encoded));

#if ANDROID
        return DecodeAndroid(encoded);
#else
        // PNG is readable without a platform codec, so take that rather than
        // refusing work we can actually do. The signature check is cheap and
        // exact: a PNG always starts with these eight bytes.
        if (encoded.Length >= 8 &&
            encoded[0] == 0x89 && encoded[1] == (byte)'P' && encoded[2] == (byte)'N' && encoded[3] == (byte)'G' &&
            encoded[4] == 0x0D && encoded[5] == 0x0A && encoded[6] == 0x1A && encoded[7] == 0x0A)
        {
            using var png = new MemoryStream(encoded.ToArray(), writable: false);
            var rgb = PngReader.Read(png, out var w, out var h);
            return new RasterImage(rgb, w, h);
        }

        throw new PlatformNotSupportedException(
            "Decoding this image needs a platform codec, and this build has none: "
            + "CircleAI targets net9.0, net10.0 and net10.0-android, and only the "
            + "android leg has one (BitmapFactory). PNG is handled everywhere by "
            + "PngReader; JPEG and WebP are not. Every pixel operation - letterbox, "
            + "crop, resize, tensor fill, PNG encode - works on this framework, so "
            + "hand these APIs an already-decoded RasterImage, or run vision work "
            + "on the android head.");
#endif
    }

#if ANDROID
    /// <summary>Android's own decoder, read as packed ARGB and converted in one pass.</summary>
    private static RasterImage DecodeAndroid(ReadOnlySpan<byte> encoded)
    {
        var bytes = encoded.ToArray();

        // Argb8888 explicitly. Left at the default, Android may hand back RGB_565
        // for a large photo, and the channel maths below would then read 5- and
        // 6-bit values as if they were 8-bit - a washed-out image that still
        // decodes, still has the right dimensions, and quietly degrades every
        // score the model produces.
        using var opts = new global::Android.Graphics.BitmapFactory.Options
        {
            InPreferredConfig = global::Android.Graphics.Bitmap.Config.Argb8888,
            InMutable = false,
        };

        using var bmp = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length, opts);
        if (bmp is null)
            throw new InvalidDataException(
                $"Android's BitmapFactory could not decode {bytes.Length} byte(s) as an image.");

        var w = bmp.Width;
        var h = bmp.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidDataException($"Decoded bitmap has no area: {w}x{h}.");

        var argb = new int[w * h];
        bmp.GetPixels(argb, 0, w, 0, 0, w, h);

        var rgb = new byte[(long)w * h * RasterImage.Channels];
        for (int i = 0, o = 0; i < argb.Length; i++, o += RasterImage.Channels)
        {
            // GetPixels returns Color ints: 0xAARRGGBB, host byte order already
            // handled by the framework. Alpha is dropped deliberately - every
            // consumer here is a three-channel model.
            var p = argb[i];
            rgb[o] = (byte)((p >> 16) & 0xFF);
            rgb[o + 1] = (byte)((p >> 8) & 0xFF);
            rgb[o + 2] = (byte)(p & 0xFF);
        }

        return new RasterImage(rgb, w, h);
    }
#endif
}
