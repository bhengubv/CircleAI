// MauiCameraCapture.cs
//
// (Phase C2) Platform-conditional camera capture. Mirrors the pattern of
// MauiAudioCapture. Each platform branch opens the OS camera, pumps
// frames, and exits cleanly on cancellation. Headless TFM (net9.0) gets
// a NullVideoCapture equivalent (raises PlatformNotSupportedException).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Vision;

#if ANDROID
using Android.Content;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.OS;
using Android.Util;
using Android.Views;
using AndroidMedia = Android.Media;
#endif

#if IOS || MACCATALYST
using AVFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
#endif

#if WINDOWS
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
#endif

namespace CircleAI.Maui;

public sealed class MauiCameraCapture : IVideoCapture
{
    private bool _disposed;

#if ANDROID
    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private AndroidMedia.ImageReader? _reader;
#endif
#if IOS || MACCATALYST
    private AVCaptureSession? _avSession;
    private AVCaptureVideoDataOutput? _avOutput;
    private FrameQueue? _avQueue;
#endif
#if WINDOWS
    private MediaFrameReader? _winFrameReader;
    private MediaCapture? _winCapture;
#endif

    public async IAsyncEnumerable<VideoFrame> CaptureAsync(
        int preferredWidth, int preferredHeight,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (preferredWidth  <= 0) throw new ArgumentOutOfRangeException(nameof(preferredWidth));
        if (preferredHeight <= 0) throw new ArgumentOutOfRangeException(nameof(preferredHeight));

#if ANDROID
        await foreach (var frame in CaptureAndroidAsync(preferredWidth, preferredHeight, ct).ConfigureAwait(false))
            yield return frame;
#elif IOS || MACCATALYST
        await foreach (var frame in CaptureAppleAsync(preferredWidth, preferredHeight, ct).ConfigureAwait(false))
            yield return frame;
#elif WINDOWS
        await foreach (var frame in CaptureWindowsAsync(preferredWidth, preferredHeight, ct).ConfigureAwait(false))
            yield return frame;
#else
        await Task.CompletedTask.ConfigureAwait(false);
        throw new PlatformNotSupportedException(
            "MauiCameraCapture is not supported on this TFM. Use NullVideoCapture for headless / test targets.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
#endif
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
#if ANDROID
        try { _session?.Close(); } catch { }
        try { _camera?.Close(); }  catch { }
        try { _reader?.Close(); }  catch { }
#endif
#if IOS || MACCATALYST
        try { _avSession?.StopRunning(); } catch { }
        _avSession?.Dispose();
        _avOutput?.Dispose();
        _avQueue?.Dispose();
#endif
#if WINDOWS
        try { if (_winFrameReader is not null) await _winFrameReader.StopAsync(); } catch { }
        _winFrameReader?.Dispose();
        _winCapture?.Dispose();
#endif
        await Task.CompletedTask.ConfigureAwait(false);
    }

#if ANDROID
    private async IAsyncEnumerable<VideoFrame> CaptureAndroidAsync(
        int width, int height, [EnumeratorCancellation] CancellationToken ct)
    {
        var context = global::Android.App.Application.Context
            ?? throw new InvalidOperationException("Android.App.Application.Context is null");
        var manager = (CameraManager?)context.GetSystemService(Context.CameraService)
            ?? throw new InvalidOperationException("CameraManager not available");
        var ids = manager.GetCameraIdList();
        if (ids.Length == 0) throw new InvalidOperationException("No cameras on device");
        var cameraId = ids[0];

        var queue = new FrameQueue();
        _reader = AndroidMedia.ImageReader.NewInstance(width, height, Android.Graphics.ImageFormatType.Yuv420888, 2);
        _reader.SetOnImageAvailableListener(new ImageListener(queue, width, height), null);

        var openTcs = new TaskCompletionSource<CameraDevice>();
        manager.OpenCamera(cameraId, new CameraStateCallback(openTcs), null);
        _camera = await openTcs.Task.ConfigureAwait(false);

        var sessionTcs = new TaskCompletionSource<CameraCaptureSession>();
        _camera.CreateCaptureSession(new[] { _reader.Surface }, new CaptureSessionCallback(sessionTcs), null);
        _session = await sessionTcs.Task.ConfigureAwait(false);

        var requestBuilder = _camera.CreateCaptureRequest(CameraTemplate.Preview);
        requestBuilder.AddTarget(_reader.Surface!);
        _session.SetRepeatingRequest(requestBuilder.Build()!, null, null);

        await foreach (var frame in queue.ReadAsync(ct).ConfigureAwait(false))
            yield return frame;
    }

    private sealed class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly TaskCompletionSource<CameraDevice> _tcs;
        public CameraStateCallback(TaskCompletionSource<CameraDevice> tcs) => _tcs = tcs;
        public override void OnOpened(CameraDevice camera)        => _tcs.TrySetResult(camera);
        public override void OnDisconnected(CameraDevice camera)  => _tcs.TrySetException(new InvalidOperationException("Camera disconnected"));
        public override void OnError(CameraDevice camera, CameraError error) => _tcs.TrySetException(new InvalidOperationException($"Camera error {error}"));
    }

    private sealed class CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly TaskCompletionSource<CameraCaptureSession> _tcs;
        public CaptureSessionCallback(TaskCompletionSource<CameraCaptureSession> tcs) => _tcs = tcs;
        public override void OnConfigured(CameraCaptureSession session)      => _tcs.TrySetResult(session);
        public override void OnConfigureFailed(CameraCaptureSession session) => _tcs.TrySetException(new InvalidOperationException("CaptureSession config failed"));
    }

    private sealed class ImageListener : Java.Lang.Object, AndroidMedia.ImageReader.IOnImageAvailableListener
    {
        private readonly FrameQueue _queue;
        private readonly int _w, _h;
        public ImageListener(FrameQueue queue, int w, int h) { _queue = queue; _w = w; _h = h; }
        public void OnImageAvailable(AndroidMedia.ImageReader? reader)
        {
            if (reader is null) return;
            using var image = reader.AcquireLatestImage();
            if (image is null) return;
            // Y-plane only — good enough for KWS / faces / QR / docs at preview quality.
            var yPlane = image.GetPlanes()![0];
            var buf    = yPlane.Buffer;
            if (buf is null) return;
            var data = new byte[buf.Remaining()];
            buf.Get(data);
            _queue.Push(new VideoFrame(data, _w, _h, VideoPixelFormat.Yuv420, DateTimeOffset.UtcNow));
        }
    }
#endif

#if IOS || MACCATALYST
    private async IAsyncEnumerable<VideoFrame> CaptureAppleAsync(
        int width, int height, [EnumeratorCancellation] CancellationToken ct)
    {
        _avSession = new AVCaptureSession { SessionPreset = AVCaptureSession.PresetMedium };
        var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video.GetConstant());
        if (device is null) throw new InvalidOperationException("No camera on device");
        var input = AVCaptureDeviceInput.FromDevice(device, out var err);
        if (err is not null) throw new InvalidOperationException("Camera input init failed: " + err.LocalizedDescription);
        if (_avSession.CanAddInput(input)) _avSession.AddInput(input);

        _avOutput = new AVCaptureVideoDataOutput();
        _avQueue  = new FrameQueue();
        _avOutput.SetSampleBufferDelegate(new SampleHandler(_avQueue, width, height), CoreFoundation.DispatchQueue.DefaultGlobalQueue);
        if (_avSession.CanAddOutput(_avOutput)) _avSession.AddOutput(_avOutput);

        _avSession.StartRunning();
        await foreach (var frame in _avQueue.ReadAsync(ct).ConfigureAwait(false))
            yield return frame;
    }

    private sealed class SampleHandler : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        private readonly FrameQueue _queue;
        private readonly int _w, _h;
        public SampleHandler(FrameQueue queue, int w, int h) { _queue = queue; _w = w; _h = h; }
        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            try
            {
                var pixelBuffer = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
                if (pixelBuffer is null) return;
                pixelBuffer.Lock(CVPixelBufferLock.ReadOnly);
                try
                {
                    var bytesPerRow = (int)pixelBuffer.BytesPerRow;
                    var height      = (int)pixelBuffer.Height;
                    var len         = bytesPerRow * height;
                    var data        = new byte[len];
                    System.Runtime.InteropServices.Marshal.Copy(pixelBuffer.BaseAddress, data, 0, len);
                    _queue.Push(new VideoFrame(data, _w, _h, VideoPixelFormat.Rgba32, DateTimeOffset.UtcNow));
                }
                finally { pixelBuffer.Unlock(CVPixelBufferLock.ReadOnly); }
            }
            finally { sampleBuffer.Dispose(); }
        }
    }
#endif

#if WINDOWS
    private async IAsyncEnumerable<VideoFrame> CaptureWindowsAsync(
        int width, int height, [EnumeratorCancellation] CancellationToken ct)
    {
        _winCapture = new MediaCapture();
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group  = groups[0];
        var src    = group.SourceInfos[0];
        await _winCapture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup           = group,
            SharingMode           = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference      = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode  = StreamingCaptureMode.Video
        });
        var source = _winCapture.FrameSources[src.Id];
        _winFrameReader = await _winCapture.CreateFrameReaderAsync(source);
        var status = await _winFrameReader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException("MediaFrameReader failed to start: " + status);

        while (!ct.IsCancellationRequested)
        {
            MediaFrameReference? frameRef = null;
            try
            {
                frameRef = _winFrameReader.TryAcquireLatestFrame();
                if (frameRef?.VideoMediaFrame?.SoftwareBitmap is null)
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                    continue;
                }
                var bitmap = frameRef.VideoMediaFrame.SoftwareBitmap;
                using var ras = new InMemoryRandomAccessStream();
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, ras);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                using var reader = new DataReader(ras.GetInputStreamAt(0));
                var size = (uint)ras.Size;
                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                yield return new VideoFrame(bytes, bitmap.PixelWidth, bitmap.PixelHeight, VideoPixelFormat.Jpeg, DateTimeOffset.UtcNow);
            }
            finally { frameRef?.Dispose(); }
        }
    }
#endif

#if ANDROID || IOS || MACCATALYST
    // Lightweight bounded queue for cross-thread frame handoff.
    private sealed class FrameQueue : IDisposable
    {
        private readonly System.Threading.Channels.Channel<VideoFrame> _ch =
            System.Threading.Channels.Channel.CreateBounded<VideoFrame>(
                new System.Threading.Channels.BoundedChannelOptions(8)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
                });
        public void Push(VideoFrame f) => _ch.Writer.TryWrite(f);
        public async IAsyncEnumerable<VideoFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (await _ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                while (_ch.Reader.TryRead(out var f)) yield return f;
        }
        public void Dispose() => _ch.Writer.TryComplete();
    }
#endif
}
