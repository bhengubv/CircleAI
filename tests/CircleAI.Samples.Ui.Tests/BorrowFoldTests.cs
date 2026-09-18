// BorrowFoldTests.cs
//
// The "Borrowing" fold on the Phone tab: the opt-in toggle and the node picker
// that set pooled-intelligence consent. The screen only renders and persists
// through the settings store; the rule (CanBorrow) is the product's
// (AppSettings.Borrowing). This pins that tapping the toggle and naming a node
// write straight back, and that the node field stays hidden until borrowing is on.

using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Samples.Ui.Tests;

public class BorrowFoldTests : TestContext
{
    /// <summary>Render Settings, open the Phone tab, open the Borrowing fold.</summary>
    private (IRenderedComponent<Settings> Screen, FakeSettings Settings) Borrowing(AppSettings? seed = null)
    {
        var settings = new FakeSettings { Settings = seed ?? new AppSettings() };
        this.WireEverything();
        Services.AddSingleton<IDeviceFacts>(new FakeDeviceFacts { Storage = StorageReport.None });
        Services.AddSingleton<ISettings>(settings);   // last wins over WireEverything's default

        var screen = RenderComponent<Settings>();
        screen.FindAll("button,div,span")
            .FirstOrDefault(e => e.TextContent.Trim() == "Phone")?.Click();
        var fold = screen.FindAll("button.fold")
            .FirstOrDefault(b => b.TextContent.Contains("Borrowing"));
        if (fold is not null && !fold.ClassList.Contains("fold-on")) fold.Click();
        return (screen, settings);
    }

    [Fact]
    public void Off_by_default_shows_the_toggle_but_hides_the_node_field()
    {
        var (screen, _) = Borrowing();
        screen.WaitForAssertion(() =>
        {
            Assert.Contains("Borrow a nearby brain", screen.Markup);
            Assert.DoesNotContain("The node's tag", screen.Markup);   // hidden until on
        });
    }

    [Fact]
    public void Turning_it_on_persists_and_reveals_the_node_field()
    {
        var (screen, settings) = Borrowing();

        // The borrow toggle is the last checkbox on the Phone tab (below Light
        // appearance and Answer-to-its-name), inside the open Borrowing fold.
        screen.FindAll("input[type=checkbox]").Last().Change(true);

        Assert.True(settings.Settings.BorrowEnabled);
        screen.WaitForAssertion(() => Assert.Contains("The node's tag", screen.Markup));
    }

    [Fact]
    public void Naming_a_node_persists_it_and_the_product_rule_allows_borrowing()
    {
        var (screen, settings) = Borrowing(new AppSettings(BorrowEnabled: true));

        screen.FindAll("input.select")
            .First(i => i.GetAttribute("aria-label") == "The Circle node to borrow from")
            .Change("KXJB7-MN2P4");

        Assert.Equal("KXJB7-MN2P4", settings.Settings.BorrowNodeId);
        Assert.True(settings.Settings.Borrowing.CanBorrow);   // the product's own rule
    }
}
