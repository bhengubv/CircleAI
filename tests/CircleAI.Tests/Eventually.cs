// Eventually.cs
//
// Waits for a thing to become true, instead of sleeping and hoping.
//
// WHY THIS EXISTS. Three tests in the Circle33/34 family failed intermittently
// across two days — ShortFillers_RotateThroughVocabulary,
// Stop_TransitionsToStopped, SendText_DefaultSilenceTextToAudio — always under
// full-suite load, always passing 3-of-3 alone. The obvious diagnosis is shared
// state between parallel tests. It was not: every one of them was racing a WALL
// CLOCK.
//
//   await Task.Delay(50);      // "by now the runtime will have started"
//   rt.Stop("c1");
//
// That comment is a guess about a machine, and it is wrong the moment 2,500 tests
// and an Android build are competing for the same cores. The test then fails for
// a reason that has nothing to do with the code it covers, which is worse than
// useless — it teaches everyone to disbelieve red.
//
// THE FIX IS NOT A BIGGER NUMBER. Doubling the sleep moves the threshold and
// slows every passing run; it does not remove the race, and the failure comes
// back on a busier day. What removes it is waiting for the PRECONDITION rather
// than for a duration: poll the state the test actually depends on, and keep a
// long timeout purely as a way to fail with a sentence instead of hanging.
//
// A note on the timeouts here: they are not tuning knobs. They are the point at
// which we stop believing the thing will ever happen. Generous by design — a slow
// pass costs seconds, a false failure costs trust.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CircleAI.Tests;

/// <summary>Waits on conditions rather than on the clock.</summary>
public static class Eventually
{
    /// <summary>How long before we accept it is never going to happen.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Blocks until <paramref name="condition"/> holds, or fails loudly.</summary>
    /// <param name="condition">
    /// Checked repeatedly, so keep it cheap. Usually side-effect free; the one
    /// sanctioned exception is poll-and-nudge, where the predicate re-sends something
    /// that may have been dropped because the receiver was not attached yet. Retrying
    /// inside the predicate is deliberate there — it borrows this method's timeout as
    /// its bound, which beats an unbounded retry loop that can hang the whole run.
    /// </param>
    /// <param name="because">
    /// What was being waited for, in words. This is the whole error message when it
    /// times out, and "Assert.True() Failure" tells the next person nothing.
    /// </param>
    /// <param name="timeout">When to give up. Defaults to <see cref="DefaultTimeout"/>.</param>
    public static async Task TrueAsync(
        Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < limit)
        {
            if (condition()) return;
            // Short enough to keep the common case fast, long enough not to burn a
            // core spinning while the thing we are waiting for needs one.
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out after {limit.TotalSeconds:F0}s waiting for: {because}");
    }

    /// <inheritdoc cref="TrueAsync(Func{bool}, string, TimeSpan?)"/>
    /// <remarks>For preconditions that can only be read asynchronously, e.g. a store count.</remarks>
    public static async Task TrueAsync(
        Func<Task<bool>> condition, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < limit)
        {
            if (await condition().ConfigureAwait(false)) return;
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out after {limit.TotalSeconds:F0}s waiting for: {because}");
    }

    /// <summary>Awaits a task, failing with a readable message rather than hanging.</summary>
    /// <remarks>
    /// A test that hangs takes the whole run with it and reports nothing. This
    /// turns that into one named failure.
    /// </remarks>
    public static async Task<T> CompletesAsync<T>(
        Task<T> task, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var done = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (!ReferenceEquals(done, task))
            Assert.Fail($"Timed out after {limit.TotalSeconds:F0}s waiting for: {because}");
        return await task.ConfigureAwait(false);
    }

    /// <inheritdoc cref="CompletesAsync{T}(Task{T}, string, TimeSpan?)"/>
    public static async Task CompletesAsync(Task task, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var done = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (!ReferenceEquals(done, task))
            Assert.Fail($"Timed out after {limit.TotalSeconds:F0}s waiting for: {because}");
        await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Gives a fire-and-forget path a moment to misbehave, for tests asserting that
    /// something NEVER happens.
    /// </summary>
    /// <remarks>
    /// You cannot wait for the absence of an event, so this one genuinely is a sleep
    /// — the opposite failure mode to the rest of this class. Too short and a real
    /// regression slips through green; it can never produce a FALSE failure, which
    /// is why a plain delay is the right tool here and nowhere else. Named so that
    /// the next reader can tell this deliberate sleep from the accidental ones.
    /// </remarks>
    /// <param name="because">What must NOT happen, in words. Documentation, not a message.</param>
    /// <param name="window">
    /// How long the thing gets to go wrong. Longer is strictly stronger here — the
    /// only cost is a slower pass — so raise it where the path under test is slow to
    /// get going, and never lower it to speed a suite up.
    /// </param>
    public static Task SettleAsync(string because, TimeSpan? window = null)
        => Task.Delay(window ?? TimeSpan.FromMilliseconds(50));
}
