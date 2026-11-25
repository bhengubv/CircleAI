using Bunit;
using CircleAI.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Notice = CircleAI.Samples.Shared.Components.OptimisingNotice;

namespace CircleAI.Samples.Ui.Tests;

// The "Optimising the brain" card draws a bar from the index share the service already publishes,
// and - as importantly - it is SILENT when nothing is being optimised. That silence is the whole
// reason it can sit on Home and Setup without becoming furniture, so it is tested as hard as the
// bar: a default FakeSetup says null, and the card must render no markup at all.
public class OptimisingNoticeTests : TestContext
{
    private void Wire(IndexProgress? indexing)
    {
        this.WireEverything();
        // Last registration wins, so this replaces the FakeSetup WireEverything set.
        Services.AddSingleton<ISetup>(new FakeSetup { Indexing = indexing });
    }

    [Fact]
    public void It_shows_the_share_and_the_line_while_the_brain_is_optimising()
    {
        Wire(new IndexProgress(42, "Until it is done it works at 42%.", false, null));
        var cut = RenderComponent<Notice>();

        cut.WaitForAssertion(() => Assert.Contains("Optimising the brain", cut.Markup));
        Assert.Contains("42%", cut.Markup);                                   // the bar's width and the line
        Assert.Contains("Until it is done it works at 42%.", cut.Markup);
    }

    [Fact]
    public void Off_the_charger_it_says_it_carries_on_while_charging()
    {
        Wire(new IndexProgress(30, "Optimising.", true, "It carries on while the phone is charging."));
        var cut = RenderComponent<Notice>();

        cut.WaitForAssertion(() =>
            Assert.Contains("It carries on while the phone is charging.", cut.Markup));
    }

    [Fact]
    public void It_renders_nothing_when_nothing_is_being_optimised()
    {
        Wire(null);   // the honest default: no index being built, so no card
        var cut = RenderComponent<Notice>();

        Assert.DoesNotContain("Optimising the brain", cut.Markup);
    }
}
