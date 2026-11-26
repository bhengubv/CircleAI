// BrainSlotTests.cs
//
// The one brain writes one answer at a time. These pin who gets the slot when a second question arrives - the policy
// that used to live, untested, in the Android worker, and rejected every turn of a conversation unseen.

using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class BrainSlotTests
{
    [Fact]
    public void An_empty_slot_admits_the_answer()
    {
        Assert.Equal(BrainSlotMove.Admit,
            BrainSlot.Next(jobPresent: false, jobFinished: false, headStart: false, toldToStop: false));
    }

    [Fact]
    public void A_finished_job_leaves_the_slot_free()
    {
        Assert.Equal(BrainSlotMove.Admit,
            BrainSlot.Next(jobPresent: true, jobFinished: true, headStart: false, toldToStop: false));
    }

    [Fact]
    public void A_live_answer_holds_the_slot_and_the_new_ask_is_busy()
    {
        Assert.Equal(BrainSlotMove.Busy,
            BrainSlot.Next(jobPresent: true, jobFinished: false, headStart: false, toldToStop: false));
    }

    [Fact]
    public void A_read_ahead_steps_aside_for_a_real_answer()
    {
        Assert.Equal(BrainSlotMove.StepAsideThenAdmit,
            BrainSlot.Next(jobPresent: true, jobFinished: false, headStart: true, toldToStop: false));
    }

    [Fact]
    public void An_answer_already_told_to_stop_steps_aside()
    {
        Assert.Equal(BrainSlotMove.StepAsideThenAdmit,
            BrainSlot.Next(jobPresent: true, jobFinished: false, headStart: false, toldToStop: true));
    }

    [Fact]
    public void A_read_ahead_that_was_also_told_to_stop_steps_aside()
    {
        Assert.Equal(BrainSlotMove.StepAsideThenAdmit,
            BrainSlot.Next(jobPresent: true, jobFinished: false, headStart: true, toldToStop: true));
    }
}
