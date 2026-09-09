// MemoryEffects.cs
//
// The loop between what was just said and what is remembered next week.
//
// THE HALF THAT WAS MISSING. The app called LearnAsync on every utterance and
// RecallAsync from nowhere but a diagnostic screen, so it accumulated everything
// anybody ever said to it and could not tell them their own name the following
// morning. Writing without reading is not memory, it is a log.
//
// Both directions live here rather than in the turn, because the turn should not
// have to remember to remember. An exchange finishing is an EVENT; what happens
// to it afterwards is this file's business, and a screen that dispatches
// TurnEnded gets the write-through whether or not its author knew about it.
//
// NEITHER DIRECTION MAY DELAY AN ANSWER. Recall sits directly in front of a
// reply somebody is waiting for and the write sits directly after one, so both
// are time-boxed and both swallow: a remembered name is worth having and never
// worth making somebody wait for, and a memory that could not be written is not
// a reason to fail a turn that already worked.

using System;
using System.Threading;
using System.Threading.Tasks;
using Fluxor;

namespace CircleAI.Samples.It.Shared.State;

public sealed class MemoryEffects
{
    private readonly IRemembers _memory;

    public MemoryEffects(IRemembers memory) => _memory = memory;

    /// <summary>How long recall may take before the turn goes on without it.</summary>
    /// <remarks>
    /// A budget rather than a timeout on the store: the store may be perfectly
    /// healthy and simply slower than the moment allows. Measured against the
    /// rest of a turn — several seconds of transcription and thinking — a fifth
    /// of a second is affordable and a second is not.
    /// </remarks>
    private static readonly TimeSpan RecallBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Ask what is worth knowing, and say so even when the answer is nothing.</summary>
    /// <remarks>
    /// ALWAYS DISPATCHES Recalled, including with an empty list. A turn that
    /// silently kept the PREVIOUS turn's recalled facts would answer this
    /// question with last question's memory, which is worse than answering it
    /// with none.
    /// </remarks>
    [EffectMethod]
    public async Task OnRecallWanted(RecallWanted action, IDispatcher dispatcher)
    {
        try
        {
            using var budget = new CancellationTokenSource(RecallBudget);
            var facts = await _memory.RecallAsync(action.About, ct: budget.Token)
                .ConfigureAwait(false);
            dispatcher.Dispatch(new Recalled(facts));
        }
        catch (OperationCanceledException)
        {
            // Too slow for this turn. Answer without it rather than late with it.
            dispatcher.Dispatch(new Recalled([]));
        }
        catch
        {
            dispatcher.Dispatch(new Recalled([]));
        }
    }

    /// <summary>
    /// A finished exchange goes through to the long-term store.
    /// </summary>
    /// <remarks>
    /// THE CACHE IS THE SOURCE, NOT THE TURN. Whatever put an exchange into the
    /// short-term cache is what makes it worth keeping, so the flush hangs off
    /// the same action — one place decides, and a caller cannot half-remember by
    /// forgetting to also call Learn.
    /// <para>
    /// Only what the PERSON said. Feeding the assistant's own replies back into
    /// long-term memory teaches it what it already believes, and on a small model
    /// that is how a wrong answer becomes a remembered fact.
    /// </para>
    /// </remarks>
    [EffectMethod]
    public async Task OnTurnEnded(TurnEnded action, IDispatcher dispatcher)
    {
        if (string.IsNullOrWhiteSpace(action.Said)) return;

        try { await _memory.LearnAsync(action.Said!, CancellationToken.None).ConfigureAwait(false); }
        catch
        {
            // A memory that could not be written is never worth failing a turn
            // that already answered somebody.
        }
    }
}
