#if IT_VOICE_ANDROID
#nullable enable

// SpeechOnset.cs
//
// Noticing that somebody has started talking - and nothing more.
//
// THIS IS WHAT LETS A PERSON INTERRUPT. Jarvis stops the moment you speak over
// him. Ours played every sentence to the end whatever you said, because nothing
// was listening while it talked. The echo canceller was already in the capture
// chain (capture: VoiceRecognition, on=[AGC, echo canceller]), which is the hard
// prerequisite; what was missing was a detector that only asks one question:
// has a voice that is not ours begun?
//
// NOT VoiceTurn. That one records an utterance and decides when it has ENDED,
// which is the wrong job here - by the time it returns, the interruption is
// over. This returns on the first sustained onset and lets the caller stop
// speaking and start a proper turn.
//
// SUSTAINED, BECAUSE THE PHONE HEARS ITSELF. Even with echo cancellation, the
// speaker leaks into the microphone on the first frames of every sentence, and a
// door or a dropped cup is a single loud frame. A person keeps going: three
// consecutive frames above the gate - about 300 ms - is speech; one is a sound.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Waits until a voice starts, while the assistant is speaking.</summary>
public sealed class SpeechOnset
{
    /// <summary>How far above the room's floor counts as a voice.</summary>
    /// <remarks>
    /// Higher than VoiceTurn's 3,0 on purpose: the "room" here includes the
    /// phone's own speaker, and a margin that is right for a quiet room is a
    /// self-interruption on a loud one.
    /// </remarks>
    public double SpeechOverNoise { get; init; } = 4.0;

    /// <summary>A level that is a voice whatever the floor says.</summary>
    /// <remarks>Double VoiceTurn's 0,02, for the same reason.</remarks>
    public double AbsoluteSpeechLevel { get; init; } = 0.04;

    /// <summary>How long a voice must persist before it counts.</summary>
    public TimeSpan Sustain { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Returns true the moment a sustained voice is heard, false when cancelled.
    /// </summary>
    public async Task<bool> WaitAsync(IAudioCapture capture, CancellationToken ct)
    {
        var floorSamples = new List<double>();
        var floor = 0.0;
        DateTimeOffset? above = null;
        var peak = 0.0;

        try
        {
            await foreach (var chunk in capture.CaptureAsync(ct).ConfigureAwait(false))
            {
                var span = chunk.Span;
                double sum = 0;
                var n = span.Length / 2;
                for (var i = 0; i < n; i++)
                {
                    var s = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768.0;
                    sum += s * s;
                }
                var rms = n > 0 ? Math.Sqrt(sum / n) : 0;
                if (rms > peak) peak = rms;

                // The first frames measure the room - and the phone's own voice,
                // which is why the floor is allowed to fall but never to rise.
                if (floorSamples.Count < 3)
                {
                    floorSamples.Add(rms);
                    floor = Math.Max(0.002, Average(floorSamples));
                    continue;
                }
                if (rms < floor) floor = Math.Max(0.002, rms);

                // BOTH, NOT EITHER. This was an OR, which is right for VoiceTurn -
                // it wants to catch a quiet speaker in a quiet room - and wrong
                // here, where a false fire cancels an answer somebody asked for.
                // Measured on a Redmi 12 on 2026-09-09: the floor fell to 0,0032,
                // so the floor-relative half fired on room noise at rms 0,0150
                // while the printed gate said 0,0400 and the interruption looked
                // impossible. A voice worth stopping for is loud in absolute
                // terms AND louder than the room.
                var loud = rms > floor * SpeechOverNoise && rms > AbsoluteSpeechLevel;
                var now = DateTimeOffset.UtcNow;

                if (!loud) { above = null; continue; }

                above ??= now;
                if (now - above.Value < Sustain) continue;

                // BOTH HALVES PRINTED. One combined "gate" hid which test fired
                // and made a real trigger read as impossible.
                Android.Util.Log.Info("CircleAI.Turn",
                    $"barge-in: a voice for {(now - above.Value).TotalMilliseconds:0} ms | "
                    + $"rms={rms:0.0000} floor={floor:0.0000} "
                    + $"needs >{floor * SpeechOverNoise:0.0000} and >{AbsoluteSpeechLevel:0.0000}");
                return true;
            }
        }
        catch (OperationCanceledException) { /* the reply finished first */ }

        return false;
    }

    private static double Average(List<double> xs)
    {
        double t = 0;
        foreach (var x in xs) t += x;
        return xs.Count == 0 ? 0 : t / xs.Count;
    }
}
#endif
