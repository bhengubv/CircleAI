// ItSaysWhatItDecidedTests.cs
//
// Part 3 of the plan: it decides, it acts, and it tells you.
//
// Every verdict this device reached about its own models was reachable only by
// ASKING. SelfRequest answers "which brain are you using?" correctly and nobody
// thinks to ask it, so a phone that had been killed by a model, given up on it,
// and quietly switched to a smaller one told its owner precisely nothing - the
// only trace was a line in logcat.
//
// HOLD-AND-POLL, because the link is a binder: request/response, no channel
// back. That is the same reason the wake count is a COUNT rather than a flag,
// and it is why the sequence number here is the load-bearing part - it is what
// lets a polling client speak a decision exactly once.
//
// These cover the policy, not the Android plumbing. CircleNeuronLinkService
// keeps one DecisionLog and exposes it over LinkVerb.Verdict; the two rules with
// any teeth in them - never repeat, never go backwards - live here where both
// framework legs run them.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class ItSaysWhatItDecidedTests
{
    [Fact]
    public void A_device_that_has_decided_nothing_says_nothing()
    {
        // THE NORMAL STATE OF A WORKING PHONE, and it must stay silent. A client
        // polls this; answering "I am ready" every few seconds is how a useful
        // channel becomes noise.
        var log = new DecisionLog();

        Assert.Null(log.Last);
        Assert.Equal(0, log.Sequence);
        Assert.False(log.HasSomethingNewFor(0));
    }

    [Fact]
    public void A_decision_is_recorded_and_the_number_moves()
    {
        var log = new DecisionLog();

        Assert.True(log.Decided(AssistantPersona.OnTheSmallerBrain));
        Assert.Equal(AssistantPersona.OnTheSmallerBrain, log.Last);
        Assert.Equal(1, log.Sequence);
    }

    [Fact]
    public void The_same_decision_twice_is_not_two_decisions()
    {
        // THE ONE THAT MATTERS ON THIS HANDSET. The selector re-runs on every
        // cold start, and on the P30 iAware kills the service several times a
        // day - so without this the phone would announce "I am using the smaller
        // one" on every restart until somebody turned the voice off.
        //
        // Same lesson as the foreground notification that posted ~3000 times a
        // day and had every one demoted by Android.
        var log = new DecisionLog();

        Assert.True(log.Decided(AssistantPersona.OnTheSmallerBrain));
        Assert.False(log.Decided(AssistantPersona.OnTheSmallerBrain));
        Assert.False(log.Decided(AssistantPersona.OnTheSmallerBrain));

        Assert.Equal(1, log.Sequence);
    }

    [Fact]
    public void A_different_decision_after_a_repeat_still_counts()
    {
        // The dedupe must compare against the LAST decision, not swallow
        // anything it has ever seen. A phone that goes small, then finds it can
        // run nothing at all, has decided something new and must say so.
        var log = new DecisionLog();

        log.Decided(AssistantPersona.OnTheSmallerBrain);
        log.Decided(AssistantPersona.OnTheSmallerBrain);

        Assert.True(log.Decided(AssistantPersona.NothingItCanRun));
        Assert.Equal(2, log.Sequence);
        Assert.Equal(AssistantPersona.NothingItCanRun, log.Last);
    }

    [Fact]
    public void Going_back_to_an_earlier_verdict_is_a_new_decision()
    {
        // Not a repeat: the device changed its mind back, and a person who heard
        // the middle sentence needs the correction. Only the IMMEDIATELY
        // preceding one is suppressed.
        var log = new DecisionLog();

        log.Decided(AssistantPersona.OnTheSmallerBrain);
        log.Decided(AssistantPersona.NothingItCanRun);

        Assert.True(log.Decided(AssistantPersona.OnTheSmallerBrain));
        Assert.Equal(3, log.Sequence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_not_a_decision(string? nothing)
    {
        // A blank verdict would move the number and leave a polling client with
        // something new to say and nothing to say it with.
        var log = new DecisionLog();
        log.Decided(AssistantPersona.OnTheSmallerBrain);

        Assert.False(log.Decided(nothing));
        Assert.Equal(1, log.Sequence);
        Assert.Equal(AssistantPersona.OnTheSmallerBrain, log.Last);
    }

    // ── the client's half of the contract ─────────────────────────────────────

    [Fact]
    public void A_client_that_has_spoken_the_latest_has_nothing_to_say()
    {
        var log = new DecisionLog();
        log.Decided(AssistantPersona.OnTheSmallerBrain);

        Assert.True(log.HasSomethingNewFor(0), "a fresh client must hear the current verdict");
        Assert.False(log.HasSomethingNewFor(1), "and must not say it a second time");
    }

    [Fact]
    public void A_client_that_missed_one_still_hears_the_latest()
    {
        // THE WHOLE REASON IT IS A NUMBER. A client polling every few seconds can
        // be away - the app backgrounded, the service restarted - while two
        // decisions are taken. It must not be stuck on the old one.
        var log = new DecisionLog();
        log.Decided(AssistantPersona.OnTheSmallerBrain);
        log.Decided(AssistantPersona.NothingItCanRun);

        Assert.True(log.HasSomethingNewFor(1));
        Assert.Equal(AssistantPersona.NothingItCanRun, log.Last);
    }

    [Fact]
    public void The_number_never_goes_backwards()
    {
        var log = new DecisionLog();
        var seen = 0;

        foreach (var s in new[]
        {
            AssistantPersona.OnTheSmallerBrain,
            AssistantPersona.OnTheSmallerBrain,
            AssistantPersona.NothingItCanRun,
            AssistantPersona.RunningWell,
            AssistantPersona.RunningWell,
        })
        {
            log.Decided(s);
            Assert.True(log.Sequence >= seen, "the sequence went backwards");
            seen = log.Sequence;
        }

        Assert.Equal(3, log.Sequence);
    }

    [Fact]
    public void The_sentences_are_the_personas_and_name_no_model()
    {
        // SHIP THE DISH, NOT THE TEST TUBE. These are spoken aloud, so a model
        // id or a decimal in one is a defect - and every verdict site composing
        // its own wording would be a second voice for the same product.
        foreach (var said in new[]
        {
            AssistantPersona.OnTheSmallerBrain,
            AssistantPersona.NothingItCanRun,
            AssistantPersona.RunningWell,
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(said));
            Assert.DoesNotContain("MNN", said);
            Assert.DoesNotContain("Qwen", said);
            Assert.DoesNotContain("GB", said);
            // NO DECIMALS - "2.8 GB" spoken aloud is a test tube. A full stop
            // between two sentences is fine and my first version of this
            // assertion banned that too, which failed on a perfectly good
            // two-sentence verdict.
            Assert.DoesNotMatch(@"\d\.\d", said);
        }
    }
}
