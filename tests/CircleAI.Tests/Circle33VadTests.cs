// Circle33VadTests.cs
//
// (3.3.0) Tests for the three voice-activity detectors (Null, Energy, Silero).

using System;
using System.Runtime.InteropServices;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public class Circle33VadTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Null_AlwaysReportsSpeech()
    {
        var vad = NullVoiceActivityDetector.Instance;
        var r = vad.Classify(Silence(160), SampleRate, TimeSpan.Zero);
        Assert.True(r.IsSpeech);
        Assert.Equal("null", vad.BackendId);
    }

    [Fact]
    public void Energy_OnSilence_ReportsNotSpeech()
    {
        var vad = new EnergyVoiceActivityDetector();
        var r = vad.Classify(Silence(480), SampleRate, TimeSpan.Zero);
        Assert.False(r.IsSpeech);
        Assert.True(r.SpeechProbability < 0.5f);
    }

    [Fact]
    public void Energy_OnTone_ReportsSpeech()
    {
        var vad = new EnergyVoiceActivityDetector();
        var r = vad.Classify(Tone(480, frequencyHz: 200, amplitude: 0.5f), SampleRate, TimeSpan.Zero);
        Assert.True(r.IsSpeech);
        Assert.True(r.SpeechProbability >= 0.5f);
    }

    [Fact]
    public void Energy_Hangover_KeepsSpeechAcrossOneSilentFrame()
    {
        var vad = new EnergyVoiceActivityDetector(hangoverFrames: 3);

        // Voiced frame trips hangover counter.
        var first = vad.Classify(Tone(480, 200, 0.5f), SampleRate, TimeSpan.Zero);
        Assert.True(first.IsSpeech);

        // Immediately-after silent frame still reported as speech (hangover).
        var second = vad.Classify(Silence(480), SampleRate, TimeSpan.FromMilliseconds(30));
        Assert.True(second.IsSpeech);
    }

    [Fact]
    public void Energy_Reset_DropsHangover()
    {
        var vad = new EnergyVoiceActivityDetector(hangoverFrames: 5);
        vad.Classify(Tone(480, 200, 0.5f), SampleRate, TimeSpan.Zero);
        vad.Reset();

        // First post-reset silent frame must be silence, since hangover is gone.
        var r = vad.Classify(Silence(480), SampleRate, TimeSpan.FromMilliseconds(30));
        Assert.False(r.IsSpeech);
    }

    [Fact]
    public void Energy_EmptyBuffer_ReturnsSilence()
    {
        var vad = new EnergyVoiceActivityDetector();
        var r = vad.Classify(ReadOnlySpan<byte>.Empty, SampleRate, TimeSpan.Zero);
        Assert.False(r.IsSpeech);
        Assert.Equal(0f, r.SpeechProbability);
    }

    [Fact]
    public void Silero_NoRunner_FallsBackToEnergyOnSilence()
    {
        var vad = new SileroVoiceActivityDetector();
        Assert.Equal("silero (fallback)", vad.BackendId);

        var r = vad.Classify(Silence(480), SampleRate, TimeSpan.Zero);
        Assert.False(r.IsSpeech);
    }

    [Fact]
    public void Silero_WithRunner_UsesItsScore()
    {
        var runner = new FakeRunner(score: 0.9f);
        var vad = new SileroVoiceActivityDetector(runner, speechThreshold: 0.5f);
        Assert.Equal("silero", vad.BackendId);

        var r = vad.Classify(Silence(480), SampleRate, TimeSpan.Zero);
        Assert.True(r.IsSpeech);
        Assert.Equal(0.9f, r.SpeechProbability, 2);
    }

    [Fact]
    public void Silero_BelowThreshold_NotSpeech()
    {
        var runner = new FakeRunner(score: 0.2f);
        var vad = new SileroVoiceActivityDetector(runner, speechThreshold: 0.5f, hangoverFrames: 0);
        var r = vad.Classify(Silence(480), SampleRate, TimeSpan.Zero);
        Assert.False(r.IsSpeech);
    }

    [Fact]
    public void Silero_Hangover_KeepsSpeechAfterDrop()
    {
        var runner = new TogglingRunner(0.9f, 0.1f);
        var vad = new SileroVoiceActivityDetector(runner, speechThreshold: 0.5f, hangoverFrames: 2);

        var a = vad.Classify(Silence(480), SampleRate, TimeSpan.Zero);
        Assert.True(a.IsSpeech);

        var b = vad.Classify(Silence(480), SampleRate, TimeSpan.FromMilliseconds(30));
        Assert.True(b.IsSpeech); // hangover

        var c = vad.Classify(Silence(480), SampleRate, TimeSpan.FromMilliseconds(60));
        Assert.True(c.IsSpeech); // still hangover (count 2)

        var d = vad.Classify(Silence(480), SampleRate, TimeSpan.FromMilliseconds(90));
        Assert.False(d.IsSpeech); // hangover spent
    }

    private static byte[] Silence(int sampleCount)
    {
        var buffer = new byte[sampleCount * 2];
        return buffer;
    }

    private static byte[] Tone(int sampleCount, double frequencyHz, double amplitude)
    {
        var buffer = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            var t = (double)i / SampleRate;
            var s = amplitude * Math.Sin(2 * Math.PI * frequencyHz * t);
            var v = (short)(s * short.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), v);
        }
        return buffer;
    }

    private sealed class FakeRunner : IVadModelRunner
    {
        private readonly float _score;
        public FakeRunner(float score) { _score = score; }
        public float ScoreFrame(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz) => _score;
    }

    private sealed class TogglingRunner : IVadModelRunner
    {
        private readonly float _first;
        private readonly float _rest;
        private int _calls;
        public TogglingRunner(float first, float rest) { _first = first; _rest = rest; }
        public float ScoreFrame(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz)
        {
            _calls++;
            return _calls == 1 ? _first : _rest;
        }
    }
}
