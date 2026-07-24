// NullImplementations.cs
//
// (Rendering 1.0) Safe, fail-closed defaults so the seam is always wireable.
// Absence of a real backend yields deterministic empty results, never a crash.
//
// NullVideoEncoder is also the HONEST GAP MARKER for true video: it advertises
// "video/mp4" but emits zero bytes. A real MP4/H.264 clip requires a genuine
// encoder that is NOT feasible in pure managed code on a low-end phone —
// the on-device, de-Googled path is AOSP MediaCodec (or FFmpeg), wired through
// IVideoEncoder from the hosting/MAUI layer. For a real pure-managed clip use
// AnimatedPngEncoder (APNG) instead.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Media.Rendering;

/// <summary>Emits an empty "video/mp4" — marks the true-H.264/MP4 gap (see file header).</summary>
public sealed class NullVideoEncoder : IVideoEncoder
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly NullVideoEncoder Instance = new();

    public string BackendId => "null";
    public string OutputMimeType => "video/mp4";

    public ValueTask<EncodedClip> EncodeAsync(
        IEnumerable<PixelBuffer> frames,
        ClipEncodeOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        // Frames are intentionally not consumed (no wasted compositing); the
        // intended length is reported from options.
        return ValueTask.FromResult(new EncodedClip(
            ReadOnlyMemory<byte>.Empty, "video/mp4", options.FrameCount, options.Size, options.FrameRate, "null"));
    }
}

/// <summary>Yields no HTML frames — the real path is a WebView capture in the MAUI host.</summary>
public sealed class NullHtmlFrameProvider : IHtmlFrameProvider
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly NullHtmlFrameProvider Instance = new();

    public string BackendId => "null";

    public ValueTask<IReadOnlyList<PixelBuffer>> RenderHtmlFramesAsync(
        HtmlTemplateSource html,
        RenderSize size,
        int frameCount,
        int frameRate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<PixelBuffer>>(Array.Empty<PixelBuffer>());
    }
}

/// <summary>Renders nothing — a 1x1 transparent still and an empty clip.</summary>
public sealed class NullMediaRenderer : IMediaRenderer
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly NullMediaRenderer Instance = new();

    public string BackendId => "null";

    public PixelBuffer RenderStill(MediaSpec spec, double posterFraction = 0.0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new PixelBuffer(1, 1);
    }

    public IEnumerable<PixelBuffer> EnumerateFrames(MediaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Array.Empty<PixelBuffer>();
    }

    public ValueTask<EncodedClip> RenderClipAsync(MediaSpec spec, IVideoEncoder encoder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(encoder);
        return ValueTask.FromResult(new EncodedClip(
            ReadOnlyMemory<byte>.Empty, encoder.OutputMimeType, 0, spec.Size, spec.FrameRate, "null"));
    }
}
