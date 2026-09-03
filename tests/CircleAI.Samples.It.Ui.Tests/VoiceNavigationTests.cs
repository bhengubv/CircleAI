// VoiceNavigationTests.cs
//
// Saying where you want to go has to actually take you there.
//
// VoiceDestinationTests pins the RULE - which sentences are requests. This pins
// the WIRING: that a turn reporting such a sentence ends early and moves, and
// that a turn reporting an ordinary question is left completely alone. A matcher
// nothing calls is the same as no matcher, which is the failure mode this whole
// day was made of.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class VoiceNavigationTests : TestContext
{
    private FakeConversation Wire(string? heard)
    {
        var talk = new FakeConversation { Heard = heard };
        Services.AddSingleton(new VoiceMark());
        Services.AddSingleton<IConversation>(talk);
        Services.AddSingleton<IShareTarget>(new FakeShareTarget());
        Services.AddSingleton<IResidentAssistant>(new FakeResidentAssistant());
        Services.GetRequiredService<NavigationManager>().NavigateTo("home");
        return talk;
    }

    private IRenderedComponent<MainLayout> Layout()
        => RenderComponent<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => { })));

    private string Where()
        => Services.GetRequiredService<NavigationManager>().Uri;

    private static void Press(IRenderedComponent<MainLayout> layout)
        => layout.Find("button.tabbar-voice").Click();

    [Fact]
    public void Takes_you_to_translating_when_you_ask_for_it()
    {
        Wire("I need translation");
        var layout = Layout();

        Press(layout);

        // The turn is async: the click dispatches it and returns, so the
        // assertion waits for the state rather than racing it.
        layout.WaitForAssertion(() => Assert.EndsWith("/translate", Where()));
    }

    [Fact]
    public void Ends_the_turn_early_rather_than_answering_the_instruction()
    {
        // THE HALF THAT MATTERS AS MUCH AS THE NAVIGATION. If the turn ran on,
        // the answering model would produce a sentence about translation on the
        // way to the interpreter - work nobody asked for, out loud, over the top
        // of the screen change.
        var talk = Wire("open the interpreter");
        var layout = Layout();

        Press(layout);

        layout.WaitForAssertion(() => Assert.True(talk.WasCancelled));
    }

    [Fact]
    public void Says_where_it_is_going()
    {
        // Somebody who spoke to a phone across the room is not watching it.
        Wire("take me to settings");
        var layout = Layout();

        Press(layout);

        layout.WaitForAssertion(() => Assert.Contains("Opening Settings", layout.Markup));
    }

    [Fact]
    public void A_question_is_answered_where_it_was_asked()
    {
        // The regression this router could most easily cause: being thrown off
        // the screen you were using, mid-sentence, for saying a word.
        var talk = Wire("how do you say hello in isiZulu");
        var layout = Layout();

        Press(layout);

        Assert.EndsWith("/home", Where());
        Assert.False(talk.WasCancelled);
    }

    [Fact]
    public void A_silent_turn_goes_nowhere()
    {
        var talk = Wire(heard: null);
        var layout = Layout();

        Press(layout);

        Assert.EndsWith("/home", Where());
        Assert.False(talk.WasCancelled);
    }

    [Fact]
    public void The_mark_is_idle_again_after_it_moves()
    {
        // A routed turn is still a turn that ended: leaving the arcs lit would
        // draw a microphone that is no longer open.
        Wire("go to my cv");
        var layout = Layout();

        Press(layout);

        layout.WaitForAssertion(() =>
            Assert.Equal(MarkState.Idle, Services.GetRequiredService<VoiceMark>().State));
        Assert.False(Services.GetRequiredService<VoiceMark>().Busy);
    }
}
