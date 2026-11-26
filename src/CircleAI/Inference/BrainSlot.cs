// BrainSlot.cs
//
// THE ONE BRAIN HAS ONE SLOT, AND THIS DECIDES WHO GETS IT. A phone that holds a model bigger than its RAM holds
// exactly one, and it writes one answer at a time; a second question asked while the first is still being written has
// to wait, step the first aside, or be told the brain is busy. That decision was inline in the Android worker
// (InferenceWorkerService.Generate), where no test leg compiles it - so the day it rejected every turn of a
// conversation, nothing but the phone could have caught it. It is engine policy, not Android plumbing, so it lives
// here with the rest of how the brain is driven (BrainInWaiting), and it is unit-tested.
//
// It says WHAT to do; the worker still does it - cancels the job's token, waits for it to finish, writes the answer.

namespace CircleAI.Inference;

/// <summary>What becomes of the one brain slot when a new answer is asked of the worker.</summary>
public enum BrainSlotMove
{
    /// <summary>The slot is free: write the answer.</summary>
    Admit,

    /// <summary>A job holds the slot but yields it - a speculative read-ahead, or an answer already told to stop: stop
    /// it, wait for it to let go, then write the answer.</summary>
    StepAsideThenAdmit,

    /// <summary>A live answer holds the slot: the new ask is told the brain is busy.</summary>
    Busy,
}

/// <summary>The single answer slot of the one brain the worker holds.</summary>
public static class BrainSlot
{
    /// <summary>
    /// The move for a new answer asked of the worker, given the job in the slot now.
    /// </summary>
    /// <param name="jobPresent">A job holds the slot.</param>
    /// <param name="jobFinished">That job has finished (its answer is written, or it failed or was cancelled).</param>
    /// <param name="headStart">That job is a speculative read-ahead - whatever real comes takes the brain from it.</param>
    /// <param name="toldToStop">That job has already been asked to stop (its cancellation was requested).</param>
    public static BrainSlotMove Next(bool jobPresent, bool jobFinished, bool headStart, bool toldToStop)
    {
        if (!jobPresent || jobFinished) return BrainSlotMove.Admit;
        if (headStart || toldToStop) return BrainSlotMove.StepAsideThenAdmit;
        return BrainSlotMove.Busy;
    }
}
