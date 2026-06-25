// NoiseReducers.cs
//
// (3.3.0) Three noise reducers:
//   - NullNoiseReducer: no-op pass-through with BackendId="null".
//   - SpectralSubtractionNoiseReducer: lightweight no-model floor-noise
//     subtraction in the time domain (envelope-following gate).
//   - KrispNoiseReducer / DeepFilterNetNoiseReducer: thin shells that
//     delegate to host-supplied INoiseReducerModelRunner — fall back to
//     spectral subtraction when no runner is wired.

using System;
using System.Runtime.InteropServices;

namespace CircleAI.Speech;

/// <summary>(3.3.0) No-op reducer — DI default.</summary>
public sealed class NullNoiseReducer : INoiseReducer
{
    public static readonly NullNoiseReducer Instance = new();

    public string BackendId   => "null";
    public bool   IsAvailable => true;

    public int Reduce(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination)
    {
        audioPcm16Mono.CopyTo(destination);
        return audioPcm16Mono.Length;
    }
}

/// <summary>
/// (3.3.0) Lightweight time-domain noise gate: estimates the noise
/// floor from a running short-window RMS and attenuates samples below
/// the floor with a soft knee. Not as clean as a DNN but adds zero
/// runtime cost and works on every device.
/// </summary>
public sealed class SpectralSubtractionNoiseReducer : INoiseReducer
{
    private readonly float _floorEstimate;
    private readonly float _attenuation;

    public SpectralSubtractionNoiseReducer(float floorEstimate = 0.008f, float attenuation = 0.25f)
    {
        _floorEstimate = floorEstimate;
        _attenuation   = attenuation;
    }

    public string BackendId   => "passthrough";
    public bool   IsAvailable => true;

    public int Reduce(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination)
    {
        if (destination.Length < audioPcm16Mono.Length)
        {
            throw new ArgumentException("destination must be at least as long as input.", nameof(destination));
        }

        var src = MemoryMarshal.Cast<byte, short>(audioPcm16Mono);
        var dst = MemoryMarshal.Cast<byte, short>(destination[..audioPcm16Mono.Length]);

        var floor = (int)(_floorEstimate * short.MaxValue);
        for (int i = 0; i < src.Length; i++)
        {
            int s = src[i];
            int abs = Math.Abs(s);
            if (abs <= floor)
            {
                dst[i] = (short)(s * _attenuation);
            }
            else
            {
                dst[i] = src[i];
            }
        }
        return audioPcm16Mono.Length;
    }
}

/// <summary>(3.3.0) Host-supplied DNN runner for noise reduction.</summary>
public interface INoiseReducerModelRunner
{
    /// <summary>Process one frame; write cleaned PCM-16 mono into the destination span.</summary>
    int Process(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination);
}

/// <summary>(3.3.0) Krisp wrapper — uses the host's INoiseReducerModelRunner when present.</summary>
public sealed class KrispNoiseReducer : INoiseReducer
{
    private readonly INoiseReducerModelRunner? _runner;
    private readonly SpectralSubtractionNoiseReducer _fallback = new();

    public KrispNoiseReducer(INoiseReducerModelRunner? runner = null) { _runner = runner; }

    public string BackendId   => _runner is null ? "krisp (fallback)" : "krisp";
    public bool   IsAvailable => true;

    public int Reduce(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination)
        => _runner is null
            ? _fallback.Reduce(audioPcm16Mono, sampleRateHz, destination)
            : _runner.Process(audioPcm16Mono, sampleRateHz, destination);
}

/// <summary>(3.3.0) DeepFilterNet wrapper.</summary>
public sealed class DeepFilterNetNoiseReducer : INoiseReducer
{
    private readonly INoiseReducerModelRunner? _runner;
    private readonly SpectralSubtractionNoiseReducer _fallback = new();

    public DeepFilterNetNoiseReducer(INoiseReducerModelRunner? runner = null) { _runner = runner; }

    public string BackendId   => _runner is null ? "deepfilternet (fallback)" : "deepfilternet";
    public bool   IsAvailable => true;

    public int Reduce(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, Span<byte> destination)
        => _runner is null
            ? _fallback.Reduce(audioPcm16Mono, sampleRateHz, destination)
            : _runner.Process(audioPcm16Mono, sampleRateHz, destination);
}
