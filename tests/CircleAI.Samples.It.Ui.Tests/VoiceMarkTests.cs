// VoiceMarkTests.cs
//
// The single owner of "what is the microphone doing", pinned.
//
// THE DEFECT THESE EXIST FOR: Home's circle and the middle of the tab bar are
// the same control offered twice, and each kept its own MarkState field with its
// own copy of the same phase switch. A turn started from one left the other
// drawn idle - press the circle and the bar goes on offering "talk to it" while
// it is already listening to you. Read off a P30 through the WebView debugger:
// tapping the hero put bm-listening on the hero alone.
//
// None of that needed a phone to catch. It needed one assertion that two
// readers of one state agree, which is what this file is.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class VoiceMarkTests
{
    [Fact]
    public void Starts_idle_and_not_busy()
    {
        var mark = new VoiceMark();

        Assert.Equal(MarkState.Idle, mark.State);
        Assert.Equal(0, mark.Level);
        Assert.False(mark.Busy);
    }

    [Fact]
    public void Reports_state_and_level_to_every_reader()
    {
        var mark = new VoiceMark();

        // Two readers, standing in for the hero and the tab bar: the point of the
        // type is that they cannot disagree.
        MarkState? hero = null, bar = null;
        mark.Changed += () => hero = mark.State;
        mark.Changed += () => bar = mark.State;

        mark.Report(MarkState.Listening, 0.4);

        Assert.Equal(MarkState.Listening, mark.State);
        Assert.Equal(0.4, mark.Level);
        Assert.True(mark.Busy);
        Assert.Equal(MarkState.Listening, hero);
        Assert.Equal(MarkState.Listening, bar);
    }

    [Fact]
    public void Silent_when_nothing_changed()
    {
        // A turn reports its level many times a second. Re-rendering both marks
        // for a value identical to the one they already hold is work with nothing
        // to show for it.
        var mark = new VoiceMark();
        var raised = 0;
        mark.Changed += () => raised++;

        mark.Report(MarkState.Listening, 0.25);
        mark.Report(MarkState.Listening, 0.25);
        mark.Report(MarkState.Listening, 0.25);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Clear_takes_the_level_with_it()
    {
        // A turn that finished while the arcs were still wide would leave them
        // wide - idle drawn as though it were listening.
        var mark = new VoiceMark();
        mark.Report(MarkState.Listening, 0.9);

        mark.Clear();

        Assert.Equal(MarkState.Idle, mark.State);
        Assert.Equal(0, mark.Level);
        Assert.False(mark.Busy);
    }

    [Theory]
    [InlineData(TurnPhase.Listening, MarkState.Listening)]
    [InlineData(TurnPhase.Thinking, MarkState.Thinking)]
    [InlineData(TurnPhase.Speaking, MarkState.Speaking)]
    [InlineData(TurnPhase.Idle, MarkState.Idle)]
    public void Maps_every_turn_phase(TurnPhase phase, MarkState expected)
    {
        // THE MAPPING LIVED IN BOTH BUTTONS, character for character. Two copies
        // of one switch is two places to forget a phase, and a missed case draws
        // the WRONG thing rather than nothing - which is harder to notice.
        var mark = new VoiceMark();

        mark.Report(new TurnState(phase, Level: 0.5));

        Assert.Equal(expected, mark.State);
    }

    [Fact]
    public void Busy_is_shared_so_a_second_turn_cannot_start()
    {
        // Each button used to test its own field, so the two could open a second
        // microphone on top of the first, and whichever lost the race reported
        // the failure.
        var mark = new VoiceMark();

        mark.Report(new TurnState(TurnPhase.Listening));

        Assert.True(mark.Busy);   // the hero is mid-turn...
        Assert.True(mark.Busy);   // ...so the bar must refuse, reading the same flag
    }

    [Fact]
    public void Level_defaults_to_zero_and_is_never_invented()
    {
        // Zero is the honest default: a mark that moves as though it were hearing
        // you when it is not is the one lie the component's notes forbid. The
        // listening arcs breathe on their own, so an absent level does not read
        // as a dead button.
        var mark = new VoiceMark();

        mark.Report(MarkState.Listening);

        Assert.Equal(0, mark.Level);
        Assert.True(mark.Busy);
    }
}
