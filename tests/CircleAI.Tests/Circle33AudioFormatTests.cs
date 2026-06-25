// Circle33AudioFormatTests.cs
//
// (3.3.0) Tests for audio format conversion.

using System;
using System.Buffers.Binary;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public class Circle33AudioFormatTests
{
    [Fact]
    public void Pcm16_To_Pcm16_SameRate_IsIdentity()
    {
        var input = TonePcm16(160, 440, 0.5);
        var output = AudioFormatConverter.Convert(input, AudioCodec.Pcm16, 16000, AudioCodec.Pcm16, 16000);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Pcm16_Resample_16kHz_To_24kHz_ChangesLength()
    {
        var input = TonePcm16(160, 440, 0.5);
        var output = AudioFormatConverter.Convert(input, AudioCodec.Pcm16, 16000, AudioCodec.Pcm16, 24000);
        Assert.Equal(160 * 3 / 2 * 2, output.Length);
    }

    [Fact]
    public void Pcm16_Resample_16kHz_To_8kHz_HalvesLength()
    {
        var input = TonePcm16(160, 440, 0.5);
        var output = AudioFormatConverter.Convert(input, AudioCodec.Pcm16, 16000, AudioCodec.Pcm16, 8000);
        Assert.Equal(160 / 2 * 2, output.Length);
    }

    [Fact]
    public void MuLaw_RoundTrip_PreservesShape()
    {
        var pcm    = TonePcm16(160, 200, 0.5);
        var mulaw  = AudioFormatConverter.EncodePcm16ToMuLaw(pcm);
        var back   = AudioFormatConverter.DecodeMuLawToPcm16(mulaw);

        // μ-law is lossy but RMS should be close.
        var beforeRms = Rms(pcm);
        var afterRms  = Rms(back);
        Assert.True(Math.Abs(beforeRms - afterRms) / beforeRms < 0.15);
    }

    [Fact]
    public void ALaw_RoundTrip_PreservesShape()
    {
        var pcm   = TonePcm16(160, 200, 0.5);
        var alaw  = AudioFormatConverter.EncodePcm16ToALaw(pcm);
        var back  = AudioFormatConverter.DecodeALawToPcm16(alaw);

        var beforeRms = Rms(pcm);
        var afterRms  = Rms(back);
        Assert.True(Math.Abs(beforeRms - afterRms) / beforeRms < 0.15);
    }

    [Fact]
    public void Convert_MuLaw8k_To_Pcm16_16k_LengthQuadruples()
    {
        var mulaw  = AudioFormatConverter.EncodePcm16ToMuLaw(TonePcm16(80, 200, 0.5));
        var pcm16  = AudioFormatConverter.Convert(mulaw, AudioCodec.MuLaw, 8000, AudioCodec.Pcm16, 16000);
        Assert.Equal(80 * 2 * 2, pcm16.Length);
    }

    [Fact]
    public void Convert_Pcm16_24k_To_MuLaw_8k_LengthShrinksToOneSixth()
    {
        var pcm = TonePcm16(240, 200, 0.5);
        var mulaw = AudioFormatConverter.Convert(pcm, AudioCodec.Pcm16, 24000, AudioCodec.MuLaw, 8000);
        Assert.Equal(80, mulaw.Length);
    }

    [Fact]
    public void Convert_ZeroSampleRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioFormatConverter.Convert(new byte[2], AudioCodec.Pcm16, 0, AudioCodec.Pcm16, 16000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioFormatConverter.Convert(new byte[2], AudioCodec.Pcm16, 16000, AudioCodec.Pcm16, 0));
    }

    [Fact]
    public void Resample_PreservesSilence()
    {
        var silence = new byte[160 * 2];
        var resampled = AudioFormatConverter.ResamplePcm16Linear(silence, 16000, 24000);
        Assert.All(resampled, b => Assert.Equal(0, b));
    }

    private static byte[] TonePcm16(int samples, double frequencyHz, double amplitude)
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
        for (int i = 0; i < data.Length; i += 2)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2));
            sum += s * s;
        }
        return Math.Sqrt(sum / (data.Length / 2));
    }
}
