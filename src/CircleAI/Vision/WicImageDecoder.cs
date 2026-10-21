// WicImageDecoder.cs
//
// Windows: the Windows Imaging Component - the codec stack Explorer, Photos, GDI+, WPF
// and WinUI all decode through. It is in every Windows since Vista, Windows Update
// patches it, and it costs this library nothing to carry.
//
// RAW COM, ON PURPOSE. This code runs from plain net9.0/net10.0 - which is what the
// Windows service is - so there is no WinRT projection to reach
// Windows.Graphics.Imaging through, and [assembly: DisableRuntimeMarshalling] rules
// out the built-in COM interop. So the five interfaces are called through their
// vtables: function pointers over blittable arguments, no marshaller, nothing for the
// trimmer or the AOT compiler to lose. A slot number is the method's position in
// wincodec.h, counting IUnknown's three first, and each is named where it is used.
//
// WHAT WINDOWS DECODES. JPEG, GIF, BMP and TIFF are built into WIC. WebP and HEIF come
// from Microsoft's "WebP Image Extensions" and "HEIF Image Extensions", which most
// Windows 10 and 11 installs carry and some (Server, LTSC, N editions) do not. Without
// one, WIC finds no decoder - and that is reported as the missing codec it is, never
// as a bad image.

// Android decodes through BitmapFactory (ImageDecoder.cs), so this is compiled off android only.
#if !ANDROID

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CircleAI.Vision;

/// <summary>Decodes through the Windows Imaging Component.</summary>
internal static unsafe partial class WicImageDecoder
{
    // CLSID_WICImagingFactory - the Vista factory, which every later Windows still serves.
    private static readonly Guid ClsidImagingFactory =
        new(0xcacaf262, 0x9370, 0x4615, 0xa1, 0x3b, 0x9f, 0x55, 0x39, 0xda, 0x4c, 0x0a);
    private static readonly Guid IidImagingFactory =
        new(0xec5ec8a9, 0xc395, 0x4314, 0x9c, 0x77, 0x54, 0xd7, 0xa9, 0x35, 0xff, 0x70);
    // GUID_WICPixelFormat24bppBGR: the 24-bit layout every WIC format converter produces.
    private static readonly Guid PixelFormat24bppBgr =
        new(0x6fddc324, 0x4e03, 0x4bfe, 0xb1, 0x85, 0x3d, 0x77, 0x76, 0x8d, 0xc9, 0x0c);

    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int WINCODEC_ERR_COMPONENTNOTFOUND = unchecked((int)0x88982F50);
    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const int WICDecodeMetadataCacheOnDemand = 0;
    private const int WICBitmapDitherTypeNone = 0;
    private const int WICBitmapPaletteTypeCustom = 0;

    // Vtable slots, in wincodec.h declaration order. 0-2 are IUnknown.
    private const int SlotRelease = 2;
    private const int SlotFactoryCreateDecoderFromStream = 4;
    private const int SlotFactoryCreateFormatConverter = 10;
    private const int SlotFactoryCreateStream = 14;
    private const int SlotStreamInitializeFromMemory = 16;   // after ISequentialStream's 2 and IStream's 9
    private const int SlotDecoderGetFrame = 13;
    private const int SlotSourceGetSize = 3;
    private const int SlotSourceCopyPixels = 7;
    private const int SlotConverterInitialize = 8;           // after IWICBitmapSource's 5

    /// <summary>True when this Windows can create an imaging factory - every desktop and server does, Nano Server does not.</summary>
    public static bool IsAvailable => s_available.Value;

    private static readonly Lazy<bool> s_available = new(Probe);

    /// <summary>Decodes the first frame to tightly packed RGB.</summary>
    public static RasterImage Decode(ReadOnlySpan<byte> encoded, EncodedImageKind kind)
    {
        var init = CoInitializeEx(null, COINIT_MULTITHREADED);
        // S_OK and S_FALSE each take a reference this call must give back. RPC_E_CHANGED_MODE
        // means the thread is already a single-threaded apartment, and WIC's objects are
        // free-threaded, so that is fine as it is.
        var initialized = init == S_OK || init == S_FALSE;
        if (!initialized && init != RPC_E_CHANGED_MODE)
            throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
                $"COM would not start on this thread (CoInitializeEx 0x{init:X8}), and Windows' codecs live behind it"));
        try
        {
            return DecodeOnThisThread(encoded, kind);
        }
        finally
        {
            if (initialized) CoUninitialize();
        }
    }

    private static RasterImage DecodeOnThisThread(ReadOnlySpan<byte> encoded, EncodedImageKind kind)
    {
        void* factory = null, stream = null, decoder = null, frame = null, converter = null;

        // Pinned for the whole call: WIC's memory stream reads these bytes in place.
        fixed (byte* bytes = encoded)
        {
            try
            {
                var clsid = ClsidImagingFactory;
                var iid = IidImagingFactory;
                var hr = CoCreateInstance(&clsid, null, CLSCTX_INPROC_SERVER, &iid, &factory);
                if (hr < 0)
                    throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
                        $"this Windows has no Imaging Component (CoCreateInstance 0x{hr:X8})"));

                Check(CallOut(factory, SlotFactoryCreateStream, &stream), "IWICImagingFactory::CreateStream", encoded.Length);
                Check(((delegate* unmanaged[Stdcall]<void*, byte*, uint, int>)Slot(stream, SlotStreamInitializeFromMemory))(
                          stream, bytes, (uint)encoded.Length),
                      "IWICStream::InitializeFromMemory", encoded.Length);

                // No vendor preference; metadata read only if asked for, which it never is.
                hr = ((delegate* unmanaged[Stdcall]<void*, void*, Guid*, int, void**, int>)Slot(factory, SlotFactoryCreateDecoderFromStream))(
                    factory, stream, null, WICDecodeMetadataCacheOnDemand, &decoder);
                if (hr == WINCODEC_ERR_COMPONENTNOTFOUND)
                {
                    if (kind is EncodedImageKind.WebP or EncodedImageKind.Heif)
                        throw new PlatformNotSupportedException(EncodedImage.NoCodec(kind,
                            $"this Windows has no {EncodedImage.Name(kind)} codec - it comes with Microsoft's \"{ExtensionFor(kind)}\""));
                    throw new InvalidDataException(
                        $"{encoded.Length} byte(s) are not an image any codec on this Windows recognises"
                        + (kind == EncodedImageKind.Unknown ? "." : $", although they start like a {EncodedImage.Name(kind)}."));
                }
                Check(hr, "IWICImagingFactory::CreateDecoderFromStream", encoded.Length);

                Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(decoder, SlotDecoderGetFrame))(decoder, 0, &frame),
                      "IWICBitmapDecoder::GetFrame", encoded.Length);

                // Whatever the frame is - grey, palette, CMYK, BGRA - into 24-bit BGR. Alpha is
                // dropped, as on android: every consumer here is a three-channel model.
                Check(CallOut(factory, SlotFactoryCreateFormatConverter, &converter), "IWICImagingFactory::CreateFormatConverter", encoded.Length);
                var bgr = PixelFormat24bppBgr;
                Check(((delegate* unmanaged[Stdcall]<void*, void*, Guid*, int, void*, double, int, int>)Slot(converter, SlotConverterInitialize))(
                          converter, frame, &bgr, WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteTypeCustom),
                      "IWICFormatConverter::Initialize", encoded.Length);

                uint width = 0, height = 0;
                Check(((delegate* unmanaged[Stdcall]<void*, uint*, uint*, int>)Slot(converter, SlotSourceGetSize))(converter, &width, &height),
                      "IWICBitmapSource::GetSize", encoded.Length);
                var size = (long)width * height * RasterImage.Channels;
                if (width == 0 || height == 0 || size > int.MaxValue)
                    throw new InvalidDataException(
                        $"Windows decoded {encoded.Length} byte(s) to {width}x{height}, which has no area or is too large for one buffer.");

                var pixels = new byte[size];
                fixed (byte* dst = pixels)
                {
                    Check(((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, byte*, int>)Slot(converter, SlotSourceCopyPixels))(
                              converter, null, width * (uint)RasterImage.Channels, (uint)size, dst),
                          "IWICBitmapSource::CopyPixels", encoded.Length);
                }

                // BGR to RGB in place: swap the outer two of every three.
                for (var i = 0; i < pixels.Length; i += RasterImage.Channels)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

                return new RasterImage(pixels, (int)width, (int)height);
            }
            finally
            {
                Release(converter);
                Release(frame);
                Release(decoder);
                Release(stream);
                Release(factory);
            }
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var init = CoInitializeEx(null, COINIT_MULTITHREADED);
            var initialized = init == S_OK || init == S_FALSE;
            try
            {
                var clsid = ClsidImagingFactory;
                var iid = IidImagingFactory;
                void* factory = null;
                var hr = CoCreateInstance(&clsid, null, CLSCTX_INPROC_SERVER, &iid, &factory);
                Release(factory);
                return hr >= 0;
            }
            finally
            {
                if (initialized) CoUninitialize();
            }
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static string ExtensionFor(EncodedImageKind kind) =>
        kind == EncodedImageKind.WebP ? "WebP Image Extensions" : "HEIF Image Extensions";

    private static void Check(int hr, string step, int length)
    {
        if (hr < 0)
            throw new InvalidDataException(
                $"Windows could not decode {length} byte(s) as an image: {step} returned 0x{hr:X8}.");
    }

    /// <summary>The address in slot <paramref name="slot"/> of a COM object's vtable.</summary>
    private static void* Slot(void* com, int slot) => (*(void***)com)[slot];

    /// <summary>A method whose only argument is the interface it hands back.</summary>
    private static int CallOut(void* com, int slot, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(com, slot))(com, result);

    private static void Release(void* com)
    {
        if (com != null)
            ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(com, SlotRelease))(com);
    }

    // LibraryImport, like every native call in this assembly: DisableRuntimeMarshalling
    // means a stub generated at compile time over blittable arguments, or nothing.
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(void* reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** instance);
}

#endif
