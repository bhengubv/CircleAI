// CensusWordingTests.cs
//
// THREE SCREENS, ONE MODEL, THREE DIFFERENT NUMBERS - and the one sentence that
// explained a half-finished download was built, sent over the link in a field added
// specifically to carry it, rebuilt on the other side, and then thrown away one
// ternary from the markup.
//
// What a person reading the app actually saw for Qwen3.6-35B-A3B-MNN
// (22 797 996 902 bytes) before this:
//
//   Setup.razor      "22.8 GB"     Size() in decimal
//   Loading.razor    "21.2 GB"     Size() in binary
//   Settings.razor   "22798 MB"    integer MB, no GB rollover
//
// and on the loading screen, for a model with 1.1 GB already down, "21.2 GB" rather
// than "1.1 GB of 22.8 GB here - it will carry on", because Detail was rendered only
// when Present was already true. CapabilityRow.Partial and CensusRow.Partial had no
// reader anywhere outside PartialDownloadTests.
//
// DECIMAL IS NOT A PREFERENCE. DeviceProbe.BytesPerGb is 10^9 and ModelFit's remark
// says why: the catalogue is persisted in those units, so a screen rendering a
// catalogued size in binary quotes a different number from the one the fit rule
// reasoned about.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class CensusWordingTests
{
    private const long ThirtyFiveB = 22_797_996_902;

    [Fact]
    public void A_half_finished_download_says_so_on_the_screen()
    {
        // THE DEFECT, AS ONE ASSERTION. This row crossed the link carrying its own
        // sentence and the screen showed a bare size instead.
        var row = new CensusRow("the brain", Present: false, Bytes: ThirtyFiveB,
                                Detail: "1.1 GB of 22.8 GB here - it will carry on",
                                Have: 1_100_000_000);

        Assert.True(row.Partial);
        Assert.Equal("1.1 GB of 22.8 GB here - it will carry on", row.Says);
    }

    [Fact]
    public void A_model_nobody_started_says_what_it_would_cost()
    {
        // The useful fact for a row that has not begun is the price, not a sentence.
        var row = new CensusRow("the brain", Present: false, Bytes: ThirtyFiveB,
                                Detail: "not on this phone yet");

        Assert.False(row.Partial);
        Assert.Equal("22.8 GB", row.Says);
    }

    [Fact]
    public void Something_already_here_says_what_it_gives_you()
    {
        var row = new CensusRow("the voice", Present: true, Bytes: 60_000_000,
                                Detail: "eleven languages", Have: 60_000_000);

        Assert.Equal("eleven languages", row.Says);
    }

    [Theory]
    [InlineData(22_797_996_902, "22.8 GB")]   // the 35B, as the catalogue states it
    [InlineData(1_386_691_327,  "1.4 GB")]    // the 2B
    [InlineData(1_000_000_000,  "1 GB")]      // the rollover boundary
    [InlineData(999_999_999,    "1000 MB")]
    [InlineData(60_000_000,     "60 MB")]
    [InlineData(1_000,          "1 kB")]
    [InlineData(0,              "")]
    [InlineData(-5,             "")]
    public void One_size_formatter_in_the_units_the_catalogue_is_persisted_in(long bytes, string expected)
        => Assert.Equal(expected, CensusRow.Size(bytes));

    [Fact]
    public void A_catalogued_size_is_never_rendered_in_binary()
    {
        // The binary reading of the 35B was 21.2 GB. If this ever passes, somebody
        // has changed the base and three screens are disagreeing again.
        Assert.DoesNotContain("21.2", CensusRow.Size(ThirtyFiveB));
    }

    [Fact]
    public void The_summary_has_one_wording_whichever_half_built_it()
    {
        // The on-device path said "3 of 5 on this phone"; the linked path said
        // "3 of 5 ready." Same census, same counts, two sentences.
        Assert.Equal("3 of 5 on this phone", Census.Line(3, 5));
        Assert.Equal("Nothing on this phone yet.", Census.Line(0, 0));
    }
}
