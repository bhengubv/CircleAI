// AgentMessageCorrelationTests.cs
//
// P3 — AgentMessage carries a CorrelationId so multi-hop agent
// exchanges stitch together in distributed traces.

using CircleAI.Agents.Peer;
using Xunit;

namespace CircleAI.Agents.Peer.Tests;

public sealed class AgentMessageCorrelationTests
{
    [Fact]
    public void Create_WithoutCorrelationId_GeneratesOne()
    {
        var msg = AgentMessage.Create(
            AgentMessageKind.Greet,
            fromUhid: "from",
            toUhid:   "to",
            contentType: "text/plain",
            payload:   new byte[] { 1 },
            signature: new byte[] { 2 });

        Assert.False(string.IsNullOrWhiteSpace(msg.CorrelationId));
        Assert.Equal(32, msg.CorrelationId!.Length); // "N" form Guid.
    }

    [Fact]
    public void Create_WithCorrelationId_KeepsIt()
    {
        var msg = AgentMessage.Create(
            AgentMessageKind.Greet,
            "f", "t", "text/plain",
            new byte[] { 1 },
            new byte[] { 2 },
            correlationId: "trace-abc");

        Assert.Equal("trace-abc", msg.CorrelationId);
    }

    [Fact]
    public void Create_GeneratesDistinctCorrelationIds()
    {
        var a = AgentMessage.Create(AgentMessageKind.Greet, "f", "t", "x", new byte[]{1}, new byte[]{2});
        var b = AgentMessage.Create(AgentMessageKind.Greet, "f", "t", "x", new byte[]{1}, new byte[]{2});
        Assert.NotEqual(a.CorrelationId, b.CorrelationId);
    }
}
