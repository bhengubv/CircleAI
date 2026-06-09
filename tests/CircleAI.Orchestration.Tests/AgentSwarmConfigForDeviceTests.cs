// AgentSwarmConfigForDeviceTests.cs
//
// Proves AgentSwarmConfig.ForDevice(probe) sizes MaxConcurrency by tier
// — the P1 device-inferred concurrency story for the orchestrator.

using CircleAI.Core;
using CircleAI.Orchestration;
using Xunit;

namespace CircleAI.Orchestration.Tests;

public sealed class AgentSwarmConfigForDeviceTests
{
    [Fact]
    public void ForDevice_UsesTierDerivedConcurrency()
    {
        var probe = DefaultDeviceContext.Instance.BuildProbe();
        var cfg   = AgentSwarmConfig.ForDevice(probe);

        var expected = DeviceTierDefaults.MaxConcurrency(probe.Classify(), probe.CpuCores);

        Assert.Equal(expected, cfg.MaxConcurrency);
        Assert.True(cfg.RequireReviewPassBeforeDeploy);
        Assert.True(cfg.RequireSecurityPassBeforeDeploy);
    }

    [Fact]
    public void Default_KeepsLegacyValue()
    {
        var cfg = AgentSwarmConfig.Default;
        Assert.Equal(4, cfg.MaxConcurrency);
    }
}
