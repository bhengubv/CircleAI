// Circle33EchoCancellerTests.cs
//
// (3.3.0) Tests for echo cancellers.

using System;
using System.Buffers.Binary;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public class Circle33EchoCancellerTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Null_PassesAudioThroughUnchanged()
    {
        var c = NullEchoCanceller.Instance;
        var near = TonePcm(160, 440, 0.4);
        var far  = TonePcm(160, 440, 0.2);
        var out_ = new byte[near.Length];

        var written = c.Cancel(near, far, SampleRate, out_);

        Assert.Equal(near.Length, written);
        Assert.Equal(near, out_);
    }

    [Fact]
    public void Nlms_ReducesEchoCorrelatedWithFarEnd()
    {
        var c = new Nlms256Steps(c => new NlmsEchoCanceller(filterLength: 64, stepSize: 0.5f));
        var farTone = TonePcm(640, 200, 0.5);
        // near-end = a copy of far-end (pure echo) + a tiny voice signal.
        var near = MixPcm(farTone, TonePcm(640, 1500, 0.05));

        var out_ = new byte[near.Length];
        c.Underlying.Cancel(near, farTone, SampleRate, out_);

        // After NLMS adaptation the output should have less energy than the input.
        var beforeRms = Rms(near);
        var afterRms  = Rms(out_);
        Assert.True(afterRms < beforeRms * 0.9, $"expected reduction; before={beforeRms} after={afterRms}");
    }

    [Fact]
    public void Nlms_ZeroFarEnd_LeavesNearAlone()
    {
        var c = new NlmsEchoCanceller();
        var near = TonePcm(160, 440, 0.5);
        var far  = new byte[near.Length]; // silence
        var out_ = new byte[near.Length];

        c.Cancel(near, far, SampleRate, out_);

        var beforeRms = Rms(near);
        var afterRms  = Rms(out_);
        Assert.True(afterRms > beforeRms * 0.8);
    }

    [Fact]
    public void Nlms_LengthMismatch_Throws()
    {
        var c = new NlmsEchoCanceller();
        Assert.Throws<ArgumentException>(() =>
            c.Cancel(new byte[100], new byte[80], SampleRate, new byte[100]));
    }

    [Fact]
    public void Nlms_DestinationTooSmall_Throws()
    {
        var c = new NlmsEchoCanceller();
        Assert.Throws<ArgumentException>(() =>
            c.Cancel(new byte[100], new byte[100], SampleRate, new byte[50]));
    }

    [Fact]
    public void Nlms_Reset_ClearsAdaptedState()
    {
        var c = new NlmsEchoCanceller(filterLength: 32);
        var tone = TonePcm(320, 200, 0.5);
        var out_ = new byte[tone.Length];
        c.Cancel(tone, tone, SampleRate, out_);
        c.Reset();
        c.Cancel(tone, new byte[tone.Length], SampleRate, out_);
        Assert.True(Rms(out_) > 100);
    }

    [Fact]
    public void WebRtc_NoRunner_FallsBackToNlms()
    {
        var c = new WebRtcEchoCanceller();
        Assert.Equal("webrtc-aec3 (fallback)", c.BackendId);

        var tone = TonePcm(160, 440, 0.4);
        var out_ = new byte[tone.Length];
        var written = c.Cancel(tone, tone, SampleRate, out_);
        Assert.Equal(tone.Length, written);
    }

    [Fact]
    public void WebRtc_WithRunner_Delegates()
    {
        var runner = new RecordingRunner();
        var c = new WebRtcEchoCanceller(runner);
        Assert.Equal("webrtc-aec3", c.BackendId);

        var tone = TonePcm(160, 440, 0.4);
        var out_ = new byte[tone.Length];
        c.Cancel(tone, tone, SampleRate, out_);

        Assert.Equal(1, runner.Calls);
        c.Reset();
        Assert.True(runner.Reset);
    }

    private static byte[] TonePcm(int samples, double frequencyHz, double amplitude)
    {
        var buf = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            var t = (double)i / SampleRate;
            var s = amplitude * Math.Sin(2 * Math.PI * frequencyHz * t);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2, 2), (short)(s * short.MaxValue));
        }
        return buf;
    }

    private static byte[] MixPcm(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        var buf = new byte[len];
        for (int i = 0; i < len; i += 2)
        {
            int va = BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(i, 2));
            int vb = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i, 2));
            int mixed = Math.Clamp(va + vb, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i, 2), (short)mixed);
        }
        return buf;
    }

    private static double Rms(byte[] data)
    {
        double sum = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2));
            sum += s * s;
        }
        return Math.Sqrt(sum / (data.Length / 2));
    }

    /// <summary>Convenience wrapper so we can keep the underlying NLMS reference for assertion.</summary>
    private sealed class Nlms256Steps
    {
        public NlmsEchoCanceller Underlying { get; }
        public Nlms256Steps(Func<int, NlmsEchoCanceller> _) { Underlying = new NlmsEchoCanceller(filterLength: 64, stepSize: 0.5f); }
    }

    private sealed class RecordingRunner : IEchoCancellerModelRunner
    {
        public int  Calls { get; private set; }
        public bool Reset { get; private set; }
        public int Process(ReadOnlySpan<byte> nearEnd, ReadOnlySpan<byte> farEnd, int sampleRateHz, Span<byte> destination)
        {
            Calls++;
            nearEnd.CopyTo(destination);
            return nearEnd.Length;
        }
        void IEchoCancellerModelRunner.Reset() { Reset = true; }
    }
}
