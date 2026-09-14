// SetupProgressReportTests.cs
//
// The line somebody watches for forty minutes.
//
// Two screens show this run now - the loading screen, and Home once the loading
// screen stopped holding people hostage for the whole download. The last time
// one fact had two screens formatting it, they disagreed.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class SetupProgressReportTests
{
    static SetupProgressReport At(
        double fraction, TimeSpan left, SetupPhase phase = SetupPhase.Fetching,
        string title = "the brain")
        => new(Index: 1, Count: 3, Title: title, Fraction: fraction,
               Remaining: left, Phase: phase);

    [Fact]
    public void Checking_never_shows_a_countdown()
    {
        // THE STATE THAT LOOKED LIKE A CRASH. Measured on a Redmi Note 12 Pro+ on
        // 2026-09-05: the 1.3 GB brain finished arriving, the phone went to 267%
        // CPU hashing it with the network idle, and the screen sat on "about 20
        // sec left" for over a minute without moving. The phase was added to fix
        // exactly that, and the page still printed the stale estimate because
        // nothing read it.
        var said = At(0.94, TimeSpan.FromSeconds(20), SetupPhase.Checking).Describe();

        Assert.Contains("checking what arrived", said, StringComparison.Ordinal);
        Assert.DoesNotContain("left", said, StringComparison.Ordinal);
        Assert.DoesNotContain("%", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_says_how_far_in_and_when_it_ends()
    {
        var said = At(0.43, TimeSpan.FromMinutes(43)).Describe();

        Assert.Contains("the brain", said, StringComparison.Ordinal);
        Assert.Contains("43%", said, StringComparison.Ordinal);
        Assert.Contains("43 min left", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Under_a_minute_is_words_not_seconds()
    {
        // Nobody waiting on a download is counting seconds, and a countdown that
        // ticks 9, 8, 7 and then sits at 0 reads as stuck.
        Assert.Contains("less than a minute left",
            At(0.99, TimeSpan.FromSeconds(12)).Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]        // not known yet
    [InlineData(-30)]      // a rate that has gone backwards
    [InlineData(60 * 60 * 13)]  // thirteen hours: the rate is still settling
    public void An_estimate_that_means_nothing_is_not_printed(int seconds)
    {
        // A PERCENTAGE IS ALWAYS HONEST; AN ESTIMATE IS NOT. The first seconds of
        // a run have no rate to divide by, and printing "about 0 sec left" over a
        // download that has not started is the worst possible first impression.
        var said = At(0.07, TimeSpan.FromSeconds(seconds)).Describe();

        Assert.Contains("7%", said, StringComparison.Ordinal);
        Assert.DoesNotContain("left", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.4)]
    [InlineData(-0.2)]
    public void A_fraction_outside_the_bar_is_clamped_not_printed(double fraction)
    {
        // The fraction is weighted by bytes across the whole plan and a resumed
        // part has been seen to report past its own end. "140%" on a progress
        // line is the screen admitting it does not know what is happening.
        var said = At(fraction, TimeSpan.FromMinutes(2)).Describe();

        Assert.True(said.Contains("100%", StringComparison.Ordinal)
                 || said.Contains("0%", StringComparison.Ordinal),
                    $"expected a clamped percentage, got: {said}");
    }

    [Fact]
    public void Done_says_so_rather_than_showing_a_full_bar()
        => Assert.Contains("done",
            At(1.0, TimeSpan.Zero, SetupPhase.Done).Describe(), StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_part_with_no_name_still_reads_as_a_sentence(string title)
    {
        // The title comes from a catalogue entry, and one has shipped without a
        // display name before. A line that opens with a dash is a bug on screen.
        var said = At(0.5, TimeSpan.FromMinutes(3), title: title).Describe();

        Assert.StartsWith("Setting it up", said, StringComparison.Ordinal);
    }
}
