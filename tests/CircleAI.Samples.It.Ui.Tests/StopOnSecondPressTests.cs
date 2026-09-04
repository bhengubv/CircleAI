// StopOnSecondPressTests.cs
//
// One button, one meaning.
//
// Pressing a microphone button while it was listening answered "It is still on
// the last one." - a refusal dressed as an explanation, while the microphone sat
// open and somebody had plainly decided they were done. The circle on Home was
// worse: disabled outright, so it did nothing at all in the exact moment a
// person wanted to stop talking.
//
// The native head settled this long ago - TalkOnce treats a re-entrant call as
// STOP - and the hybrid argued instead.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared;
using CircleAI.Samples.It.Shared.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class StopOnSecondPressTests : TestContext
{
    [Fact]
    public void A_stopped_turn_is_not_reported_as_a_failure()
    {
        // The turn cancels itself when somebody ends it, and that must not
        // surface as an error - it is the button doing what it was told.
        using var router = new VoiceTurnRouter();

        router.Stop();

        Assert.True(router.StoppedByHand);
        Assert.True(router.Token.IsCancellationRequested);
        Assert.True(router.Ended(new OperationCanceledException()));
    }

    [Fact]
    public void Someone_elses_cancellation_is_still_a_failure()
    {
        using var router = new VoiceTurnRouter();

        Assert.False(router.Ended(new OperationCanceledException()));
        Assert.False(router.Ended(new InvalidOperationException()));
    }

    [Fact]
    public void The_bar_button_ends_the_turn_it_started()
    {
        var talk = new FakeConversation { Heard = null };
        this.WireEverything();
        Services.AddSingleton<IConversation>(talk);
        Services.GetRequiredService<NavigationManager>().NavigateTo("home");

        var layout = RenderComponent<MainLayout>(
            p => p.Add(c => c.Body, (RenderFragment)(b => { })));

        layout.Find("button.tabbar-voice").Click();   // start
        layout.Find("button.tabbar-voice").Click();   // stop

        layout.WaitForAssertion(() => Assert.Contains("Stopped.", layout.Markup));
    }

    [Fact]
    public void The_circle_is_not_dead_while_it_is_listening()
    {
        // A disabled circle is a dead control in the one moment somebody wants
        // to stop, leaving only the small button in the bar as a way out.
        this.WireEverything();
        var mark = Services.GetRequiredService<VoiceMark>();

        var home = RenderComponent<CircleAI.Samples.It.Shared.Pages.Home>();
        home.InvokeAsync(() => mark.Report(new TurnState(TurnPhase.Listening)));

        home.WaitForAssertion(() =>
        {
            var hero = home.Find("button.hero");
            Assert.False(hero.HasAttribute("disabled"));
            Assert.Equal("Stop", hero.GetAttribute("aria-label"));
        });
    }
}
