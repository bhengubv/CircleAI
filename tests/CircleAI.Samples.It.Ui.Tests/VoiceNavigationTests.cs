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
        Services.AddSingleton(CapabilityRegistry.For(new FakeBrain(), new FakeSettings()));
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
        //
        // THIS SENTENCE IS NOW ANSWERED RATHER THAN IGNORED, which is why the
        // turn ends: TranslateCapability recognises that it carries both the
        // words and the target language, so there is nothing to look up and
        // nowhere to go. Before, the choice was a paragraph ABOUT isiZulu from
        // the general model, or a trip to a screen with two languages to set.
        // What must NOT change is the staying put - that is the hazard.
        Wire("how do you say hello in isiZulu");
        var layout = Layout();

        Press(layout);

        layout.WaitForAssertion(() => Assert.Contains("translated", layout.Markup));
        Assert.EndsWith("/home", Where());
    }

    [Fact]
    public void A_question_nothing_can_answer_still_runs_as_an_ordinary_turn()
    {
        // The other half, and the one the rule was written for: a sentence that
        // merely MENTIONS a screen must reach the answering model untouched.
        var talk = Wire("what did you think of the settings we discussed");
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

/// <summary>
/// The circle on Home obeys the same sentences the bar does.
/// </summary>
/// <remarks>
/// A SEPARATE CLASS BECAUSE HOME TAKES A DIFFERENT ROUTE TO THE SAME TURN: its
/// circle checks the brain first and greets in a catalogued language when there
/// is none, so a test that did not report a ready brain would be exercising the
/// greeting and proving nothing about routing.
/// </remarks>
public class HomeVoiceNavigationTests : TestContext
{
    private FakeConversation Wire(string? heard)
    {
        var talk = new FakeConversation { Heard = heard, Ready = true };
        Services.AddSingleton(new VoiceMark());
        Services.AddSingleton(CapabilityRegistry.For(new FakeBrain(), new FakeSettings()));
        Services.AddSingleton<IConversation>(talk);
        Services.AddSingleton<IVoiceHost>(new FakeVoiceHost { Catalogue = [new VoiceRow("en", 1)] });
        Services.AddSingleton<ISetup>(new FakeSetup());
        Services.AddSingleton<ISettings>(new FakeSettings());
        Services.AddSingleton<ISpokenLanguage>(new FakeSpokenLanguage());
        Services.AddSingleton<IWhereAmI>(new FakeWhereAmI());
        Services.GetRequiredService<NavigationManager>().NavigateTo("home");
        return talk;
    }

    private string Where() => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void The_circle_takes_you_where_you_asked()
    {
        Wire("I need translation");
        var home = RenderComponent<CircleAI.Samples.It.Shared.Pages.Home>();

        home.Find("button.hero").Click();

        home.WaitForAssertion(() => Assert.EndsWith("/translate", Where()));
    }

    [Fact]
    public void The_circle_leaves_a_question_where_it_was_asked()
    {
        // Same rule as the bar's, and it has to be the SAME rule: the biggest
        // control on the screen doing something different for one sentence is
        // the two-owner bug this app keeps producing. Both press one router.
        var talk = Wire("what did you think of the settings we discussed");
        var home = RenderComponent<CircleAI.Samples.It.Shared.Pages.Home>();

        home.Find("button.hero").Click();

        home.WaitForAssertion(() => Assert.False(Services.GetRequiredService<VoiceMark>().Busy));
        Assert.EndsWith("/home", Where());
        Assert.False(talk.WasCancelled);
    }

    [Fact]
    public void The_circle_answers_a_translation_without_moving_you()
    {
        // "Services stays for browsing, circle does the work" - this is what
        // that means in practice. The answer arrives on the screen the person
        // was already on.
        Wire("how do you say hello in isiZulu");
        var home = RenderComponent<CircleAI.Samples.It.Shared.Pages.Home>();

        home.Find("button.hero").Click();

        home.WaitForAssertion(() => Assert.Contains("translated", home.Markup));
        Assert.EndsWith("/home", Where());
    }
}
