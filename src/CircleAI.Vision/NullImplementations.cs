// NullImplementations.cs
//
// (2.2.0) Safe null defaults — every interface has a working
// implementation that returns empty/no-op results. Lets the hosting
// layer wire CircleAI.Vision optionally; absence of a real backend
// degrades to deterministic empty answers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Vision;

/// <summary>(2.2.0) No-op vision runtime.</summary>
public sealed class NullComputerVisionRuntime : IComputerVisionRuntime
{
    public static readonly NullComputerVisionRuntime Instance = new();
    public string BackendId => "null";
    public ValueTask<object?> DecodeAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => ValueTask.FromResult<object?>(null);
    public ValueTask<object?> ResizeAsync(object image, int width, int height, CancellationToken ct = default)
        => ValueTask.FromResult<object?>(null);
}

/// <summary>(2.2.0) Returns no faces. Useful as the default DI registration.</summary>
public sealed class NullFaceDetector : IFaceDetector
{
    public static readonly NullFaceDetector Instance = new();
    public ValueTask<IReadOnlyList<DetectedFace>> DetectAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<DetectedFace>>(Array.Empty<DetectedFace>());
}

/// <summary>(2.2.0) Returns a zero-vector at the configured dimension.</summary>
public sealed class NullFaceEmbedder(int dimension = 512) : IFaceEmbedder
{
    public int Dimension { get; } = dimension;
    public ValueTask<FaceEmbedding> EmbedAsync(ReadOnlyMemory<byte> imageBytes, DetectedFace face, CancellationToken ct = default)
        => ValueTask.FromResult(new FaceEmbedding(new float[Dimension], Dimension));
}

/// <summary>(2.2.0) Reports "no liveness backend" — fail-closed default.</summary>
public sealed class NullFaceLivenessDetector : IFaceLivenessDetector
{
    public static readonly NullFaceLivenessDetector Instance = new();
    public ValueTask<LivenessResult> CheckAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => ValueTask.FromResult(new LivenessResult(IsLive: false, Confidence: 0f, FailureReason: "no liveness backend registered"));
}

/// <summary>(2.2.0) Reports unverified — fail-closed default.</summary>
public sealed class NullDocumentVerifier : IDocumentVerifier
{
    public static readonly NullDocumentVerifier Instance = new();
    public ValueTask<DocumentVerificationResult> VerifyAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => ValueTask.FromResult(new DocumentVerificationResult(
            IsValid:            false,
            DocumentType:       "unknown",
            IssuingCountry:     "unknown",
            Fields:             Array.Empty<DocumentField>(),
            OverallConfidence:  0f,
            Warnings:           new[] { "no document verifier backend registered" }));
}

/// <summary>(2.2.0) Returns no plates.</summary>
public sealed class NullPlateRecognizer : IPlateRecognizer
{
    public static readonly NullPlateRecognizer Instance = new();
    public ValueTask<IReadOnlyList<PlateRecognitionResult>> RecognizeAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<PlateRecognitionResult>>(Array.Empty<PlateRecognitionResult>());
}

/// <summary>(2.2.0) Reports no anomalies; subscribers never fire.</summary>
public sealed class NullBluetoothAnomalyDetector : IBluetoothAnomalyDetector
{
    public string BackendId => "null";
    public IDisposable Subscribe(Func<BluetoothAnomaly, ValueTask> handler) => EmptyDisposable.Instance;
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default)  => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
