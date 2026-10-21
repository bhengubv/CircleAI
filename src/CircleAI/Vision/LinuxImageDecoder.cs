// LinuxImageDecoder.cs
//
// Linux: there is no one OS image codec, so this uses the two libraries every mainstream
// distribution ships and its desktop already loads - libjpeg-turbo's TurboJPEG
// (libturbojpeg.so.0) and libwebp (libwebp.so.7) - FROM THE SYSTEM, never from this
// package. The distribution patches them: libwebp's CVE-2023-4863 reached Linux machines
// through their package managers, not through app updates, and that is the point.
//
// LOADED BY NAME AT RUN TIME, every export checked, because a server or a container
// without them must get an answer - a PlatformNotSupportedException naming the package
// to install - and not a DllNotFoundException from the middle of a decode.
//
// JPEG and WebP only. GIF, BMP, TIFF and HEIF have no library every distribution ships,
// and PNG never reaches this file: PngReader reads it everywhere.

// Android decodes through BitmapFactory (ImageDecoder.cs), so this is compiled off android only.
#if !ANDROID

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircleAI.Vision;

/// <summary>Decodes JPEG and WebP through the distribution's libjpeg-turbo and libwebp.</summary>
internal static unsafe class LinuxImageDecoder
{
    // turbojpeg.h
    private const int TJPF_RGB = 0;
    private const int TJPF_CMYK = 11;
    private const int TJCS_CMYK = 3;
    private const int TJCS_YCCK = 4;

    private const string TurboJpegPackage =
        "libjpeg-turbo's TurboJPEG library (libturbojpeg.so.0 - Debian and Ubuntu: libturbojpeg0, Fedora: turbojpeg, Alpine: libjpeg-turbo)";
    private const string WebPPackage =
        "libwebp (libwebp.so.7 - Debian and Ubuntu: libwebp7, Fedora and Alpine: libwebp)";

    private static readonly Lazy<TurboJpeg?> s_jpeg = new(LoadTurboJpeg);
    private static readonly Lazy<WebP?> s_webp = new(LoadWebP);

    /// <summary>True when the system's TurboJPEG library loaded.</summary>
    public static bool HasJpeg => s_jpeg.Value is not null;

    /// <summary>Decodes the first frame of a JPEG or WebP to tightly packed RGB.</summary>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded, EncodedImageKind kind) => kind switch
    {
        EncodedImageKind.Jpeg => DecodeJpeg(encoded, s_jpeg.Value
            ?? throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind, $"this Linux has no {TurboJpegPackage} installed"))),
        EncodedImageKind.WebP => DecodeWebP(encoded, s_webp.Value
            ?? throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind, $"this Linux has no {WebPPackage} installed"))),
        EncodedImageKind.Unknown => throw new InvalidDataException(
            $"{encoded.Length} byte(s) are not PNG, JPEG or WebP, the formats this build decodes on Linux."),
        _ => throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
            "on Linux this build reads only PNG, JPEG and WebP, through libraries every distribution ships")),
    };

    private static RasterImage DecodeJpeg(ReadOnlySpan<byte> encoded, TurboJpeg tj)
    {
        var handle = ((delegate* unmanaged<nint>)tj.InitDecompress)();
        if (handle == 0)
            throw new InvalidOperationException("TurboJPEG would not create a decompressor (tjInitDecompress returned null).");
        try
        {
            fixed (byte* src = encoded)
            {
                int width = 0, height = 0, subsampling = 0, colorspace = 0;
                if (((delegate* unmanaged<nint, byte*, nuint, int*, int*, int*, int*, int>)tj.DecompressHeader3)(
                        handle, src, (nuint)encoded.Length, &width, &height, &subsampling, &colorspace) != 0)
                    throw new InvalidDataException(
                        $"TurboJPEG could not read the header of {encoded.Length} byte(s): {tj.ErrorFor(handle)}");

                // TurboJPEG will not turn ink into light itself (that is a colour-management
                // question it declines), so a CMYK or YCCK JPEG comes out as CMYK and is
                // converted below.
                var cmyk = colorspace == TJCS_CMYK || colorspace == TJCS_YCCK;
                var bytesPerPixel = cmyk ? 4 : RasterImage.Channels;
                var size = (long)width * height * bytesPerPixel;
                if (width <= 0 || height <= 0 || size > int.MaxValue)
                    throw new InvalidDataException(
                        $"TurboJPEG read {encoded.Length} byte(s) as {width}x{height}, which has no area or is too large for one buffer.");

                var buffer = new byte[size];
                fixed (byte* dst = buffer)
                {
                    if (((delegate* unmanaged<nint, byte*, nuint, byte*, int, int, int, int, int, int>)tj.Decompress2)(
                            handle, src, (nuint)encoded.Length, dst, width, width * bytesPerPixel, height,
                            cmyk ? TJPF_CMYK : TJPF_RGB, 0) != 0)
                        throw new InvalidDataException(
                            $"TurboJPEG could not decode {encoded.Length} byte(s) of {width}x{height} JPEG: {tj.ErrorFor(handle)}");
                }
                return new RasterImage(cmyk ? CmykToRgb(buffer) : buffer, width, height);
            }
        }
        finally
        {
            ((delegate* unmanaged<nint, int>)tj.Destroy)(handle);
        }
    }

    private static RasterImage DecodeWebP(ReadOnlySpan<byte> encoded, WebP webp)
    {
        fixed (byte* src = encoded)
        {
            int width = 0, height = 0;
            if (((delegate* unmanaged<byte*, nuint, int*, int*, int>)webp.GetInfo)(src, (nuint)encoded.Length, &width, &height) == 0)
                throw new InvalidDataException($"libwebp could not read the header of {encoded.Length} byte(s).");

            var size = (long)width * height * RasterImage.Channels;
            if (width <= 0 || height <= 0 || size > int.MaxValue)
                throw new InvalidDataException(
                    $"libwebp read {encoded.Length} byte(s) as {width}x{height}, which has no area or is too large for one buffer.");

            var rgb = new byte[size];
            fixed (byte* dst = rgb)
            {
                if (((delegate* unmanaged<byte*, nuint, byte*, nuint, int, byte*>)webp.DecodeRgbInto)(
                        src, (nuint)encoded.Length, dst, (nuint)size, width * RasterImage.Channels) == null)
                    throw new InvalidDataException(
                        $"libwebp could not decode {encoded.Length} byte(s) of {width}x{height} WebP (an animated WebP is not decoded here).");
            }
            return new RasterImage(rgb, width, height);
        }
    }

    // Adobe writes CMYK JPEGs inverted - 0 is full ink - and nearly every CMYK JPEG is
    // Adobe's, so the stored values already read as "how much light": R = C * K / 255.
    private static byte[] CmykToRgb(byte[] cmyk)
    {
        var rgb = new byte[cmyk.Length / 4 * RasterImage.Channels];
        for (int i = 0, o = 0; i < cmyk.Length; i += 4, o += RasterImage.Channels)
        {
            var k = cmyk[i + 3];
            rgb[o] = (byte)(cmyk[i] * k / 255);
            rgb[o + 1] = (byte)(cmyk[i + 1] * k / 255);
            rgb[o + 2] = (byte)(cmyk[i + 2] * k / 255);
        }
        return rgb;
    }

    private static TurboJpeg? LoadTurboJpeg()
    {
        if (!TryLoadFirst(out var library, "libturbojpeg.so.0", "libturbojpeg.so")) return null;
        if (!NativeLibrary.TryGetExport(library, "tjInitDecompress", out var init)
            || !NativeLibrary.TryGetExport(library, "tjDecompressHeader3", out var header)
            || !NativeLibrary.TryGetExport(library, "tjDecompress2", out var decompress)
            || !NativeLibrary.TryGetExport(library, "tjDestroy", out var destroy))
            return null;

        // tjGetErrorStr2 (libjpeg-turbo 2.0 and later) answers per handle; 1.x has only
        // the global tjGetErrorStr.
        NativeLibrary.TryGetExport(library, "tjGetErrorStr2", out var perHandle);
        NativeLibrary.TryGetExport(library, "tjGetErrorStr", out var global);
        return new TurboJpeg(init, header, decompress, destroy, perHandle, global);
    }

    private static WebP? LoadWebP()
    {
        if (!TryLoadFirst(out var library, "libwebp.so.7", "libwebp.so.6", "libwebp.so")) return null;
        if (!NativeLibrary.TryGetExport(library, "WebPGetInfo", out var info)
            || !NativeLibrary.TryGetExport(library, "WebPDecodeRGBInto", out var decode))
            return null;
        return new WebP(info, decode);
    }

    private static bool TryLoadFirst(out nint library, params string[] names)
    {
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out library)) return true;
        library = 0;
        return false;
    }

    private sealed record TurboJpeg(
        nint InitDecompress, nint DecompressHeader3, nint Decompress2, nint Destroy, nint ErrorStr2, nint ErrorStr)
    {
        /// <summary>TurboJPEG's own words for the last failure on this handle.</summary>
        public string ErrorFor(nint handle)
        {
            byte* text = ErrorStr2 != 0 ? ((delegate* unmanaged<nint, byte*>)ErrorStr2)(handle)
                       : ErrorStr != 0 ? ((delegate* unmanaged<byte*>)ErrorStr)()
                       : null;
            return text == null ? "no detail given" : Marshal.PtrToStringUTF8((nint)text) ?? "no detail given";
        }
    }

    private sealed record WebP(nint GetInfo, nint DecodeRgbInto);
}

#endif
