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
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

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

    /// <summary>Render Settings on the Phone tab, with a given setup host.</summary>
    private IRenderedComponent<Settings> PhoneTab(FakeSetup setup, FakeResidentAssistant resident)
    {
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);
        Services.AddSingleton<IResidentAssistant>(resident);
        Services.AddSingleton<IDeviceFacts>(new LiveFacts(resident));

        var screen = RenderComponent<Settings>();
        screen.FindAll("button,div,span").ToList()
            .FirstOrDefault(e => e.TextContent.Trim() == "Phone")?.Click();
        return screen;
    }

    [Fact]
    public void A_phone_that_kills_background_apps_says_so_next_to_the_switch()
    {
        // THE ONE THING THAT DECIDES WHETHER THE SWITCH STILL MEANS ANYTHING IN
        // AN HOUR, and it was reachable only from first-run setup. Measured on a
        // P30 on 2026-09-05: exemption never granted, EMUI hibernated the
        // always-listening service, and its owner spoke to the phone for eleven
        // minutes while it heard a quiet room. Nothing on any screen mentioned
        // it - there was no screen that could.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();

        var screen = PhoneTab(new FakeSetup { BackgroundAllowed = false }, resident);

        screen.WaitForAssertion(
            () => Assert.Contains("This phone may stop it listening", screen.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_phone_that_already_allows_it_is_not_nagged()
    {
        // A permanent notice about battery optimisation is noise on the phones
        // that already work, and noise is how the one warning that matters gets
        // ignored.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();

        var screen = PhoneTab(new FakeSetup { BackgroundAllowed = true }, resident);

        screen.WaitForAssertion(
            () => Assert.DoesNotContain("This phone may stop it listening", screen.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Nothing_is_said_about_it_while_it_is_not_listening()
    {
        // Irrelevant on a phone that is not listening at all: there is nothing
        // running for the battery settings to kill.
        var resident = new FakeResidentAssistant();   // never started

        var screen = PhoneTab(new FakeSetup { BackgroundAllowed = false }, resident);

        screen.WaitForAssertion(
            () => Assert.Contains("Turn on", screen.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("This phone may stop it listening", screen.Markup);
    }

    [Fact]
    public void Fixing_it_asks_the_phone()
    {
        // Tapping it opens Android's screen. That is the whole job of the tap.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();
        var setup = new FakeSetup { BackgroundAllowed = false };

        var screen = PhoneTab(setup, resident);
        screen.WaitForAssertion(
            () => Assert.Contains("Fix it", screen.Markup), TimeSpan.FromSeconds(10));

        screen.FindAll("button").ToList().First(b => b.TextContent.Contains("Fix it")).Click();

        screen.WaitForAssertion(() => Assert.Equal(1, setup.BackgroundAsks), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_tap_alone_does_not_clear_the_warning()
    {
        // THE BUG, AS A TEST. The screen used to re-read the exemption in the
        // same breath as opening the dialog - before anybody could have answered
        // - so it always read the old value. Measured on a P30 on 2026-09-07:
        // granting it changed nothing on screen, and the button sat there
        // inviting a second tap from somebody who reasonably concluded nothing
        // had happened.
        //
        // While the phone still says no, the warning MUST stay. Anything else is
        // a screen congratulating itself for opening a dialog.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();
        var setup = new FakeSetup { BackgroundAllowed = false };

        var screen = PhoneTab(setup, resident);
        screen.WaitForAssertion(
            () => Assert.Contains("Fix it", screen.Markup), TimeSpan.FromSeconds(10));

        screen.FindAll("button").ToList().First(b => b.TextContent.Contains("Fix it")).Click();
        screen.WaitForAssertion(() => Assert.Equal(1, setup.BackgroundAsks), TimeSpan.FromSeconds(10));

        // Came back without granting - reversed out of it, or said no.
        screen.InvokeAsync(() => screen.Instance.CameBack()).GetAwaiter().GetResult();

        Assert.Contains("This phone may stop it listening", screen.Markup);
    }

    [Fact]
    public void The_warning_goes_when_the_phone_says_yes_and_the_app_comes_back()
    {
        // The answer comes back from Android, not from the tap. The person grants
        // it in another activity while this screen sits in the background, so the
        // prompt clears on the way back in - because the phone now says yes, not
        // because a button was pressed.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();
        var setup = new FakeSetup { BackgroundAllowed = false };

        var screen = PhoneTab(setup, resident);
        screen.WaitForAssertion(
            () => Assert.Contains("Fix it", screen.Markup), TimeSpan.FromSeconds(10));

        screen.FindAll("button").ToList().First(b => b.TextContent.Contains("Fix it")).Click();

        setup.Grant();
        screen.InvokeAsync(() => screen.Instance.CameBack()).GetAwaiter().GetResult();

        screen.WaitForAssertion(
            () => Assert.DoesNotContain("This phone may stop it listening", screen.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Granting_it_elsewhere_also_clears_the_warning()
    {
        // Nobody has to use our button. The exemption can be granted in Android's
        // own settings while this screen is in the background, and a screen that
        // only believed its own button would be wrong in exactly the case
        // somebody went around it.
        var resident = new FakeResidentAssistant();
        resident.StartAsync().GetAwaiter().GetResult();
        var setup = new FakeSetup { BackgroundAllowed = false };

        var screen = PhoneTab(setup, resident);
        screen.WaitForAssertion(
            () => Assert.Contains("This phone may stop it listening", screen.Markup),
            TimeSpan.FromSeconds(10));

        setup.Grant();
        screen.InvokeAsync(() => screen.Instance.CameBack()).GetAwaiter().GetResult();

        screen.WaitForAssertion(
            () => Assert.DoesNotContain("This phone may stop it listening", screen.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Equal(0, setup.BackgroundAsks);
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
