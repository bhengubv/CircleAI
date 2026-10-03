// PartialDownloadTests.cs
//
// A download that stopped halfway had nowhere to say so.
//
// Found on a Circle OS device on 2026-10-04. Qwen3.6-35B-A3B-MNN is 22.8 GB; the
// device held 1.1 GB of it - embeddings and graph down, llm.mnn.weight (21.3 GB)
// never arrived, and a .tmp.parts resume marker sitting beside them. Every surface
// reported "not on this phone yet" and quoted the full 22.8 GB, which is exactly
// what a model nobody had ever started would have said.
//
// Present was a bool. Missing, partial and present need three different sentences
// and three different offers - begin, carry on, or nothing - and the types could
// only express two.

using CircleAI.Assistant;
using CircleAI.Linking;
using Xunit;

namespace CircleAI.Tests;

public sealed class PartialDownloadTests
{
    [Fact]
    public void A_row_with_some_bytes_down_is_partial_not_missing()
    {
        var row = new CapabilityRow("the brain", Present: false, Bytes: 22_800_000_000,
                                    "1.1 GB of 22.8 GB here - it will carry on", Have: 1_100_000_000);

        Assert.True(row.Partial);
        Assert.False(row.Present);
    }

    [Fact]
    public void A_row_with_nothing_down_is_simply_missing()
    {
        // THE DISTINCTION THAT WAS LOST. Never started and stopped halfway are
        // different offers to a person, and they used to read identically.
        var row = new CapabilityRow("the brain", Present: false, Bytes: 22_800_000_000,
                                    "not on this phone yet");

        Assert.False(row.Partial);
        Assert.False(row.Present);
    }

    [Fact]
    public void Something_already_here_is_never_partial()
    {
        var row = new CapabilityRow("the voice", Present: true, Bytes: 60_000_000, "ready", Have: 60_000_000);

        Assert.False(row.Partial);
        Assert.True(row.Present);
    }

    [Fact]
    public void The_link_carries_the_fifth_field()
    {
        // A LINK DROPS WHAT IT DOES NOT CARRY, and both halves fail green. The
        // service could measure a half-finished download long before the wire had
        // anywhere to put the number.
        var wire = LinkSetupRows.CensusRow("the brain", present: false,
                                           bytes: 22_800_000_000, detail: "carrying on",
                                           have: 1_100_000_000);

        Assert.Equal(5, wire.Count);
        Assert.Equal("1100000000", wire[4]);
        Assert.Equal(1_100_000_000, LinkSetupRows.Number(wire, 4));
    }

    [Fact]
    public void An_older_reader_is_not_broken_by_it()
    {
        // APPENDED, NOT INSERTED. The first four columns keep their meaning, so a
        // client that has not been updated reads exactly what it always read.
        var wire = LinkSetupRows.CensusRow("the brain", present: false,
                                           bytes: 22_800_000_000, detail: "carrying on",
                                           have: 1_100_000_000);

        Assert.Equal("the brain", LinkSetupRows.Text(wire, 0));
        Assert.False(LinkSetupRows.Flag(wire, 1));
        Assert.Equal(22_800_000_000, LinkSetupRows.Number(wire, 2));
        Assert.Equal("carrying on", LinkSetupRows.Text(wire, 3));
    }

    [Fact]
    public void A_census_row_crossing_the_link_keeps_the_number()
    {
        var row = new CensusRow("the brain", Present: false, Bytes: 22_800_000_000,
                                Detail: "carrying on", Have: 1_100_000_000);

        Assert.True(row.Partial);
        Assert.Equal(1_100_000_000, row.Have);
    }
}
