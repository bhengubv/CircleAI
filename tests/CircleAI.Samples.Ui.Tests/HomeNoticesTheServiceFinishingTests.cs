// HomeNoticesTheServiceFinishingTests.cs
//
// The phone finished setting itself up and the screen did not notice.
//
// Measured on the P30, 2026-10-09. ModelFetchService fetched the whole first-run
// plan by itself - 269.8 MB, four models, 75 seconds - and Home went on saying
//
//     Finish setting it up
//     Some of it is still missing. Tap to get the rest.
//
// for as long as it was left alone. Force-stopping the app and relaunching it
// showed "Tap and talk" immediately, so nothing was wrong with the readiness
// calculation: the screen simply never asked again.
//
// WHY IT COULD NOT. Every refresh Home had was wired to a download the APP
// drives - the IProgress callback inside FollowSetupAsync, and OnPartLandedAsync
// hanging off it. The first-run fetch now runs in the SERVICE process, whose
// progress this process cannot see at all. OnResumed covered coming back to the
// app and nothing covered staying in it.
//
// This is the same family as the download never starting: a thing that is
// supposed to happen on its own, with the one surface that reports it wired to a
// path that no longer carries it.
//
// POLLED, NOT PUSHED. The link is a binder - request/response, no channel back -
// which is why Heard is a counter this side reads rather than an event the
// service raises. A push channel for one headline would be a transport to
// maintain forever.

using System;
using System.Threading.Tasks;
using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

// INSIDE the namespace, which is load-bearing. Page components are also
// namespaces in the single CircleAI assembly since 3.8.0, and an enclosing
// namespace beats a using-alias declared outside it - put this above the
// namespace and it compiles to CS0118. HandoverTests says the same thing.
using Home = CircleAI.Samples.Shared.Pages.Home;

public sealed class HomeNoticesTheServiceFinishingTests : TestContext
{
    /// <summary>Longer than Home's 3 s poll, with room for a slow agent.</summary>
    private static readonly TimeSpan LongEnoughForOnePoll = TimeSpan.FromSeconds(12);

    private static Readiness StillFilling => new(
        ReadyStage.NeedsSetup, "Finish setting it up",
        "Some of it is still missing. Tap to get the rest.", CanTalk: false);

    private static Readiness Done => new(
        ReadyStage.Ready, "Tap and talk", "", CanTalk: true);

    [Fact]
    public void The_headline_changes_when_the_service_finishes_without_the_app_being_restarted()
    {
        // THE BUG, EXACTLY. Nothing in this test touches Home after the first
        // render - no resume, no remount, no tap. The phone becomes ready
        // underneath it, which is what a service-side fetch completing looks
        // like from here.
        var setup = new FakeSetup { Readiness = StillFilling };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);

        var home = RenderComponent<Home>();
        home.WaitForAssertion(() =>
            Assert.Contains("Finish setting it up", home.Markup));

        setup.Readiness = Done;

        home.WaitForAssertion(
            () => Assert.Contains("Tap and talk", home.Markup),
            LongEnoughForOnePoll);

        Assert.DoesNotContain("Finish setting it up", home.Markup);
    }

    [Fact]
    public void A_phone_that_is_still_filling_up_keeps_saying_so()
    {
        // THE OTHER HALF, because a watcher that simply rewrote the headline
        // would pass the test above and be worse than the bug. Nothing arrived,
        // so nothing should change.
        var setup = new FakeSetup { Readiness = StillFilling };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);

        var home = RenderComponent<Home>();
        home.WaitForAssertion(() => Assert.Contains("Finish setting it up", home.Markup));

        // Past one poll, with the fake unchanged.
        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(4));

        Assert.Contains("Finish setting it up", home.Markup);
    }

    [Fact]
    public void A_readiness_read_that_throws_leaves_the_headline_alone()
    {
        // A FAILED READ IS NOT NEWS. The watcher runs every three seconds for as
        // long as a phone is incomplete, so a transient failure - the service
        // being restarted by the phone, say, which is routine on this handset -
        // must not blank the screen or stop the watching.
        var setup = new ThrowingSetup { Readiness = StillFilling };
        this.WireEverything();
        Services.AddSingleton<ISetup>(setup);

        var home = RenderComponent<Home>();
        home.WaitForAssertion(() => Assert.Contains("Finish setting it up", home.Markup));

        setup.Throw = true;
        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(4));
        Assert.Contains("Finish setting it up", home.Markup);

        // And it is still watching: stop throwing, become ready, and it notices.
        setup.Throw = false;
        setup.Readiness = Done;

        home.WaitForAssertion(
            () => Assert.Contains("Tap and talk", home.Markup),
            LongEnoughForOnePoll);
    }

    /// <summary>A setup whose readiness read can be made to fail.</summary>
    private sealed class ThrowingSetup : ISetup
    {
        private readonly FakeSetup _inner = new();

        public bool Throw { get; set; }
        public Readiness Readiness { get => _inner.Readiness; set => _inner.Readiness = value; }
        public bool IsRunning => _inner.IsRunning;

        public Task<Readiness> ReadinessAsync(System.Threading.CancellationToken ct = default)
            => Throw
                ? Task.FromException<Readiness>(new InvalidOperationException("the service is restarting"))
                : _inner.ReadinessAsync(ct);

        public Task<System.Collections.Generic.IReadOnlyList<SetupItem>> PlanAsync(
            System.Threading.CancellationToken ct = default) => _inner.PlanAsync(ct);

        public Task<Census> CensusAsync(System.Threading.CancellationToken ct = default)
            => _inner.CensusAsync(ct);

        public Task RunAsync(IProgress<SetupProgressReport> progress,
                             System.Threading.CancellationToken ct = default)
            => _inner.RunAsync(progress, ct);

        public Task<System.Collections.Generic.IReadOnlyList<TourStep>> TourAsync(
            TimeSpan remaining, System.Threading.CancellationToken ct = default)
            => _inner.TourAsync(remaining, ct);

        public Task<bool> AllowMicrophoneAsync(System.Threading.CancellationToken ct = default)
            => _inner.AllowMicrophoneAsync(ct);

        public Task<bool> AllowBackgroundAsync(System.Threading.CancellationToken ct = default)
            => _inner.AllowBackgroundAsync(ct);

        public Task<bool> BackgroundAllowedAsync(System.Threading.CancellationToken ct = default)
            => _inner.BackgroundAllowedAsync(ct);

        public Task<bool> LinkApprovedAsync(System.Threading.CancellationToken ct = default)
            => _inner.LinkApprovedAsync(ct);

        public Task<bool> ApproveLinkAsync(System.Threading.CancellationToken ct = default)
            => _inner.ApproveLinkAsync(ct);
    }
}
