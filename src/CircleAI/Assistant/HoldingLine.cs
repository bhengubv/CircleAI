// HoldingLine.cs
//
// SILENCE IS THE WORST ANSWER, AND IT IS THE ONE THIS GIVES TODAY.
//
// Measured on a P30 on 2026-10-10. Somebody asks a question; the brain is not
// resident because the phone killed the service an hour ago; the turn waits up
// to FORTY-FIVE SECONDS for it to load, and in all that time the person hears
// nothing at all. Then either an answer arrives or
//
//     Circle AI could not get its brain started on this phone.
//
// From the other side of the room that is indistinguishable from a device that
// is ignoring you - which is exactly what was reported about the dead-weight
// offer, in the same words: "I have been responding but nothing happens."
//
// The bar is Alexa. It does not answer faster than it can; it stops the
// silence. A sound within a second tells a person they were heard, and the
// wait that follows is a wait instead of a failure.
//
// BUT ONLY WHEN THERE IS A WAIT. An assistant that says "give me a moment"
// before every answer is worse than one that pauses - it adds a second to
// every fast turn and teaches people to talk over it. So this fills the gap
// only once a gap exists, and never when the answer was already on its way.
//
// IT LIVES HERE, NOT IN A SCREEN, because both heads need it and the hard part
// is pure timing: speak only if nothing has arrived yet, never speak after the
// answer starts, never let the filler itself delay the answer, and never take
// a turn down because the filler failed. Those are four rules and they are all
// testable with a fake clock - the same reason ReadinessWait and DecisionLog
// were pulled out of the Android service.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>Says something while an answer is still coming, if it is slow.</summary>
public static class HoldingLine
{
    /// <summary>How long a person will wait before silence reads as failure.</summary>
    /// <remarks>
    /// Short enough that it beats the gap, long enough that an answer already
    /// in flight is never interrupted by a needless "give me a moment". Most
    /// fast turns on this device produce their first fragment well inside it,
    /// so the common case stays silent.
    /// </remarks>
    public static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Speaks <paramref name="line"/> if <paramref name="answerStarted"/> has
    /// not completed within <paramref name="patience"/>.
    /// </summary>
    /// <param name="answerStarted">
    /// Completes when the FIRST fragment of the real answer exists - not when
    /// the whole answer does. Waiting for the whole thing would fill a gap
    /// that is already over.
    /// </param>
    /// <param name="say">Speaks the line. Awaited, so the caller can order sound.</param>
    /// <param name="line">What to say, from <see cref="AssistantPersona"/>.</param>
    /// <param name="patience">Override for <see cref="Patience"/>.</param>
    /// <param name="delay">The sleep; null means <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="ct">Cancellation. A cancelled turn says nothing.</param>
    /// <returns>True if it spoke.</returns>
    /// <remarks>
    /// NEVER THROWS, INCLUDING WHEN THE VOICE FAILS. This exists to improve a
    /// turn that is already going badly; taking the turn down because the
    /// consolation prize could not be delivered would be absurd.
    /// <para>
    /// IT DOES NOT AWAIT THE ANSWER. Only the race between the answer's first
    /// fragment and the patience window. The caller owns the answer and must
    /// still await it - this returns as soon as the question "is it slow?" has
    /// been settled.
    /// </para>
    /// </remarks>
    public static async Task<bool> SpeakIfSlowAsync(
        Task answerStarted,
        Func<string, CancellationToken, Task> say,
        string line,
        TimeSpan? patience = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answerStarted);
        ArgumentNullException.ThrowIfNull(say);

        if (string.IsNullOrWhiteSpace(line)) return false;
        if (ct.IsCancellationRequested) return false;

        // Already here. The commonest case and it must cost nothing.
        if (answerStarted.IsCompleted) return false;

        var wait = patience ?? Patience;
        if (wait <= TimeSpan.Zero) wait = Patience;

        var sleep = delay ?? Task.Delay;

        try
        {
            var timer = sleep(wait, ct);
            var first = await Task.WhenAny(answerStarted, timer).ConfigureAwait(false);

            // The answer won the race. Say nothing - the gap never happened.
            if (!ReferenceEquals(first, timer)) return false;

            // CHECKED AGAIN AFTER THE WAIT. Task.WhenAny resolves to the timer
            // when both finish together, and a turn that completed during the
            // window must not then be talked over.
            if (answerStarted.IsCompleted || ct.IsCancellationRequested) return false;

            await say(line, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch { return false; }
    }
}
