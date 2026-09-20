// FailureAnalystDelegateTests.cs
//
// The delegate ctor — the seam that lets a head back the analyst with a brain it
// already runs (rather than a second IAIService / a second model). Proves the
// delegate is called with the authoritative system prompt + the failure, that its
// reply is parsed the same way, and that it still degrades safely.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.SelfHealing;
using Xunit;

namespace CircleAI.Tests;

public sealed class FailureAnalystDelegateTests
{
    [Fact]
    public async Task Delegate_receives_the_system_instruction_and_the_failure()
    {
        string? gotSystem = null, gotUser = null;
        var analyst = new FailureAnalyst((system, user, ct) =>
        {
            gotSystem = system;
            gotUser = user;
            return Task.FromResult("{\"kind\":\"quick-fix\",\"category\":\"cache\",\"summary\":\"retry it\"}");
        });

        var verdict = await analyst.AnalyseAsync(new FailureContext("connection reset", Source: "PayService"));

        Assert.NotNull(gotSystem);
        Assert.Contains("failure analyst", gotSystem!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection reset", gotUser!);
        Assert.Contains("PayService", gotUser!);
        Assert.Equal(HealingKind.QuickFix, verdict.Kind);
        Assert.Equal("cache", verdict.Category);
    }

    [Fact]
    public async Task Delegate_ctor_parses_a_patch_verdict()
    {
        var analyst = new FailureAnalyst((_, _, _) =>
            Task.FromResult("{\"kind\":\"patch\",\"category\":\"null-ref\",\"summary\":\"guard it\",\"action\":\"add null check\"}"));

        var verdict = await analyst.AnalyseAsync(new FailureContext("NRE"));

        Assert.Equal(HealingKind.Patch, verdict.Kind);
        Assert.Equal("add null check", verdict.RecommendedAction);
    }

    [Fact]
    public async Task An_unparseable_reply_degrades_to_needs_a_human()
    {
        var analyst = new FailureAnalyst((_, _, _) => Task.FromResult("the model rambled, no json here"));

        var verdict = await analyst.AnalyseAsync(new FailureContext("boom"));

        Assert.Equal(HealingKind.Defer, verdict.Kind);   // NeedsAHuman
    }

    [Fact]
    public async Task A_throwing_brain_degrades_to_needs_a_human_never_throws()
    {
        var analyst = new FailureAnalyst((_, _, _) =>
            Task.FromException<string>(new InvalidOperationException("brain down")));

        var verdict = await analyst.AnalyseAsync(new FailureContext("boom"));

        Assert.Equal(HealingKind.Defer, verdict.Kind);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_becoming_a_verdict()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var analyst = new FailureAnalyst((_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyst.AnalyseAsync(new FailureContext("boom"), cts.Token));
    }

    [Fact]
    public void Null_delegate_is_rejected()
        => Assert.Throws<ArgumentNullException>(() =>
            new FailureAnalyst((Func<string, string, CancellationToken, Task<string>>)null!));
}
