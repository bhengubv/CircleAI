// WakeOpensAConversationTests.cs
//
// Saying the name again for every sentence is not a conversation.
//
// A wake used to buy exactly ONE turn: ask, get an answer, and the next thing
// you say is heard by nothing until you have said "Hey Circle AI" again. Nobody
// talks to a person that way, and on a phone you are holding at arm's length it
// is worse than a button, because at least a button is where you left it.
//
// A wake now opens a conversation and each turn decides whether there is
// another: words mean listen again, silence means somebody has stopped talking
// and the phone goes back to waiting for its name. No timer of its own, because
// the absence of speech is already the signal a person uses.
//
// THE BUTTON IS DELIBERATELY NOT THIS. Pressing it is an act per turn, and a
// press that quietly held the microphone open afterwards would be a surprise of
// the worst kind on the one control somebody uses when they do NOT want to be
// listened to continuously.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class WakeOpensAConversationTests : TestContext
{
    private FakeResidentAssistant Wire(FakeConversation talk)
    {
        var resident = new FakeResidentAssistant();
        Services.AddSingleton<IConversation>(talk);
        Services.AddSingleton<IResidentAssistant>(resident);
        Services.AddSingleton<IShareTarget>(new FakeShareTarget());
        Services.AddSingleton(new VoiceMark());
        Services.AddSingleton(new CapabilityRegistry([]));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return resident;
    }

    [Fact]
    public void A_wake_carries_on_while_there_is_something_to_hear()
    {
        // THE BUG, AS A TEST. Two things said, then quiet: three turns - two that
        // heard and one that found the room empty and let go.
        var talk = new FakeConversation { Ready = true, Heard = "what is the time", HearsForTurns = 2 };
        var resident = Wire(talk);
        var layout = RenderComponent<MainLayout>();

        resident.RaiseWoke("Hey Circle AI");

        layout.WaitForAssertion(() => Assert.Equal(3, talk.Turns), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_wake_lets_go_when_nobody_says_anything()
    {
        // Woken by a television, a passing conversation, a false positive. One
        // turn, nothing heard, and the microphone goes back to the wake word
        // rather than staying open on a room that is not talking to it.
        var talk = new FakeConversation { Ready = true, Heard = "hello", HearsForTurns = 0 };
        var resident = Wire(talk);
        var layout = RenderComponent<MainLayout>();

        resident.RaiseWoke("Hey Circle AI");

        layout.WaitForAssertion(() => Assert.Equal(1, talk.Turns), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_button_press_takes_exactly_one_turn()
    {
        // THE HALF THAT MUST NOT CHANGE. A press is deliberate per turn, and one
        // that held the microphone open afterwards would be a nasty surprise on
        // the control somebody reaches for precisely when they do not want to be
        // listened to continuously.
        var talk = new FakeConversation { Ready = true, Heard = "what is the time" };
        Wire(talk);

        // The bar is hidden on the full-screen stages - loading and setup own the
        // whole screen - and "/" is one of them, so the button does not exist
        // until somewhere ordinary is open.
        Services.GetRequiredService<NavigationManager>().NavigateTo("home");

        var layout = RenderComponent<MainLayout>();

        layout.Find("button.tabbar-voice").Click();

        layout.WaitForAssertion(() => Assert.Equal(1, talk.Turns), TimeSpan.FromSeconds(10));

        // And it stays one. If the loop leaked into the press this would climb.
        Thread.Sleep(400);
        Assert.Equal(1, talk.Turns);
    }

    [Fact]
    public void A_conversation_cannot_hold_the_microphone_for_ever()
    {
        // A television in the room, or a meeting happening near the phone, keeps
        // producing words - and an always-listening assistant that never lets go
        // is the one behaviour it must never have. The ceiling is what makes this
        // a conversation rather than a lock-in.
        var talk = new FakeConversation { Ready = true, Heard = "and another thing" };
        var resident = Wire(talk);
        var layout = RenderComponent<MainLayout>();

        resident.RaiseWoke("Hey Circle AI");

        layout.WaitForAssertion(
            () => Assert.True(talk.Turns >= 2, "the conversation did not carry on at all"),
            TimeSpan.FromSeconds(10));

        layout.WaitForAssertion(
            () => Assert.True(talk.Turns is > 1 and <= 8,
                $"a wake took {talk.Turns} turns — it is not letting go"),
            TimeSpan.FromSeconds(20));
    }
}
