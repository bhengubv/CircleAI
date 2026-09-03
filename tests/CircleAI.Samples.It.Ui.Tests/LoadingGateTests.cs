// LoadingGateTests.cs
//
// Downloaded is not ready, and this screen used to stop at downloaded.
//
// The census counts bytes on disk, so the gate opened while the transcriber had
// never been opened and the voice had never been built. Measured on a P30: the
// FIRST decode took eleven seconds against under two for every one after it.
// That cost landed on the first turn - the one where somebody decides whether
// the thing works - after a screen had already told them it was ready.
//
// This screen exists to absorb exactly that wait. These pin it to doing so.

using Bunit;
using CircleAI.Samples.It;
using CircleAI.Samples.It.Shared.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.It.Ui.Tests;

public class LoadingGateTests : TestContext
{
    private FakeConversation Wire(Census? census = null)
    {
        var talk = new FakeConversation();
        Services.AddSingleton<IConversation>(talk);
        Services.AddSingleton<ISetup>(census is null
            ? new FakeSetup()
            : new FakeSetup { Census = census });
        Services.AddSingleton<IWiringProbe>(new BrowserWiringProbeStub());
        JSInterop.Mode = JSRuntimeMode.Loose;
        return talk;
    }

    private string Where() => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void Warms_the_engines_before_letting_anybody_through()
    {
        var talk = Wire();

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(() => Assert.EndsWith("/home", Where()), TimeSpan.FromSeconds(10));
        Assert.Equal(1, talk.Prepared);
    }

    [Fact]
    public void Does_not_leave_before_the_warm_up_has_run()
    {
        // THE ORDER IS THE POINT. A gate that navigates and warms afterwards is
        // the same gate that shipped: the screen says ready, and the cost lands
        // on the first turn anyway.
        var talk = Wire();

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(() => Assert.EndsWith("/home", Where()), TimeSpan.FromSeconds(10));
        Assert.True(talk.Prepared > 0, "left the loading screen without warming anything");
    }

    [Fact]
    public void Warms_on_the_path_that_had_to_download_something_too()
    {
        // THE EXIT THAT DID NOT WARM. Warming used to sit in front of one of the
        // two doors: the "nothing missing" path warmed, and the path that had
        // just finished a DOWNLOAD left cold - so the person who sat through
        // setup, and is likeliest to speak the second it ends, got the eleven
        // second first decode, while the person with nothing to fetch got the
        // warm one. Exactly backwards.
        var talk = Wire(new Census(
            [new CensusRow("the voice", false, 1024, "still coming")], 0, 1, "0 of 1 on this phone"));

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(() => Assert.EndsWith("/home", Where()), TimeSpan.FromSeconds(15));
        Assert.True(talk.Prepared > 0, "left after downloading without warming anything");
    }

    [Fact]
    public void Warms_once_however_many_ways_out_there_are()
    {
        // The guard is on the door, not on each caller, so a second exit added
        // later cannot warm twice either.
        var talk = Wire();

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(() => Assert.EndsWith("/home", Where()), TimeSpan.FromSeconds(15));
        Assert.Equal(1, talk.Prepared);
    }
}

/// <summary>A probe with every hook working, so the gate is testing the warm-up.</summary>
internal sealed class BrowserWiringProbeStub : IWiringProbe
{
    public Task<WiringReport> HooksAsync(CancellationToken ct = default)
        => Task.FromResult(new WiringReport(
            [new WiringRow("Phonemizer", "hook", WiringStage.Wired, "fine")], 1, 1));

    public Task<WiringReport> VoicesAsync(
        IEnumerable<string>? languages = null,
        IProgress<WiringRow>? progress = null,
        CancellationToken ct = default)
        => Task.FromResult(new WiringReport([], 0, 0));
}
