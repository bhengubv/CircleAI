// WolverineTests.cs
//
// The self-healing dashboard. It reads IHealingView; these drive it with a fake and
// check the four things a person comes to it for: the health glance, the autonomy
// dial (shown and settable), the escalations they can clear, and the healed feed —
// each with an empty state that teaches rather than alarms.

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

    [Fact]
    public void The_health_counts_are_on_screen()
    {
        Wire(new FakeHealingView
        {
            Health = new HealthSummary(RecentFailures: 5, Healed: 3, NeedsYou: 2,
                                       LastHealed: DateTimeOffset.UtcNow, Autonomy: AutonomyLevel.AutoFixSafe),
        });

        var cut = RenderComponent<Wolverine>();

        var counts = cut.FindAll(".stat-n").Select(e => e.TextContent.Trim()).ToList();
        Assert.Contains("3", counts);
        Assert.Contains("2", counts);
        Assert.Contains("5", counts);
    }

    [Fact]
    public void The_current_autonomy_is_highlighted()
    {
        Wire(new FakeHealingView { Health = new HealthSummary(0, 0, 0, null, AutonomyLevel.SuggestOnly) });

        var cut = RenderComponent<Wolverine>();

        Assert.Equal("Suggest", cut.Find("button.seg-on").TextContent.Trim());
    }

    [Fact]
    public void Choosing_an_autonomy_level_sets_it()
    {
        var view = Wire(new FakeHealingView { Health = new HealthSummary(0, 0, 0, null, AutonomyLevel.AutoFixSafe) });
        var cut = RenderComponent<Wolverine>();

        cut.FindAll("button.seg-btn").First().Click();   // Off is first

        Assert.Equal(AutonomyLevel.Off, view.SetTo);
    }

    [Fact]
    public void The_boundary_is_always_shown()
    {
        Wire(new FakeHealingView());
        var cut = RenderComponent<Wolverine>();
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

        Assert.Contains("out of memory", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("auto-fixed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_lists_teach_rather_than_alarm()
    {
        Wire(new FakeHealingView());
        var cut = RenderComponent<Wolverine>();

        Assert.Contains("Nothing needs you", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nothing yet", cut.Markup, StringComparison.Ordinal);
    }
}
