// NobodyTurnsTheBrainOnTests.cs
//
// "when you speak to someone do you ask them to turn their brain on first?"
//   - the owner, 2026-10-09, looking at this screen on the P30:
//
//       Not ready yet
//       Circle AI stopped — this phone closed it. Ask again and it will start up.
//                        [ Turn it on ]
//
// You do not. And every part of that screen was wrong.
//
// WHAT WAS ACTUALLY HAPPENING, from the phone's own log:
//
//   19:03:49  Killing 8610:…circleai.service (adj 100):
//             iAwareK[abnormProc](adj:-10000,type:service)
//   19:04:21  back up as pid 11233, brain loaded, 593 MB resident
//   19:05:25  W/CircleAI.Link(8465): transact failed: DeadObjectException
//   19:06:03  W/CircleAI.Link(8465): transact failed: DeadObjectException
//
// The service was alive and serving from 19:04:21. The app kept transacting with
// the dead process for another two minutes, because CircleAiLinkClient held the
// binder in a one-shot TaskCompletionSource: TrySetResult on the RECONNECT was a
// no-op, so Bind.AutoCreate's fresh binder was thrown away every time.
//
// Three consequences, all on screen at once:
//   1. "Ask again and it will start up" was false - asking again reused the
//      corpse and failed identically, forever.
//   2. The composer was WITHDRAWN while not ready, so a person could not even
//      type the question.
//   3. Send returned silently on !_ready, so the one control left did nothing.
//
// The binder fix lives in CircleAiLinkClient and cannot be reached from here -
// IBinder is Android-only and this suite is net10.0. What these pin is the half a
// person touches: the box is there, pressing Send asks, and nothing anywhere
// tells somebody to go and switch the brain on.
//
// Sibling: OnByDefaultTests, which pins that opening the app asks the brain for
// its state at all - that ask is what starts it.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public sealed class NobodyTurnsTheBrainOnTests : TestContext
{
    private FakeBrain ColdBrain()
    {
        var brain = new FakeBrain { Ready = false, Answer = "it answered anyway" };
        this.WireEverything();
        Services.AddSingleton<IBrain>(brain);
        return brain;
    }

    [Fact]
    public void Nothing_asks_a_person_to_turn_the_brain_on()
    {
        // THE WHOLE POINT. There is no switch, because the app being open IS the
        // service running; the button only ever led to a settings page that could
        // not help, and it was covering a client bug.
        ColdBrain();

        var chat = RenderComponent<Chat>();

        Assert.DoesNotContain("Turn it on", chat.Markup);
        Assert.DoesNotContain("Not ready yet", chat.Markup);
    }

    [Fact]
    public void The_box_to_type_in_is_there_before_the_brain_is()
    {
        // A phone under memory pressure is exactly when somebody is most likely to
        // be asking why nothing works, and it was the moment the app removed the
        // keyboard.
        ColdBrain();

        var chat = RenderComponent<Chat>();

        var input = chat.Find(".input");
        Assert.False(input.HasAttribute("disabled"),
            "the field was disabled while the brain was down, so the question could not even be typed");
        Assert.NotNull(chat.Find(".send"));
    }

    [Fact]
    public async Task A_question_asked_before_the_brain_is_ready_still_reaches_it()
    {
        // THE SILENT RETURN. Send began `if (text.Length == 0 || _busy || !_ready)
        // return;` - so on a not-ready phone the draft stayed in the box and
        // nothing happened at all. No bubble, no error, no log. That is the
        // "no response in 30 secs".
        var brain = ColdBrain();
        var chat = RenderComponent<Chat>();

        chat.Find(".input").Input("what time is it");
        await chat.Find(".send").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        chat.WaitForAssertion(() =>
            Assert.True(brain.Asks > 0,
                "Send did not ask the brain, which is the silent-return bug this replaces"));

        chat.WaitForAssertion(() =>
            Assert.Contains("it answered anyway", chat.Markup));
    }

    [Fact]
    public void The_reason_it_is_not_ready_is_still_said()
    {
        // REMOVING THE BUTTON MUST NOT REMOVE THE HONESTY. "Getting ready" on its
        // own is the generic apology this repo keeps replacing; the specific
        // detail from the brain has to stay on screen, which is what
        // ScreenClaimTests asserted before and still does.
        ColdBrain();

        var chat = RenderComponent<Chat>();

        chat.WaitForAssertion(() =>
            Assert.Contains("no brain in a test", chat.Find(".empty").TextContent));
    }
}
