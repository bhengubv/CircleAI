// WolverineTests.cs
//
// The self-healing dashboard. It reads IHealingView; these drive it with a fake and
// check the things a person comes to it for: the health glance, the autonomy dial
// (shown and settable), the escalations they can clear, the healed feed, and the
// self-check — each on its own tab, with counts on the tabs so nothing hides below a
// fold. Empty states teach rather than alarm.

using System;
using System.Linq;
using Bunit;
using CircleAI.Assistant;
using CircleAI.Samples.Shared.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CircleAI.Samples.Ui.Tests;

public class WolverineTests : TestContext
{
    FakeHealingView Wire(FakeHealingView view)
    {
        Services.AddSingleton<IHealingView>(view);   // before WireEverything, whose TryAdd keeps it
        this.WireEverything();
        return view;
    }

    static HealingItem Item(string id, string title, string detail,
        string? action = null, string outcome = "Healed", bool needs = false)
        => new(id, DateTimeOffset.UtcNow, title, detail, action, outcome, needs);

    // Switch to a tab by its label (Waiting/Healed carry a trailing count).
    static void Show(IRenderedFragment cut, string tab)
        => cut.FindAll("button.tab").First(b => b.TextContent.Contains(tab, StringComparison.Ordinal)).Click();

    [Fact]
    public void The_health_counts_are_on_the_health_tab()
    {
        Wire(new FakeHealingView
        {
            Health = new HealthSummary(RecentFailures: 5, Healed: 3, NeedsYou: 2,
                                       LastHealed: DateTimeOffset.UtcNow, Autonomy: AutonomyLevel.AutoFixSafe),
        });

        var cut = RenderComponent<Wolverine>();   // Health is the default tab

        var counts = cut.FindAll(".stat-n").Select(e => e.TextContent.Trim()).ToList();
        Assert.Contains("3", counts);
        Assert.Contains("2", counts);
        Assert.Contains("5", counts);
    }

    [Fact]
    public void Counts_ride_the_tabs_so_nothing_hides()
    {
        Wire(new FakeHealingView
        {
            NeedsYou = [Item("a", "x", "y", outcome: "Needs you", needs: true)],
            Healed = [Item("b", "p", "q")],
        });

        var cut = RenderComponent<Wolverine>();

        // Without opening either tab, both counts are visible on the tab row.
        var waiting = cut.FindAll("button.tab").First(b => b.TextContent.Contains("Waiting", StringComparison.Ordinal));
        var healed = cut.FindAll("button.tab").First(b => b.TextContent.Contains("Healed", StringComparison.Ordinal));
        Assert.Contains("1", waiting.TextContent);
        Assert.Contains("1", healed.TextContent);
    }

    [Fact]
    public void The_current_autonomy_is_highlighted()
    {
        Wire(new FakeHealingView { Health = new HealthSummary(0, 0, 0, null, AutonomyLevel.SuggestOnly) });

        var cut = RenderComponent<Wolverine>();
        Show(cut, "Autonomy");

        Assert.Equal("Suggest", cut.Find("button.seg-on").TextContent.Trim());
    }

    [Fact]
    public void Choosing_an_autonomy_level_sets_it()
    {
        var view = Wire(new FakeHealingView { Health = new HealthSummary(0, 0, 0, null, AutonomyLevel.AutoFixSafe) });
        var cut = RenderComponent<Wolverine>();
        Show(cut, "Autonomy");

        cut.FindAll("button.seg-btn").First().Click();   // Off is first

        Assert.Equal(AutonomyLevel.Off, view.SetTo);
    }

    [Fact]
    public void The_boundary_is_shown_with_the_autonomy_control()
    {
        Wire(new FakeHealingView());
        var cut = RenderComponent<Wolverine>();
        Show(cut, "Autonomy");

        Assert.Contains("never touches code, money, or security", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Escalations_are_listed_and_can_be_marked_handled()
    {
        var view = Wire(new FakeHealingView
        {
            NeedsYou = [Item("ny1", "PayService: connection reset", "Needs a code change",
                             action: "add a null guard", outcome: "Needs you", needs: true)],
        });

        var cut = RenderComponent<Wolverine>();
        Show(cut, "Waiting");

        Assert.Contains("PayService", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("add a null guard", cut.Markup, StringComparison.Ordinal);

        cut.Find("button.strike").Click();
        Assert.Equal("ny1", view.HandledId);
    }

    [Fact]
    public void Healed_items_are_listed()
    {
        Wire(new FakeHealingView { Healed = [Item("h1", "app: out of memory", "auto-fixed — memory")] });

        var cut = RenderComponent<Wolverine>();
        Show(cut, "Healed");

        Assert.Contains("out of memory", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("auto-fixed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_lists_teach_rather_than_alarm()
    {
        Wire(new FakeHealingView());
        var cut = RenderComponent<Wolverine>();

        Show(cut, "Waiting");
        Assert.Contains("Nothing needs you", cut.Markup, StringComparison.Ordinal);

        Show(cut, "Healed");
        Assert.Contains("Nothing yet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_self_check_runs_a_failure_through_the_loop()
    {
        var view = Wire(new FakeHealingView());
        var cut = RenderComponent<Wolverine>();   // the check lives on the Health tab (default)

        cut.Find("button.check").Click();

        Assert.Equal(1, view.SelfChecks);
    }
}
