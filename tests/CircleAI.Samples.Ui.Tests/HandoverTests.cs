// HandoverTests.cs
//
// You could not talk to it while it downloaded.
//
// On a fresh install the loading screen awaited the WHOLE plan before it
// navigated - around 800 MB - and it is full screen with no bottom bar. So the
// first experience of this product was forty minutes of a progress bar with no
// circle, no tour, and no way out of the screen. The other head's whole design
// is the opposite: the plan is ordered so the voice and the ears arrive first,
// the circle goes live the moment those two land, and the bar and the tour run
// on Home while the brain - which is most of the bytes - keeps coming.
//
// ISetup already had everything needed for this and nothing used it: RunAsync is
// idempotent and attaches to a run in flight, and IsRunning exists, in its own
// words, because "a component dies the moment somebody taps Home on the bar
// while 817 MB is coming down".

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

/// <summary>A setup that really runs: reports parts, and becomes talkable partway.</summary>
internal sealed class StagedSetup : ISetup
{
    private readonly TaskCompletionSource _finish = new();

    /// <summary>Flipped by <see cref="LandTheVoiceAndEars"/>, read by ReadinessAsync.</summary>
    private volatile bool _talkable;

    /// <summary>The progress sink RunAsync was handed, so a test can drive it.</summary>
    private IProgress<SetupProgressReport>? _progress;

    /// <summary>How many times RunAsync was called. Two would be two downloads.</summary>
    public int Runs;

    public bool IsRunning { get; set; } = true;

    /// <summary>How long TourAsync was told there was, or null if never asked.</summary>
    public TimeSpan? ToldThereWas { get; private set; }

    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.FromResult(_talkable
            ? new Readiness(ReadyStage.CanListen, "Tap and talk",
                "Still waking up - the first answer may take a moment.", CanTalk: true)
            : new Readiness(ReadyStage.Waking, "Getting ready",
                "You can talk to it in a moment.", CanTalk: false));

    public Task<Census> CensusAsync(CancellationToken ct = default)
        => Task.FromResult(new Census(
            [new CensusRow("the voice", _talkable, 1024, _talkable ? "here" : "still coming"),
             new CensusRow("the brain", false, 1_300_000_000, "still coming")],
            _talkable ? 1 : 0, 2, "0 of 2 on this phone"));

    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SetupItem>>(
            [new SetupItem("the voice", 1024), new SetupItem("the brain", 1_300_000_000)]);

    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Runs);
        _progress = progress;
        return _finish.Task;
    }

    /// <summary>The first part of the plan lands: the phone can now hold a turn.</summary>
    public void LandTheVoiceAndEars()
    {
        _talkable = true;
        _progress?.Report(new SetupProgressReport(
            Index: 1, Count: 2, Title: "the brain", Fraction: 0.1,
            Remaining: TimeSpan.FromMinutes(38)));
    }

    /// <summary>The rest of it arrives.</summary>
    public void Finish()
    {
        IsRunning = false;
        _finish.TrySetResult();
    }

    public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
    {
        ToldThereWas = remaining;
        return Task.FromResult<IReadOnlyList<TourStep>>(
            [new TourStep("Try a language", "It speaks 74.", "Hear one", "languages")]);
    }

    public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> BackgroundAllowedAsync(CancellationToken ct = default) => Task.FromResult(true);
}

public class HandoverTests : TestContext
{
    private StagedSetup Wire()
    {
        var setup = new StagedSetup();
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);
        return setup;
    }

    private string Where() => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void The_loading_screen_leaves_as_soon_as_the_phone_can_hold_a_turn()
    {
        // THE GAP ITSELF. Before this it awaited the whole plan, so the answer
        // was "in about forty minutes".
        var setup = Wire();

        var loading = RenderComponent<Loading>();
        loading.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("/home", Where(), StringComparison.Ordinal);

        setup.LandTheVoiceAndEars();

        loading.WaitForAssertion(
            () => Assert.EndsWith("/home", Where(), StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void The_download_is_not_waited_for_before_leaving()
    {
        // The run is still in flight when the handover happens - that is the
        // whole point. If leaving required the run to complete, this would hang.
        var setup = Wire();

        var loading = RenderComponent<Loading>();
        loading.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));
        setup.LandTheVoiceAndEars();

        loading.WaitForAssertion(
            () => Assert.EndsWith("/home", Where(), StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        Assert.True(setup.IsRunning, "left only after the download finished");
    }

    [Fact]
    public void Home_attaches_to_the_running_download_rather_than_starting_another()
    {
        // A SECOND CONCURRENT DOWNLOAD OF THE SAME FILES, over the same
        // connection, onto a phone already struggling with the first. ISetup's
        // own remarks describe this happening once already.
        var setup = Wire();

        var home = RenderComponent<Home>();
        home.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));

        Assert.Equal(1, setup.Runs);
    }

    [Fact]
    public void Home_shows_the_rest_of_it_coming_down()
    {
        var setup = Wire();

        var home = RenderComponent<Home>();
        home.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));
        setup.LandTheVoiceAndEars();

        // The determinate bar, which the indeterminate warm-up bar is not.
        home.WaitForAssertion(
            () => Assert.NotEmpty(home.FindAll("div.bar-known")),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Home_offers_something_to_do_with_the_wait()
    {
        // On a slow link this is forty minutes somebody has to spend anyway.
        // Offering something useful during it is the difference between a
        // progress bar and an app.
        var setup = Wire();

        var home = RenderComponent<Home>();
        home.WaitForState(() => setup.Runs > 0, TimeSpan.FromSeconds(10));
        setup.LandTheVoiceAndEars();

        home.WaitForAssertion(
            () => Assert.NotEmpty(home.FindAll("div.tour")),
            TimeSpan.FromSeconds(10));

        // ASKED WITH THE TIME THERE ACTUALLY IS, because the steps are chosen
        // for how long is left - a tour built for five minutes is a different
        // tour from one built for forty.
        Assert.Equal(TimeSpan.FromMinutes(38), setup.ToldThereWas);
    }

    [Fact]
    public void A_phone_with_nothing_to_download_is_asked_for_no_run_at_all()
    {
        // The fifth launch. Nothing is coming, so Home must not attach to a run
        // that does not exist, and no bar should appear over a finished phone.
        var setup = Wire();
        setup.IsRunning = false;

        var home = RenderComponent<Home>();

        Assert.Equal(0, setup.Runs);
        Assert.Empty(home.FindAll("div.bar-known"));
    }
}
