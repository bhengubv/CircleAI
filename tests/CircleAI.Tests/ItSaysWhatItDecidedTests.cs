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

    // ── an offer you can actually answer ─────────────────────────────────────

    [Fact]
    public void The_offer_says_which_word_to_say()
    {
        // "Say the word and I will clear it" NEVER SAID WHICH WORD, and the
        // matcher wanted an exact utterance from a list of ten. Reported within
        // minutes of it first being spoken aloud on the P30:
        //
        //     "I have been responding but nothing happens"
        //
        // An offer a person cannot answer is worse than no offer - it reads as
        // the assistant ignoring them.
        var offer = AssistantPersona.DeadWeight("2.8 GB");

        Assert.Contains("clear it", offer, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Say the word", offer);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes.")]
    [InlineData("yeah")]
    [InlineData("ok")]
    [InlineData("go ahead")]
    [InlineData("do it")]
    [InlineData("clear it")]
    [InlineData("clear it please")]
    [InlineData("ok clear it then")]
    [InlineData("Hey B, clear it")]
    public void A_person_answering_the_offer_is_understood(string said)
    {
        Assert.Equal(SelfAsk.ClearIt, SelfRequest.Of(said, anOfferIsOpen: true));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("go ahead")]
    [InlineData("do it")]
    public void But_none_of_them_delete_anything_when_nothing_was_offered(string said)
    {
        // THE GUARD THAT MAKES THE ABOVE SAFE. A bare "yes" must never clear
        // gigabytes on its own - it means yes only because something just asked.
        // Whisper mangles plenty of sentences into a stray "yes".
        Assert.Equal(SelfAsk.None, SelfRequest.Of(said, anOfferIsOpen: false));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("do not clear it")]
    [InlineData("dont clear it")]
    [InlineData("no clear it later")]
    [InlineData("why would I clear it")]
    [InlineData("cancel")]
    public void Declining_is_not_agreeing(string said)
    {
        // The lenient pass matches a CLEAR phrase inside a short sentence, which
        // without this would turn every refusal into an instruction.
        Assert.NotEqual(SelfAsk.ClearIt, SelfRequest.Of(said, anOfferIsOpen: true));
    }

    [Fact]
    public void An_offer_is_open_only_until_it_is_answered()
    {
        var log = new DecisionLog();

        log.Decided(AssistantPersona.DeadWeight("2.8 GB"), awaitingAnswer: true);
        Assert.True(log.AwaitingAnswer);

        log.Answered();
        Assert.False(log.AwaitingAnswer, "a cleared offer must not keep accepting yes");
    }

    [Fact]
    public void An_ordinary_decision_does_not_leave_an_offer_open()
    {
        // "I am using the smaller one" is a statement, not a question, and a
        // "yes" after it must not delete anything.
        var log = new DecisionLog();

        log.Decided(AssistantPersona.OnTheSmallerBrain);

        Assert.False(log.AwaitingAnswer);
        Assert.Equal(SelfAsk.None, SelfRequest.Of("yes", log.AwaitingAnswer));
    }

    [Fact]
    public void A_later_decision_closes_an_earlier_offer()
    {
        // The phone moved on. A "yes" now is answering whatever it just said,
        // not an offer from twenty minutes ago.
        var log = new DecisionLog();

        log.Decided(AssistantPersona.DeadWeight("2.8 GB"), awaitingAnswer: true);
        log.Decided(AssistantPersona.OnTheSmallerBrain);

        Assert.False(log.AwaitingAnswer);
    }

    [Fact]
    public void An_instruction_still_counts_when_the_recall_preamble_is_in_front_of_it()
    {
        // THE ACTUAL REASON "NOTHING HAPPENS", found only by taking the offer up
        // on the phone after the wording and the matcher were both fixed.
        //
        // The typed screen does not send what was typed. Recalling.Ask prepends
        // what the device remembers, so the service receives the person's words
        // as the LAST LINE of a composed prompt - and a matcher comparing the
        // whole utterance sees none of its phrases.
        //
        // Measured on the P30 on 2026-10-10: "clear it" went past the switch and
        // the brain answered "I have processed the information you provided",
        // while 4.8 GB of unusable model stayed exactly where it was.
        var composed =
            "Here is what you already know about this person:" + System.Environment.NewLine
            + "- their name is Thabo" + System.Environment.NewLine
            + "- they live in Johannesburg" + System.Environment.NewLine
            + System.Environment.NewLine
            + "clear it";

        Assert.Equal(SelfAsk.ClearIt, SelfRequest.Of(composed, anOfferIsOpen: true));
    }

    [Fact]
    public void Asking_about_itself_survives_the_preamble_too()
    {
        // The same blindness hid every self-request, not just the offer - a
        // person asking which brain it is using got the brain's guess about the
        // question instead of the fact.
        var composed =
            "Here is what you already know about this person:" + System.Environment.NewLine
            + "- their name is Thabo" + System.Environment.NewLine
            + System.Environment.NewLine
            + "which brain are you using";

        Assert.Equal(SelfAsk.WhichBrain, SelfRequest.Of(composed));
    }

    [Fact]
    public void A_preamble_that_merely_mentions_clearing_does_not_delete_anything()
    {
        // ONLY THE LAST LINE IS THE PERSON SPEAKING. A remembered fact that
        // happens to contain the phrase must never be read as an instruction -
        // the memory is data, not a command.
        var composed =
            "Here is what you already know about this person:" + System.Environment.NewLine
            + "- they asked me to clear it last week" + System.Environment.NewLine
            + System.Environment.NewLine
            + "what is the weather like";

        Assert.Equal(SelfAsk.None, SelfRequest.Of(composed, anOfferIsOpen: true));
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
