// TilesNameTheirPackTests.cs
//
// OPEN-GAPS A9: a capability tile sent its opener and nothing else, so the domain pack behind
// it was on no path. The tile now names its pack in the chat link it opens.

using Bunit;
using Bunit.TestDoubles;
using CircleAI.Assistant;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

public class TilesNameTheirPackTests : TestContext
{
    [Fact]
    public void Tapping_a_tile_opens_the_chat_with_its_opener_and_its_pack()
    {
        this.WireEverything();
        var cut = RenderComponent<CircleAI.Samples.Shared.Pages.Services>();

        // Open the section that holds the Pets tile, then tap it.
        var section = Capabilities.All.ToList().FindIndex(g => g.Items.Any(c => c.Title == "Pets"));
        cut.FindAll("button.panel-head").ToList()[section].Click();
        cut.FindAll("button.tile").ToList().Single(b => b.TextContent.Contains("Pets", StringComparison.Ordinal)).Click();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        Assert.Contains("chat?ask=", uri, StringComparison.Ordinal);
        Assert.Contains("domain=Pets", uri, StringComparison.Ordinal);
    }
}
