// LoadingAsksForTheMicrophoneTests.cs
//
// The first-run download ran under the loading screen from end to end, so nothing
// asked for the microphone.
//
// Loading hands over to Home when the phone can talk, and on the P30 that is when the
// brain lands - last, by design, because it is most of the bytes. So on the fresh
// installs of 2026-10-23 the whole download ran under "Waking up", Home's tour never
// appeared, and the wake word - on by default - never had the microphone it listens
// with. A step that waited for a progress report to move to a new item with time left
// on it never appeared on Loading either: on the phone that moment did not come.
//
// Now the screen asks for the tour when the download starts, says why, and asks the
// phone for the microphone itself, once - the phone's own Allow is the only question.
// A no leaves a button for a second try.

using Bunit;
using CircleAI.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

// Inside the namespace, for the reason HandoverTests gives.
using Loading = CircleAI.Samples.Shared.Pages.Loading;

/// <summary>A download that never makes the phone talkable, so Loading stays up.</summary>
internal sealed class BrainLastSetup : ISetup
{
    private readonly TaskCompletionSource _finish = new();

    /// <summary>The person's answer to the ask made on their behalf, given when the test says.</summary>
    /// <remarks>Null is "nothing was answered" - see ISetup.AllowMicrophoneAsync.</remarks>
    public TaskCompletionSource<bool?> Offered { get; } = new();

    /// <summary>The person's answer when they press the button.</summary>
    public bool? Pressed { get; init; } = true;

    /// <summary>What the tour offers. Replaced to test a step that answers itself.</summary>
    public IReadOnlyList<TourStep> Steps { get; set; } =
        [new TourStep("Try a language", "It speaks 74.", "Hear one", "languages"), MicrophoneAsk.Step];

    public int Runs;
    public int QuietAsks;
    public int PressedAsks;
    public int Tours;

    public bool IsRunning => true;

    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.FromResult(new Readiness(ReadyStage.Waking, "Waking up",
            "The brain is still coming.", CanTalk: false));

    public Task<Census> CensusAsync(CancellationToken ct = default)
        => Task.FromResult(new Census(
            [new CensusRow("the ears", true, 6_762_672, "here"),
             new CensusRow("the brain", false, 547_645_481, "still coming")],
            1, 2, "1 of 2 on this phone"));

    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>([new SetupItem("the brain", 547_645_481)]);

    /// <summary>Reports nothing at all, as the phone's run effectively did.</summary>
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Runs);
        return _finish.Task;
    }

    /// <summary>A place to go and a thing to do, so the filter has something to filter.</summary>
    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Tours);
        return Task.FromResult(Steps);
    }

    public Task<bool?> OfferMicrophoneAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref QuietAsks);
        return Offered.Task;
    }

    public Task<bool?> AllowMicrophoneAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref PressedAsks);
        return Task.FromResult(Pressed);
    }

    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> LinkApprovedAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> ApproveLinkAsync(CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class LoadingAsksForTheMicrophoneTests : TestContext
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private BrainLastSetup Wire(bool pressed = true)
    {
        var setup = new BrainLastSetup { Pressed = pressed };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);
        return setup;
    }

    /// <summary>The loading screen mid-download, the phone's dialog up.</summary>
    private IRenderedComponent<Loading> LoadingAsking(BrainLastSetup setup)
    {
        var loading = RenderComponent<Loading>();
        loading.WaitForState(() => setup.Runs > 0 && setup.QuietAsks > 0, Patience);
        loading.WaitForAssertion(() => Assert.NotEmpty(loading.FindAll("div.tour")), Patience);
        return loading;
    }

    [Fact]
    public void On_first_run_it_asks_without_a_press_and_says_why_first()
    {
        var setup = Wire();
        var loading = LoadingAsking(setup);

        // THE REASON IS ON THE SCREEN WHILE THE PHONE ASKS, and nobody pressed anything.
        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.Announce, loading.Markup), Patience);
        Assert.Equal(1, setup.QuietAsks);
        Assert.Equal(0, setup.PressedAsks);

        // Only what acts here is offered: a place to go would leave mid-download.
        Assert.DoesNotContain("Hear one", loading.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Allowed_it_says_so_and_the_step_goes()
    {
        var setup = Wire();
        var loading = LoadingAsking(setup);

        setup.Offered.SetResult(true);

        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.Allowed, loading.Markup), Patience);
        loading.WaitForAssertion(() => Assert.Empty(loading.FindAll("div.tour")), Patience);
    }

    [Fact]
    public void A_no_is_said_and_the_button_stays_for_a_second_try()
    {
        var setup = Wire(pressed: true);
        var loading = LoadingAsking(setup);

        setup.Offered.SetResult(false);

        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.NotAllowed, loading.Markup), Patience);
        Assert.Contains(MicrophoneAsk.Action, loading.Find("button.tour-action").TextContent, StringComparison.Ordinal);

        // NOT ASKED AGAIN ON ITS OWN. The second ask is the person's.
        loading.WaitForAssertion(() => Assert.False(loading.Find("button.tour-action").HasAttribute("disabled")), Patience);
        loading.Find("button.tour-action").Click();

        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.Allowed, loading.Markup), Patience);
        Assert.Equal(1, setup.QuietAsks);
        Assert.Equal(1, setup.PressedAsks);
    }

    [Fact]
    public void A_pressed_yes_takes_the_card_away_with_it()
    {
        // IT STAYED, ENABLED, UNDER THE SENTENCE SAYING IT HAD WORKED - so "Allow the
        // microphone" was still inviting a second press at the moment the first one
        // had just succeeded.
        var setup = Wire(pressed: true);
        var loading = LoadingAsking(setup);

        setup.Offered.SetResult(false);
        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.NotAllowed, loading.Markup), Patience);

        loading.Find("button.tour-action").Click();
        loading.WaitForAssertion(() => Assert.Contains(MicrophoneAsk.Allowed, loading.Markup), Patience);
        loading.WaitForAssertion(() => Assert.Empty(loading.FindAll("div.tour")), Patience);
    }

    [Fact]
    public void A_question_still_on_screen_is_not_reported_as_a_refusal()
    {
        // THE TWO-MINUTE WAIT GAVE OUT AND THE SCREEN SAID "Not allowed", under a
        // dialog the person had not answered - and then said nothing when they did.
        var setup = Wire();
        var loading = LoadingAsking(setup);

        setup.Offered.SetResult(null);

        loading.WaitForAssertion(() => Assert.DoesNotContain(MicrophoneAsk.NotAllowed, loading.Markup), Patience);
        Assert.DoesNotContain(MicrophoneAsk.Allowed, loading.Markup, StringComparison.Ordinal);

        // The button is still there for a deliberate try.
        Assert.Contains(MicrophoneAsk.Action, loading.Find("button.tour-action").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tour_that_has_nothing_to_offer_is_not_probed_for_a_minute()
    {
        // THE RETRY LOOP'S ONLY EXIT WAS "A STEP APPEARED". It exists for one case -
        // the first answer can arrive before the link is up, and is then "approve the
        // link", a place to go, which this screen does not offer - and on a phone with
        // the microphone already allowed it probed twenty times, three seconds apart.
        // On the receipt branch, which awaits the ask, that is a minute of holding
        // somebody on a loading screen.
        var setup = new BrainLastSetup { Steps = [] };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);

        var loading = RenderComponent<Loading>();
        loading.WaitForState(() => setup.Tours > 0, Patience);

        // One ask, and then it stops: a linked service with nothing to offer IS the
        // answer. Given generously - the point is that it is not twenty.
        Thread.Sleep(TimeSpan.FromSeconds(4));
        Assert.True(setup.Tours <= 2, $"the tour was asked {setup.Tours} times for a phone with nothing to offer");
        Assert.Equal(0, setup.QuietAsks);
    }
}
