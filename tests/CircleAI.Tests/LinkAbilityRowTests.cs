// LinkAbilityRowTests.cs
//
// The abilities a linked client is shown, and the one column that makes them do
// anything.
//
// A ROW CROSSED THE LINK WITHOUT ITS ROUTE AND THE SCREEN WENT QUIET. AbilityRow
// carries TryRoute so a head can tell "start the listener" from "fetch the model":
// Waking is the one ability whose button starts something already on the phone, and
// the abilities screen decides which branch to take by reading that route. The link
// carried four columns, so a linked client received every row with TryRoute null and
// sent every Waking tap down the download path - a full setup run for a bundle that
// was already there, after which the row still said "Turn on".
//
// Measured on a P30 on 2026-10-01: "Answer to its name" ticked and the microphone
// open, and the Waking row on the same screen offering to turn it on.

using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class LinkAbilityRowTests
{
    [Fact]
    public void Carries_the_route_so_the_screen_can_start_rather_than_download()
    {
        var row = LinkSetupRows.Ability("Waking", "Hears you without being touched",
            "Ready", bytes: null, route: "wake");

        Assert.Equal("wake", LinkSetupRows.Text(row, 4));
    }

    [Fact]
    public void An_ability_the_serving_head_has_no_screen_for_carries_no_route()
    {
        var row = LinkSetupRows.Ability("Seeing", "Looks at a photo", "Available", 311_000_000);

        Assert.Equal(string.Empty, LinkSetupRows.Text(row, 4));
        Assert.Equal("311000000", LinkSetupRows.Text(row, 3));
    }

    [Fact]
    public void The_columns_before_it_did_not_move()
    {
        var row = LinkSetupRows.Ability("Music", "Makes a piece of music", "On", null, null);

        Assert.Equal("Music", LinkSetupRows.Text(row, 0));
        Assert.Equal("Makes a piece of music", LinkSetupRows.Text(row, 1));
        Assert.Equal("On", LinkSetupRows.Text(row, 2));
        Assert.Equal(string.Empty, LinkSetupRows.Text(row, 3));
    }

    [Fact]
    public void A_four_column_row_from_an_older_service_still_reads()
    {
        // THE OLD SHAPE IS STILL ON PHONES. A client updated before the service
        // reads column 4 of a four-column row; Text must answer empty rather than
        // throw, which is the contract it already documents.
        var old = new[] { "Waking", "Hears you", "Ready", string.Empty };

        Assert.Equal(string.Empty, LinkSetupRows.Text(old, 4));
    }
}
