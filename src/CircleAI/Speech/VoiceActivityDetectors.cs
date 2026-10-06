// VoiceActivityDetectors.cs
//
// (3.3.0) Three voice-activity detectors:
//   - NullVoiceActivityDetector: always-speech, used as DI default.
//   - EnergyVoiceActivityDetector: RMS energy + zero-crossing rate +
//     hangover frames. Works on every device, no model needed.
//   - SileroVoiceActivityDetector: wraps a host-supplied IVadModelRunner
//     (ONNX runner). The runner is null by default — falls back to
//     energy-based output until a host wires the real model.

using System;
using System.Runtime.InteropServices;

namespace CircleAI.Speech;

/// <summary>(3.3.0) Always reports speech — DI default so nothing breaks before a real VAD is wired.</summary>
public sealed class NullVoiceActivityDetector : IVoiceActivityDetector
{
    public static readonly NullVoiceActivityDetector Instance = new();

    public string BackendId       => "null";
    public float  SpeechThreshold => 0.5f;

    public VadFrameResult Classify(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, TimeSpan offset)
        => new(IsSpeech: true, SpeechProbability: 1f, offset);

    public void Reset() { }
}

/// <summary>
/// (3.3.0) Production-grade VAD using RMS energy + zero-crossing rate +
/// hangover-frame smoothing. No ML model required — works on every
/// device. Calibrated against typical phone-call mu-law and 16 kHz
/// PCM-16 inputs.
/// </summary>
public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly float _energyThreshold;
    private readonly int   _hangoverFrames;
    private int _hangoverRemaining;

    public EnergyVoiceActivityDetector(
        float speechThreshold = 0.55f,
        float energyThreshold = 0.012f,
        int   hangoverFrames  = 8)
    {
        SpeechThreshold  = speechThreshold;
        _energyThreshold = energyThreshold;
        _hangoverFrames  = hangoverFrames;
    }

    public string BackendId       => "energy";
    public float  SpeechThreshold { get; }

    public VadFrameResult Classify(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, TimeSpan offset)
    {
        if (audioPcm16Mono.Length < 2)
        {
            return new VadFrameResult(IsSpeech: false, SpeechProbability: 0f, offset);
        }

        var samples = MemoryMarshal.Cast<byte, short>(audioPcm16Mono);
        double sumSquares = 0;
        int    zeroCrossings = 0;
        short  previous = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int s = samples[i];
            sumSquares += s * s;
            if (i > 0 && Math.Sign(s) != Math.Sign(previous) && s != 0 && previous != 0)
            {
                zeroCrossings++;
            }
            previous = samples[i];
        }
        var rms     = Math.Sqrt(sumSquares / samples.Length) / short.MaxValue; // 0..1
        var zcrRate = (float)zeroCrossings / samples.Length;

        // Speech: high RMS + moderate ZCR (~0.05–0.25 for voiced speech).
        var energyGood = rms     >= _energyThreshold;
        var zcrGood   = zcrRate >= 0.02f && zcrRate <= 0.30f;
        var rawProb   = energyGood ? (zcrGood ? 0.85f : 0.6f) : 0.1f;

        bool isSpeech;
        if (rawProb >= SpeechThreshold)
        {
            isSpeech = true;
            _hangoverRemaining = _hangoverFrames;
        }
        else if (_hangoverRemaining > 0)
        {
            isSpeech = true;
            _hangoverRemaining--;
            rawProb = Math.Max(rawProb, SpeechThreshold);
        }
        else
        {
            isSpeech = false;
        }

        return new VadFrameResult(isSpeech, rawProb, offset);
    }

    public void Reset() => _hangoverRemaining = 0;
}

/// <summary>(3.3.0) ONNX model runner contract supplied by the host package.</summary>
public interface IVadModelRunner
{
    /// <summary>Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1.</summary>
    float ScoreFrame(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz);
}

/// <summary>
/// (3.3.0) Silero VAD wrapper. Delegates the per-frame score to a host
/// <see cref="IVadModelRunner"/>; when no runner is wired it transparently
/// falls back to <see cref="EnergyVoiceActivityDetector"/>'s scoring.
/// </summary>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly IVadModelRunner? _runner;
    private readonly EnergyVoiceActivityDetector _fallback;
    private readonly int _hangoverFrames;
    private int _hangoverRemaining;

    public SileroVoiceActivityDetector(
        IVadModelRunner? runner          = null,
        float            speechThreshold = 0.5f,
        int              hangoverFrames  = 8)
    {
        _runner          = runner;
        _fallback        = new EnergyVoiceActivityDetector(speechThreshold);
        SpeechThreshold  = speechThreshold;
        _hangoverFrames  = hangoverFrames;
    }

    public string BackendId       => _runner is null ? "silero (fallback)" : "silero";
    public float  SpeechThreshold { get; }

    public VadFrameResult Classify(ReadOnlySpan<byte> audioPcm16Mono, int sampleRateHz, TimeSpan offset)
    {
        if (_runner is null)
        {
            return _fallback.Classify(audioPcm16Mono, sampleRateHz, offset);
        }

        var prob = _runner.ScoreFrame(audioPcm16Mono, sampleRateHz);
        bool isSpeech;
        if (prob >= SpeechThreshold)
        {
            isSpeech = true;
            _hangoverRemaining = _hangoverFrames;
        }
        else if (_hangoverRemaining > 0)
        {
            isSpeech = true;
            _hangoverRemaining--;
        }
        else
        {
            isSpeech = false;
        }
        return new VadFrameResult(isSpeech, prob, offset);
    }

    public void Reset()
    {
        _hangoverRemaining = 0;
        _fallback.Reset();
    }
}
