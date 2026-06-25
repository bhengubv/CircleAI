// Circle33PacaRealtimeTests.cs
//
// (3.3.0) Tests for paca realtime hub.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Workflows;
using Xunit;

namespace CircleAI.Tests;

public class Circle33PacaRealtimeTests
{
    [Fact]
    public async Task Join_PermissionAllowed_AddsMember()
    {
        var hub = new PacaRealtimeHub(new RecordingBroadcaster());
        var joined = await hub.JoinAsync("u1", "project:p1");
        Assert.True(joined);
        Assert.Contains("u1", hub.Members("project:p1"));
    }

    [Fact]
    public async Task Join_PermissionDenied_Rejects()
    {
        var hub = new PacaRealtimeHub(new RecordingBroadcaster(),
            permission: (m, r, ct) => ValueTask.FromResult(false));
        var joined = await hub.JoinAsync("u1", "project:p1");
        Assert.False(joined);
        Assert.DoesNotContain("u1", hub.Members("project:p1"));
    }

    [Fact]
    public async Task Leave_RemovesMember()
    {
        var hub = new PacaRealtimeHub(new RecordingBroadcaster());
        await hub.JoinAsync("u1", "project:p1");
        hub.Leave("u1", "project:p1");
        Assert.Empty(hub.Members("project:p1"));
    }

    [Fact]
    public async Task PublishAsync_BroadcastsToProjectRoom()
    {
        var b = new RecordingBroadcaster();
        var hub = new PacaRealtimeHub(b);
        await hub.PublishAsync(new TaskUpdatedEvent("p1", DateTimeOffset.UtcNow, 42));
        Assert.Single(b.Sent);
        Assert.Equal("project:p1", b.Sent[0].Room);
    }

    [Fact]
    public async Task PublishToDocAsync_BroadcastsToDocRoom()
    {
        var b = new RecordingBroadcaster();
        var hub = new PacaRealtimeHub(b);
        await hub.PublishToDocAsync("d1", new DocCursorMoveEvent("p1", DateTimeOffset.UtcNow, "d1", "u1", 100));
        Assert.Equal("doc:d1", b.Sent[0].Room);
    }

    [Fact]
    public void QueryInvalidation_KeysFor_TaskUpdate()
    {
        var keys = QueryInvalidation.KeysFor(new TaskUpdatedEvent("p1", DateTimeOffset.UtcNow, 5));
        Assert.Contains("tasks/p1",   keys);
        Assert.Contains("task/p1/5",  keys);
    }

    [Fact]
    public void QueryInvalidation_KeysFor_ConversationStep()
    {
        var step = new ConversationStep("c1", 1, "agent", "{}", DateTimeOffset.UtcNow);
        var keys = QueryInvalidation.KeysFor(new ConversationStepEvent("p1", DateTimeOffset.UtcNow, "c1", step));
        Assert.Contains("conversation/c1",   keys);
        Assert.Contains("conversations/p1",  keys);
    }

    [Fact]
    public void QueryInvalidation_KeysFor_AgentActivity()
    {
        var keys = QueryInvalidation.KeysFor(new AgentActivityEvent("p1", DateTimeOffset.UtcNow, "agent1", "edited", "{}"));
        Assert.Contains("activity/p1",  keys);
        Assert.Contains("agent/agent1", keys);
    }

    private sealed class RecordingBroadcaster : IRealtimeBroadcaster
    {
        public List<(string Room, RealtimePacaEvent Ev)> Sent { get; } = new();
        public ValueTask BroadcastAsync(string room, RealtimePacaEvent ev, CancellationToken ct = default)
        {
            Sent.Add((room, ev));
            return ValueTask.CompletedTask;
        }
    }
}
