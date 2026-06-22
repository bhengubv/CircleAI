// Circle32MultiplayerTests.cs
//
// (3.2.0) Tests for CircleAI.Hosting.Multiplayer. The SignalR Hub itself
// needs a TestServer to exercise — out of scope for this round. We test
// what we can reach directly: GuestPeerIdentity defaults, the static
// CurrentRev / ResetStateForTesting helpers (which Hub callers depend
// on), and the ColourFor stability + uniqueness for distinct ids.

using System;
using System.Reflection;
using CircleAI.Hosting.Multiplayer;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32MultiplayerTests
{
    [Fact]
    public void Guest_DefaultIdentity_HasNonEmptyFields()
    {
        var g = new GuestPeerIdentity();
        Assert.False(string.IsNullOrEmpty(g.PeerId));
        Assert.Equal("Guest", g.DisplayName);
    }

    [Fact]
    public void Guest_CustomIdentity_RoundTrips()
    {
        var g = new GuestPeerIdentity("peer-123", "Lerato");
        Assert.Equal("peer-123", g.PeerId);
        Assert.Equal("Lerato",   g.DisplayName);
    }

    [Fact]
    public void Guest_TwoDefaults_GetDifferentPeerIds()
    {
        var a = new GuestPeerIdentity();
        var b = new GuestPeerIdentity();
        Assert.NotEqual(a.PeerId, b.PeerId);
    }

    [Fact]
    public void Hub_StaticRev_StartsAtZero()
    {
        MultiplayerHub.ResetStateForTesting();
        Assert.Equal(0L, MultiplayerHub.CurrentRev("never-touched-doc"));
    }

    [Fact]
    public void Hub_StaticPeers_StartsEmpty()
    {
        MultiplayerHub.ResetStateForTesting();
        Assert.Empty(MultiplayerHub.Peers("any-doc"));
    }

    // ── ColourFor (deterministic hash → HSL) ──────────────────────────

    [Fact]
    public void Colour_SameId_AlwaysSame()
    {
        var a = InvokeColourFor("abc");
        var b = InvokeColourFor("abc");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Colour_DifferentIds_DifferentResults()
    {
        var a = InvokeColourFor("alice");
        var b = InvokeColourFor("bob");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Colour_EmptyId_ReturnsDefault()
    {
        Assert.Equal("#5a4fcf", InvokeColourFor(""));
    }

    [Fact]
    public void Colour_FormatsAsHsl()
    {
        var c = InvokeColourFor("non-empty-id");
        Assert.StartsWith("hsl(", c);
        Assert.EndsWith(")",  c);
        Assert.Contains("70%", c);
        Assert.Contains("55%", c);
    }

    private static string InvokeColourFor(string peerId)
    {
        var method = typeof(MultiplayerHub).GetMethod(
            "ColourFor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { peerId })!;
    }
}
