// Circle33CapabilityRegistryTests.cs
//
// (3.3.0) Tests for the external capability registry.

using System.Linq;
using CircleAI.Companion;
using Xunit;

namespace CircleAI.Tests;

public class Circle33CapabilityRegistryTests
{
    [Fact]
    public void Registry_HasThirtyEntries()
    {
        Assert.Equal(30, ExternalCapabilityRegistry.All.Count);
    }

    [Fact]
    public void Registry_HasUniqueIds()
    {
        var ids = ExternalCapabilityRegistry.All.Select(c => c.Id).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
    }

    [Fact]
    public void Registry_EveryEntry_HasValueBullets()
    {
        Assert.All(ExternalCapabilityRegistry.All,
            c => Assert.NotEmpty(c.ValueBullets));
    }

    [Fact]
    public void Find_KnownId_Succeeds()
    {
        var c = ExternalCapabilityRegistry.Find("claude-mem");
        Assert.NotNull(c);
        Assert.Equal("CircleAI.Memory", c!.TargetPackage);
    }

    [Fact]
    public void Find_Unknown_ReturnsNull()
    {
        Assert.Null(ExternalCapabilityRegistry.Find("ghost"));
    }

    [Fact]
    public void ByPackage_GroupsCorrectly()
    {
        var inputs = ExternalCapabilityRegistry.ByPackage("CircleAI.Inputs");
        Assert.True(inputs.Count >= 2);
        Assert.Contains(inputs, c => c.Id == "Agent-Reach");
        Assert.Contains(inputs, c => c.Id == "last30days");
    }

    [Fact]
    public void Registry_StrategiesAreKnown()
    {
        Assert.All(ExternalCapabilityRegistry.All,
            c => Assert.Contains(c.Strategy, new[] { "vendor", "pattern-port", "wrap" }));
    }
}
