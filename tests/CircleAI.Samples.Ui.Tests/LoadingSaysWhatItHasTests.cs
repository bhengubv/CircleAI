// LoadingSaysWhatItHasTests.cs
//
// THE SENTENCE REACHED THE SCREEN'S DATA AND NOT THE SCREEN.
//
// A Circle OS device held 1.1 GB of a 22.8 GB model. FirstRun.Census measured it and
// wrote "1.1 GB of 22.8 GB here - it will carry on". LinkVerbs grew a FIFTH FIELD on
// the census row specifically to carry that number across the app/service link.
// LinkedSetup rebuilt it into a CensusRow. And then this screen rendered
//
//     @(row.Present ? row.Detail : Size(row.Bytes))
//
// so Detail was shown only for a row that was ALREADY FINISHED, and every missing
// row - the only ones a person is actually waiting on - got a bare size instead.
// CensusRow.Partial had no reader anywhere outside its own unit tests.
//
// Three layers of plumbing were built to carry one fact to a person, and a ternary
// at the last inch dropped it.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

public class LoadingSaysWhatItHasTests : TestContext
{
    private void Wire(Census census)
    {
        Services.AddSingleton<IConversation>(new FakeConversation());
        Services.AddSingleton<ISetup>(new FakeSetup { Census = census });
        Services.AddSingleton<IWiringProbe>(new BrowserWiringProbeStub());
        Services.AddSingleton<IBrain>(new FakeBrain());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void A_download_that_stopped_halfway_says_it_will_carry_on()
    {
        Wire(new Census(
            [new CensusRow("the brain", false, 22_797_996_902,
                           "1.1 GB of 22.8 GB here - it will carry on", 1_100_000_000)],
            0, 1, Census.Line(0, 1)));

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(
            () => Assert.Contains("it will carry on", loading.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_model_nobody_started_shows_what_it_would_cost()
    {
        // The other side of the same ternary: a row with nothing down has no
        // sentence worth showing, and the price is the useful fact.
        Wire(new Census(
            [new CensusRow("the brain", false, 22_797_996_902, "not on this phone yet")],
            0, 1, Census.Line(0, 1)));

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(
            () => Assert.Contains("22.8 GB", loading.Markup),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_catalogued_size_is_not_quoted_in_binary_here()
    {
        // This screen had its own binary formatter, so the same bundle read 21.2 GB
        // here and 22.8 GB on the setup screen. A person comparing two screens of
        // one app was entitled to think one was lying.
        Wire(new Census(
            [new CensusRow("the brain", false, 22_797_996_902, "not on this phone yet")],
            0, 1, Census.Line(0, 1)));

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(
            () => Assert.Contains("22.8 GB", loading.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("21.2 GB", loading.Markup);
    }

    [Fact]
    public void Something_already_here_says_what_it_gives_you()
    {
        Wire(new Census(
            [new CensusRow("the voice", true, 60_000_000, "eleven languages", 60_000_000)],
            1, 1, Census.Line(1, 1)));

        var loading = RenderComponent<Loading>();

        loading.WaitForAssertion(
            () => Assert.Contains("eleven languages", loading.Markup),
            TimeSpan.FromSeconds(10));
    }
}
