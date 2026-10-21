// ImageIOImageDecoder.cs
//
// Apple: ImageIO, the codec stack under every UIImage and NSImage - on macOS, iOS and
// Mac Catalyst alike. Apple patches it, it is hardware-backed for JPEG and HEIF, and it
// costs this library nothing to carry.
//
// THE C API, NOT THE Microsoft.iOS BINDINGS. Those bindings exist only on the ios and
// maccatalyst legs, while plain net10.0 also runs on a Mac - and the C functions below
// are exactly what the bindings call. So one path serves all three platforms, and the
// net10.0 test suite run on a Mac exercises the same code an iPhone runs.
//
// INTO sRGB. The pixels are drawn into an sRGB bitmap context: an untagged JPEG is sRGB
// by convention, and a Display P3 photo from a recent iPhone is converted into the space
// every model here was trained on, rather than read as though it were already in it.

// Android decodes through BitmapFactory (ImageDecoder.cs), so this is compiled off android only.
#if !ANDROID

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircleAI.Vision;

/// <summary>Decodes through ImageIO and Core Graphics.</summary>
internal static unsafe partial class ImageIOImageDecoder
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // kCGImageAlphaNoneSkipLast with the default byte order: R, G, B, X in memory.
    private const uint RgbxBitmapInfo = 5;

    /// <summary>Decodes the first frame to tightly packed RGB.</summary>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded, EncodedImageKind kind)
    {
        nint data = 0, source = 0, image = 0, space = 0, context = 0;
        try
        {
            // CFData copies the bytes, so nothing here needs to stay pinned past this line.
            fixed (byte* bytes = encoded)
                data = CFDataCreate(0, bytes, encoded.Length);
            if (data == 0)
                throw new InvalidOperationException($"Core Foundation could not hold {encoded.Length} byte(s).");

            source = CGImageSourceCreateWithData(data, 0);
            if (source == 0 || CGImageSourceGetCount(source) == 0)
            {
                if (kind is EncodedImageKind.WebP or EncodedImageKind.Heif)
                    throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
                        $"this {AppleOs()} has no {EncodedImage.Name(kind)} codec - ImageIO reads WebP from macOS 11 and iOS 14, "
                        + "and HEIF from macOS 10.13 and iOS 11"));
                throw new InvalidDataException(
                    $"ImageIO could not read {encoded.Length} byte(s) as an image"
                    + (kind == EncodedImageKind.Unknown ? "." : $", although they start like a {EncodedImage.Name(kind)}."));
            }

            image = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (image == 0)
                throw new InvalidDataException(
                    $"ImageIO recognised {encoded.Length} byte(s) as a {EncodedImage.Name(kind)} but could not decode its first frame.");

            var width = CGImageGetWidth(image);
            var height = CGImageGetHeight(image);
            var rgbxSize = (ulong)width * height * 4;
            if (width == 0 || height == 0 || rgbxSize > int.MaxValue)
                throw new InvalidDataException(
                    $"ImageIO decoded {encoded.Length} byte(s) to {width}x{height}, which has no area or is too large for one buffer.");

            space = CreateSrgb();
            if (space == 0)
                throw new InvalidOperationException("Core Graphics would not create an RGB color space.");

            var rgbx = new byte[(int)rgbxSize];
            fixed (byte* dst = rgbx)
            {
                context = CGBitmapContextCreate(dst, width, height, 8, width * 4, space, RgbxBitmapInfo);
                if (context == 0)
                    throw new InvalidOperationException($"Core Graphics would not create a {width}x{height} RGBX bitmap.");

                // A bitmap context's memory runs top row first, so this lands upright.
                CGContextDrawImage(context, new CGRect { Width = width, Height = height }, image);
            }

            var rgb = new byte[(long)width * (long)height * RasterImage.Channels];
            for (long i = 0, o = 0; i < rgbx.LongLength; i += 4, o += RasterImage.Channels)
            {
                rgb[o] = rgbx[i];
                rgb[o + 1] = rgbx[i + 1];
                rgb[o + 2] = rgbx[i + 2];
            }
            return new RasterImage(rgb, (int)width, (int)height);
        }
        finally
        {
            if (context != 0) CFRelease(context);
            if (space != 0) CFRelease(space);
            if (image != 0) CFRelease(image);
            if (source != 0) CFRelease(source);
            if (data != 0) CFRelease(data);
        }
    }

    /// <summary>sRGB by name, or the device RGB space on a system too old to name it.</summary>
    private static nint CreateSrgb()
    {
        // kCGColorSpaceSRGB is a CFStringRef CONSTANT that Core Graphics exports - a data
        // symbol, so it is read through its address rather than called.
        if (NativeLibrary.TryLoad(CoreGraphics, out var library)
            && NativeLibrary.TryGetExport(library, "kCGColorSpaceSRGB", out var symbol))
        {
            var name = *(nint*)symbol;
            if (name != 0)
            {
                var srgb = CGColorSpaceCreateWithName(name);
                if (srgb != 0) return srgb;
            }
        }
        return CGColorSpaceCreateDeviceRGB();
    }

    private static string AppleOs() =>
        OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsMacCatalyst() ? "Mac" : "iOS";

    /// <summary>Core Graphics' CGRect: four CGFloats, which are doubles on every 64-bit Apple platform.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateWithData(nint data, nint options);

    [LibraryImport(ImageIO)]
    private static partial nuint CGImageSourceGetCount(nint source);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateImageAtIndex(nint source, nuint index, nint options);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetWidth(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetHeight(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateWithName(nint name);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGBitmapContextCreate(
        void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextDrawImage(nint context, CGRect rect, nint image);
}

#endif
