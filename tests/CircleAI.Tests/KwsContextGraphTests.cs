// KwsContextGraphTests.cs
//
// The keyword trie decides whether a beam search can say NO. Its arithmetic is
// small enough to check by hand and consequential enough that getting it wrong
// produces a wake word that fires on anything — which is a worse failure than one
// that never fires, because it ships.
//
// The invariant that matters most is the first test: BOOST IS A LOAN. Anything
// that walks partway into a phrase and then leaves must end up exactly where it
// started, or every half-match of every keyword floats permanently at the top of
// the beam and the search stops discriminating.

using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class KwsContextGraphTests
{
    // "1 2 3" and "2 3" — deliberately overlapping, so the fail and output links
    // are exercised rather than just the happy path down one branch.
    private static KwsContextGraph Graph(float boost = 1.5f, float threshold = 0.3f) =>
        new(new[] { new[] { 1, 2, 3 }, new[] { 2, 3 } }, boost, threshold,
            phrases: new[] { "one two three", "two three" });

    [Fact]
    public void WalkingIntoAPhraseAndLeavingItCostsNothingOverall()
    {
        // THE LOAN. Two tokens in, then a token belonging to nothing: the sum of
        // every score paid along the way must be exactly zero. If it is positive,
        // a wrong guess has been rewarded and will outrank honest hypotheses
        // forever; the beam then fills with debris and the keyword it was meant to
        // protect gets pruned.
        var g = Graph();
        var total = 0f;
        var s = g.Root;

        var (a, s1, _) = g.ForwardOneStep(s, 1);   total += a; s = s1;
        var (b, s2, _) = g.ForwardOneStep(s, 2);   total += b; s = s2;
        var (c, s3, _) = g.ForwardOneStep(s, 99);  total += c; s = s3;

        Assert.Equal(0f, total, 4);
        Assert.Equal(-1, s.Token);                 // and back at the root
    }

    [Fact]
    public void EachStepPaysTheBoostAndCompletingPaysTheLumpSum()
    {
        // One phrase only, so the arithmetic is exactly what it looks like.
        var g = new KwsContextGraph(new[] { new[] { 1, 2, 3 } }, 1.5f, 0.3f,
                                    phrases: new[] { "one two three" });

        var (s1, n1, m1) = g.ForwardOneStep(g.Root, 1);
        Assert.Equal(1.5f, s1, 4);
        Assert.Equal(1, n1.Level);
        Assert.Null(m1);

        var (s2, n2, m2) = g.ForwardOneStep(n1, 2);
        Assert.Equal(1.5f, s2, 4);
        Assert.Equal(2, n2.Level);
        Assert.Null(m2);

        // Completing pays the step AND the accumulated node score again: 1.5 for
        // the arc plus 4.5 for the three tokens. That lump sum is what lifts a
        // finished phrase to the TOP of the beam, which matters because detection
        // is only ever tested on the leading hypothesis.
        var (s3, n3, m3) = g.ForwardOneStep(n2, 3);
        Assert.Equal(1.5f + 4.5f, s3, 4);
        Assert.True(n3.IsEnd);
        Assert.Equal("one two three", m3!.Phrase);
        Assert.Equal(3, m3.Level);
    }

    [Fact]
    public void FinishingAPhraseAlsoPaysForAnyPhraseThatEndsInsideIt()
    {
        // "2 3" is a suffix of "1 2 3", so arriving at the end of the longer one
        // ends the shorter one too and BOTH output scores are paid: 1.5 for the
        // arc, 4.5 for "1 2 3", 3.0 for "2 3". Overlapping wake phrases are the
        // normal case — "Hey B" and "B" — and this stacking is what stops the
        // longer phrase from silently swallowing the shorter one.
        var g = Graph(boost: 1.5f);
        var (_, s, _) = g.ForwardOneStep(g.Root, 1);
        (_, s, _) = g.ForwardOneStep(s, 2);
        var (score, _, matched) = g.ForwardOneStep(s, 3);

        Assert.Equal(1.5f + 4.5f + 3.0f, score, 4);
        Assert.Equal("one two three", matched!.Phrase);   // the longer one wins the report
    }

    [Fact]
    public void AShorterPhraseIsFoundViaTheFailLinks()
    {
        // "2 3" said on its own, with a stray token first. Without fail links the
        // walk dies at the root and the phrase is missed entirely.
        var g = Graph();
        var (_, s, _) = g.ForwardOneStep(g.Root, 77);
        Assert.Equal(-1, s.Token);

        (_, s, _) = g.ForwardOneStep(s, 2);
        (_, s, var matched) = g.ForwardOneStep(s, 3);

        Assert.NotNull(matched);
        Assert.Equal("two three", matched!.Phrase);
        Assert.Equal(2, matched.Level);
    }

    [Fact]
    public void AWrongTurnMidPhraseFallsBackToTheLongestLiveSuffix()
    {
        // 1 then 2 puts us inside "1 2 3". A 2 next is wrong for that phrase but
        // is a valid START of "2 3", and Aho-Corasick keeps it in one step instead
        // of dropping to the root and losing the word.
        var g = Graph();
        var (_, s, _) = g.ForwardOneStep(g.Root, 1);
        (_, s, _) = g.ForwardOneStep(s, 2);
        (_, s, _) = g.ForwardOneStep(s, 2);

        Assert.Equal(2, s.Token);
        Assert.Equal(1, s.Level);                  // first token of "2 3"

        (_, s, var matched) = g.ForwardOneStep(s, 3);
        Assert.Equal("two three", matched!.Phrase);
    }

    [Fact]
    public void IsMatchedOnlyHoldsAtAPhraseEnd()
    {
        var g = Graph();
        Assert.False(g.IsMatched(g.Root).Matched);

        var (_, mid, _) = g.ForwardOneStep(g.Root, 1);
        Assert.False(g.IsMatched(mid).Matched);

        (_, mid, _) = g.ForwardOneStep(mid, 2);
        (_, var end, _) = g.ForwardOneStep(mid, 3);
        var (ok, at) = g.IsMatched(end);
        Assert.True(ok);
        Assert.Equal("one two three", at!.Phrase);
    }

    [Fact]
    public void PerPhraseThresholdsAndBoostsOverrideTheDefaults()
    {
        // A phrase that is hard to hear can be given more help, or held to a
        // stricter bar, without loosening every other phrase in the graph.
        var g = new KwsContextGraph(
            new[] { new[] { 1, 2 }, new[] { 5, 6 } }, 1.0f, 0.25f,
            scores:       new[] { 0f, 3f },      // 0 means "take the default"
            phrases:      new[] { "plain", "boosted" },
            acThresholds: new[] { 0f, 0.8f });

        var (plain, p1, _) = g.ForwardOneStep(g.Root, 1);
        Assert.Equal(1.0f, plain, 4);
        var (_, pEnd, _) = g.ForwardOneStep(p1, 2);
        Assert.Equal(0.25f, pEnd.AcThreshold, 4);

        var (boosted, b1, _) = g.ForwardOneStep(g.Root, 5);
        Assert.Equal(3.0f, boosted, 4);
        var (_, bEnd, _) = g.ForwardOneStep(b1, 6);
        Assert.Equal(0.8f, bEnd.AcThreshold, 4);
    }

    [Fact]
    public void ASharedPrefixTakesTheMostGenerousBoost()
    {
        // Adding a phrase must never make an existing one harder to hear, so a
        // shared arc keeps the larger of the two boosts.
        var g = new KwsContextGraph(
            new[] { new[] { 1, 2 }, new[] { 1, 3 } }, 1.0f, 0.25f,
            scores: new[] { 1.0f, 4.0f });

        var (score, _, _) = g.ForwardOneStep(g.Root, 1);
        Assert.Equal(4.0f, score, 4);
    }

    [Fact]
    public void ProgressReportingKnowsWhichPhraseAPrefixBelongsTo()
    {
        var g = Graph();
        var (_, s, _) = g.ForwardOneStep(g.Root, 1);
        Assert.Equal("one two three", s.PrefixPhrase);
        Assert.Equal(3, s.PrefixLength);
        Assert.Equal(1, s.Level);
    }
}
