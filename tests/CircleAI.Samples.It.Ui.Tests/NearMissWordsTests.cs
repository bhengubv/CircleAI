// NearMissWordsTests.cs
//
// The screen whose entire job is answering "does waking work" answered it with
// silence.
//
// WHAT HAPPENED. The wake screen watches the resident listener rather than
// opening a second microphone, and it subscribed to ONE event: it fired. So four
// different situations produced the same screen —
//
//   the microphone is dead
//   you are too far away and it reaches one token of eight
//   the phrase completed and stage two refused it
//   nobody said anything
//
// — and the person standing in front of it saying the phrase over and over got
// "Listening" for all four. Measured on a P30 on 2026-09-06: the log knew the
// phrase had reached 1 of 8 tokens. The screen did not say so.
//
// The near-miss channel now carries that up. What actually reaches somebody is a
// SENTENCE, so the sentence is what is pinned here — the event plumbing is only
// worth having if the words at the end of it are the right words.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class NearMissWordsTests
{
    [Fact]
    public void A_partial_match_says_how_much_landed()
    {
        // THE NUMBER IS THE POINT. "It nearly heard you" is not actionable;
        // "1 of 8" tells somebody the microphone is alive and they are too far
        // away, which is a thing they can do something about in one step.
        var hint = NearMissWords.Hint(1, 8, refused: null);

        Assert.Contains("1 of 8", hint);
        Assert.Contains("closer", hint);
    }

    [Fact]
    public void A_refusal_gives_the_reason_instead_of_the_count()
    {
        // A REFUSAL IS 8 OF 8 AND SAYING SO WOULD BE USELESS. The phrase was
        // heard perfectly; what the person needs is why it was turned down.
        // The reason arrives already phrased for a human — this is the real one
        // measured on the P30 that night.
        var hint = NearMissWords.Hint(8, 8,
            "had been speaking 1320 ms before the phrase ended (max 600)");

        Assert.StartsWith("Heard it, but ", hint);
        Assert.Contains("1320 ms", hint);
        Assert.DoesNotContain("8 of 8", hint);
        Assert.DoesNotContain("closer", hint);
    }

    [Fact]
    public void An_empty_reason_is_not_a_reason()
    {
        // "Heard it, but " with nothing after it is worse than the count. The
        // veto path can hand up a null reason — ConfirmedKeywordSpotter's own
        // logging says "no reason given" for exactly that case.
        Assert.Equal(NearMissWords.Hint(3, 8, null), NearMissWords.Hint(3, 8, ""));
        Assert.Contains("3 of 8", NearMissWords.Hint(3, 8, ""));
    }

    [Fact]
    public void A_phrase_with_no_tokens_does_not_divide_by_zero()
    {
        // Cannot come from a working spotter, and this runs on the UI thread of
        // the one screen somebody opens when they already think the app is
        // broken. It says something true and vague rather than throwing.
        var hint = NearMissWords.Hint(0, 0, refused: null);

        Assert.DoesNotContain("of 0", hint);
        Assert.Contains("closer", hint);
    }

    [Fact]
    public void The_large_line_never_claims_it_woke()
    {
        // THE ASYMMETRY THAT MATTERS. This screen is the one place somebody
        // decides whether the feature works. A near miss saying anything like
        // "Heard you" would be the same lie as the silence it replaces, in the
        // opposite direction.
        Assert.Equal("Nearly", NearMissWords.Status);
        Assert.DoesNotContain("Heard you", NearMissWords.Status);
    }
}
