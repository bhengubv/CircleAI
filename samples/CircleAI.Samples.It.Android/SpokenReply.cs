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

    /// <summary>
    /// Shortest FIRST utterance worth breaking a sentence apart for.
    /// </summary>
    /// <remarks>
    /// THE FIRST CHUNK GATES EVERYTHING AND NOTHING ELSE DOES. Synthesis was
    /// measured on the P30 at roughly 75 ms per character, so waiting for a full
    /// stop cost 9 493 ms of silence for a 97-character opening sentence — while
    /// every LATER sentence is synthesised during playback of the one before it
    /// and costs nothing anybody notices.
    /// <para>
    /// So the first utterance may end at a comma, semicolon or colon once it is
    /// this long, and only the first. "The capital of France is Paris," starts
    /// playing while the rest is still being made. The cost is a slightly
    /// flatter cadence on one clause; the alternative is nine seconds of nothing
    /// at all, which is what a person reads as broken.
    /// </para>
    /// </remarks>
    const int FirstClauseMin = 28;

    /// <summary>Whether anything has been handed to the mouth yet.</summary>
    bool _firstTaken;

    readonly Task<(CircleAI.Samples.It.Voice.ItSpeaker? Speaker, string Status)> _voice;
    readonly Queue<string> _queue = new();
    readonly SemaphoreSlim _ready = new(0);
    readonly StringBuilder _pending = new();
    readonly CancellationToken _ct;
    readonly Action<float>? _onLevel;
    readonly string? _language;
    readonly Task _pump;

    /// <summary>
    /// Runs from construction, which is the moment thinking starts.
    /// </summary>
    /// <remarks>
    /// TIME TO FIRST SOUND WAS THE ONE NUMBER NOBODY HAD. This class logs when
    /// it fails and says nothing when it works, so a turn that went perfectly
    /// left no trace of WHEN it started talking — and that instant is the whole
    /// measure of whether the wait feels bearable. The chain around it was
    /// timed to the millisecond and then stopped one step short of the only
    /// event the person actually perceives.
    /// </remarks>
    readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

    bool _closed;

    /// <param name="voice">
    /// The voice, already being loaded. Started by the caller BEFORE thinking
    /// begins so the two overlap — that head start is half the point of this class.
    /// </param>
    /// <param name="onLevel">
    /// Reports how loud the sentence now playing is, 0..1, so the mark can move
    /// with the words instead of to a metronome.
    /// </param>
    /// <param name="languageCode">
    /// The language the person spoke, as the transcriber reported it, so the reply
    /// is voiced in the same one. Null or unknown leaves the voice at its default.
    /// </param>
    public SpokenReply(
        Task<(CircleAI.Samples.It.Voice.ItSpeaker?, string)> voice,
        Action<float>? onLevel,
        CancellationToken ct,
        string? languageCode = null)
    {
        _voice    = voice;
        _onLevel  = onLevel;
        _ct       = ct;
        _language = languageCode;
        _pump     = Task.Run(PumpAsync);
    }

    /// <summary>True once at least one sentence has actually been spoken aloud.</summary>
    /// <remarks>
    /// The caller uses this to decide whether the turn ended audibly. If nothing
    /// was ever spoken the person across the room heard nothing at all, and that
    /// deserves a sound of its own rather than silent text.
    /// </remarks>
    public bool SpokeAnything { get; private set; }

    /// <summary>
    /// Milliseconds from construction to the first audible word, or -1 if
    /// nothing was ever spoken.
    /// </summary>
    /// <remarks>
    /// Reported so the turn can log one honest end-to-end figure instead of a
    /// sum of stages that stops before the sound.
    /// </remarks>
    public long FirstSoundMs { get; private set; } = -1;

    /// <summary>How long the voice took to load, once it had finished.</summary>
    public long VoiceReadyMs { get; private set; } = -1;

    /// <summary>Why nothing was spoken, in words, when nothing was.</summary>
    /// <remarks>
    /// KEPT BECAUSE "it went wrong" IS NOT A BUG REPORT. The first time this path
    /// fired on the P30 the screen apologised and the reason was a Warn line that
    /// had already rolled out of the log buffer, so the failure was unexplainable
    /// after the fact. The reason now travels with the result and can be put on
    /// the screen, which is the one place it is guaranteed to be seen.
    /// </remarks>
    public string? FailureReason { get; private set; }

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

        // Only the opening utterance may be cut at a clause; once sound is
        // flowing, the rest is built behind it and full sentences are free.
        var opening = !_firstTaken;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var endsSentence = c is '.' or '!' or '?' or '\n';
            var endsClause   = opening && c is ',' or ';' or ':';
            if (!endsSentence && !endsClause) continue;

            // "3." in a numbered list is not the end of a thought. Treat a stop
            // that follows a digit and precedes a space as part of the list.
            if (c == '.' && i > 0 && char.IsDigit(text[i - 1])) continue;

            var take = text[..(i + 1)].Trim();

            // A clause has to earn being spoken alone; a sentence does not.
            var floor = endsSentence ? MinSpeakable : FirstClauseMin;
            if (take.Length < floor && i + 1 < text.Length) continue;

            _pending.Remove(0, i + 1);
            if (take.Length == 0) return null;

            _firstTaken = true;
            return take;
        }
        return null;
    }

    async Task PumpAsync()
    {
        // Whatever is left of the voice load happens here, once, off the caller's
        // path — by now it has usually finished during the thinking.
        CircleAI.Samples.It.Voice.ItSpeaker? speaker;
        string status;
        try
        {
            (speaker, status) = await _voice.ConfigureAwait(false);
            VoiceReadyMs = _since.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            // The voice is loaded in parallel with the brain now, so it can fail on
            // its own — most likely for memory, since both want hundreds of MB on a
            // phone that has 3.7 GB in total. That throw used to escape into the
            // pump task and vanish.
            FailureReason = "voice did not load: " + Short(ex);
            Log.Warn(Tag, FailureReason);
            return;
        }

        if (speaker is null)
        {
            FailureReason = string.IsNullOrWhiteSpace(status) ? "no voice available" : status;
            Log.Warn(Tag, "no voice, answer will be text only: " + FailureReason);
            return;
        }

        using var mouth = speaker;

        // Voice the reply in the language it was asked in.
        mouth.SpeakLanguage(_language);
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
                var synth = System.Diagnostics.Stopwatch.StartNew();
                var pcm = await mouth.Engine.SynthesiseAsync(next, _ct).ConfigureAwait(false);
                if (pcm.AudioData.Length == 0) continue;

                _onLevel?.Invoke(Loudness(pcm.AudioData.Span, pcm.BitsPerSample));

                // THE MOMENT THE PERSON HEARS SOMETHING. Everything upstream is
                // measured and logged; this is where the measuring stopped, so
                // the only number that describes their actual wait was the one
                // nobody had. Logged before the audio starts rather than after,
                // because the wait ends when the sound begins, not when the
                // sentence finishes playing.
                if (!SpokeAnything)
                {
                    FirstSoundMs = _since.ElapsedMilliseconds;
                    Log.Info(Tag,
                        $"spoke: first sound at {FirstSoundMs} ms " +
                        $"(voice ready {VoiceReadyMs} ms, synth {synth.ElapsedMilliseconds} ms, " +
                        $"{next.Length} chars)");
                }

                SpokeAnything = true;
                await player.PlayAsync(pcm.AudioData, pcm.SampleRate,
                                       pcm.Channels, pcm.BitsPerSample, _ct)
                            .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One sentence failing is not the answer failing. Keep going: a
                // gap mid-answer beats stopping mid-answer. The reason is kept only
                // while nothing at all has been said — once some of the answer is
                // out loud, a stumble is not worth reporting as a failure.
                if (!SpokeAnything) FailureReason = "synthesis failed: " + Short(ex);
                Log.Warn(Tag, "sentence not spoken: " + ex.Message);
            }
        }
    }

    /// <summary>The useful half of an exception, short enough for a caption.</summary>
    static string Short(Exception ex)
    {
        // The innermost message is the one that names the actual cause; the outer
        // wrappers just say something inside them failed.
        var e = ex;
        while (e.InnerException is { } inner) e = inner;
        var m = e.Message.Trim();
        return m.Length <= 90 ? m : m[..90] + "…";
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

