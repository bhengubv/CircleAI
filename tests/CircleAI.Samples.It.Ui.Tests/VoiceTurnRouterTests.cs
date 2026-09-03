// VoiceTurnRouterTests.cs
//
// The router, on its own rather than through a component.
//
// Both microphone buttons hold one of these, so its rules are worth pinning
// where a component cannot obscure them: routes once, cancels the turn, and
// tells a cancellation it caused apart from one it did not.

using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared;

namespace CircleAI.Samples.It.Ui.Tests;

public class VoiceTurnRouterTests
{
    [Fact]
    public void Routes_on_a_transcript_that_asks_to_go_somewhere()
    {
        using var router = new VoiceTurnRouter();

        Assert.True(router.Observe(new TurnState(TurnPhase.Thinking, Heard: "open settings")));
        Assert.Equal("settings", router.Routed?.Route);
    }

    [Fact]
    public void Cancels_the_turn_so_the_answer_never_runs()
    {
        // Otherwise the answering model produces a paragraph about settings on
        // the way to Settings - work nobody asked for, spoken over the top of the
        // screen change.
        using var router = new VoiceTurnRouter();

        router.Observe(new TurnState(TurnPhase.Thinking, Heard: "open settings"));

        Assert.True(router.Token.IsCancellationRequested);
    }

    [Fact]
    public void Routes_once_however_often_the_transcript_is_reported()
    {
        // Heard is reported again as a turn goes on, and routing twice would
        // navigate on top of a navigation.
        using var router = new VoiceTurnRouter();
        var t = new TurnState(TurnPhase.Thinking, Heard: "open settings");

        Assert.True(router.Observe(t));
        Assert.False(router.Observe(t));
        Assert.False(router.Observe(new TurnState(TurnPhase.Thinking, Heard: "open chat")));
        Assert.Equal("settings", router.Routed?.Route);
    }

    [Fact]
    public void Leaves_a_question_completely_alone()
    {
        using var router = new VoiceTurnRouter();

        Assert.False(router.Observe(new TurnState(
            TurnPhase.Thinking, Heard: "how do you say hello in isiZulu")));
        Assert.Null(router.Routed);
        Assert.False(router.Token.IsCancellationRequested);
    }

    [Fact]
    public void Ignores_a_turn_that_heard_nothing()
    {
        using var router = new VoiceTurnRouter();

        Assert.False(router.Observe(new TurnState(TurnPhase.Listening)));
        Assert.False(router.Observe(new TurnState(TurnPhase.Idle, Heard: "")));
        Assert.Null(router.Routed);
    }

    [Fact]
    public void Knows_a_cancellation_it_caused_from_one_it_did_not()
    {
        // A routed turn cancels itself and that is not a failure. Any OTHER
        // cancellation still is - a turn cut off by something else is something
        // somebody needs to be told about.
        using var routed = new VoiceTurnRouter();
        routed.Observe(new TurnState(TurnPhase.Thinking, Heard: "open settings"));
        Assert.True(routed.Ended(new OperationCanceledException()));

        using var quiet = new VoiceTurnRouter();
        Assert.False(quiet.Ended(new OperationCanceledException()));
        Assert.False(routed.Ended(new InvalidOperationException()));
    }

    [Fact]
    public void Writes_down_what_it_heard_and_what_it_decided()
    {
        // Both outcomes. The matcher is tuned against typed guesses until real
        // transcripts are written somewhere, and the MISSES are what there is to
        // tune on - "Anita Slation." was this phone's version of "I need
        // translation".
        var lines = new List<string>();
        VoiceTurnRouter.Trace = lines.Add;
        try
        {
            using var hit = new VoiceTurnRouter();
            hit.Observe(new TurnState(TurnPhase.Thinking, Heard: "open settings"));

            using var miss = new VoiceTurnRouter();
            miss.Observe(new TurnState(TurnPhase.Thinking, Heard: "Anita Slation."));

            Assert.Contains(lines, l => l.Contains("/settings"));
            Assert.Contains(lines, l => l.Contains("no match") && l.Contains("Anita Slation"));
        }
        finally { VoiceTurnRouter.Trace = null; }
    }
}
