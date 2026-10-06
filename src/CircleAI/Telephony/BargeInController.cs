// BargeInController.cs
//
// (3.3.0) Barge-in: when the caller interrupts the AI mid-response,
// pause the TTS playback, decide if the interruption was real (versus
// a cough/ambient noise), and either resume or cancel the turn.

using System;
using System.Threading;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) State of the AI's current turn.</summary>
public enum BargeInState
{
    /// <summary>AI is speaking.</summary>
    Speaking,
    /// <summary>Caller interrupted; playback paused while we decide.</summary>
    Paused,
    /// <summary>Confirmed real interruption — turn cancelled.</summary>
    Cancelled,
    /// <summary>Decided false alarm — resumed speaking.</summary>
    Resumed,
}

/// <summary>(3.3.0) One state transition.</summary>
public sealed record BargeInTransition(BargeInState From, BargeInState To, DateTimeOffset At, string Reason);

/// <summary>(3.3.0) Configuration for barge-in detection.</summary>
/// <param name="PauseAfter">How long the caller must be talking before we pause. Default 100 ms.</param>
/// <param name="CancelAfter">Continued speech that confirms it's a real interruption. Default 600 ms.</param>
public sealed record BargeInOptions(
    TimeSpan? PauseAfter   = null,
    TimeSpan? CancelAfter  = null)
{
    public TimeSpan PauseAfterOrDefault  => PauseAfter  ?? TimeSpan.FromMilliseconds(100);
    public TimeSpan CancelAfterOrDefault => CancelAfter ?? TimeSpan.FromMilliseconds(600);
}

/// <summary>(3.3.0) Drives barge-in pause/resume/cancel decisions.</summary>
public sealed class BargeInController
{
    private readonly BargeInOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private BargeInState _state = BargeInState.Speaking;
    private DateTimeOffset? _callerSpeechStartedAt;

    public BargeInController(BargeInOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new BargeInOptions();
        _clock   = clock   ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The current state of the AI turn.</summary>
    public BargeInState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Call when AI playback begins.</summary>
    public void OnPlaybackStart()
    {
        lock (_gate)
        {
            _state = BargeInState.Speaking;
            _callerSpeechStartedAt = null;
        }
    }

    /// <summary>Call on each frame where the VAD reports caller speech.</summary>
    public BargeInTransition? OnCallerSpeech()
    {
        var now = _clock();
        lock (_gate)
        {
            if (_state == BargeInState.Cancelled) return null;

            if (_callerSpeechStartedAt is null)
            {
                _callerSpeechStartedAt = now;
                return null;
            }

            var elapsed = now - _callerSpeechStartedAt.Value;
            if (_state == BargeInState.Speaking && elapsed >= _options.PauseAfterOrDefault)
            {
                var t = new BargeInTransition(_state, BargeInState.Paused, now, $"Caller speech {elapsed.TotalMilliseconds:F0} ms");
                _state = BargeInState.Paused;
                return t;
            }
            if (_state == BargeInState.Paused && elapsed >= _options.CancelAfterOrDefault)
            {
                var t = new BargeInTransition(_state, BargeInState.Cancelled, now, $"Confirmed barge-in after {elapsed.TotalMilliseconds:F0} ms");
                _state = BargeInState.Cancelled;
                return t;
            }
            return null;
        }
    }

    /// <summary>Call on each frame where VAD reports silence.</summary>
    public BargeInTransition? OnCallerSilence()
    {
        var now = _clock();
        lock (_gate)
        {
            _callerSpeechStartedAt = null;

            if (_state == BargeInState.Paused)
            {
                var t = new BargeInTransition(_state, BargeInState.Resumed, now, "Caller fell silent after pause");
                _state = BargeInState.Speaking; // resume
                return t;
            }
            return null;
        }
    }

    /// <summary>Whether the AI should keep emitting audio frames right now.</summary>
    public bool ShouldEmitAudio
    {
        get { lock (_gate) return _state == BargeInState.Speaking; }
    }

    /// <summary>Whether the turn was confirmed barge-in (caller wins, AI should drop).</summary>
    public bool WasBargedIn
    {
        get { lock (_gate) return _state == BargeInState.Cancelled; }
    }
}
