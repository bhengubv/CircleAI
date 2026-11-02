// FailureAnalystRemediesTests.cs
//
// For a fault no rule knows, the brain chooses from the list of remedies that exist and never
// invents an action (S6): the list reaches the prompt, an answer on it is kept, and an answer
// off it is "needs a human" - whatever the model was sure of.

using CircleAI.Hosting.SelfHealing;
using Xunit;

namespace CircleAI.Tests;

public sealed class FailureAnalystRemediesTests
{
    private static FailureAnalyst Replying(string json, Action<string>? system = null) =>
        new((s, _, _) =>
        {
            system?.Invoke(s);
            return Task.FromResult(json);
        });

    private static FailureContext Offered(params string[] remedies) =>
        new("the voice cache is stale", Source: "Voice") { Remedies = remedies };

    [Fact]
    public async Task The_brain_is_handed_the_remedies_and_told_to_choose_from_them()
    {
        string? prompt = null;
        var verdict = await Replying("{\"kind\":\"quick-fix\",\"category\":\"Fewer-Threads\",\"summary\":\"x\"}", s => prompt = s)
            .AnalyseAsync(Offered(Remedies.FewerThreads, Remedies.Cache));

        Assert.Contains("\"fewer-threads\"", prompt);
        Assert.Contains("\"cache\"", prompt);
        Assert.Contains("MUST be exactly one of", prompt);
        Assert.Equal(HealingKind.QuickFix, verdict.Kind);
        Assert.Equal(Remedies.FewerThreads, verdict.Category);   // the list's own spelling
    }

    [Fact]
    public async Task An_action_the_brain_invented_is_not_a_fix()
    {
        var verdict = await Replying("{\"kind\":\"quick-fix\",\"category\":\"reboot-the-phone\",\"summary\":\"x\",\"confidence\":0.99}")
            .AnalyseAsync(Offered(Remedies.FewerThreads, Remedies.Cache));

        Assert.Equal(HealingKind.Defer, verdict.Kind);
        Assert.Equal("unclassified", verdict.Category);
    }

    [Fact]
    public async Task A_patch_or_a_defer_is_not_held_to_the_list()
    {
        var verdict = await Replying("{\"kind\":\"patch\",\"category\":\"null-reference\",\"summary\":\"add a guard\"}")
            .AnalyseAsync(Offered(Remedies.Cache));

        Assert.Equal(HealingKind.Patch, verdict.Kind);
        Assert.Equal("null-reference", verdict.Category);
    }

    [Fact]
    public async Task With_no_list_the_analyst_is_what_it_always_was()
    {
        string? prompt = null;
        var verdict = await Replying("{\"kind\":\"quick-fix\",\"category\":\"transient-network\",\"summary\":\"x\"}", s => prompt = s)
            .AnalyseAsync(new FailureContext("connection reset"));

        Assert.DoesNotContain("MUST be exactly one of", prompt);
        Assert.Equal("transient-network", verdict.Category);
        Assert.Equal(HealingKind.QuickFix, verdict.Kind);
    }
}
