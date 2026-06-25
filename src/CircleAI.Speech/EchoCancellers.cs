// EchoCancellers.cs
//
// (3.3.0) Three echo cancellers:
//   - NullEchoCanceller: pass-through DI default.
//   - NlmsEchoCanceller: normalised-LMS adaptive filter.
//   - WebRtcEchoCanceller: shell that delegates to a host-supplied
//     IEchoCancellerModelRunner (the WebRTC AEC3 implementation lives
//     in the host package).

using System;
using System.Buffers.Binary;

namespace CircleAI.Speech;

/// <summary>(3.3.0) Pass-through DI default.</summary>
public sealed class NullEchoCanceller : IEchoCanceller
{
    public static readonly NullEchoCanceller Instance = new();
    public string BackendId => "null";

    public int Cancel(
        ReadOnlySpan<byte> nearEndMicrophone,
        ReadOnlySpan<byte> farEndReference,
        int                sampleRateHz,
        Span<byte>         destination)
    {
        nearEndMicrophone.CopyTo(destination);
        return nearEndMicrophone.Length;
    }

    public void Reset() { }
}

/// <summary>
/// (3.3.0) Normalised LMS adaptive-filter AEC. Pure C#, no model
/// downloads, runs on every device. Filter length defaults to 256 taps
/// (~16 ms @ 16 kHz) which covers typical phone-call echo paths.
/// </summary>
public sealed class NlmsEchoCanceller : IEchoCanceller
{
    private readonly float[] _w;
    private readonly float _stepSize;
    private readonly float _epsilon;
    private readonly int   _filterLength;
    private readonly float[] _refBuffer;
    private int _refIndex;

    public NlmsEchoCanceller(int filterLength = 256, float stepSize = 0.4f, float epsilon = 1e-6f)
    {
        _filterLength = filterLength;
        _stepSize     = stepSize;
        _epsilon      = epsilon;
        _w            = new float[filterLength];
        _refBuffer    = new float[filterLength];
    }

    public string BackendId => "nlms";

    public int Cancel(
        ReadOnlySpan<byte> nearEndMicrophone,
        ReadOnlySpan<byte> farEndReference,
        int                sampleRateHz,
        Span<byte>         destination)
    {
        if (nearEndMicrophone.Length != farEndReference.Length)
        {
            throw new ArgumentException("near-end and far-end must be the same length.", nameof(farEndReference));
        }
        if (destination.Length < nearEndMicrophone.Length)
        {
            throw new ArgumentException("destination must be at least as long as input.", nameof(destination));
        }

        int sampleCount = nearEndMicrophone.Length / 2;
        for (int n = 0; n < sampleCount; n++)
        {
            float micSample = BinaryPrimitives.ReadInt16LittleEndian(nearEndMicrophone.Slice(n * 2, 2)) / (float)short.MaxValue;
            float farSample = BinaryPrimitives.ReadInt16LittleEndian(farEndReference.Slice(n * 2, 2)) / (float)short.MaxValue;

            // Push far-end into circular reference buffer.
            _refBuffer[_refIndex] = farSample;

            // Estimated echo: dot(w, ref).
            float echoEstimate = 0;
            float power        = _epsilon;
            for (int k = 0; k < _filterLength; k++)
            {
                int rIdx = (_refIndex - k + _filterLength) % _filterLength;
                var x = _refBuffer[rIdx];
                echoEstimate += _w[k] * x;
                power        += x * x;
            }

            // Error = mic - echo estimate.
            float error = micSample - echoEstimate;

            // Update filter weights.
            float mu = _stepSize / power;
            for (int k = 0; k < _filterLength; k++)
            {
                int rIdx = (_refIndex - k + _filterLength) % _filterLength;
                _w[k] += mu * error * _refBuffer[rIdx];
            }

            _refIndex = (_refIndex + 1) % _filterLength;

            // Clamp + write.
            int outSample = (int)Math.Clamp(error * short.MaxValue, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(n * 2, 2), (short)outSample);
        }

        return nearEndMicrophone.Length;
    }

    public void Reset()
    {
        Array.Clear(_w);
        Array.Clear(_refBuffer);
        _refIndex = 0;
    }
}

/// <summary>(3.3.0) Host-supplied AEC model runner (e.g. WebRTC AEC3).</summary>
public interface IEchoCancellerModelRunner
{
    int Process(
        ReadOnlySpan<byte> nearEnd,
        ReadOnlySpan<byte> farEnd,
        int                sampleRateHz,
        Span<byte>         destination);

    void Reset();
}

/// <summary>(3.3.0) WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired.</summary>
public sealed class WebRtcEchoCanceller : IEchoCanceller
{
    private readonly IEchoCancellerModelRunner? _runner;
    private readonly NlmsEchoCanceller _fallback = new();

    public WebRtcEchoCanceller(IEchoCancellerModelRunner? runner = null) { _runner = runner; }

    public string BackendId => _runner is null ? "webrtc-aec3 (fallback)" : "webrtc-aec3";

    public int Cancel(
        ReadOnlySpan<byte> nearEndMicrophone,
        ReadOnlySpan<byte> farEndReference,
        int                sampleRateHz,
        Span<byte>         destination)
        => _runner is null
            ? _fallback.Cancel(nearEndMicrophone, farEndReference, sampleRateHz, destination)
            : _runner.Process(nearEndMicrophone, farEndReference, sampleRateHz, destination);

    public void Reset()
    {
        _fallback.Reset();
        _runner?.Reset();
    }
}
