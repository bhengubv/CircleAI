// ColdTurn.cs
//
// WHAT HAPPENS WHEN SOMEBODY ASKS A QUESTION AND THE BRAIN IS NOT UP.
//
// It happens constantly on the P30 and it is not a fault: EMUI kills the service
// when the phone is idle, a cold model load is thirteen to twenty-three seconds,
// and the whole point of a wake word is that nobody opened an app first. So the
// FIRST question after a quiet hour always arrives at a dead brain. That is the
// normal case, and it is the one the product handled worst.
//
// THREE CALLERS ANSWERED IT THREE DIFFERENT WAYS, and one of them answered with
// silence. Measured on a P30 on 2026-10-10:
//
//   * CircleNeuronLinkService.ServeAskAsync - starts the brain, waits 45 s, and
//     on failure returns "Circle AI could not get its brain started on this
//     phone", written about itself in the third person.
//   * WakeTurn.AskAsync - checks IsReady, finds it false, logs "brain is not
//     ready - nothing to ask", returns null, and THE TURN ENDS WITHOUT A SOUND.
//     It never asks it to start. It never says anything. The person said "Hey B",
//     heard a hello and a beep, asked their question, and got nothing - which is
//     what was reported, in these words: "Voice still doesn't respond to me."
//   * DeviceConversation - streams into SpokenReply and never considers it.
//
// One fact with two owners always ends up with two answers; this one had three.
//
// SO THIS IS THE ONE OWNER, and the rule it owns is short: a question is an
// instruction to start. It starts the brain, waits for it with a budget, fills
// the wait with a sentence rather than silence, and if it never comes up it SAYS
// so. Nothing here returns quietly.
//
// THE BAR IS ALEXA, and Alexa is not faster than its hardware - it is never
// silent. A sound inside a second means "heard you", and the wait that follows
// is a wait instead of a failure.
//
// NOTHING IN HERE IS ANDROID, which is the point: the policy is a budget, a
// patience window and an ordering, and every one of those was previously a
// private static on an Android Service that no test leg could see. The platform
// supplies four lambdas - is it ready, start it, ask it, say this - and the
// ordering is proven on net9 and net10 in microseconds.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;

namespace CircleAI.Assistant;

/// <summary>What a turn produced, and what was said while producing it.</summary>
/// <param name="Answer">The brain's answer, or null if there is not one.</param>
/// <param name="Excuse">
/// What to say instead, or null when the answer speaks for itself.
/// </param>
/// <param name="Filled">Whether a holding line went out while waiting.</param>
/// <remarks>
/// THE EXCUSE IS A SEPARATE FIELD BECAUSE ITS CALLERS DIFFER. A spoken turn has
/// a voice and this type has already used it; the link has none - the app on the
/// other end does the speaking - so it needs the sentence handed back to put in
/// a reply. Returning one string for both would make the link say nothing or the
/// voice say everything twice.
/// </remarks>
public readonly record struct ColdTurnResult(string? Answer, string? Excuse, bool Filled)
{
    /// <summary>The brain answered.</summary>
    public bool Answered => !string.IsNullOrWhiteSpace(Answer);

    /// <summary>Nothing was produced and nothing explains it. Should be impossible.</summary>
    /// <remarks>
    /// A GUARD, NOT A STATE. Every path out of <see cref="ColdTurn.AnswerAsync"/>
    /// carries an answer or an excuse, except a cancelled turn - where saying
    /// something would be talking to a person who has left. A test asserts this is
    /// false everywhere else, because "silently produced nothing" is the defect
    /// this type exists to remove and it must not creep back in as a fourth path.
    /// </remarks>
    public bool Silent => !Answered && string.IsNullOrWhiteSpace(Excuse);
}

/// <summary>Answering a question that arrived before the brain was up.</summary>
public static class ColdTurn
{
    /// <summary>How long to wait for a cold brain before giving up on it.</summary>
    /// <remarks>
    /// MEASURED, NOT CHOSEN. A cold load on the P30 is 13 to 23 seconds and the
    /// Circle OS device has been slower; 45 leaves room for the worst observed
    /// case without being a number a person would ever sit through, because they
    /// do not - <see cref="HoldingLine"/> speaks inside the first second and the
    /// wait becomes a wait.
    /// </remarks>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(45);

    /// <summary>How long a WARM brain may think before the wait is filled.</summary>
    /// <remarks>
    /// LONGER THAN <see cref="HoldingLine.Patience"/>, DELIBERATELY, and the two
    /// cases are not the same wait. A dead brain is thirteen to twenty-three
    /// seconds away, so the sooner something is said the better. A resident brain
    /// generating an answer is five to thirteen - and a quick one is back inside
    /// two, where "let me think" would be an interruption that made a fast turn
    /// slower. Two and a half seconds is past every fast answer measured on this
    /// phone and well short of a silence anybody would call a failure.
    /// </remarks>
    public static readonly TimeSpan ThinkingPatience = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Puts a question to the brain, starting and waiting for it if it is down.
    /// </summary>
    /// <param name="ready">Is the brain up? Cheap, and may throw - a throw is "no".</param>
    /// <param name="start">
    /// Ask it to come up. CALLED ONLY WHEN IT IS DOWN, and only once: the question
    /// IS the instruction to start, and starting something already running is
    /// noise. A throw is swallowed - the wait below is the real test of whether it
    /// worked, and a start that reported failure and then succeeded is a phone
    /// answering a question, which is what matters.
    /// </param>
    /// <param name="ask">Put the question. Its exceptions are the caller's - see remarks.</param>
    /// <param name="started">
    /// Completes when the FIRST PIECE of the answer is out, for a caller that
    /// streams. Null means the whole answer is the only signal there is.
    /// <para>
    /// WITHOUT THIS A STREAMING CALLER GETS THE FILLER IN THE MIDDLE OF ITS OWN
    /// ANSWER. WakeTurn pushes sentences into <see cref="SpokenReply"/> as they
    /// are written, so by the time <paramref name="ask"/> returns, three
    /// sentences may already have been spoken - and a holding line decided on
    /// "has ask returned yet" would queue "let me think" behind the first one.
    /// What the patience window is really asking is "has anything been heard
    /// yet", and only the caller knows that.
    /// </para>
    /// </param>
    /// <param name="say">
    /// Speaks a line, for the holding line and the excuse. Null for a caller with
    /// no voice (the link), which then gets the excuse back in the result instead.
    /// </param>
    /// <param name="budget">Override for <see cref="WarmUp"/>.</param>
    /// <param name="patience">Override for <see cref="HoldingLine.Patience"/>.</param>
    /// <param name="log">Where the diagnostics go. Fragments, as ReadinessWait wants them.</param>
    /// <param name="givenUp">
    /// Has it reported that it is never coming up? Ends the wait early; see
    /// <see cref="ReadinessWait.UntilAsync"/> for why that is worth 43 seconds.
    /// </param>
    /// <param name="now">The clock. Null means the real one.</param>
    /// <param name="delay">The sleep. Null means the real one.</param>
    /// <param name="ct">Cancellation. A cancelled turn says nothing at all.</param>
    /// <remarks>
    /// IT DOES NOT CATCH WHAT <paramref name="ask"/> THROWS. This owns readiness,
    /// not generation: a brain that came up and then failed mid-answer is a
    /// different fault with a different sentence, and swallowing it here would
    /// turn a crash into "ask me again in a moment" forever. Its callers already
    /// have a handler.
    /// </remarks>
    public static async Task<ColdTurnResult> AnswerAsync(
        Func<bool> ready,
        Action? start,
        Func<CancellationToken, Task<string?>> ask,
        Func<string, CancellationToken, Task>? say = null,
        Task? started = null,
        TimeSpan? budget = null,
        TimeSpan? patience = null,
        Action<string>? log = null,
        Func<bool>? givenUp = null,
        Func<DateTime>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(ask);

        if (ct.IsCancellationRequested) return default;

        var warm = true;
        try { warm = ready(); }
        catch { warm = false; }

        // ONE HOLDING LINE FOR THE WHOLE WAIT, AND THE CAUSE PICKS THE WORDS.
        //
        // Covering the load and the generation with a single filler rather than
        // one each is not tidiness: two fillers means being interrupted twice on
        // the one turn that was already going slowly. And the line is chosen
        // HERE, from the state at entry, because that is the only moment the
        // cause is known - "my brain is still coming up" said while it is up and
        // thinking is a lie about something the person can check.
        var done = new TaskCompletionSource();

        // EITHER SIGNAL ENDS THE NEED FOR A FILLER: the first sound of the answer
        // if the caller can tell us about it, or the whole answer if it cannot.
        // Both, because a streamed answer that turns out to be empty never raises
        // the first signal at all, and the turn must still finish.
        var heard = started is null
            ? done.Task
            : Task.WhenAny(started, done.Task);

        var filler = say is null
            ? Task.FromResult(false)
            : HoldingLine.SpeakIfSlowAsync(
                heard,
                say,
                warm ? AssistantPersona.Thinking : AssistantPersona.NotUpYet,
                patience ?? (warm ? ThinkingPatience : HoldingLine.Patience),
                delay,
                ct);

        // NOTHING RETURNS BEFORE THE FILLER HAS FINISHED SPEAKING. Both run
        // through the same voice, and two sounds at once is worse than either.
        // SpeakIfSlowAsync awaits its own say, so awaiting it here is the whole
        // guarantee - which is why every exit below goes through this.
        async Task<bool> StopFillingAsync()
        {
            done.TrySetResult();
            try { return await filler.ConfigureAwait(false); }
            catch { return false; }
        }

        if (!warm)
        {
            // THE QUESTION IS THE INSTRUCTION TO START. WakeTurn's whole failure
            // was treating "not ready" as an answer rather than as a thing to do
            // something about.
            if (start is not null)
            {
                try { start(); }
                catch (Exception ex) { log?.Invoke("could not be started: " + ex.Message); }
            }

            var came = await ReadinessWait.UntilAsync(
                ready: ready,
                budget: budget ?? WarmUp,
                say: log,
                now: now,
                delay: delay,
                ct: ct,
                givenUp: givenUp).ConfigureAwait(false);

            if (!came)
            {
                var spoke = await StopFillingAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return default;

                // IT SAYS SO. This is the branch that used to be `return null`,
                // and the sentence is said with the model down - which is the
                // whole reason AssistantPersona's lines are constants and not
                // generated.
                await SayAnywayAsync(say, AssistantPersona.CouldNotStart, log, ct)
                    .ConfigureAwait(false);

                return new ColdTurnResult(null, AssistantPersona.CouldNotStart, spoke);
            }
        }

        string? answer;
        try { answer = await ask(ct).ConfigureAwait(false); }
        catch { await StopFillingAsync().ConfigureAwait(false); throw; }

        var filled = await StopFillingAsync().ConfigureAwait(false);

        // IT WAS UP AND IT SAID NOTHING. Rare, and not this type's fault to
        // explain - but a turn that makes no sound is the defect being removed, so
        // the honest sentence is the same one: ask again.
        if (string.IsNullOrWhiteSpace(answer))
        {
            if (ct.IsCancellationRequested) return default;

            await SayAnywayAsync(say, AssistantPersona.CouldNotStart, log, ct)
                .ConfigureAwait(false);

            return new ColdTurnResult(null, AssistantPersona.CouldNotStart, filled);
        }

        return new ColdTurnResult(answer, null, filled);
    }

    /// <summary>Says a line, and never lets the voice failing end the turn.</summary>
    /// <remarks>
    /// The excuse is what is left when everything else has already gone wrong. A
    /// voice that cannot deliver it must not turn a bad turn into a thrown one -
    /// the caller still has the sentence in the result.
    /// </remarks>
    private static async Task SayAnywayAsync(
        Func<string, CancellationToken, Task>? say,
        string line,
        Action<string>? log,
        CancellationToken ct)
    {
        if (say is null) return;

        try { await say(line, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Invoke("could not say so either: " + ex.Message); }
    }
}
