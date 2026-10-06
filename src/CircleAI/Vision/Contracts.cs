// Contracts.cs
//
// (2.2.0) The CircleAI.Vision contract surface. Null implementations
// ship out of the box; real backends — compv (CV foundation), facex
// (face stack), FaceLivenessDetection-SDK, KYC-Documents-Verif-SDK,
// ultimateALPR-SDK, Bluehound — land in 2.2.1 when the C++ SDKs are
// vendored under native/<sdk>/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision;

/// <summary>
/// (2.2.0) Generic CV-runtime primitive. The 2.2.1 ship swaps this for
/// the compv-backed implementation; consumers that need basic image
/// decoding / resize / colour-space ops dispatch through this surface.
/// </summary>
public interface IComputerVisionRuntime
{
    /// <summary>Decode bytes into a backend-private opaque image.</summary>
    ValueTask<object?> DecodeAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default);

    /// <summary>Resize an opaque image. Returns a new opaque image.</summary>
    ValueTask<object?> ResizeAsync(object image, int width, int height, CancellationToken ct = default);

    /// <summary>Backend self-identification — "compv-3.x", "null", etc.</summary>
    string BackendId { get; }
}

/// <summary>(2.2.0) Find faces in an image.</summary>
public interface IFaceDetector
{
    ValueTask<IReadOnlyList<DetectedFace>> DetectAsync(
        ReadOnlyMemory<byte> imageBytes,
        CancellationToken    ct = default);
}

/// <summary>(2.2.0) Convert a detected face into a similarity-search vector.</summary>
public interface IFaceEmbedder
{
    int Dimension { get; }

    ValueTask<FaceEmbedding> EmbedAsync(
        ReadOnlyMemory<byte> imageBytes,
        DetectedFace         face,
        CancellationToken    ct = default);
}

/// <summary>(2.2.0) Decide whether the camera is looking at a real person.</summary>
public interface IFaceLivenessDetector
{
    ValueTask<LivenessResult> CheckAsync(
        ReadOnlyMemory<byte> imageBytes,
        CancellationToken    ct = default);
}

/// <summary>(2.2.0) Parse + verify a KYC document image.</summary>
public interface IDocumentVerifier
{
    ValueTask<DocumentVerificationResult> VerifyAsync(
        ReadOnlyMemory<byte> imageBytes,
        CancellationToken    ct = default);
}

/// <summary>(2.2.0) Read a license plate from an image.</summary>
public interface IPlateRecognizer
{
    ValueTask<IReadOnlyList<PlateRecognitionResult>> RecognizeAsync(
        ReadOnlyMemory<byte> imageBytes,
        CancellationToken    ct = default);
}

/// <summary>
/// (2.2.0) Surface for AetherNet adversary detection — BLE / RF anomalies
/// raised by the platform's Bluetooth radio. Implementations are
/// long-running (`StartAsync`/`StopAsync` lifecycle).
/// </summary>
public interface IBluetoothAnomalyDetector : IAsyncDisposable
{
    /// <summary>Subscribe to anomaly events. Returns an unsubscribe handle.</summary>
    IDisposable Subscribe(Func<BluetoothAnomaly, ValueTask> handler);

    /// <summary>Begin monitoring. Idempotent.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop monitoring. Idempotent.</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>Backend self-identification — "bluehound-1.x", "null", etc.</summary>
    string BackendId { get; }
}
