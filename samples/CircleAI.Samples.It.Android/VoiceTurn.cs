#if IT_VOICE_ANDROID
#nullable enable

// VoiceTurn.cs
//
// One press, one exchange: listen until they stop, then answer.
//
// WHY NOT REUSE VoiceLoop. That one is built around a wake word — it listens
// forever and a phrase opens the gate. This is the other half of the same
// product: you touched the thing, so it is already awake and should just listen.
// A person who has pressed a button does not also want to say a magic word.
//
// KNOWING WHEN SOMEONE HAS FINISHED IS THE WHOLE PROBLEM. Cut them off and they
// have to start again, which is the single most infuriating thing an assistant
// does. Wait too long and it feels broken. So the rule is deliberately generous:
// only silence AFTER speech ends a turn, the pause allowed is nearly a second and
// a half — longer than the gap in "what's the weather… in Durban" — and the floor
// adapts to the room instead of assuming a quiet one.
//
// IT REPORTS ITS LEVEL WHILE LISTENING, and that is not decoration. A circle that
// moves with your voice is the only honest way to answer the question every
// person asks silently when they start speaking to a machine: can it hear me? A
// spinner cannot answer that. A meter that follows your own voice does, instantly
// and without a word of explanation.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Records one utterance, ending when the speaker stops.</summary>
public sealed class VoiceTurn
{
    /// <summary>Silence after speech that ends the turn.</summary>
    /// <remarks>
    /// 1.4 s. Long enough for "what is the weather… in Durban", short enough that
    /// finishing does not feel like waiting. Being cut off mid-sentence costs the
    /// person the whole turn; waiting an extra half-second costs half a second.
    /// </remarks>
    public TimeSpan EndOfSpeech { get; init; } = TimeSpan.FromMilliseconds(1400);

    /// <summary>Give up if nobody says anything at all.</summary>
    public TimeSpan NoSpeechTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Hard ceiling on one turn.</summary>
    public TimeSpan MaxLength { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How far above the room's own noise counts as speech.</summary>
    /// <remarks>
    /// A MULTIPLE OF THE MEASURED FLOOR, not a fixed level. A fixed threshold is a
    /// promise about the room, and it is wrong in a kitchen with a kettle on, in a
    /// taxi, and in a quiet bedroom at night — three places this has to work.
    /// </remarks>
    public double SpeechOverNoise { get; init; } = 3.0;

    /// <summary>Raised roughly every 100 ms with the current level, 0 to 1.</summary>
    public event EventHandler<float>? Level;

    /// <summary>Raised once, when the speaker is first heard.</summary>
    public event EventHandler? SpeechStarted;

    /// <summary>
    /// Listens until they stop, and returns the audio as PCM16 16 kHz mono.
    /// </summary>
    /// <returns>The utterance, or an empty buffer if nobody spoke.</returns>
    public async Task<ReadOnlyMemory<byte>> ListenAsync(
        IAudioCapture capture, CancellationToken ct = default)
    {
        var captured = new List<byte>(16_000 * 2 * 8);
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? lastVoice = null;
        var heardAnything = false;

        // The first few frames measure the room rather than the speaker, so the
        // floor is the room's own noise instead of a number chosen at a desk.
        var floorSamples = new List<double>();
        var floor = 0.0;

        await foreach (var chunk in capture.CaptureAsync(ct).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var span = chunk.Span;

            double sum = 0;
            var n = span.Length / 2;
            for (var i = 0; i < n; i++)
            {
                var s = (short)(span[i * 2] | (span[i * 2 + 1] << 8)) / 32768.0;
                sum += s * s;
            }
            var rms = n > 0 ? Math.Sqrt(sum / n) : 0;

            if (floorSamples.Count < 3)
            {
                floorSamples.Add(rms);
                floor = Math.Max(0.002, Average(floorSamples));
                // Still reported, so the circle is alive from the first instant
                // rather than dead for the first third of a second.
                Level?.Invoke(this, (float)Math.Clamp(rms * 12, 0, 1));
                continue;
            }

            captured.AddRange(chunk.ToArray());
            Level?.Invoke(this, (float)Math.Clamp(rms / (floor * 12), 0, 1));

            if (rms > floor * SpeechOverNoise)
            {
                if (!heardAnything) { heardAnything = true; SpeechStarted?.Invoke(this, EventArgs.Empty); }
                lastVoice = now;
            }

            // ONLY silence after speech ends the turn. Ending on silence alone
            // would cut off anyone who pauses to think before they start.
            if (heardAnything && lastVoice is { } last && now - last > EndOfSpeech) break;
            if (!heardAnything && now - started > NoSpeechTimeout) break;
            if (now - started > MaxLength) break;
        }

        return heardAnything ? captured.ToArray() : ReadOnlyMemory<byte>.Empty;
    }

    static double Average(List<double> xs)
    {
        double t = 0;
        foreach (var x in xs) t += x;
        return xs.Count == 0 ? 0 : t / xs.Count;
    }
}
#endif
