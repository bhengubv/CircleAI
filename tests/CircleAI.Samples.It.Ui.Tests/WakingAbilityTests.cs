// WakingAbilityTests.cs
//
// "Waking ✓ On" while nothing was listening.
//
// AbilityState.On meant "a model is on disk". So Settings printed a tick the
// moment the wake bundle finished downloading, and the only way to actually
// start waking was to spot the small "Try it" link beside that tick and open the
// wake screen - which builds a detector of its own.
//
// Three rows below, on the same screen, a toggle reads IsListening LIVE, because
// Android can kill the service and a remembered bool drifts. One screen, two
// answers about one fact, and only the toggle was telling the truth.
//
// The file that produced the row already carried the warning: "a build
// advertised Waking ✓ On on a phone that could not wake at all."

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class WakingAbilityTests : TestContext
{
    private FakeResidentAssistant Wire(AbilityState waking, bool startWorks = true)
    {
        var resident = new FakeResidentAssistant { StartSucceeds = startWorks };
        this.WireEverything();
        Services.AddSingleton<IDeviceFacts>(new FakeDeviceFacts
        {
            Abilities = [new AbilityRow(
                "Waking", "Hears you without being touched", waking, TryRoute: "wake")],
        });
        Services.AddSingleton<IResidentAssistant>(resident);
        return resident;
    }

    /// <summary>Render Settings and open the fold the abilities live in.</summary>
    /// <remarks>
    /// The list is behind the "Turned on" fold on the Phone tab, and a collapsed
    /// fold renders nothing - so a test that skipped this would be asserting
    /// against an empty page and passing for the wrong reason.
    /// </remarks>
    private IRenderedComponent<Settings> Screen()
    {
        var screen = RenderComponent<Settings>();

        var phone = screen.FindAll("button,div,span")
            .FirstOrDefault(e => e.TextContent.Trim() == "Phone");
        phone?.Click();

        // Open() TOGGLES, and selecting a tab already sets _open = 0 - which is
        // this fold. Clicking it would CLOSE the thing the test is looking at.
        var fold = screen.FindAll("button.fold")
            .FirstOrDefault(b => b.TextContent.Contains("Turned on"));
        if (fold is not null && !fold.ClassList.Contains("fold-on")) fold.Click();

        return screen;
    }

    [Fact]
    public void Ready_offers_a_switch_rather_than_a_tick()
    {
        // The whole defect in one assertion: everything is downloaded, nothing
        // is listening, and the screen must say so.
        Wire(AbilityState.Ready);

        var screen = Screen();

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Turn on", screen.Markup);
            Assert.DoesNotContain("Try it", screen.Markup);
        });
    }

    [Fact]
    public void On_is_reserved_for_actually_listening()
    {
        Wire(AbilityState.On);

        var screen = Screen();

        screen.WaitForAssertion(() => Assert.Contains("Try it", screen.Markup));
    }

    [Fact]
    public void Turning_it_on_starts_the_listener_rather_than_downloading()
    {
        // Everything it needs is already on the phone. Sending this down the
        // download path would spend somebody's data re-fetching a bundle they
        // already have.
        var resident = Wire(AbilityState.Ready);
        var screen = Screen();

        screen.WaitForAssertion(() => Assert.Contains("Turn on", screen.Markup));
        screen.FindAll("button").First(b => b.TextContent.Contains("Turn on")).Click();

        screen.WaitForAssertion(() => Assert.Equal(1, resident.Starts));
    }

    [Fact]
    public void A_switch_that_does_nothing_says_why()
    {
        // Huawei, Xiaomi, Oppo and Vivo kill foreground services on their own
        // schedule. A control that silently fails is the bug this row already was.
        var resident = Wire(AbilityState.Ready, startWorks: false);
        var screen = Screen();

        screen.WaitForAssertion(() => Assert.Contains("Turn on", screen.Markup));
        screen.FindAll("button").First(b => b.TextContent.Contains("Turn on")).Click();

        screen.WaitForAssertion(() =>
            Assert.Contains("Allow it there", screen.Markup));
    }

    [Fact]
    public void Available_still_offers_the_download()
    {
        // Ready and Available both show "Turn on" and mean different things -
        // one needs a switch, the other needs bytes. Only the second may show a
        // size.
        Wire(AbilityState.Available);

        var screen = Screen();

        screen.WaitForAssertion(() => Assert.Contains("Turn on", screen.Markup));
    }
}
