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

    /// <summary>A facts host that answers from the resident, the way the device does.</summary>
    /// <remarks>
    /// The fixed-list fake above cannot catch a STALE row: it returns the same
    /// answer however the world changed, so a screen that never re-asks looks
    /// identical to one that does. This one derives Waking from IsListening,
    /// which is what DeviceFacts actually does, so "did the screen ask again"
    /// becomes a question the test can put.
    /// </remarks>
    private sealed class LiveFacts(IResidentAssistant resident) : IDeviceFacts
    {
        public Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AbilityRow>>(
                [new AbilityRow("Waking", "Hears you without being touched",
                    resident.IsListening ? AbilityState.On : AbilityState.Ready,
                    TryRoute: "wake")]);

        public Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
            => Task.FromResult(new PhoneFacts([], []));

        public Task<string> TurnOnAsync(
            string title, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult("nothing to turn on in a test");
    }

    [Fact]
    public void Switching_the_listener_off_updates_the_row_that_reports_it()
    {
        // MEASURED ON A P30, 2026-09-05. Unticking "Answer to its name" unticked
        // the box and rewrote its subtitle, and four inches below it "Waking ✓"
        // sat under "Turned on" with a Try it beside it - because _abilities is
        // read once and never again. Switching tabs did not help; only leaving
        // the screen entirely did.
        //
        // So AbilityState.Ready, which was added precisely to stop the app
        // claiming "Waking ✓ On" while nothing listens, could not reach the
        // screen in the one situation it was written for.
        var resident = new FakeResidentAssistant();
        this.WireEverything();
        Services.AddSingleton<IResidentAssistant>(resident);
        Services.AddSingleton<IDeviceFacts>(new LiveFacts(resident));

        // Start it, so the row has something true to say before the toggle.
        resident.StartAsync().GetAwaiter().GetResult();

        var screen = Screen();
        screen.WaitForAssertion(() => Assert.Contains("Try it", screen.Markup));

        // The resident checkbox is the one bound to ToggleResident. It is the
        // second checkbox on the tab; the first is the theme.
        var boxes = screen.FindAll("input[type=checkbox]").ToList();
        boxes[1].Change(false);

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Turn on", screen.Markup);
            Assert.DoesNotContain("Try it", screen.Markup);
        });
    }

    /// <summary>Open a fold by its title, on whichever tab holds it.</summary>
    private IRenderedComponent<Settings> Screen(string tab, string fold)
    {
        var screen = RenderComponent<Settings>();

        screen.FindAll("button,div,span")
            .FirstOrDefault(e => e.TextContent.Trim() == tab)?.Click();

        var f = screen.FindAll("button.fold").ToList()
            .FirstOrDefault(b => b.TextContent.Contains(fold));
        if (f is not null && !f.ClassList.Contains("fold-on")) f.Click();

        return screen;
    }

    [Fact]
    public void Turning_off_listen_for_the_wake_phrase_actually_stops_listening()
    {
        // IT WROTE A SETTING AND NOTHING ELSE. Unticking this saved
        // WakeEnabled = false and left the resident service holding the
        // microphone, because the service is governed by a control on a
        // DIFFERENT tab that never reads this one. The switch's own subtitle
        // says "the microphone stays open on this phone" - and turning it off
        // left it open.
        //
        // Survivable while the resident defaulted to off. Not survivable now
        // that listening is the default.
        var resident = new FakeResidentAssistant();
        this.WireEverything();
        Services.AddSingleton<IResidentAssistant>(resident);
        Services.AddSingleton<IDeviceFacts>(new LiveFacts(resident));

        resident.StartAsync().GetAwaiter().GetResult();
        Assert.True(resident.IsListening);

        var screen = Screen("Language", "Waking");
        var box = screen.FindAll("input[type=checkbox]").ToList()
            .Last();   // the wake-phrase switch inside the open fold

        box.Change(false);

        screen.WaitForAssertion(() => Assert.False(resident.IsListening));
    }

    [Fact]
    public void Turning_it_back_on_starts_listening_again()
    {
        var resident = new FakeResidentAssistant();
        this.WireEverything();
        Services.AddSingleton<IResidentAssistant>(resident);
        Services.AddSingleton<IDeviceFacts>(new LiveFacts(resident));

        var screen = Screen("Language", "Waking");
        var box = screen.FindAll("input[type=checkbox]").ToList().Last();

        box.Change(true);

        screen.WaitForAssertion(() => Assert.True(resident.IsListening));
        Assert.Equal(1, resident.Starts);
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
