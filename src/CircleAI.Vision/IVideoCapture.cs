// IVideoCapture.cs
//
// (Phase C2) Generic camera capture contract — the camera analogue of
// CircleAI.Voice.IAudioCapture. Yields raw frame buffers with metadata
// (pixel format, dimensions) that downstream consumers (face detection,
// QR scanning, document capture) can decode.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision;

public enum VideoPixelFormat { Yuv420, Nv21, Rgba32, Bgr24, Jpeg }

public sealed record VideoFrame(
    ReadOnlyMemory<byte> Bytes,
    int Width,
    int Height,
    VideoPixelFormat PixelFormat,
    DateTimeOffset CapturedAtUtc,
    int? RotationDegrees = null);

/// <summary>(Phase C2) Async-stream of camera frames.</summary>
public interface IVideoCapture : IAsyncDisposable
{
    /// <summary>Open the camera at the requested resolution and start streaming.
    /// The capture loop is bound to <paramref name="ct"/>.</summary>
    IAsyncEnumerable<VideoFrame> CaptureAsync(
        int preferredWidth, int preferredHeight, CancellationToken ct);
}

/// <summary>(Phase C2) Headless / no-camera fallback — yields nothing.</summary>
public sealed class NullVideoCapture : IVideoCapture
{
    public async IAsyncEnumerable<VideoFrame> CaptureAsync(
        int preferredWidth, int preferredHeight,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
