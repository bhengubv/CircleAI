// ReassuranceFiller.cs
//
// (3.3.0) When a tool call takes more than the awkward-silence
// threshold (~600 ms) the AI plays a filler line like "Give me a
// moment to check that…" so the caller doesn't think the line dropped.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Phrases the filler picks from. Rotated to avoid repetition.</summary>
public sealed record ReassuranceVocabulary(
    IReadOnlyList<string> ShortFillers,
    IReadOnlyList<string> LongFillers)
{
    /// <summary>(3.3.0) Sensible English defaults.</summary>
    public static ReassuranceVocabulary Default { get; } = new(
        ShortFillers: new[]
        {
            "One moment.",
            "Let me check.",
            "Give me a sec.",
            "Just a moment.",
        },
        LongFillers: new[]
        {
            "Still looking that up for you.",
            "This is taking a bit longer than usual — bear with me.",
            "Almost there — still pulling that information.",
            "Thanks for your patience, I'm checking that now.",
        });
}

/// <summary>(3.3.0) Configuration for the filler driver.</summary>
/// <param name="ShortFillerAfter">Silence after which to play a short filler. Default 600 ms.</param>
/// <param name="LongFillerEvery">Cadence for long fillers after the first short one. Default 3 s.</param>
/// <param name="Vocabulary">Phrase pool.</param>
public sealed record ReassuranceFillerOptions(
    TimeSpan?               ShortFillerAfter = null,
    TimeSpan?               LongFillerEvery  = null,
    ReassuranceVocabulary?  Vocabulary       = null)
{
    public TimeSpan ShortFillerAfterOrDefault => ShortFillerAfter ?? TimeSpan.FromMilliseconds(600);
    public TimeSpan LongFillerEveryOrDefault  => LongFillerEvery  ?? TimeSpan.FromSeconds(3);
    public ReassuranceVocabulary VocabularyOrDefault => Vocabulary ?? ReassuranceVocabulary.Default;
}

/// <summary>(3.3.0) Driver that plays fillers while a long task runs.</summary>
public interface IReassuranceFiller
{
    /// <summary>
    /// Run <paramref name="work"/>. If it doesn't complete before the
    /// short-filler threshold, speak a short phrase via
    /// <paramref name="tts"/>; while still pending speak long phrases on
    /// the configured cadence. Returns the work's result.
    /// </summary>
    Task<T> RunWithFillerAsync<T>(
        Func<CancellationToken, Task<T>> work,
        ICallSession                     session,
        BriefingSynthesiser              tts,
        CancellationToken                ct = default);
}

/// <summary>(3.3.0) Default in-memory filler driver.</summary>
public sealed class DefaultReassuranceFiller : IReassuranceFiller
{
    private readonly ReassuranceFillerOptions _options;
    private int _shortRotation;
    private int _longRotation;

    public DefaultReassuranceFiller(ReassuranceFillerOptions? options = null)
    {
        _options = options ?? new ReassuranceFillerOptions();
    }

    public async Task<T> RunWithFillerAsync<T>(
        Func<CancellationToken, Task<T>> work,
        ICallSession                     session,
        BriefingSynthesiser              tts,
        CancellationToken                ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tts);

        using var fillerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fillerTask = SpeakFillersAsync(session, tts, fillerCts.Token);
        try
        {
            var result = await work(ct).ConfigureAwait(false);
            fillerCts.Cancel();
            try { await fillerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return result;
        }
        catch
        {
            fillerCts.Cancel();
            try { await fillerTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private async Task SpeakFillersAsync(ICallSession session, BriefingSynthesiser tts, CancellationToken ct)
    {
        var vocab = _options.VocabularyOrDefault;
        try
        {
            await Task.Delay(_options.ShortFillerAfterOrDefault, ct).ConfigureAwait(false);
            await SpeakAsync(session, tts, NextShort(vocab), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.LongFillerEveryOrDefault, ct).ConfigureAwait(false);
                await SpeakAsync(session, tts, NextLong(vocab), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected when work finishes */ }
    }

    private string NextShort(ReassuranceVocabulary v)
    {
        if (v.ShortFillers.Count == 0) return "One moment.";
        var idx = Interlocked.Increment(ref _shortRotation) - 1;
        return v.ShortFillers[Math.Abs(idx) % v.ShortFillers.Count];
    }

    private string NextLong(ReassuranceVocabulary v)
    {
        if (v.LongFillers.Count == 0) return "Almost there.";
        var idx = Interlocked.Increment(ref _longRotation) - 1;
        return v.LongFillers[Math.Abs(idx) % v.LongFillers.Count];
    }

    private static async Task SpeakAsync(ICallSession session, BriefingSynthesiser tts, string text, CancellationToken ct)
    {
        var audio = await tts(text, ct).ConfigureAwait(false);
        if (!audio.IsEmpty)
        {
            await session.SendAudioAsync(new AudioFrame(audio, CallMediaFormat.Pcm24000, TimeSpan.Zero), ct)
                .ConfigureAwait(false);
        }
    }
}
