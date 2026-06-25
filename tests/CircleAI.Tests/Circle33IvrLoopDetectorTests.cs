// Circle33IvrLoopDetectorTests.cs
//
// (3.3.0) Tests for IVR loop detector.

using System;
using CircleAI.Telephony;
using Xunit;

namespace CircleAI.Tests;

public class Circle33IvrLoopDetectorTests
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public void NotEnoughRounds_NotLooping()
    {
        var d = new IvrLoopDetector();
        var v = d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        Assert.False(v.IsLooping);
    }

    [Fact]
    public void SamePromptSameKeyTwice_NotEnoughForLoop()
    {
        var d = new IvrLoopDetector();
        d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        var v = d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        Assert.False(v.IsLooping);
    }

    [Fact]
    public void TripleSamePromptAndKey_DetectsLoop()
    {
        var d = new IvrLoopDetector();
        d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        var v = d.Observe(new IvrRound("Press 1 for sales.", "1", Now));
        Assert.True(v.IsLooping);
        Assert.Equal(1, v.LoopLength);
    }

    [Fact]
    public void TwoStepCycle_Detected()
    {
        var d = new IvrLoopDetector();
        d.Observe(new IvrRound("Main menu. Press 1 for sales.", "1", Now));
        d.Observe(new IvrRound("Sales submenu. Press 2 for orders.", "2", Now));
        d.Observe(new IvrRound("Main menu. Press 1 for sales.", "1", Now));
        var v = d.Observe(new IvrRound("Sales submenu. Press 2 for orders.", "2", Now));
        Assert.True(v.IsLooping);
        Assert.Equal(2, v.LoopLength);
    }

    [Fact]
    public void NonLoopingConversation_NoLoop()
    {
        var d = new IvrLoopDetector();
        d.Observe(new IvrRound("Welcome.", null, Now));
        d.Observe(new IvrRound("Please describe the issue.", null, Now));
        var v = d.Observe(new IvrRound("Thanks. Connecting you now.", null, Now));
        Assert.False(v.IsLooping);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var d = new IvrLoopDetector();
        for (int i = 0; i < 4; i++)
            d.Observe(new IvrRound("repeat", "1", Now));
        Assert.True(d.CurrentVerdict().IsLooping);

        d.Reset();
        Assert.False(d.CurrentVerdict().IsLooping);
    }

    [Fact]
    public void SimilarPromptsAreTreatedAsSame()
    {
        var d = new IvrLoopDetector(similarityThreshold: 0.5);
        d.Observe(new IvrRound("Press 1 for sales now please now.", "1", Now));
        d.Observe(new IvrRound("Press 1 for sales now please now.", "1", Now));
        d.Observe(new IvrRound("Press 1 for sales now please now.", "1", Now));
        Assert.True(d.CurrentVerdict().IsLooping);
    }

    [Fact]
    public void HistoryCapped_DoesNotGrowUnbounded()
    {
        var d = new IvrLoopDetector(maxRoundsToTrack: 5);
        for (int i = 0; i < 20; i++) d.Observe(new IvrRound($"step {i}", null, Now));
        Assert.False(d.CurrentVerdict().IsLooping);
    }
}
