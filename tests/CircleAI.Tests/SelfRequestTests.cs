// SelfRequestTests.cs
//
// THE REFUSAL HAD NO WAY BACK. CrashVerdict writes one when a model load aborts the
// process; IModelCatalog.Pardon withdraws it; nothing reached Pardon. A device that
// refused something wrongly refused it forever, and the only recovery was deleting a
// database file over adb.
//
// Voice is the primary surface, so the way back is spoken: "try the big one again".
// And it has to be answerable WITH THE BRAIN DOWN, because the state worth asking
// about is "it did not come up" - below this seam the link service returns "brain
// warming up, try again shortly", so a person asking "are you ready?" of a phone
// whose brain had died would be told to try again shortly, forever.
//
// PRECISION MATTERS MORE THAN COVERAGE HERE. Opener's header says it and the same
// applies: the failure to avoid is a phone that stops answering real questions
// because they happened to contain "are you ready". Answering the wrong thing with
// total confidence is worse than being slow.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class SelfRequestTests
{
    [Theory]
    [InlineData("Which brain are you using?")]
    [InlineData("which brain are you using")]
    [InlineData("What model are you using?")]
    [InlineData("Are you ready?")]
    [InlineData("Why are you slow?")]
    [InlineData("How are you set up?")]
    public void Asking_what_it_is_running_is_recognised(string said)
        => Assert.Equal(SelfAsk.WhichBrain, SelfRequest.Of(said));

    [Theory]
    [InlineData("Try the big one again.")]
    [InlineData("try the big brain again")]
    [InlineData("Use the big brain anyway")]
    [InlineData("Give the big one another go")]
    public void Telling_it_to_try_again_is_recognised(string said)
        => Assert.Equal(SelfAsk.TryAgain, SelfRequest.Of(said));

    [Theory]
    [InlineData("Hey, B. Which brain are you using?")]
    [InlineData("Hey B, are you ready?")]
    [InlineData("OK B, try the big one again")]
    [InlineData("Hey Circle AI, are you ready?")]
    public void The_wake_phrase_in_front_does_not_hide_it(string said)
    {
        // Whisper returns one breath of speech as one string, so the attention-getter
        // and the sentence arrive together. This is the same problem Opener solves.
        Assert.NotEqual(SelfAsk.None, SelfRequest.Of(said));
    }

    [Theory]
    [InlineData("Are you ready please")]
    [InlineData("please try the big one again")]
    [InlineData("try the big one again now")]
    public void Politeness_does_not_hide_it(string said)
        => Assert.NotEqual(SelfAsk.None, SelfRequest.Of(said));

    [Theory]
    [InlineData("Are you ready to book the table?")]       // a question about a table
    [InlineData("Which brain surgery is riskiest?")]
    [InlineData("Why are you slow to answer when I ask about tax?")]
    [InlineData("Tell me a joke")]
    [InlineData("What is the weather like today?")]
    [InlineData("Try the big one again on Tuesday and tell me what happens")]
    [InlineData("I am ready")]
    [InlineData("")]
    [InlineData(null)]
    public void A_real_question_is_not_mistaken_for_one_of_these(string? said)
    {
        // THE FAILURE TO AVOID. Answering "I am ready." to "are you ready to book the
        // table" is confident and wrong, which is worse than taking eight seconds.
        Assert.Equal(SelfAsk.None, SelfRequest.Of(said));
    }

    [Fact]
    public void Nothing_running_says_so_before_anything_else()
    {
        Assert.Equal(AssistantPersona.NothingItCanRun,
            AssistantPersona.WhichBrain(ready: false, running: false, gaveUpOnSomething: false));
        Assert.Equal(AssistantPersona.NothingItCanRun,
            AssistantPersona.WhichBrain(ready: true, running: false, gaveUpOnSomething: true));
    }

    [Fact]
    public void Chosen_but_not_up_yet_says_wait_rather_than_ready()
    {
        Assert.Equal(AssistantPersona.NotUpYet,
            AssistantPersona.WhichBrain(ready: false, running: true, gaveUpOnSomething: false));
    }

    [Fact]
    public void Running_after_giving_up_on_something_says_which_and_why()
    {
        // THE HONEST VERSION OF A SILENT DOWNGRADE. A Circle OS device ran a 2 B for
        // three days while the model its owner chose sat half-downloaded, and said
        // nothing at all.
        var said = AssistantPersona.WhichBrain(ready: true, running: true, gaveUpOnSomething: true);

        Assert.Equal(AssistantPersona.OnTheSmallerBrain, said);
        Assert.Contains("smaller brain", said);
    }

    [Fact]
    public void Running_the_one_it_meant_to_just_says_ready()
        => Assert.Equal(AssistantPersona.RunningWell,
            AssistantPersona.WhichBrain(ready: true, running: true, gaveUpOnSomething: false));

    [Fact]
    public void Trying_again_announces_rather_than_asking()
    {
        // announce-dont-confirm-the-instruction-is-the-auth: somebody who says "try
        // the big one again" has decided. No question mark anywhere in the reply.
        var said = AssistantPersona.TryAgain(somethingWasRefused: true);

        Assert.DoesNotContain("?", said);
        Assert.Contains("will try", said);
    }

    [Fact]
    public void Trying_again_with_nothing_refused_says_so()
        => Assert.Equal(AssistantPersona.NothingToTryAgain,
            AssistantPersona.TryAgain(somethingWasRefused: false));

    [Theory]
    [InlineData(nameof(AssistantPersona.RunningWell))]
    [InlineData(nameof(AssistantPersona.OnTheSmallerBrain))]
    [InlineData(nameof(AssistantPersona.NotUpYet))]
    [InlineData(nameof(AssistantPersona.NothingItCanRun))]
    [InlineData(nameof(AssistantPersona.WillTryAgain))]
    [InlineData(nameof(AssistantPersona.NothingToTryAgain))]
    public void Nothing_it_says_aloud_contains_jargon_or_a_decimal(string which)
    {
        // VOICE IS THE PRIMARY SURFACE. aether-surface-dishes-not-test-tubes: ship
        // the dish. Nobody wants "Qwen3.6-35B-A3B-MNN requires 30.3 gigabytes" spoken
        // at them.
        var said = (string)typeof(AssistantPersona).GetField(which)!.GetValue(null)!;

        Assert.DoesNotContain("MNN", said);
        Assert.DoesNotContain("Qwen", said);
        Assert.DoesNotContain("GB", said);
        Assert.DoesNotContain("SIG", said);
        Assert.DoesNotMatch(@"\d+\.\d", said);
    }

    [Fact]
    public void The_dead_weight_offer_states_the_cost_and_waits()
    {
        // feedback_never_delete_without_asking. 22 GB is a lot of somebody's data to
        // reclaim on a hunch, so it says the number and stops.
        var said = AssistantPersona.DeadWeight("22.8 GB");

        Assert.Contains("22.8 GB", said);
        Assert.Contains("Say the word", said);
        Assert.DoesNotContain("deleted", said);
    }
}
