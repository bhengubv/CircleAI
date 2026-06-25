// AnsweringMachineDetector.cs
//
// (3.3.0) Heuristic AMD: classify whether the answering side of an
// outbound call is a human or an answering machine, based on the
// length of the first contiguous speech burst and the timing of any
// follow-up audio. Cheaper than carrier-side AMD; runs on the audio
// frames we already have, no extra cost.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Verdict from the answering-machine detector.</summary>
public enum AmdVerdict { Unknown, Human, AnsweringMachine }

/// <summary>(3.3.0) Heuristic AMD configuration.</summary>
/// <param name="HumanMaxFirstUtteranceMs">Above this length, it's likely a machine. Default 1800 ms.</param>
/// <param name="HumanMinFirstUtteranceMs">Below this it's too short to decide. Default 300 ms.</param>
/// <param name="MaxObservationWindow">Stop accumulating once this elapses. Default 3500 ms.</param>
/// <param name="SilenceFrameThresholdMs">Frames silent for this long end the current utterance. Default 250 ms.</param>
public sealed record AmdOptions(
    int? HumanMaxFirstUtteranceMs  = null,
    int? HumanMinFirstUtteranceMs  = null,
    int? MaxObservationWindow      = null,
    int? SilenceFrameThresholdMs   = null)
{
    public int HumanMaxFirstUtteranceMsOrDefault  => HumanMaxFirstUtteranceMs ?? 1800;
    public int HumanMinFirstUtteranceMsOrDefault  => HumanMinFirstUtteranceMs ?? 300;
    public int MaxObservationWindowOrDefault      => MaxObservationWindow     ?? 3500;
    public int SilenceFrameThresholdMsOrDefault   => SilenceFrameThresholdMs  ?? 250;
}

/// <summary>(3.3.0) Frame-by-frame AMD. Feed PCM-16 frames in until <see cref="CurrentVerdict"/> stabilises.</summary>
public sealed class AnsweringMachineDetector
{
    private readonly AmdOptions _options;
    private readonly object _gate = new();
    private TimeSpan _firstUtteranceLength;
    private TimeSpan _accumulatedAudio;
    private bool _utteranceInProgress;
    private TimeSpan _trailingSilence;
    private AmdVerdict _verdict = AmdVerdict.Unknown;

    public AnsweringMachineDetector(AmdOptions? options = null)
    {
        _options = options ?? new AmdOptions();
    }

    public AmdVerdict CurrentVerdict { get { lock (_gate) return _verdict; } }

    /// <summary>(3.3.0) Feed one frame of PCM-16 mono. Returns the (possibly updated) verdict.</summary>
    public AmdVerdict Observe(ReadOnlySpan<byte> pcmFrame, int sampleRateHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (pcmFrame.Length < 2) return CurrentVerdict;

        var frameDuration = TimeSpan.FromMilliseconds(1000.0 * (pcmFrame.Length / 2) / sampleRateHz);
        bool isSpeech     = FrameHasSpeech(pcmFrame);

        lock (_gate)
        {
            if (_verdict != AmdVerdict.Unknown) return _verdict;

            _accumulatedAudio += frameDuration;

            if (isSpeech)
            {
                if (!_utteranceInProgress)
                {
                    _utteranceInProgress = true;
                }
                _firstUtteranceLength += frameDuration;
                _trailingSilence = TimeSpan.Zero;
            }
            else if (_utteranceInProgress)
            {
                _trailingSilence += frameDuration;
                if (_trailingSilence.TotalMilliseconds >= _options.SilenceFrameThresholdMsOrDefault)
                {
                    _utteranceInProgress = false;
                }
            }

            // Decide.
            var firstMs = _firstUtteranceLength.TotalMilliseconds;
            if (firstMs >= _options.HumanMaxFirstUtteranceMsOrDefault)
            {
                _verdict = AmdVerdict.AnsweringMachine;
            }
            else if (!_utteranceInProgress &&
                     firstMs >= _options.HumanMinFirstUtteranceMsOrDefault &&
                     firstMs <  _options.HumanMaxFirstUtteranceMsOrDefault)
            {
                _verdict = AmdVerdict.Human;
            }
            else if (_accumulatedAudio.TotalMilliseconds >= _options.MaxObservationWindowOrDefault)
            {
                _verdict = firstMs < _options.HumanMinFirstUtteranceMsOrDefault
                    ? AmdVerdict.Unknown
                    : AmdVerdict.AnsweringMachine;
            }
            return _verdict;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _firstUtteranceLength = TimeSpan.Zero;
            _accumulatedAudio     = TimeSpan.Zero;
            _utteranceInProgress  = false;
            _trailingSilence      = TimeSpan.Zero;
            _verdict              = AmdVerdict.Unknown;
        }
    }

    private static bool FrameHasSpeech(ReadOnlySpan<byte> pcm)
    {
        const float energyThreshold = 0.012f;
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        double sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int s = samples[i];
            sumSquares += s * s;
        }
        var rms = Math.Sqrt(sumSquares / samples.Length) / short.MaxValue;
        return rms >= energyThreshold;
    }
}
