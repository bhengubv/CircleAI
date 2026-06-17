// CircleAIVisionContractTests.cs
//
// (2.2.0) Surface tests for the Vision pack contract layer. The Null
// implementations are deterministic, so we can verify they behave as
// the fail-closed contract requires.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Vision;
using Xunit;

namespace CircleAI.Tests;

public sealed class CircleAIVisionContractTests
{
    [Fact]
    public async Task NullFaceDetector_ReturnsEmpty()
    {
        var faces = await NullFaceDetector.Instance.DetectAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Empty(faces);
    }

    [Fact]
    public async Task NullFaceEmbedder_ReturnsZeroVectorAtDimension()
    {
        var embedder = new NullFaceEmbedder(dimension: 384);
        var emb      = await embedder.EmbedAsync(ReadOnlyMemory<byte>.Empty,
            new DetectedFace(new BoundingBox(0, 0, 10, 10), 0.9f));
        Assert.Equal(384, emb.Dimension);
        Assert.Equal(384, emb.Vector.Length);
        Assert.All(emb.Vector, v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task NullFaceLivenessDetector_FailsClosed()
    {
        var r = await NullFaceLivenessDetector.Instance.CheckAsync(ReadOnlyMemory<byte>.Empty);
        Assert.False(r.IsLive);
        Assert.Equal(0f, r.Confidence);
        Assert.Contains("backend", r.FailureReason ?? "");
    }

    [Fact]
    public async Task NullDocumentVerifier_FailsClosed()
    {
        var r = await NullDocumentVerifier.Instance.VerifyAsync(ReadOnlyMemory<byte>.Empty);
        Assert.False(r.IsValid);
        Assert.NotNull(r.Warnings);
        Assert.NotEmpty(r.Warnings!);
    }

    [Fact]
    public async Task NullPlateRecognizer_ReturnsEmpty()
    {
        var hits = await NullPlateRecognizer.Instance.RecognizeAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task NullBluetoothAnomalyDetector_StartStopAreSafe()
    {
        var det = new NullBluetoothAnomalyDetector();
        await det.StartAsync();
        await det.StopAsync();
        await det.DisposeAsync();
        Assert.Equal("null", det.BackendId);
    }

    [Fact]
    public void NullBluetoothAnomalyDetector_SubscribeIsCallableButNeverFires()
    {
        var det = new NullBluetoothAnomalyDetector();
        var fired = false;
        using var sub = det.Subscribe(_ => { fired = true; return ValueTask.CompletedTask; });
        Assert.False(fired); // never fires; just verifies the surface is callable.
    }

    [Fact]
    public void Primitives_BoundingBoxAndLandmarkPoint_AreValueTypes()
    {
        var b1 = new BoundingBox(1, 2, 3, 4);
        var b2 = new BoundingBox(1, 2, 3, 4);
        Assert.Equal(b1, b2);

        var p1 = new LandmarkPoint(7, 8);
        var p2 = new LandmarkPoint(7, 8);
        Assert.Equal(p1, p2);
    }
}
