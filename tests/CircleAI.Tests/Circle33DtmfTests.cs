// Circle33DtmfTests.cs
//
// (3.3.0) Tests for DTMF tone generator.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33DtmfTests
{
    [Fact]
    public void Generate_KnownDigit_ProducesNonSilence()
    {
        var pcm = DtmfToneGenerator.Generate('5', sampleRateHz: 8000, durationMs: 100);
        Assert.Equal(8000 / 10 * 2, pcm.Length);
        Assert.True(Rms(pcm) > 100);
    }

    [Fact]
    public void Generate_AllDigits_Supported()
    {
        foreach (var d in "0123456789*#ABCD")
        {
            var pcm = DtmfToneGenerator.Generate(d, sampleRateHz: 8000, durationMs: 50);
            Assert.True(pcm.Length > 0);
        }
    }

    [Fact]
    public void Generate_UnsupportedDigit_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DtmfToneGenerator.Generate('Z', sampleRateHz: 8000));
    }

    [Fact]
    public void Generate_ZeroSampleRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DtmfToneGenerator.Generate('1', sampleRateHz: 0));
    }

    [Fact]
    public void Generate_ZeroDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DtmfToneGenerator.Generate('1', sampleRateHz: 8000, durationMs: 0));
    }

    [Fact]
    public void GenerateSequence_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(DtmfToneGenerator.GenerateSequence("", sampleRateHz: 8000));
    }

    [Fact]
    public void GenerateSequence_ContainsGapBetweenTones()
    {
        var pcm = DtmfToneGenerator.GenerateSequence("12",
            sampleRateHz:    8000,
            toneDurationMs:  50,
            interDigitGapMs: 25);
        // 2 tones (50ms each) + 1 gap (25ms) = 125ms total.
        var expectedSamples = 8000 * 125 / 1000;
        Assert.Equal(expectedSamples * 2, pcm.Length);
    }

    [Fact]
    public async Task SendThroughSession_PushesAudioFrame()
    {
        var session = new RecordingCallSession();
        await DtmfToneGenerator.SendThroughSessionAsync(session, "123",
            sampleRateHz: 16000, toneDurationMs: 30, interDigitGapMs: 10);

        Assert.True(session.AudioFrameCount > 0);
        var frame = session.LastFrame!;
        Assert.Equal(CallMediaFormat.Pcm16000, frame.Format);
    }

    private static double Rms(byte[] data)
    {
        double sum = 0;
        int n = data.Length / 2;
        for (int i = 0; i < n; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2, 2));
            sum += s * s;
        }
        return Math.Sqrt(sum / n);
    }

    private sealed class RecordingCallSession : ICallSession
    {
        public CallInfo  Info   { get; } = new("c", CallDirection.Inbound, "+1", "+2", "fake", CallMediaFormat.Pcm24000, DateTimeOffset.UtcNow);
        public CallStatus Status => CallStatus.Active;
        public int AudioFrameCount { get; private set; }
        public AudioFrame? LastFrame { get; private set; }
        public event EventHandler<CallStatus>? StatusChanged { add { } remove { } }

        public async IAsyncEnumerable<AudioFrame> ReceiveAudioAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendAudioAsync(AudioFrame frame, CancellationToken ct = default)
        {
            AudioFrameCount++;
            LastFrame = frame;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DtmfEvent> ReceiveDtmfAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public ValueTask SendDtmfAsync(string digits, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask TransferAsync(string t, TransferMode m, string? b = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask HangUpAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
