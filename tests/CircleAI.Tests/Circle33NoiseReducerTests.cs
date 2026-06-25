// Circle33NoiseReducerTests.cs
//
// (3.3.0) Tests for noise reducers.

using System;
using System.Buffers.Binary;
using CircleAI.Speech;
using Xunit;

namespace CircleAI.Tests;

public class Circle33NoiseReducerTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Null_PassesAudioThroughUnchanged()
    {
        var r = NullNoiseReducer.Instance;
        var input  = TonePcm(160, frequencyHz: 440, amplitude: 0.5);
        var output = new byte[input.Length];

        var written = r.Reduce(input, SampleRate, output);

        Assert.Equal(input.Length, written);
        Assert.Equal(input, output);
        Assert.Equal("null", r.BackendId);
    }

    [Fact]
    public void Spectral_AttenuatesLowAmplitudeSignal()
    {
        var r = new SpectralSubtractionNoiseReducer(floorEstimate: 0.05f, attenuation: 0.25f);
        var input  = TonePcm(160, frequencyHz: 440, amplitude: 0.01); // below floor
        var output = new byte[input.Length];

        r.Reduce(input, SampleRate, output);

        var inEnergy  = Rms(input);
        var outEnergy = Rms(output);
        Assert.True(outEnergy < inEnergy * 0.5);
    }

    [Fact]
    public void Spectral_LeavesLoudSignalIntact()
    {
        var r = new SpectralSubtractionNoiseReducer(floorEstimate: 0.005f);
        var input  = TonePcm(160, frequencyHz: 440, amplitude: 0.6);
        var output = new byte[input.Length];

        r.Reduce(input, SampleRate, output);

        var inEnergy  = Rms(input);
        var outEnergy = Rms(output);
        Assert.True(outEnergy > inEnergy * 0.9);
    }

    [Fact]
    public void Spectral_RejectsSmallDestination()
    {
        var r = new SpectralSubtractionNoiseReducer();
        Assert.Throws<ArgumentException>(() =>
            r.Reduce(new byte[100], SampleRate, new byte[50]));
    }

    [Fact]
    public void Krisp_NoRunner_FallsBackToSpectral()
    {
        var r = new KrispNoiseReducer();
        Assert.Equal("krisp (fallback)", r.BackendId);
        var input  = TonePcm(160, 440, 0.5);
        var output = new byte[input.Length];
        var written = r.Reduce(input, SampleRate, output);
        Assert.Equal(input.Length, written);
    }

    [Fact]
    public void Krisp_WithRunner_DelegatesToIt()
    {
        var runner = new RecordingRunner();
        var r = new KrispNoiseReducer(runner);
        Assert.Equal("krisp", r.BackendId);
        var input  = TonePcm(160, 440, 0.5);
        var output = new byte[input.Length];
        r.Reduce(input, SampleRate, output);
        Assert.True(runner.CallCount > 0);
    }

    [Fact]
    public void DeepFilterNet_NoRunner_FallsBackToSpectral()
    {
        var r = new DeepFilterNetNoiseReducer();
        Assert.Equal("deepfilternet (fallback)", r.BackendId);
        var input  = TonePcm(160, 440, 0.5);
        var output = new byte[input.Length];
        var written = r.Reduce(input, SampleRate, output);
        Assert.Equal(input.Length, written);
    }

    [Fact]
    public void DeepFilterNet_WithRunner_DelegatesToIt()
    {
        var runner = new RecordingRunner();
        var r = new DeepFilterNetNoiseReducer(runner);
        Assert.Equal("deepfilternet", r.BackendId);
        var input  = TonePcm(160, 440, 0.5);
        var output = new byte[input.Length];
        r.Reduce(input, SampleRate, output);
        Assert.Equal(1, runner.CallCount);
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

    private sealed class RecordingRunner : INoiseReducerModelRunner
    {
        public int CallCount { get; private set; }
        public int Process(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination)
        {
            CallCount++;
            audioPcm16Mono.CopyTo(destination);
            return audioPcm16Mono.Length;
        }
    }
}
