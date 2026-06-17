// Circle27ContractTests.cs
//
// (2.7.0) Contract tests for Server.Enterprise + Observability + Operator + SDD.

using System;
using System.Threading.Tasks;
using CircleAI.Inference.Server.Enterprise;
using CircleAI.Observability;
using CircleAI.Operator;
using CircleAI.SDD;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle27ContractTests
{
    // ── Server.Enterprise ────────────────────────────────────────────

    [Fact]
    public async Task NullTenantRouter_ReturnsNullNode()
        => Assert.Null(await NullTenantRouter.Instance.ChooseNodeAsync(
            new TenantContext("t1", null), "model"));

    [Fact]
    public async Task NullBatchScheduler_ReserveAndRelease()
    {
        var slot = await NullBatchScheduler.Instance.ReserveAsync("m", 100, TimeSpan.FromSeconds(1));
        Assert.Equal("m", slot.ModelId);
        await NullBatchScheduler.Instance.ReleaseAsync(slot);
    }

    [Fact]
    public async Task NullModelShardPlanner_ReturnsEmpty()
        => Assert.Empty(await NullModelShardPlanner.Instance.PlanAsync("m", 1_000_000));

    [Fact]
    public async Task NullCrossTierOffload_NeverOffloads()
    {
        var d = await NullCrossTierOffload.Instance.ShouldOffloadAsync("m", 100, ServerTier.SingleNode);
        Assert.False(d.ShouldOffload);
    }

    // ── Observability ────────────────────────────────────────────────

    [Fact]
    public async Task NullMetricSink_NoThrow()
        => await NullMetricSink.Instance.EmitAsync(new MetricSample("m", 1.0));

    [Fact]
    public async Task NullTraceSink_NoThrow()
        => await NullTraceSink.Instance.EmitAsync(new TraceSpan(
            "t", "s", null, "span", DateTimeOffset.UtcNow, TimeSpan.Zero));

    [Fact]
    public async Task NullDashboardPublisher_NoThrow()
        => await NullDashboardPublisher.Instance.PublishAsync(new DashboardSpec("d", "T", "{}"));

    // ── Operator ─────────────────────────────────────────────────────

    [Fact]
    public async Task NullModelOperator_NoOp()
    {
        await NullModelOperator.Instance.ApplyAsync(new ModelDeployment("m", "ns", 1, "phone"));
        Assert.Null(await NullModelOperator.Instance.GetStatusAsync("m", "ns"));
    }

    [Fact]
    public void NullDeploymentObserver_SubscribeReturnsDisposable()
    {
        using var sub = NullDeploymentObserver.Instance.Subscribe(_ => ValueTask.CompletedTask);
        Assert.NotNull(sub);
    }

    // ── SDD ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NullSpecificationStore_NoOp()
    {
        await NullSpecificationStore.Instance.UpsertAsync(new Specification("s", "T", "Body", null));
        Assert.Null(await NullSpecificationStore.Instance.GetAsync("s"));
        Assert.Empty(await NullSpecificationStore.Instance.ListAsync());
    }

    [Fact]
    public async Task NullSpecificationValidator_AlwaysInvalid()
    {
        var r = await NullSpecificationValidator.Instance.ValidateAsync(
            new Specification("s", "T", "Body", null));
        Assert.False(r.IsValid);
    }

    [Fact]
    public async Task NullSpecToScaffold_ReturnsEmptyFiles()
    {
        var s = await NullSpecToScaffold.Instance.ScaffoldAsync(
            new Specification("s", "T", "Body", null), "csharp");
        Assert.Empty(s.Files);
    }
}
