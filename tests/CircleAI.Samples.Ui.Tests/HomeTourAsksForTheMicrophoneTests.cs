// HomeTourAsksForTheMicrophoneTests.cs
//
// "Allow the microphone" on Home was a link to nowhere.
//
// Home shows the setup tour while a download runs - the first screen of a fresh
// install - and drew every step as <a href="@route">. A step whose route is
// "#microphone" acts rather than goes, which Setup already knew and Home did not, so
// the one button that could have let the wake word hear anything was
// href="#microphone": nothing moved and nothing asked.
//
// Found on the P30 on 2026-10-23, where nothing on a fresh install ever asked for
// Circle AI's microphone at all; this is the screen half of that fix.

using Bunit;
using CircleAI.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

// Inside the namespace, as HandoverTests explains: an enclosing namespace beats a
// using-alias declared outside it.
using Home = CircleAI.Samples.Shared.Pages.Home;

/// <summary>A running setup whose tour is the microphone step.</summary>
internal sealed class MicrophoneTourSetup : ISetup
{
    private readonly StagedSetup _run = new();

    /// <summary>What the person answers. Null is "nothing was answered".</summary>
    public bool? Allow { get; init; } = true;

    /// <summary>How many times the microphone was asked for.</summary>
    public int MicrophoneAsks;

    /// <summary>
    /// Whether the tour still offers the microphone step, as a service's would stop
    /// doing once the permission is held.
    /// </summary>
    public bool StillWanted = true;

    public int Runs => _run.Runs;
    public void LandTheVoiceAndEars() => _run.LandTheVoiceAndEars();

    public bool IsRunning => _run.IsRunning;
    public Task<Readiness> ReadinessAsync(CancellationToken ct = default) => _run.ReadinessAsync(ct);
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default) => _run.PlanAsync(ct);
    public Task<Census> CensusAsync(CancellationToken ct = default) => _run.CensusAsync(ct);

    /// <summary>The sink the screen was handed, so a test can report a SECOND part.</summary>
    /// <remarks>
    /// StagedSetup always reports index 1, and Home only re-reads the tour when the
    /// index moves - which is right (it is what stops the card flickering as the
    /// estimate wobbles) and means a test about the NEXT boundary needs its own
    /// report.
    /// </remarks>
    private IProgress<SetupProgressReport>? _progress;

    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        _progress = progress;
        return _run.RunAsync(progress, ct);
    }

    /// <summary>Another part of the plan lands, so the tour moves on.</summary>
    public void LandAnotherPart() => _progress?.Report(new SetupProgressReport(
        Index: 2, Count: 3, Title: "the brain", Fraction: 0.5, Remaining: TimeSpan.FromMinutes(20)));

    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TourStep>>(
            StillWanted ? [MicrophoneAsk.Step] : []);

    public Task<bool?> AllowMicrophoneAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref MicrophoneAsks);
        return Task.FromResult(Allow);
    }

    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => _run.AllowBackgroundAsync(ct);
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => _run.BackgroundAllowedAsync(ct);
    public Task<bool> LinkApprovedAsync(CancellationToken ct = default) => _run.LinkApprovedAsync(ct);
    public Task<bool> ApproveLinkAsync(CancellationToken ct = default) => _run.ApproveLinkAsync(ct);
}

public sealed class HomeTourAsksForTheMicrophoneTests : TestContext
{
    private MicrophoneTourSetup Wire(bool allow = true)
    {
        var setup = new MicrophoneTourSetup { Allow = allow };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);
        return setup;
    }

    /// <summary>Home mid-download, with the tour showing its one step.</summary>
    private IRenderedComponent<Home> HomeShowingTheStep(MicrophoneTourSetup setup)
    {
        var home = RenderComponent<Home>();
        home.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));
        setup.LandTheVoiceAndEars();

        home.WaitForAssertion(
            () => Assert.NotEmpty(home.FindAll("div.tour")),
            TimeSpan.FromSeconds(10));
        return home;
    }

    [Fact]
    public void The_step_is_a_button_and_not_a_link_to_nowhere()
    {
        var home = HomeShowingTheStep(Wire());

        Assert.Empty(home.FindAll("a[href='#microphone']"));
        Assert.Contains(MicrophoneAsk.Action, home.Find("button.tour-action").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Pressing_it_asks_and_says_it_can_hear_you()
    {
        var setup = Wire();
        var home = HomeShowingTheStep(setup);

        home.Find("button.tour-action").Click();

        home.WaitForAssertion(
            () => Assert.Contains("It can hear you now.", home.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Equal(1, setup.MicrophoneAsks);
    }

    [Fact]
    public void A_no_is_said_where_the_button_was()
    {
        var home = HomeShowingTheStep(Wire(allow: false));

        home.Find("button.tour-action").Click();

        home.WaitForAssertion(
            () => Assert.Contains("Not allowed. It can still be typed to.", home.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_step_that_answered_itself_is_not_offered_again_at_the_next_part()
    {
        // LOADING CAN HAND OVER WITH THE SYSTEM DIALOG STILL UP. The person taps Allow
        // there, and this screen went on offering "Let it hear its name / Allow the
        // microphone" for the rest of the download, because the tour was read once and
        // never again. It is the only step whose own condition changes under it.
        var setup = Wire();
        var home = HomeShowingTheStep(setup);

        setup.StillWanted = false;
        setup.LandAnotherPart();

        home.WaitForAssertion(
            () => Assert.Empty(home.FindAll("div.tour")),
            TimeSpan.FromSeconds(10));
        Assert.Equal(0, setup.MicrophoneAsks);
    }
}
