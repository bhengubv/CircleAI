// SearchMergeTests.cs
//
// Two ranked lists, one screen, and neither allowed to starve the other.
//
// THE BUG THIS EXISTS FOR WAS MINE, AND IT WAS THE DEFECT THIS WHOLE REGISTER IS
// ABOUT. CircleAISession.SearchAsync built one list - memory results, then
// transcript results - and capped the whole thing with .Take(take). So a memory
// that answered with twenty hits left room for NO transcript at all, and the
// transcript store, which is the entire reason "search across everything" stopped
// meaning "search memory", disappeared from the results exactly when the other
// source was doing well.
//
// AND IT WENT UNNOTICED FOR THE USUAL REASON. FileTranscriptStore has thirteen
// tests. LexicalIndex and Bm25 have eighteen. The step that COMBINES them had
// none, because combining lived on a session and building a session means a
// model - so the one piece with no test was the one piece with the bug.
//
// That is why Merge is public and static: the same argument
// ModelScopeCatalogClient.InferModality makes for itself. A list that has
// silently dropped one of its two sources looks exactly like a list that had
// nothing to show from it.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Assistant;   // both Found and CircleAISession — the Runtime
                            // assembly shares the Assistant namespace
using Xunit;

namespace CircleAI.Tests;

public sealed class SearchMergeTests
{
    private static IReadOnlyList<Found> Memory(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new Found("memory", "", "Remembered", $"m{i}"))];

    private static IReadOnlyList<Found> Transcripts(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new Found("transcript", $"t{i}", "Recording", $"t{i}"))];

    [Fact]
    public void A_busy_memory_can_no_longer_hide_every_transcript()
    {
        // THE REGRESSION. Twenty memories and five transcript lines, twenty
        // slots. The old concatenate-then-cap returned twenty memories and not
        // one recording.
        var merged = CircleAISession.Merge(Memory(20), Transcripts(5), take: 20);

        Assert.Equal(20, merged.Count);
        Assert.Contains(merged, f => f.Kind == "transcript");
        Assert.Equal(5, merged.Count(f => f.Kind == "transcript"));
        Assert.Equal(15, merged.Count(f => f.Kind == "memory"));
    }

    [Fact]
    public void Memory_still_keeps_the_top_of_the_list()
    {
        // The priority argument in SearchAsync stands - a remembered fact was
        // told to the app on purpose, a transcript line is what a microphone
        // happened to catch. First, though, not instead.
        var merged = CircleAISession.Merge(Memory(20), Transcripts(20), take: 20);

        Assert.Equal("memory", merged[0].Kind);
        Assert.Equal(10, merged.Count(f => f.Kind == "memory"));
        Assert.Equal(10, merged.Count(f => f.Kind == "transcript"));

        // And memory's block is contiguous at the top, not interleaved -
        // alternating would bury a strong recall hit under a weak line.
        var firstTranscript = merged.ToList().FindIndex(f => f.Kind == "transcript");
        Assert.DoesNotContain(merged.Skip(firstTranscript), f => f.Kind == "memory");
    }

    [Fact]
    public void One_source_answering_alone_fills_the_list()
    {
        // A phone that has recorded nothing must still get a full page of
        // memories, and one that has been told nothing a full page of lines.
        Assert.Equal(20, CircleAISession.Merge(Memory(30), Transcripts(0), take: 20).Count);
        Assert.Equal(20, CircleAISession.Merge(Memory(0), Transcripts(30), take: 20).Count);

        Assert.All(CircleAISession.Merge(Memory(0), Transcripts(30), take: 20),
            f => Assert.Equal("transcript", f.Kind));
    }

    [Fact]
    public void An_unused_share_goes_to_the_other_side()
    {
        // Three memories and plenty of recordings: the seventeen slots memory
        // cannot fill are transcripts', not wasted.
        var merged = CircleAISession.Merge(Memory(3), Transcripts(30), take: 20);

        Assert.Equal(20, merged.Count);
        Assert.Equal(3,  merged.Count(f => f.Kind == "memory"));
        Assert.Equal(17, merged.Count(f => f.Kind == "transcript"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    public void The_cap_is_never_exceeded_and_nothing_is_wasted(int take)
    {
        var merged = CircleAISession.Merge(Memory(50), Transcripts(50), take);

        Assert.Equal(take, merged.Count);   // both sides have plenty, so it fills
    }

    [Fact]
    public void A_single_slot_goes_to_memory()
    {
        // With room for one answer there is no fair split to make, and the
        // priority rule decides it. Stated rather than left to arithmetic.
        var merged = CircleAISession.Merge(Memory(5), Transcripts(5), take: 1);

        Assert.Equal("memory", Assert.Single(merged).Kind);
    }

    [Fact]
    public void Two_slots_are_one_each()
    {
        var merged = CircleAISession.Merge(Memory(5), Transcripts(5), take: 2);

        Assert.Equal(1, merged.Count(f => f.Kind == "memory"));
        Assert.Equal(1, merged.Count(f => f.Kind == "transcript"));
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        Assert.Empty(CircleAISession.Merge(Memory(0), Transcripts(0), take: 20));
        Assert.Empty(CircleAISession.Merge(Memory(5), Transcripts(5), take: 0));
        Assert.Empty(CircleAISession.Merge(Memory(5), Transcripts(5), take: -1));
    }

    [Fact]
    public void The_order_within_each_source_is_the_order_it_was_given()
    {
        // Both sources rank their own results and this must not reshuffle them -
        // a recall rank and a BM25 score are different units, so there is no
        // comparison to make between the two lists and none is attempted.
        var merged = CircleAISession.Merge(Memory(4), Transcripts(4), take: 8);

        Assert.Equal(["m0", "m1", "m2", "m3"],
            merged.Where(f => f.Kind == "memory").Select(f => f.Text));
        Assert.Equal(["t0", "t1", "t2", "t3"],
            merged.Where(f => f.Kind == "transcript").Select(f => f.Text));
    }
}
