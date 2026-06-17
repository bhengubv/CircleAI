// CircleAISpeechContractTests.cs
//
// (2.3.0) Contract tests for the Speech pack.

using System;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public sealed class CircleAISpeechContractTests
{
    [Fact]
    public async Task NullSpeechRecognizer_ReturnsEmpty()
    {
        var r = await NullSpeechRecognizer.Instance.TranscribeAsync(ReadOnlyMemory<byte>.Empty, 16000);
        Assert.Equal("", r.Text);
        Assert.Empty(r.Segments);
        Assert.Equal(TimeSpan.Zero, r.TotalDuration);
    }

    [Fact]
    public async Task NullSpeechSynthesizer_ReturnsZeroLengthBuffer()
    {
        var r = await NullSpeechSynthesizer.Instance.SynthesizeAsync("hello");
        Assert.True(r.AudioPcm16Mono.IsEmpty);
        Assert.Equal(16_000, r.SampleRateHz);
        Assert.Equal(TimeSpan.Zero, r.Duration);
    }

    [Fact]
    public async Task NullWakeWordDetector_StartStopAreSafe()
    {
        var det = new NullWakeWordDetector();
        await det.StartAsync();
        await det.StopAsync();
        await det.DisposeAsync();
        Assert.Equal("null", det.BackendId);
    }

    [Fact]
    public void NullWakeWordDetector_SubscribeReturnsDisposable()
    {
        var det = new NullWakeWordDetector();
        using var sub = det.Subscribe(_ => ValueTask.CompletedTask);
        Assert.NotNull(sub);
    }

    [Fact]
    public async Task NullOcr_ReturnsEmpty()
    {
        var r = await NullOpticalCharacterRecognizer.Instance.RecognizeAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Equal("", r.Text);
        Assert.Empty(r.Blocks);
    }

    [Fact]
    public void Records_AreValueEqual()
    {
        var a = new TranscribedSegment("hi", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), "en", 0.9f);
        var b = new TranscribedSegment("hi", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1), "en", 0.9f);
        Assert.Equal(a, b);
    }
}
