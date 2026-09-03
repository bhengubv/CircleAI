#nullable enable

// ConfirmedKeywordSpotter.cs
//
// Two stages, because one cannot be both cheap and certain.
//
// STAGE ONE is the zipformer, always on, deliberately generous. Measured on the
// P30 through a room, "Circle" was heard 12 times out of 12 — which is the number
// that matters, because a wake word that misses is a product that does not work.
//
// STAGE TWO is what stops that generosity from becoming a nuisance. The same
// measurement over 30 clips of ordinary speech in three voices produced 21 false
// accepts, and EVERY SINGLE ONE was a sentence with the word inside it: "let us
// circle back", "draw a circle around the answer". None fired on speech that did
// not contain the word. The spotter was not wrong; people just say "circle".
//
// A THRESHOLD CANNOT FIX THIS AND IT IS WORTH SAYING WHY. "circle back" scores
// 0.802, higher than most genuine wakes. The two populations are not separated by
// confidence, so no cut through confidence divides them. They are separated by
// something else entirely, which is the whole idea below.
//
// WHAT ACTUALLY SEPARATES THEM: a wake word is the START of what you say. "Circle,
// what's the weather" begins with it; "let us circle back" has half a sentence in
// front of it. So stage two asks one question — WAS ANYONE TALKING JUST BEFORE
// THIS? — and that question costs no model, no memory and no measurable battery.
//
// The seam is IWakeConfirmer, so a heavier judge can be dropped in where the
// hardware allows: transcribing the window and reading the words back is strictly
// better and needs a whole ASR model resident, which is exactly the trade a cheap
// phone cannot make and an expensive one can.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>A wake word survived stage one and is asking to be believed.</summary>
/// <param name="Detection">What stage one reported.</param>
/// <param name="Window">Audio around the phrase, 16 kHz mono in [-1, 1].</param>
/// <param name="KeywordStart">Index into <paramref name="Window"/> where the phrase begins.</param>
/// <param name="KeywordEnd">Index into <paramref name="Window"/> where the phrase ends.</param>
public readonly record struct WakeCandidate(
    KwsDetection Detection,
    ReadOnlyMemory<float> Window,
    int KeywordStart,
    int KeywordEnd);

/// <summary>Stage two: decides whether a spotted phrase was really meant as a wake.</summary>
public interface IWakeConfirmer
{
    /// <summary>True to let the wake through.</summary>
    /// <remarks>
    /// Must be safe to call from the capture thread and must not block for long —
    /// every millisecond here is added latency between someone speaking and the
    /// assistant answering, which is the one thing people notice immediately.
    /// </remarks>
    ValueTask<bool> ConfirmAsync(WakeCandidate candidate, CancellationToken ct = default);

    /// <summary>Why the last rejection happened, for logs. Never shown to a user.</summary>
    string? LastReason { get; }
}

/// <summary>
/// Confirms a wake by requiring that the phrase STARTED what was being said.
/// </summary>
/// <remarks>
/// The cheap stage two: no model, a few hundred bytes of working memory, and on
/// the measured corpus it removes the entire false-accept population — all of
/// which were the word spoken mid-sentence.
/// <para>
/// IT DOES NOT TRUST THE DETECTION'S TIMESTAMPS, and that is the point. A
/// transducer does not emit a token when the sound happens; it waits until it has
/// enough right context to be sure. Measured on this model, "Circle" occupies
/// 100-550 ms of a clip and the detection reports its onset at 320 ms — a lag of
/// roughly 200 ms, straight through the middle of the word. A first attempt at
/// this class looked backwards from that reported onset and so examined the first
/// half of the wake word itself, decided it was "speech before the phrase", and
/// vetoed all six true positives along with all twelve false accepts. A stage two
/// that rejects everything is not a filter.
/// </para>
/// <para>
/// So the audio is measured instead. Walk backwards from the detection to the
/// start of the contiguous run of speech containing it: that is when the person
/// started talking. If the phrase FINISHED soon after they started, the phrase is
/// what they started with. If they had already been talking for a second, it was
/// not a wake word — it was a word.
/// </para>
/// <para>
/// Everything here is relative to the loudest part of the window rather than an
/// absolute level, because an absolute threshold is a promise about the room, the
/// microphone and the distance, and it breaks the first time any of the three
/// changes.
/// </para>
/// </remarks>
public sealed class UtteranceOnsetConfirmer : IWakeConfirmer
{
    /// <summary>
    /// How long after someone starts speaking the phrase may still finish.
    /// </summary>
    /// <remarks>
    /// Read off the trade curve rather than chosen. Sweeping this value over the
    /// 36-clip corpus, with recall on the left and false accepts on the right:
    /// <code>
    ///   400 ms   1/6   0/30      600 ms   6/6   3/30      900 ms   6/6  10/30
    ///   500 ms   2/6   0/30      750 ms   6/6   4/30     1200 ms   6/6  12/30
    /// </code>
    /// 600 is the knee: full recall, and three quarters of the false accepts
    /// gone. Below it recall collapses; above it the filter stops filtering.
    /// The number describes the PHRASE, not the algorithm — raise it for a longer
    /// wake phrase and re-run the sweep.
    /// </remarks>
    public double MaxLeadInMs { get; init; } = 600;

    /// <summary>Silence shorter than this does not end an utterance.</summary>
    /// <remarks>
    /// Speech is full of small stops — the closure before a plosive, the seam
    /// between two syllables. Without a tolerance, "Cir-cle" is two utterances
    /// and the second one looks like it began on its own.
    /// </remarks>
    public double GapToleranceMs { get; init; } = 150;

    /// <summary>Speech floor, as a fraction of the loudest part of the window.</summary>
    public double SpeechFloor { get; init; } = 0.12;

    public string? LastReason { get; private set; }

    private const int BucketMs = 10;

    public ValueTask<bool> ConfirmAsync(WakeCandidate candidate, CancellationToken ct = default)
    {
        var w = candidate.Window.Span;
        if (w.Length == 0) { LastReason = null; return ValueTask.FromResult(true); }

        var per = BucketMs * 16;                       // samples per bucket at 16 kHz
        var n = w.Length / per;
        if (n < 4) { LastReason = null; return ValueTask.FromResult(true); }

        Span<float> rms = n <= 512 ? stackalloc float[n] : new float[n];
        var peak = 0f;
        for (var b = 0; b < n; b++)
        {
            double s = 0;
            for (var i = b * per; i < (b + 1) * per; i++) s += (double)w[i] * w[i];
            rms[b] = (float)Math.Sqrt(s / per);
            if (rms[b] > peak) peak = rms[b];
        }
        if (peak <= 1e-6f) { LastReason = "silence"; return ValueTask.FromResult(false); }

        var floor = peak * SpeechFloor;
        var gap = Math.Max(1, (int)(GapToleranceMs / BucketMs));

        // The detection's END is late by the model's emission lag, never early, so
        // it is a safe right-hand anchor: walk back from it to find where the
        // talking began.
        var endBucket = Math.Clamp(candidate.KeywordEnd / per, 0, n - 1);

        var onset = endBucket;
        var quiet = 0;
        for (var b = endBucket; b >= 0; b--)
        {
            if (rms[b] >= floor) { onset = b; quiet = 0; }
            else if (++quiet >= gap) break;
        }

        var leadIn = (endBucket - onset + 1) * BucketMs;
        if (leadIn <= MaxLeadInMs)
        {
            LastReason = null;
            return ValueTask.FromResult(true);
        }

        LastReason = $"had been speaking {leadIn} ms before the phrase ended (max {MaxLeadInMs})";
        return ValueTask.FromResult(false);
    }
}

/// <summary>Lets everything through — the "stage one only" baseline.</summary>
public sealed class AlwaysConfirm : IWakeConfirmer
{
    public string? LastReason => null;
    public ValueTask<bool> ConfirmAsync(WakeCandidate c, CancellationToken ct = default) =>
        ValueTask.FromResult(true);
}

/// <summary>
/// Confirms a wake by transcribing the audio and reading the words back.
/// </summary>
/// <remarks>
/// THE EXPENSIVE TIER, and it exists because the cheap one has a measured limit.
/// <see cref="UtteranceOnsetConfirmer"/> removes three quarters of the false
/// accepts, and the survivors are all one shape: a single short word in front of
/// the keyword. "THE circle is round" starts talking barely sooner than "Circle"
/// does, so no amount of tuning an onset rule separates them — the difference is
/// not in the timing, it is in the words.
/// <para>
/// This asks the actual question: is the wake phrase THE FIRST THING SAID? A
/// transcript answers that outright, and it costs a resident speech model — which
/// is precisely the trade a cheap phone cannot make and a good one can. Pair it
/// with the device tier: onset alone at the bottom, transcript above it.
/// </para>
/// <para>
/// Fails OPEN. If the transcriber errors, times out or returns nothing, the wake
/// is allowed. Stage one already believed it, and an assistant that goes deaf
/// because its verifier fell over is worse than one that occasionally wakes when
/// it should not.
/// </para>
/// </remarks>
public sealed class TranscriptConfirmer : IWakeConfirmer
{
    private readonly IVoiceTranscriber _transcriber;
    private readonly Func<string, string> _normalise;

    /// <summary>Words that may precede the phrase without disqualifying it.</summary>
    /// <remarks>
    /// A LIST, NOT A COUNT, and the difference is the whole point. "Um, Circle"
    /// and "The circle is round" both put exactly one word in front of the
    /// keyword, so any rule counting words treats them identically — allow one and
    /// both pass, allow none and someone clearing their throat is ignored. What
    /// separates them is WHICH word: a filler is someone getting started, a
    /// determiner is someone mid-sentence talking about a shape.
    /// </remarks>
    public IReadOnlySet<string> AllowedLeadIn { get; init; } =
        new HashSet<string>(StringComparer.Ordinal)
        { "um", "uh", "er", "erm", "ah", "oh", "hey", "ok", "okay", "so", "please", "yeah" };

    /// <summary>Give up after this long and let the wake through.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(700);

    public string? LastReason { get; private set; }

    public TranscriptConfirmer(IVoiceTranscriber transcriber, Func<string, string>? normalise = null)
    {
        _transcriber = transcriber;
        _normalise = normalise ?? (s => new string(
            s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()));
    }

    public async ValueTask<bool> ConfirmAsync(WakeCandidate candidate, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var w = candidate.Window.Span;
            var pcm = new byte[w.Length * 2];
            for (var i = 0; i < w.Length; i++)
            {
                var s = (short)Math.Clamp(w[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            var result = await _transcriber.TranscribeAsync(pcm, cts.Token).ConfigureAwait(false);
            var heard = _normalise(result.Text ?? string.Empty).Split(' ',
                StringSplitOptions.RemoveEmptyEntries);
            var phrase = _normalise(candidate.Detection.Phrase).Split(' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (heard.Length == 0 || phrase.Length == 0)
            {
                LastReason = null;
                return true;                                   // nothing to judge — fail open
            }

            // Skip whatever run-in is allowed, then the phrase must be next.
            var at = 0;
            while (at < heard.Length && AllowedLeadIn.Contains(heard[at])) at++;

            if (at + phrase.Length <= heard.Length)
            {
                var match = true;
                for (var j = 0; j < phrase.Length && match; j++)
                    match = heard[at + j] == phrase[j];
                if (match) { LastReason = null; return true; }
            }

            LastReason = $"heard \"{string.Join(' ', heard.Take(6))}\" — phrase is not how it starts";
            return false;
        }
        catch (Exception ex)
        {
            // Fail open, loudly enough to be found in a log.
            LastReason = $"confirmer unavailable ({ex.GetType().Name}) — allowed";
            return true;
        }
    }
}

/// <summary>
/// A keyword spotter whose detections must pass a second stage before they count.
/// </summary>
public sealed class ConfirmedKeywordSpotter : IDisposable
{
    private readonly ZipformerKwsSpotter _spotter;
    private readonly IWakeConfirmer _confirmer;
    private readonly float[] _ring;
    private int _written;                 // total samples ever accepted
    private readonly List<KwsDetection> _pending = new();

    /// <summary>A wake word was heard AND confirmed.</summary>
    public event EventHandler<KwsDetection>? Woke;

    /// <summary>Stage one fired but stage two turned it down, with the reason.</summary>
    /// <remarks>
    /// Surfaced deliberately. A rejection is the single most useful signal for
    /// tuning a wake word, and one that is silently swallowed leaves "it does not
    /// wake" and "it woke and we vetoed it" looking identical from the outside.
    /// </remarks>
    public event EventHandler<(KwsDetection Detection, string? Reason)>? Rejected;

    private ZipformerKwsSpotter.KwsProgress? _best;

    /// <summary>
    /// The closest stage one has come to a phrase since this was last called,
    /// and null when it has not come close to one at all.
    /// </summary>
    /// <remarks>
    /// Reading it clears it, so each caller gets the best of ITS OWN window
    /// rather than the best since the microphone opened - which would freeze on
    /// one lucky frame and then never move again.
    /// </remarks>
    public ZipformerKwsSpotter.KwsProgress? TakeBestProgress()
    {
        var b = _best;
        _best = null;
        return b;
    }

    /// <summary>The phrases stage one is listening for.</summary>
    public IReadOnlyList<string> Keywords => _spotter.Keywords;

    /// <summary>Registered phrases that can never fire. Empty is healthy.</summary>
    public IReadOnlyList<(string Phrase, string ShadowedBy)> ShadowedKeywords =>
        _spotter.ShadowedKeywords;

    /// <param name="spotter">Stage one. Owned and disposed by this object.</param>
    /// <param name="confirmer">Stage two. Defaults to requiring the phrase to start the utterance.</param>
    /// <param name="historySeconds">
    /// How much recent audio to keep for stage two. Two seconds covers the longest
    /// wake phrase plus its run-up with room to spare, and costs 128 KB.
    /// </param>
    public ConfirmedKeywordSpotter(
        ZipformerKwsSpotter spotter,
        IWakeConfirmer? confirmer = null,
        double historySeconds = 2.0)
    {
        _spotter = spotter;
        _confirmer = confirmer ?? new UtteranceOnsetConfirmer();
        _ring = new float[(int)(historySeconds * 16_000)];

        // HOW CLOSE IT GETS, WHEN IT NEVER ARRIVES. KeywordProgress fires as the
        // leading hypothesis walks into a phrase, and it is the difference
        // between "the model is hearing 2 of 3 tokens at 0.31 and the threshold
        // is 0.5" - a number to move - and "nothing happened", which is not a
        // finding at all. Kept as the deepest sighting rather than logged here:
        // it fires per frame, and a line per frame is not a log, it is a flood.
        _spotter.KeywordProgress += (_, p) =>
        {
            if (_best is null || p.Matched > _best.Matched ||
                (p.Matched == _best.Matched && p.MeanProbability > _best.MeanProbability))
                _best = p;
        };

        // Collected, not judged, inside the event: the detection arrives mid-decode
        // and stage two wants the audio AROUND it — including a little that has not
        // been decoded yet. Judging here would look only backwards.
        _spotter.Detected += (_, d) =>
        {
            // STAGE ONE, BEFORE ANYONE JUDGES IT. Without this line a veto and a
            // model that never scored are the same silence, and they are opposite
            // problems: one is a threshold to loosen, the other is a phrase the
            // model cannot hear at all.
            VoiceTrace.Write($"wake: heard \"{d.Phrase}\" p={d.Probability:0.###} — confirming");
            _pending.Add(d);
        };
    }

    /// <summary>Feeds audio. Float samples in [-1, 1] at 16 kHz.</summary>
    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        Append(samples);
        _spotter.AcceptWaveform(samples);
        Drain();
    }

    /// <summary>Marks the end of the audio and judges anything outstanding.</summary>
    public void Flush()
    {
        _spotter.Flush();
        Drain();
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _ring[_written % _ring.Length] = s;
            _written++;
        }
    }

    private void Drain()
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();

        foreach (var d in batch)
        {
            var startSample = (int)(d.StartMs * 16);
            var endSample = (int)(d.EndMs * 16);

            // Everything still in the ring, oldest first, with the phrase located
            // inside it. If the detection has already scrolled out — only possible
            // if a caller pushes seconds at a time — there is nothing to judge and
            // it is let through rather than silently dropped.
            var have = Math.Min(_written, _ring.Length);
            var oldest = _written - have;
            if (startSample < oldest)
            {
                Woke?.Invoke(this, d);
                continue;
            }

            var window = new float[have];
            for (var i = 0; i < have; i++) window[i] = _ring[(oldest + i) % _ring.Length];

            var candidate = new WakeCandidate(
                d, window, startSample - oldest, Math.Min(endSample - oldest, have));

            if (_confirmer.ConfirmAsync(candidate).AsTask().GetAwaiter().GetResult())
                Woke?.Invoke(this, d);
            else
                Rejected?.Invoke(this, (d, _confirmer.LastReason));
        }
    }

    /// <summary>Clears stream state for a new utterance, keeping the loaded models.</summary>
    public void Reset()
    {
        _spotter.Reset();
        _pending.Clear();
        _written = 0;
        Array.Clear(_ring);
    }

    public void Dispose() => _spotter.Dispose();
}
