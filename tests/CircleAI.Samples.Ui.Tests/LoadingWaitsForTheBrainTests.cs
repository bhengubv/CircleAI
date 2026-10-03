// LoadingWaitsForTheBrainTests.cs
//
// A loading screen that hands over a cold model has not loaded anything.
//
// It warmed the voice and left. So Home arrived, said it was ready, and the first
// question came back "brain warming up, try again shortly" - thirteen to
// twenty-three seconds on a P30, nineteen for a 2B on a Tensor G2. Every second of
// that was spent AFTER the person had been told the phone was ready, which is the
// one thing this screen exists not to do.

using System;
using System.IO;
using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public sealed class LoadingWaitsForTheBrainTests : TestContext
{
    private string Where() => Services.GetRequiredService<NavigationManager>().Uri;

    private FakeBrain Wire(int coldForAsks)
    {
        var brain = new FakeBrain { ColdForAsks = coldForAsks };
        Services.AddSingleton<IConversation>(new FakeConversation());
        Services.AddSingleton<ISetup>(new FakeSetup());
        Services.AddSingleton<IWiringProbe>(new BrowserWiringProbeStub());
        Services.AddSingleton<IBrain>(brain);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return brain;
    }

    [Fact]
    public void It_waits_for_the_brain_before_it_navigates()
    {
        // SOURCE-LEVEL, BECAUSE THE HARNESS CANNOT HOLD THIS. A brain that is cold
        // for a few asks makes the screen wait on a real timer inside
        // OnInitializedAsync - which is the lifecycle method bUnit's renderer is
        // itself awaiting, so WaitForAssertion blocks the very render it is waiting
        // for and nothing moves. Three attempts at expressing it live all sat out
        // their whole timeout; the behaviour is proven on device instead, where the
        // screen now holds until "brain ready".
        //
        // What CAN be pinned here is the order, which is the thing that regresses:
        // a gate that navigates first and warms afterwards is the gate that shipped.
        var source = File.ReadAllText(Path.Combine(
            Root(), "samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.Shared",
            "Pages", "Loading.razor"));

        var warms  = source.IndexOf("await BrainAsync();", StringComparison.Ordinal);
        var leaves = source.IndexOf("Nav.NavigateTo(\"home\")", StringComparison.Ordinal);

        Assert.True(warms  > 0, "the loading screen never waits for the brain");
        Assert.True(leaves > 0, "the loading screen never navigates");
        Assert.True(warms < leaves,
            "the loading screen navigates to Home before it has waited for the brain");
    }

    [Fact]
    public void It_leaves_when_the_brain_is_ready_without_dawdling()
    {
        // THE OTHER HALF. On the fifth launch everything is warm, and this screen's
        // whole philosophy is that it is then a half-second receipt on the way to
        // Home - not a fixed delay somebody sits through for symmetry.
        var brain = Wire(coldForAsks: 0);

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(() => Assert.EndsWith("/home", Where()), TimeSpan.FromSeconds(10));
        Assert.Equal(1, brain.StateAsks);
    }

    [Fact]
    public void It_does_not_wait_for_ever()
    {
        // A CEILING, NOT A TARGET. This screen already refuses to hold somebody for
        // the last byte of a download; a model that will not load must not become a
        // spinner with no way past it. Source-level, because the wait is forty
        // seconds and no test should take that long to prove a number exists.
        var source = File.ReadAllText(Path.Combine(
            Root(), "samples", "CircleAI.Samples.Hybrid", "CircleAI.Samples.Shared",
            "Pages", "Loading.razor"));

        Assert.Contains("AddSeconds(40)", source);
        Assert.Contains("deadline", source);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "samples")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
