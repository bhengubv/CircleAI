// Circle29ContractTests.cs
//
// (2.9.0) Contract tests for BuildFarm / DepBot / DocAnalytics /
// Testing / Distribution / Media / WindowsAutomation / MicroAgents.

using System;
using System.Threading.Tasks;
using CircleAI.BuildFarm;
using CircleAI.DepBot;
using CircleAI.Distribution;
using CircleAI.DocAnalytics;
using CircleAI.MediaHub;
using CircleAI.MicroAgents;
using CircleAI.Testing;
using CircleAI.WindowsAutomation;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle29ContractTests
{
    [Fact]
    public async Task NullBuildAgentPool_NoneAvailable()
        => Assert.Null(await NullBuildAgentPool.Instance.AcquireAsync(BuildAgentKind.Linux));

    [Fact]
    public async Task NullBuildJobRunner_StartsFailed()
    {
        var j = await NullBuildJobRunner.Instance.StartAsync("a", "r", "main");
        Assert.Equal(BuildJobPhase.Failed, j.Phase);
    }

    [Fact]
    public async Task NullDependencyAnalyzer_NoFindings()
        => Assert.Empty(await NullDependencyAnalyzer.Instance.ScanAsync("/"));

    [Fact]
    public async Task NullDependencyUpdater_NoUpdates()
        => Assert.Empty(await NullDependencyUpdater.Instance.ProposeUpdatesAsync("/"));

    [Fact]
    public async Task NullDocumentTracker_NoViews()
        => Assert.Empty(await NullDocumentTracker.Instance.ListViewsAsync("d"));

    [Fact]
    public async Task NullDocumentInsights_None()
        => Assert.Null(await NullDocumentInsights.Instance.ComputeAsync("d"));

    [Fact]
    public async Task NullSnapshotComparer_NeverEqual()
    {
        var r = await NullSnapshotComparer.Instance.CompareAsync("t", "x");
        Assert.False(r.Equal);
    }

    [Fact]
    public async Task NullGoldenStore_ReadNull()
        => Assert.Null(await NullGoldenStore.Instance.ReadAsync("t"));

    [Fact]
    public async Task NullFileSync_HasReturnsFalse()
        => Assert.False(await NullFileSync.Instance.HasAsync("abc"));

    [Fact]
    public async Task NullPeerAdvertiser_NoPeers()
        => Assert.Empty(await NullPeerAdvertiser.Instance.DiscoverAsync());

    [Fact]
    public async Task NullMediaLibrary_NoItems()
        => Assert.Empty(await NullMediaLibrary.Instance.SearchAsync("x"));

    [Fact]
    public async Task NullUiAutomationDriver_EmptySnapshot()
        => Assert.Empty(await NullUiAutomationDriver.Instance.SnapshotAsync());

    [Fact]
    public async Task NullMicroAgent_ReturnsEmpty()
    {
        var a = new NullMicroAgent();
        var r = await a.InvokeAsync("hi");
        Assert.Equal("", r.Output);
    }

    [Fact]
    public async Task InMemoryMicroAgentHost_RegisterAndInvoke()
    {
        var host = new InMemoryMicroAgentHost();
        host.Register(new NullMicroAgent());
        Assert.Single(host.List());
        var r = await host.InvokeAsync("null", "hi");
        Assert.NotNull(r);
        Assert.Equal("null", r!.AgentId);
    }
}
