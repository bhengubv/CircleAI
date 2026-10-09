// ReadinessWait.cs
//
// "when you speak to someone do you ask them to turn their brain on first?"
//
// You do not, and on 2026-10-09 this app did. The service handled a question by
// calling Start() on the brain and then checking IsReady in the very next
// statement - so on any phone where the model was not already resident it could
// not possibly be ready, and the person got
//
//     brain warming up, try again shortly
//
// A cold model load is THIRTEEN TO TWENTY-THREE SECONDS on the P30, so asking
// again inside that window returned the same sentence. "Shortly" meant "keep
// asking until you happen to catch it".
//
// WHY THIS IS A TYPE AND NOT A LOOP IN THE SERVICE. The first fix WAS a loop in
// CircleNeuronLinkService - correct, device-proven, and untestable: the method is
// private, static and on an Android Service, so the net9/net10 suites could not
// reach it and the only evidence it worked was me watching a phone. A waiting
// policy with a budget, a poll interval and an off-by-one at each end is exactly
// the kind of thing that should not be believed on one demonstration.
//
// So the POLICY lives here, where it is platform-neutral and runs on both legs,
// and the service supplies the two platform things: what "ready" means and what
// to log. Nothing in this file knows what a brain is.
//
// THE CLOCK AND THE SLEEP ARE INJECTABLE for one reason: a test of a 45 second
// budget must not take 45 seconds. Left null they are DateTime.UtcNow and
// Task.Delay, which is what the device uses.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>Waiting for something to become ready, with a budget.</summary>
public static class ReadinessWait
{
    /// <summary>The default gap between checks.</summary>
    /// <remarks>
    /// Short enough that a brain which comes up at 13 s is answered at 13 s rather
    /// than at the next long tick, and long enough that a 45 second wait is 180
    /// cheap property reads rather than a spin.
    /// </remarks>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Polls <paramref name="ready"/> until it answers true or the budget runs out.
    /// </summary>
    /// <param name="ready">
    /// Asked repeatedly. Must be cheap and must not throw; a throw is treated as
    /// "not ready yet", because a probe that fails during startup is the normal
    /// case, not an error - the thing being probed may not exist yet.
    /// </param>
    /// <param name="budget">
    /// Total time to wait. Zero or negative means "check once and answer" rather
    /// than "never check", so a caller that computes a budget badly still gets the
    /// honest current state instead of an automatic no.
    /// </param>
    /// <param name="poll">Gap between checks; defaults to <see cref="DefaultPoll"/>.</param>
    /// <param name="say">
    /// THE LINES ARE FRAGMENTS, NOT SENTENCES, and must read after a caller's own
    /// subject: CircleNeuronLinkService logs "serve: the brain is " + line. The
    /// first version said "it came up; carrying on", which produced
    /// "serve: the brain is it came up; carrying on" on the P30 - caught by
    /// reading the device log, not by any test, because no test knew what the
    /// caller would put in front.
    /// <para>
    /// Told ONCE when a wait actually begins, and once more if it ends in success.
    /// Not called at all when the first check passes, which is the common case and
    /// is not news. A line per poll would be 180 lines for one turn, and on Android
    /// chatty then drops the ones that matter.
    /// </para>
    /// </param>
    /// <param name="now">The clock. Null means <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="delay">The sleep. Null means <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="ct">Cancellation. A cancelled wait answers false; it does not throw.</param>
    /// <returns>True if it became ready inside the budget.</returns>
    /// <remarks>
    /// CHECKED BEFORE THE FIRST SLEEP, always. A caller whose thing is already up
    /// must not pay the poll interval to find that out - that is 250 ms added to
    /// every single turn on a healthy phone, for nothing.
    /// <para>
    /// NEVER THROWS, including on cancellation. Every caller of this is on a path
    /// where the alternative to an answer is a person being told to ask again, and
    /// an exception there just becomes a worse sentence.
    /// </para>
    /// </remarks>
    public static async Task<bool> UntilAsync(
        Func<bool> ready,
        TimeSpan budget,
        TimeSpan? poll = null,
        Action<string>? say = null,
        Func<DateTime>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ready);

        var clock = now ?? (() => DateTime.UtcNow);
        var sleep = delay ?? Task.Delay;
        var gap = poll ?? DefaultPoll;
        if (gap <= TimeSpan.Zero) gap = DefaultPoll;

        var until = clock() + (budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        var waited = false;

        while (true)
        {
            bool isReady;
            try { isReady = ready(); }
            catch { isReady = false; }

            if (isReady)
            {
                if (waited) say?.Invoke("up now; carrying on");
                return true;
            }

            if (ct.IsCancellationRequested) return false;
            if (clock() >= until) return false;

            if (!waited)
            {
                say?.Invoke("not up yet; waiting for it");
                waited = true;
            }

            try { await sleep(gap, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }
}
