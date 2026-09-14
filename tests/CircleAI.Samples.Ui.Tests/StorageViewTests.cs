// StorageViewTests.cs
//
// The Memory Manager's footprint, made visible on the Phone tab of Settings, and
// the one-tap Free that reclaims the regenerable scratch. Distinct from "Space
// free" (the whole phone) — this is only what Circle AI itself uses.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public class StorageViewTests : TestContext
{
    /// <summary>Render Settings on the Phone tab with the "This phone" fold open.</summary>
    private (IRenderedComponent<Settings> Screen, FakeDeviceFacts Facts) PhoneStorage(StorageReport report)
    {
        var facts = new FakeDeviceFacts { Storage = report };
        this.WireEverything();
        Services.AddSingleton<IDeviceFacts>(facts);   // last wins over WireEverything's default

        var screen = RenderComponent<Settings>();
        screen.FindAll("button,div,span")
            .FirstOrDefault(e => e.TextContent.Trim() == "Phone")?.Click();

        var fold = screen.FindAll("button.fold")
            .FirstOrDefault(b => b.TextContent.Contains("This phone"));
        if (fold is not null && !fold.ClassList.Contains("fold-on")) fold.Click();

        return (screen, facts);
    }

    [Fact]
    public void The_footprint_breakdown_shows_what_Circle_AI_uses()
    {
        var (screen, _) = PhoneStorage(new StorageReport(
            [
                new StorageLine("Downloaded models", "2.0 GB", false),
                new StorageLine("Scratch & audio", "40 MB", true),
            ],
            Total: "2.0 GB", Freeable: "40 MB"));

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("What Circle AI uses", screen.Markup);
            Assert.Contains("Downloaded models", screen.Markup);
            Assert.Contains("Scratch &amp; audio", screen.Markup);   // HTML-encoded ampersand
            Assert.Contains("Free up 40 MB", screen.Markup);
        });
    }

    [Fact]
    public void The_free_button_reclaims_the_cache_and_says_what_came_back()
    {
        var (screen, facts) = PhoneStorage(new StorageReport(
            [new StorageLine("Scratch & audio", "40 MB", true)],
            Total: "40 MB", Freeable: "40 MB"));

        screen.WaitForAssertion(() => Assert.Contains("Free up 40 MB", screen.Markup));
        screen.FindAll("button").First(b => b.TextContent.Contains("Free up")).Click();

        screen.WaitForAssertion(() =>
        {
            Assert.Equal(1, facts.Reclaims);
            Assert.Contains("Freed 40 MB", screen.Markup);
        });
    }

    [Fact]
    public void A_head_that_cannot_measure_a_footprint_shows_no_storage_block()
    {
        // The browser inherits StorageReport.None; the block simply is not there,
        // rather than showing a fabricated "0 MB".
        var (screen, _) = PhoneStorage(StorageReport.None);
        screen.WaitForAssertion(() => Assert.DoesNotContain("What Circle AI uses", screen.Markup));
    }

    [Fact]
    public void Nothing_to_free_hides_the_button()
    {
        // A footprint with no reclaimable scratch shows the breakdown but no Free
        // button — a button that frees nothing is a button that lies.
        var (screen, _) = PhoneStorage(new StorageReport(
            [new StorageLine("Downloaded models", "2.0 GB", false)],
            Total: "2.0 GB", Freeable: ""));

        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Downloaded models", screen.Markup);
            Assert.DoesNotContain("Free up", screen.Markup);
        });
    }
}
