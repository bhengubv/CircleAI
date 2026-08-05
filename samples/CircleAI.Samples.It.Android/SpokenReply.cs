#if IT_VOICE_ANDROID
#nullable enable

// SpokenReply.cs
//
// Speaks the answer while the answer is still being written.
//
// THE TURN USED TO BE FOUR THINGS IN A ROW, and you waited for the sum of them:
//
//   think the WHOLE answer   ->  load the voice  ->  synthesise the WHOLE answer  ->  play
//
// Measured on the P30, the thinking alone ran 25-75 s, and one turn was still
// going at 73 s when the log recorded
//
//   [mem] OS memory pressure (RunningLow) - evicting the specialist
//
// Nothing was audible for any of it. For someone holding the phone that is a
// slow assistant; for someone who called it from across the room it is a broken
// one, because silence is silence whatever the reason.
//
// Two things were being wasted. The voice was loaded AFTER the thinking, though
// it needs nothing from it and could have loaded during. And synthesis waited
// for the last word of the answer, though the first sentence is usually
// finished long before — and a sentence is exactly the unit a person listens in.
//
// So the voice loads in parallel with the thinking, and each sentence is spoken
// as soon as it is complete. Time to first sound stops being
//
//   all-of-thinking + voice-load + all-of-synthesis
//
// and becomes
//
//   first-sentence + whatever is left of the voice load
//
// while the rest of the answer is still being written behind it. The wait does
// not get shorter; it stops being silent, which is the part that mattered.
//
// ORDER IS NOT NEGOTIABLE. Sentences are played strictly in the order written,
// one at a time, by a single pump. Speech that overlaps itself is worse than
// speech that is late.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Streams a generated answer into speech, a sentence at a time.</summary>
public sealed class SpokenReply : IAsyncDisposable
{
    const string Tag = "CircleAI.Spoken";

    /// <summary>
    /// Shortest run of text worth synthesising on its own.
    /// </summary>
    /// <remarks>
    /// Guards against "Yes." or "1." becoming their own utterance, which makes the
    /// answer stutter. Below this the fragment waits and joins the next sentence.
    /// </remarks>
    const int MinSpeakable = 12;

    readonly Task<(CircleAI.Samples.It.Voice.ItSpeaker? Speaker, string Status)> _voice;
    readonly Queue<string> _queue = new();
    readonly SemaphoreSlim _ready = new(0);
    readonly StringBuilder _pending = new();
    readonly CancellationToken _ct;
    readonly Action<float>? _onLevel;
    readonly Task _pump;
    bool _closed;

    /// <param name="voice">
    /// The voice, already being loaded. Started by the caller BEFORE thinking
    /// begins so the two overlap — that head start is half the point of this class.
    /// </param>
    /// <param name="onLevel">
    /// Reports how loud the sentence now playing is, 0..1, so the mark can move
    /// with the words instead of to a metronome.
    /// </param>
    public SpokenReply(
        Task<(CircleAI.Samples.It.Voice.ItSpeaker?, string)> voice,
        Action<float>? onLevel,
        CancellationToken ct)
    {
        _voice   = voice;
        _onLevel = onLevel;
        _ct      = ct;
        _pump    = Task.Run(PumpAsync);
    }

    /// <summary>True once at least one sentence has actually been spoken aloud.</summary>
    /// <remarks>
    /// The caller uses this to decide whether the turn ended audibly. If nothing
    /// was ever spoken the person across the room heard nothing at all, and that
    /// deserves a sound of its own rather than silent text.
    /// </remarks>
    public bool SpokeAnything { get; private set; }

    /// <summary>Feeds one streamed chunk of the answer in.</summary>
    public void Add(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || _closed) return;

        lock (_pending)
        {
            _pending.Append(chunk);
            // Drain every complete sentence sitting in the buffer. A chunk can
            // carry more than one, and can also carry none.
            while (TakeSentence() is { } sentence) Enqueue(sentence);
        }
    }

    /// <summary>Speaks whatever is left and waits for the last word to finish.</summary>
    public async Task FinishAsync()
    {
        lock (_pending)
        {
            var tail = _pending.ToString().Trim();
            _pending.Clear();
            if (tail.Length > 0) Enqueue(tail);
            _closed = true;
        }
        _ready.Release();                 // wake the pump so it can see the close
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    void Enqueue(string s)
    {
        lock (_queue) _queue.Enqueue(s);
        _ready.Release();
    }

    /// <summary>
    /// Pulls one complete sentence out of the buffer, or null if none is ready.
    /// </summary>
    /// <remarks>
    /// A sentence ends at . ! ? or a line break. The terminator is KEPT, because
    /// the synthesiser uses it for the falling intonation that makes an answer
    /// sound finished rather than interrupted.
    /// </remarks>
    string? TakeSentence()
    {
        var text = _pending.ToString();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('.' or '!' or '?' or '\n')) continue;

            // "3." in a numbered list is not the end of a thought. Treat a stop
            // that follows a digit and precedes a space as part of the list.
            if (c == '.' && i > 0 && char.IsDigit(text[i - 1])) continue;

            var take = text[..(i + 1)].Trim();
            if (take.Length < MinSpeakable && i + 1 < text.Length) continue;

            _pending.Remove(0, i + 1);
            return take.Length > 0 ? take : null;
        }
        return null;
    }

    async Task PumpAsync()
    {
        // Whatever is left of the voice load happens here, once, off the caller's
        // path — by now it has usually finished during the thinking.
        var (speaker, status) = await _voice.ConfigureAwait(false);
        if (speaker is null)
        {
            Log.Warn(Tag, "no voice, answer will be text only: " + status);
            return;
        }

        using var mouth = speaker;
        await using var player = new AndroidAudioPlayer();

        while (true)
        {
            await _ready.WaitAsync(_ct).ConfigureAwait(false);

            string? next = null;
            lock (_queue) if (_queue.Count > 0) next = _queue.Dequeue();

            if (next is null)
            {
                if (_closed) return;      // closed and drained
                continue;
            }

            try
            {
                var pcm = await mouth.Engine.SynthesiseAsync(next, _ct).ConfigureAwait(false);
                if (pcm.AudioData.Length == 0) continue;

                _onLevel?.Invoke(Loudness(pcm.AudioData.Span, pcm.BitsPerSample));
                SpokeAnything = true;
                await player.PlayAsync(pcm.AudioData, pcm.SampleRate,
                                       pcm.Channels, pcm.BitsPerSample, _ct)
                            .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One sentence failing is not the answer failing. Keep going: a
                // gap mid-answer beats stopping mid-answer.
                Log.Warn(Tag, "sentence not spoken: " + ex.Message);
            }
        }
    }

    /// <summary>Mean amplitude of a PCM buffer, 0..1, for driving the mark.</summary>
    static float Loudness(ReadOnlySpan<byte> pcm, int bits)
    {
        if (bits != 16 || pcm.Length < 2) return 0.5f;

        long sum = 0;
        var n = 0;
        // Every 32nd sample is plenty for a level meter and keeps this off the
        // critical path of starting playback.
        for (var i = 0; i + 1 < pcm.Length; i += 64)
        {
            var s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += Math.Abs((int)s);
            n++;
        }
        if (n == 0) return 0.5f;
        return Math.Clamp((float)(sum / (double)n) / 8000f, 0f, 1f);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            _closed = true;
            _ready.Release();
            try { await _pump.ConfigureAwait(false); } catch { }
        }
        _ready.Dispose();
    }
}
#endif
