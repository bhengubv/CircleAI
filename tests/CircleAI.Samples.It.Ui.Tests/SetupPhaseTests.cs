// SetupPhaseTests.cs
//
// The setup screen saying what it is doing, rather than a countdown that stopped.
//
// MEASURED ON A REDMI NOTE 12 PRO+, 2026-09-05. The 1.3 GB brain finished
// downloading, the phone went to 267% CPU hashing it with the network idle, and
// the screen sat on "about 20 sec left" for over a minute. Nothing was wrong.
// DownloadPhase.Verifying existed and even carried the comment "No bytes move;
// can take seconds on a phone" - it was simply dropped twice on its way to the
// UI, once at SetupProgress and again at SetupProgressReport, so the only thing
// left to render was an estimate nobody was updating.
//
// A number that has stopped moving is the universal sign of a hang, so this is
// worth a test: the failure is invisible in code review and looks like a crash
// to whoever is holding the phone.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class SetupPhaseTests : TestContext
{
    /// <summary>A setup that reports whatever the test wants, when asked to run.</summary>
    private sealed class ReportingSetup : ISetup
    {
        public SetupProgressReport Report { get; init; } =
            new(0, 1, "the brain", 0.99, TimeSpan.FromSeconds(20));

        public Readiness Readiness { get; init; } =
            new(ReadyStage.Ready, "Getting it ready", "", false);

        /// <summary>
        /// False, so the page does not attach to an imaginary run at init - the
        /// test drives it by pressing Start, which is what a person does.
        /// </summary>
        public bool IsRunning => false;

        public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
            => Task.FromResult(Readiness);

        public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SetupItem>>([new SetupItem("the brain", 1_300_000_000)]);

        public Task<Census> CensusAsync(CancellationToken ct = default)
            => Task.FromResult(new Census([], 4, 5, "4 of 5 on this phone"));

        /// <summary>Reports once and stays in flight, the way a real run does.</summary>
        /// <remarks>
        /// COMPLETING IMMEDIATELY HID THE THING UNDER TEST. Setup's subtitle asks
        /// whether it is DONE before it asks what phase it is in, so a fake that
        /// returned Task.CompletedTask made the page skip straight to "Everything
        /// it needs is on this phone" and render no phase at all. The states this
        /// file exists to check only exist mid-run.
        /// </remarks>
        public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        {
            progress.Report(Report);
            return new TaskCompletionSource().Task;
        }

        public Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TourStep>>([]);

        public Task<bool> AllowMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AllowBackgroundAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private IRenderedComponent<Setup> Screen(SetupProgressReport report)
    {
        this.WireEverything();
        Services.AddSingleton<ISetup>(new ReportingSetup { Report = report });

        var screen = RenderComponent<Setup>();

        // The progress view only exists once a run has been started, so press
        // the button rather than reaching into the component's state.
        screen.WaitForAssertion(() =>
            Assert.NotNull(screen.FindAll("button.start").ToList().FirstOrDefault()));
        screen.FindAll("button.start").ToList()[0].Click();

        return screen;
    }

    [Fact]
    public void Checking_says_so_instead_of_a_countdown_that_has_stopped()
    {
        // The whole defect. The estimate is stale by construction here - twenty
        // seconds that will never tick down, because nothing reports during a
        // hash - so the screen must not print it.
        var screen = Screen(new(0, 1, "the brain", 0.99,
            TimeSpan.FromSeconds(20), SetupPhase.Checking));

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Checking what arrived", screen.Markup);
            Assert.DoesNotContain("seconds left", screen.Markup);
        });
    }

    [Fact]
    public void A_real_download_still_shows_its_estimate()
    {
        // The other half: while bytes ARE moving the number is honest and is the
        // most useful thing on the screen - "43 minutes left" is a decision
        // somebody can act on. Losing it would be a worse bug than the one being
        // fixed.
        var screen = Screen(new(0, 1, "the brain", 0.25,
            TimeSpan.FromMinutes(11), SetupPhase.Fetching));

        screen.WaitForAssertion(() => Assert.Contains("About 11 minutes left", screen.Markup));
    }

    [Fact]
    public void Fetching_is_the_default_so_old_callers_are_unchanged()
    {
        // Phase is defaulted on the record. Every head that has not been taught
        // about it keeps reporting a download, which is what it was doing.
        var report = new SetupProgressReport(0, 1, "the brain", 0.5, TimeSpan.FromMinutes(3));

        Assert.Equal(SetupPhase.Fetching, report.Phase);
    }

    [Fact]
    public void Done_does_not_claim_time_remaining_either()
    {
        var screen = Screen(new(0, 1, "the brain", 1.0, TimeSpan.Zero, SetupPhase.Done));

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Finishing up", screen.Markup);
            Assert.DoesNotContain("Working out how long", screen.Markup);
        });
    }
}
