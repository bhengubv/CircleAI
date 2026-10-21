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
// EACH PLATFORM'S OWN DECODER, NOT A LIBRARY:
//   android                    BitmapFactory, below - AOSP, so degoogled-by-default holds
//   Windows                    the Windows Imaging Component          WicImageDecoder.cs
//   macOS, iOS, Mac Catalyst   ImageIO                                ImageIOImageDecoder.cs
//   Linux                      the distribution's libjpeg-turbo and
//                              libwebp                                LinuxImageDecoder.cs
// Every one is already on the machine or one system package away, adds nothing to
// an APK or to the package, and is patched by whoever ships the OS - which is the
// reason. SixLabors.ImageSharp was decoding in managed code beside a hardware
// decoder, for four call sites, while carrying three high-severity advisories
// (GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w) and the Six Labors
// Split License - Apache-2.0 only below 1M USD annual revenue, and CircleAI
// referenced it DIRECTLY.
//
// UNTIL 3.9.1 ONLY ANDROID DECODED, and the Windows service had a vision model that
// could not read one photo. The note that stood here said a Windows or iOS branch
// "would be code that cannot build here and cannot be tested here", because the
// library built only net9.0, net10.0 and net10.0-android. Both halves changed: it
// builds ios and maccatalyst now, so the Apple path compiles; and the platform codecs
// turn out to be reachable from PLAIN net10.0 - WIC through COM, ImageIO through its
// C API - which is what a Windows or Mac service runs, and what the test suite runs
// on Windows and on a Mac.
//
// WHY THERE IS STILL NO Microsoft.Maui.Graphics PATH. Its PlatformImage lives only in
// the platform assemblies, so it would serve the ios and maccatalyst legs and never
// the plain net10.0 a desktop service runs; and on Windows it brings the Windows App
// SDK and Win2D, under Microsoft licences.
//
// WHY IT THROWS WHERE THERE IS NO CODEC INSTEAD OF COPING. A decoder that returned an
// empty or grey raster would let every vision model score noise and report confident
// nonsense - the plausible zero that has cost this repo more than any crash. So it
// says what is missing, in one sentence, and names what would fix it.
//
// PIXELS AS STORED. No platform here applies a JPEG's EXIF orientation - BitmapFactory
// does not either - so a phone photo taken sideways decodes sideways, the same way
// everywhere.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircleAI.Vision;

/// <summary>Decodes encoded image bytes into an RGB <see cref="RasterImage"/>.</summary>
public static class ImageDecoder
{
    /// <summary>True when this build can decode photos - JPEG - on this platform.</summary>
    /// <remarks>
    /// A capability a caller can ASK about rather than discover through an
    /// exception, so a setup screen can say "vision needs a device" instead of
    /// offering a model that will throw on first use. PNG decodes everywhere and
    /// needs no asking. WebP has its own question, <see cref="IsWebPSupported"/>,
    /// because whether it decodes depends on the machine, not only the platform.
    /// </remarks>
    public static bool IsSupported =>
#if ANDROID
        true;
#else
        OperatingSystem.IsWindows()
            ? WicImageDecoder.IsAvailable
            : IsApple || (OperatingSystem.IsLinux() && LinuxImageDecoder.HasJpeg);
#endif

    /// <summary>True when WebP decodes here as well.</summary>
    /// <remarks>
    /// Answered by decoding a one-pixel WebP once, because no platform says it
    /// directly: Windows reads WebP only with Microsoft's "WebP Image Extensions"
    /// installed, Apple from macOS 11 and iOS 14, Linux only with libwebp present.
    /// </remarks>
    public static bool IsWebPSupported => s_webp.Value;

    private static readonly Lazy<bool> s_webp = new(ProbeWebP);

    /// <summary>
    /// Decodes JPEG, PNG and WebP bytes - and GIF, BMP, TIFF or HEIF where the platform
    /// reads them - to tightly packed RGB.
    /// </summary>
    /// <remarks>
    /// PNG is read by <see cref="PngReader"/> on every platform but android, so the same
    /// bytes give the same pixels everywhere. Everything else goes to the platform's codec:
    /// the format list beyond JPEG, PNG and WebP is the platform's, not ours. Only the
    /// first frame of an animated image is decoded, and alpha is dropped - every consumer
    /// here is a three-channel model.
    /// </remarks>
    /// <exception cref="ArgumentException">The buffer is empty.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no codec for the format.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a decodable image.</exception>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
            throw new ArgumentException("No image bytes to decode.", nameof(encoded));

#if ANDROID
        return DecodeAndroid(encoded);
#else
        var kind = EncodedImage.Sniff(encoded);

        if (kind == EncodedImageKind.Png)
        {
            using var png = new MemoryStream(encoded.ToArray(), writable: false);
            var rgb = PngReader.Read(png, out var w, out var h);
            return new RasterImage(rgb, w, h);
        }

        if (OperatingSystem.IsWindows()) return WicImageDecoder.Decode(encoded, kind);
        if (IsApple) return ImageIOImageDecoder.Decode(encoded, kind);
        if (OperatingSystem.IsLinux()) return LinuxImageDecoder.Decode(encoded, kind);

        throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
            $"this build has no path to one on {RuntimeInformation.OSDescription}"));
#endif
    }

    /// <summary>
    /// A 1x1 opaque white lossless WebP: RIFF, then a VP8L chunk with no transform, no
    /// colour cache and five one-symbol prefix codes, so the pixel costs no bits at all.
    /// Packed by hand; the tests rebuild it from the format's rules and compare.
    /// </summary>
    internal static ReadOnlySpan<byte> WebPProbe =>
    [
        0x52, 0x49, 0x46, 0x46, 0x18, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50,   // "RIFF", 24, "WEBP"
        0x56, 0x50, 0x38, 0x4C, 0x0C, 0x00, 0x00, 0x00,                           // "VP8L", 12
        0x2F, 0x00, 0x00, 0x00, 0x00,                                             // signature; 1x1, no alpha, version 0
        0xE8, 0x7F, 0xFF, 0xFB, 0xDF, 0xFF, 0x00,                                 // the codes: 255, 255, 255, 255, distance 0
    ];

#if !ANDROID
    private static bool IsApple =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();
#endif

    private static bool ProbeWebP()
    {
#if ANDROID
        return true;    // BitmapFactory has read WebP, lossless included, since Android 4.2.1
#else
        try
        {
            var pixel = Decode(WebPProbe);
            // White, as packed. A codec that decodes it to anything else is worse than none.
            return pixel.Width == 1 && pixel.Height == 1 && pixel[0, 0] == (255, 255, 255);
        }
        catch (Exception)
        {
            // No codec (PlatformNotSupported) or one that refuses it (InvalidData): either way, no.
            return false;
        }
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
