// Circle33HoldMusicMixerTests.cs
//
// (3.3.0) Tests for the hold-music mixer.

using System;
using System.Buffers.Binary;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33HoldMusicMixerTests
{
    [Fact]
    public void Mix_NoSpeech_RendersScaledBackground()
    {
        var bg = TonePcm(160, 440, 0.5);
        var mixer = new HoldMusicMixer(bg, backgroundGain: 0.5f);
        var out_ = new byte[bg.Length];

        var written = mixer.MixFrame(ReadOnlySpan<byte>.Empty, out_);

        Assert.Equal(out_.Length, written);
        var inRms  = Rms(bg);
        var outRms = Rms(out_);
        // Background was attenuated by 0.5; expected output RMS ~= 0.5 * input RMS.
        Assert.InRange(outRms / inRms, 0.4, 0.6);
    }

    [Fact]
    public void Mix_WithSpeech_DucksBackground()
    {
        var bg = TonePcm(160, 440, 0.5);
        var mixer = new HoldMusicMixer(bg, backgroundGain: 0.6f, duckedGain: 0.1f);
        var speech = TonePcm(160, 200, 0.5);
        var out_ = new byte[bg.Length];

        mixer.MixFrame(speech, out_);

        // Energy should be roughly speech + 0.1*bg, dominated by speech.
        var speechRms = Rms(speech);
        var outRms    = Rms(out_);
        Assert.True(outRms >= speechRms * 0.8 && outRms < speechRms * 1.5,
            $"outRms={outRms}, speechRms={speechRms}");
    }

    [Fact]
    public void Mix_LoopsBackgroundWhenLongerFrameRequested()
    {
        var bg = TonePcm(80, 200, 0.5); // half a frame
        var mixer = new HoldMusicMixer(bg, backgroundGain: 1.0f);

        var out_ = new byte[160 * 2];
        mixer.MixFrame(ReadOnlySpan<byte>.Empty, out_);

        // Should not throw, output should be non-empty (it loops).
        Assert.True(Rms(out_) > 0);
    }

    [Fact]
    public void Mix_ResetReturnsCursorToStart()
    {
        var bg = TonePcm(80, 200, 0.5);
        var mixer = new HoldMusicMixer(bg, backgroundGain: 1.0f);

        var first = new byte[16];
        mixer.MixFrame(ReadOnlySpan<byte>.Empty, first);
        mixer.Reset();
        var second = new byte[16];
        mixer.MixFrame(ReadOnlySpan<byte>.Empty, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Mix_DestinationTooSmall_Throws()
    {
        var bg = TonePcm(160, 200, 0.5);
        var mixer = new HoldMusicMixer(bg);
        var speech = TonePcm(160, 200, 0.5);
        Assert.Throws<ArgumentException>(() => mixer.MixFrame(speech, new byte[10]));
    }

    [Fact]
    public void Constructor_EmptyLoop_Throws()
    {
        Assert.Throws<ArgumentException>(() => new HoldMusicMixer(new byte[1]));
    }

    [Fact]
    public void Constructor_InvalidGain_Throws()
    {
        var bg = TonePcm(160, 200, 0.5);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoldMusicMixer(bg, backgroundGain: 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoldMusicMixer(bg, duckedGain: -0.1f));
    }

    private static byte[] TonePcm(int samples, double frequencyHz, double amplitude)
    {
        const int rate = 16000;
        var buf = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            var s = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / rate);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 2, 2), (short)(s * short.MaxValue));
        }
        return buf;
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
}
