// Contracts.cs
//
// (Rendering 1.0) The programmatic-media seam. Four interfaces:
//
//   IMediaRenderer    — compose a MediaSpec into a still or a frame sequence.
//   IVideoEncoder     — turn a frame sequence into a single clip file.
//   IImageDecoder     — decode the user's own image bytes into RGBA.
//   IHtmlFrameProvider— capture an HTML scene into frames (WebView seam).
//
// The renderer, the managed encoder (APNG), the managed decoder (PNG/BMP) and
// every Null default ship in-box. The genuinely-hard, device-specific pieces —
// a real H.264/MP4 muxer and a JPEG/HTML rasteriser — are seams a hosting
// layer fills (Android MediaCodec / FFmpeg / SkiaSharp / a WebView). Absence
// of a real backend degrades to a deterministic empty result, never a crash.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Media.Rendering;

/// <summary>A finished, encoded clip (or the empty result of a Null encoder).</summary>
public sealed record EncodedClip(
    ReadOnlyMemory<byte> Bytes,
    string MimeType,
    int FrameCount,
    RenderSize Size,
    int FrameRate,
    string BackendId);

/// <summary>Parameters an IVideoEncoder needs that the frame stream alone does not carry.</summary>
public sealed record ClipEncodeOptions(
    RenderSize Size,
    int FrameRate,
    int FrameCount,
    int LoopCount = 0);

/// <summary>
/// Composes a MediaSpec. Stills and frames are produced pure-managed; clip
/// muxing is delegated to an injected IVideoEncoder so the codec is a
/// swappable policy, not baked in.
/// </summary>
public interface IMediaRenderer
{
    /// <summary>Backend self-identification — "managed", "null".</summary>
    string BackendId { get; }

    /// <summary>Compose one still. <paramref name="posterFraction"/> (0..1) picks where on any
    /// motion track the poster is sampled — 0 = first frame.</summary>
    PixelBuffer RenderStill(MediaSpec spec, double posterFraction = 0.0);

    /// <summary>Lazily yield the clip's frames (one composed <see cref="PixelBuffer"/> per frame),
    /// so an encoder can stream them without the whole clip resident in memory.</summary>
    IEnumerable<PixelBuffer> EnumerateFrames(MediaSpec spec);

    /// <summary>Compose the timeline and hand the frame stream to <paramref name="encoder"/>.</summary>
    ValueTask<EncodedClip> RenderClipAsync(MediaSpec spec, IVideoEncoder encoder, CancellationToken ct = default);
}

/// <summary>
/// Turns an ordered frame stream into one clip file. The in-box managed
/// implementation is <see cref="AnimatedPngEncoder"/> (APNG — real, playable,
/// full-colour, zero native deps). A true social-media MP4/H.264 encoder is a
/// device seam: on de-Googled Android that is AOSP MediaCodec (or FFmpeg)
/// wired here from the hosting layer. See <see cref="NullVideoEncoder"/> for
/// the honest gap marker.
/// </summary>
public interface IVideoEncoder
{
    /// <summary>Backend self-identification — "apng", "null", "mediacodec-h264", ...</summary>
    string BackendId { get; }

    /// <summary>MIME type this backend emits, e.g. "image/apng" or "video/mp4".</summary>
    string OutputMimeType { get; }

    /// <summary>Encode <paramref name="frames"/> (consumed lazily) into a single clip.</summary>
    ValueTask<EncodedClip> EncodeAsync(
        IEnumerable<PixelBuffer> frames,
        ClipEncodeOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Decodes encoded image bytes (the user's own photo) into an RGBA buffer.
/// The in-box <see cref="ManagedImageDecoder"/> handles PNG and BMP with no
/// native code. JPEG is intentionally NOT decoded in managed code (a full
/// baseline+progressive JPEG decoder is out of scope and error-prone); a host
/// wires a platform decoder — Android BitmapFactory (AOSP, no GMS) or
/// SkiaSharp (MIT/BSD) — behind this same interface.
/// </summary>
public interface IImageDecoder
{
    /// <summary>Backend self-identification — "managed-png-bmp", "skiasharp", ...</summary>
    string BackendId { get; }

    /// <summary>Decode or throw <see cref="NotSupportedException"/> for an unhandled format.</summary>
    PixelBuffer Decode(ReadOnlyMemory<byte> bytes, string? mimeHint = null);

    /// <summary>Try to decode; returns false (with a null image) for unsupported/corrupt input.</summary>
    bool TryDecode(ReadOnlyMemory<byte> bytes, string? mimeHint, out PixelBuffer? image);
}

/// <summary>
/// Renders an HTML scene to frames. A pure-managed library cannot lay out
/// arbitrary HTML/CSS, so the in-box default (<see cref="NullHtmlFrameProvider"/>)
/// yields nothing; the real path is a WebView capture in the MAUI host — the
/// on-device analogue of the html-video pipeline.
/// </summary>
public interface IHtmlFrameProvider
{
    /// <summary>Backend self-identification — "webview", "null".</summary>
    string BackendId { get; }

    /// <summary>Render/capture <paramref name="frameCount"/> frames of the HTML scene.</summary>
    ValueTask<IReadOnlyList<PixelBuffer>> RenderHtmlFramesAsync(
        HtmlTemplateSource html,
        RenderSize size,
        int frameCount,
        int frameRate,
        CancellationToken ct = default);
}
