// MarkAgreementTests.cs
//
// The two microphone buttons must never disagree.
//
// FOUND ON A PHONE, HOURS IN, THROUGH A WEBVIEW DEBUGGER: tapping the circle on
// Home put bm-listening on the hero alone, and tapping the middle of the tab bar
// put it on the bar alone. Neither ever moved the other. Both marks animated
// perfectly - they were being told about different turns.
//
// This renders the real layout against a real VoiceMark and asks what the bar
// draws for a turn it did not start. On the broken build it drew idle.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class MarkAgreementTests : TestContext
{
    private VoiceMark Wire()
    {
        var mark = new VoiceMark();
        Services.AddSingleton(mark);
        Services.AddSingleton<IConversation>(new FakeConversation());
        Services.AddSingleton<IShareTarget>(new FakeShareTarget());
        Services.AddSingleton<IResidentAssistant>(new FakeResidentAssistant());

        // The bar is hidden on the full-screen stages - loading and setup own the
        // whole screen - so a test that never leaves "/" is testing nothing.
        Services.GetRequiredService<NavigationManager>().NavigateTo("home");
        return mark;
    }

    private IRenderedComponent<MainLayout> Layout()
        => RenderComponent<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => { })));

    [Fact]
    public void Bar_is_idle_when_nothing_is_happening()
    {
        Wire();

        var bar = Layout();

        Assert.Contains("bm-idle", bar.Markup);
        Assert.DoesNotContain("voice-on", bar.Markup);
    }

    [Fact]
    public void Bar_shows_listening_for_a_turn_it_did_not_start()
    {
        // THE REGRESSION, IN ONE ASSERTION. Nothing here presses the bar: the
        // turn is reported to the shared mark the way Home's circle reports it,
        // and the bar has to follow.
        var mark = Wire();
        var bar = Layout();

        bar.InvokeAsync(() => mark.Report(new TurnState(TurnPhase.Listening)));

        Assert.Contains("bm-listening", bar.Markup);
        Assert.Contains("voice-on", bar.Markup);
        Assert.DoesNotContain("bm-idle", bar.Markup);
    }

    [Theory]
    [InlineData(TurnPhase.Thinking, "bm-thinking")]
    [InlineData(TurnPhase.Speaking, "bm-speaking")]
    public void Bar_follows_every_phase(TurnPhase phase, string expected)
    {
        // Not just listening: the mark has a different motion per phase, and a
        // phase the bar cannot draw is a phase it draws WRONG rather than not at
        // all - which is harder to notice than a dead button.
        var mark = Wire();
        var bar = Layout();

        bar.InvokeAsync(() => mark.Report(new TurnState(phase)));

        Assert.Contains(expected, bar.Markup);
    }

    [Fact]
    public void Bar_goes_back_to_idle_when_the_turn_ends()
    {
        var mark = Wire();
        var bar = Layout();

        bar.InvokeAsync(() => mark.Report(new TurnState(TurnPhase.Listening)));
        bar.InvokeAsync(() => mark.Clear());

        Assert.Contains("bm-idle", bar.Markup);
        Assert.DoesNotContain("voice-on", bar.Markup);
    }

    [Fact]
    public void Bar_says_listening_to_a_screen_reader_too()
    {
        // The mark is the whole of that button, and a logo is not a name - so the
        // state has to reach somebody who cannot see the arcs at all.
        var mark = Wire();
        var bar = Layout();

        bar.InvokeAsync(() => mark.Report(new TurnState(TurnPhase.Listening)));

        Assert.Contains("Listening", bar.Markup);
    }
}
