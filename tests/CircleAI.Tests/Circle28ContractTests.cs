// Circle28ContractTests.cs
//
// (2.8.0) Contract tests for Banking / Markets / Pipelines / Workflows /
// Visualization / Collaboration / CRM.

using System;
using System.Threading.Tasks;
using CircleAI.Banking;
using CircleAI.Collaboration;
using CircleAI.CRM;
using CircleAI.Markets;
using CircleAI.Pipelines;
using CircleAI.Visualization;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle28ContractTests
{
    // ── Banking ──────────────────────────────────────────────────────

    [Fact]
    public async Task NullAccountReader_ReturnsNull()
    {
        Assert.Null(await NullAccountReader.Instance.GetAccountAsync("a"));
        Assert.Empty(await NullAccountReader.Instance.ListForOwnerAsync("o"));
    }

    [Fact]
    public async Task NullPaymentProcessor_RejectsByDefault()
    {
        var r = await NullPaymentProcessor.Instance.ProcessAsync(
            new PaymentRequest("a1", "a2", 100m, "ZAR", "test"));
        Assert.False(r.Accepted);
    }

    // ── Markets ──────────────────────────────────────────────────────

    [Fact]
    public async Task NullOrderRouter_RejectsByDefault()
    {
        var r = await NullOrderRouter.Instance.SubmitAsync(
            new OrderRequest("X", OrderSide.Buy, OrderType.Market, 1m, null));
        Assert.False(r.Accepted);
    }

    [Fact]
    public async Task NullMarketDataFeed_QuoteIsNull()
        => Assert.Null(await NullMarketDataFeed.Instance.GetQuoteAsync("X"));

    // ── Pipelines ────────────────────────────────────────────────────

    [Fact]
    public async Task NullPipelineExecutor_ReturnsFailedRun()
    {
        var r = await NullPipelineExecutor.Instance.RunAsync("p1");
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task NullDatabaseQueryTool_ReturnsEmptyResult()
    {
        var r = await NullDatabaseQueryTool.Instance.QueryAsync("select 1");
        Assert.Equal(0, r.RowCount);
    }

    // ── Workflows ────────────────────────────────────────────────────

    [Fact]
    public async Task NullWorkflowRunner_StartReturnsFailed()
    {
        var e = await NullWorkflowRunner.Instance.StartAsync("d1");
        Assert.Equal(WorkflowPhase.Failed, e.Phase);
    }

    [Fact]
    public async Task NullWorkflowState_LoadIsNull()
        => Assert.Null(await NullWorkflowState.Instance.LoadAsync("r", "s"));

    // ── Visualization ────────────────────────────────────────────────

    [Fact]
    public async Task NullDashboardStore_ListIsEmpty()
        => Assert.Empty(await NullDashboardDefinitionStore.Instance.ListAsync());

    [Fact]
    public async Task NullApiDocBuilder_BuildsEmptyDoc()
    {
        var d = await NullApiDocBuilder.Instance.BuildAsync("{}");
        Assert.Equal("{}", d.OpenApiJson);
    }

    // ── Collaboration ────────────────────────────────────────────────

    [Fact]
    public async Task NullChannelStore_ReturnsEmpty()
    {
        Assert.Null(await NullChannelStore.Instance.GetAsync("c"));
        Assert.Empty(await NullChannelStore.Instance.ListForTeamAsync("t"));
    }

    [Fact]
    public async Task NullMessageStore_ReadEmpty()
        => Assert.Empty(await NullMessageStore.Instance.ReadAsync("c"));

    // ── CRM ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NullContactStore_NoData()
    {
        await NullContactStore.Instance.UpsertAsync(new Contact("c", "Name", null, null, null));
        Assert.Null(await NullContactStore.Instance.GetAsync("c"));
        Assert.Empty(await NullContactStore.Instance.SearchAsync("name"));
    }

    [Fact]
    public async Task NullActivityLog_ReadEmpty()
        => Assert.Empty(await NullActivityLog.Instance.ReadForContactAsync("c"));
}
