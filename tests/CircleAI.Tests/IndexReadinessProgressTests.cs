using System;
using CircleAI.Assistant.Lifecycle;
using Xunit;

namespace CircleAI.Tests;

// IndexReadiness.Progress maps the "index" component of a readiness snapshot to the typed view a
// screen draws a bar from - and returns NOTHING for every state that is not a build in progress, so
// the card is silent when there is nothing to optimise. The share, the charge-hold and the silence
// all have one owner here rather than a parser on each side of the link.
public class IndexReadinessProgressTests
{
    private static ReadinessSnapshot Snapshot(params ComponentState[] components) =>
        new(ReadinessSnapshot.CurrentSchema, 1, "boot",
            ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, "", DateTimeOffset.UtcNow),
            components, [], []);

    private static ComponentState Index(
        BrainPhase phase, double? progress, PauseReason pause = PauseReason.None, string detail = "optimising") =>
        new(ReadinessComponents.Index, phase, LoadStep.None, null, pause, detail, null, DateTimeOffset.UtcNow, progress);

    [Fact]
    public void A_building_index_becomes_a_share_and_its_line()
    {
        var progress = IndexReadiness.Progress(Snapshot(Index(BrainPhase.Loading, 0.42, detail: "Working at 42%.")));

        Assert.NotNull(progress);
        Assert.Equal(42, progress!.Percent);
        Assert.Equal("Working at 42%.", progress.Line);
        Assert.False(progress.OnHold);
        Assert.Null(progress.HoldReason);
    }

    [Fact]
    public void Off_the_charger_an_unfinished_index_says_it_waits_for_charge()
    {
        var progress = IndexReadiness.Progress(Snapshot(Index(BrainPhase.Loading, 0.1, PauseReason.WaitingForCharge)));

        Assert.NotNull(progress);
        Assert.True(progress!.OnHold);
        Assert.Equal(IndexReadiness.HoldLine, progress.HoldReason);
    }

    // "Learning" is a quieter line under Tap and talk, not a progress bar: a complete index shows nothing here.
    [Fact]
    public void A_complete_index_shows_nothing_here()
        => Assert.Null(IndexReadiness.Progress(Snapshot(Index(BrainPhase.Ready, null))));

    [Fact]
    public void A_taken_back_index_shows_nothing()
        => Assert.Null(IndexReadiness.Progress(Snapshot(Index(BrainPhase.Unknown, null))));

    [Fact]
    public void No_index_component_shows_nothing()
        => Assert.Null(IndexReadiness.Progress(Snapshot()));

    [Fact]
    public void A_null_snapshot_shows_nothing()
        => Assert.Null(IndexReadiness.Progress(null));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(0.999, 100)]
    [InlineData(1.0, 100)]
    [InlineData(1.5, 100)]   // a fraction over one is clamped, never shown past full
    public void The_share_is_a_clamped_percent(double fraction, int percent)
        => Assert.Equal(percent, IndexReadiness.Progress(Snapshot(Index(BrainPhase.Loading, fraction)))!.Percent);

    // A build that has not measured a share yet reads zero, not a blank bar.
    [Fact]
    public void A_building_index_with_no_measured_share_is_zero()
        => Assert.Equal(0, IndexReadiness.Progress(Snapshot(Index(BrainPhase.Loading, null)))!.Percent);
}
