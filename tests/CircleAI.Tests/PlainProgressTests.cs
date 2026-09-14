// PlainProgressTests.cs
//
// The voice pipeline reports to whoever is debugging it. One head put those
// lines straight into a row subtitle, so somebody choosing a language watched
// their phone print "voice-under-test=af_ZA-google-nasional" and "phones=41" at
// them for forty seconds.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class PlainProgressTests
{
    [Theory]
    [InlineData("prereq espeak-ng-data", "fetching what it needs")]
    [InlineData("sideload found af_ZA", "importing the voice")]
    [InlineData("downloaded 41 MB", "downloaded — loading")]
    [InlineData("phones=41", "sounding out the words")]
    [InlineData("saying it now", "saying it")]
    [InlineData("respelt 3 words", "saying it")]
    [InlineData("synthesised 2.1s", "ready to play")]
    public void An_engine_line_becomes_a_phase(string line, string expected)
        => Assert.Equal(expected, PlainProgress.Say(line));

    [Fact]
    public void Voice_under_test_is_matched_before_the_bare_voice_prefix()
    {
        // THE ORDERING BUG. "voice-under-test" also starts with "voice", so
        // testing the general prefix first collapses every voice line into
        // "found the voice" - and the subtitle then never changes while the
        // engine is actually loading.
        Assert.Equal("getting ready", PlainProgress.Say("voice-under-test=af_ZA-google-nasional"));
        Assert.Equal("found the voice", PlainProgress.Say("voice af_ZA ready"));
    }

    [Fact]
    public void A_percentage_is_the_download_whatever_else_the_line_says()
    {
        // Progress carrying a number is the one line worth showing nearly
        // verbatim: it is the only one that tells somebody it is still moving.
        Assert.StartsWith("downloading…", PlainProgress.Say("voice af_ZA 41%"),
                          StringComparison.Ordinal);
        Assert.Contains("41%", PlainProgress.Say("voice af_ZA 41%"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_warm_engine_reads_differently_from_a_loading_one()
    {
        // Same prefix, opposite meanings: one says wait, the other says go.
        Assert.Equal("voice ready", PlainProgress.Say("engine WARM in 812ms"));
        Assert.Equal("loading the voice", PlainProgress.Say("engine loading"));
    }

    [Fact]
    public void Pocket_reports_in_words_already_so_it_passes_through()
        => Assert.Equal("pocket: cloning your voice",
                        PlainProgress.Say("pocket: cloning your voice"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("some line nobody thought about")]
    public void Anything_unrecognised_is_still_progress_not_diagnostics(string? line)
    {
        // SHOWING THE RAW LINE HERE would put the diagnostics back on screen for
        // exactly the lines nobody anticipated - which is where they would be
        // least expected and least explicable.
        Assert.Equal("getting ready", PlainProgress.Say(line));
    }
}
