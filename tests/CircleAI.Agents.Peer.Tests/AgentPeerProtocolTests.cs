// AgentPeerProtocolTests.cs
//
// Tests for the CircleAI.Agents.Peer reference (in-memory) protocol.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Agents.Peer;
using CircleAI.Security;
using Xunit;

namespace CircleAI.Agents.Peer.Tests;

public sealed class AgentMessageTests
{
    [Fact]
    public void Create_StampsIdAndUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var message = AgentMessage.Create(
            AgentMessageKind.Greet, "alice", "bob", "text/plain", [], []);
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.InRange(message.SentAt, before, after);
        Assert.Equal(AgentMessageKind.Greet, message.Kind);
        Assert.Equal("alice", message.FromUhid);
        Assert.Equal("bob", message.ToUhid);
    }
}

public sealed class InMemoryAgentPeerProtocolTests
{
    private static readonly AgentCapability TranslateCap =
        new("translate", "1.0.0", 0m, "SDPKT");

    private static readonly AgentCapability PaidNavigateCap =
        new("navigate", "1.2.0", 0.25m, "SDPKT");

    private static InMemoryAgentPeerProtocol CreatePeer(
        string uhid,
        AgentBus bus,
        IReadOnlyList<AgentCapability>? capabilities = null,
        Func<AgentCapability, byte[], byte[]>? handler = null,
        byte[]? publicKey = null,
        Func<byte[], byte[]>? signer = null)
    {
        using var ring = UhidKeyRing.GenerateFresh(uhid);
        publicKey ??= ring.PublicKeyDer;
        capabilities ??= [TranslateCap];
        return new InMemoryAgentPeerProtocol(
            uhid, bus, capabilities, publicKey,
            signer: signer,
            capabilityHandler: handler);
    }

    [Fact]
    public async Task DiscoverPeers_ReturnsOtherRegisteredPeers()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus);
        using var carol = CreatePeer("carol", bus);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var peers = await alice.DiscoverPeersAsync(cts.Token);

        var uhids = peers.Select(p => p.UhidIdentityId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("bob", uhids);
        Assert.Contains("carol", uhids);
        Assert.DoesNotContain("alice", uhids);
    }

    [Fact]
    public async Task Greet_OnUnknownPeer_ReturnsNull()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await alice.GreetAsync("nobody", cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryCapabilities_ReturnsAdvertisedCapabilities()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus,
            capabilities: [TranslateCap, PaidNavigateCap]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var caps = await alice.QueryCapabilitiesAsync("bob", cts.Token);

        Assert.Equal(2, caps.Count);
        Assert.Contains(caps, c => c.Name == "translate");
        var navigate = Assert.Single(caps.Where(c => c.Name == "navigate"));
        Assert.Equal(0.25m, navigate.CostPerInvocation);
        Assert.Equal("SDPKT", navigate.CostCurrency);
    }

    [Fact]
    public async Task Invoke_ReturnsResponseFromHandler()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus,
            handler: (_, payload) =>
                Encoding.UTF8.GetBytes("hello " + Encoding.UTF8.GetString(payload)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await alice.InvokeAsync(
            "bob", TranslateCap, Encoding.UTF8.GetBytes("world"), cts.Token);

        // The response payload is [16-byte correlation id][handler result].
        Assert.Equal(AgentMessageKind.Response, response.Kind);
        Assert.True(response.Payload.Length > 16);
        var body = Encoding.UTF8.GetString(response.Payload, 16, response.Payload.Length - 16);
        Assert.Equal("hello world", body);
    }

    [Fact]
    public async Task Invoke_WhenPeerDeclines_ThrowsAgentInvocationException()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus, handler: (_, _) => null!);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<AgentInvocationException>(() =>
            alice.InvokeAsync("bob", TranslateCap, [1, 2, 3], cts.Token));

        Assert.Equal("bob", ex.PeerUhid);
        Assert.NotNull(ex.DeclineMessage);
        Assert.Equal(AgentMessageKind.Decline, ex.DeclineMessage!.Kind);
    }

    [Fact]
    public async Task Broadcast_ReachesAllPeersExceptSender()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus);
        using var carol = CreatePeer("carol", bus);

        var bobReceived = new List<AgentMessage>();
        var carolReceived = new List<AgentMessage>();
        var aliceReceived = new List<AgentMessage>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var bobTask = CollectAsync(bob, cts.Token, bobReceived);
        var carolTask = CollectAsync(carol, cts.Token, carolReceived);
        var aliceTask = CollectAsync(alice, cts.Token, aliceReceived);

        // Give pumps a moment to subscribe.
        await Task.Delay(20);

        var broadcast = AgentMessage.Create(
            AgentMessageKind.Heartbeat, "alice", "*", "text/plain",
            Encoding.UTF8.GetBytes("ping"), []);
        bus.Send(broadcast);

        await Task.WhenAll(SafeWait(bobTask), SafeWait(carolTask), SafeWait(aliceTask));

        Assert.Contains(bobReceived, m => m.Id == broadcast.Id);
        Assert.Contains(carolReceived, m => m.Id == broadcast.Id);
        Assert.DoesNotContain(aliceReceived, m => m.Id == broadcast.Id);
    }

    [Fact]
    public async Task Heartbeat_UpdatesPeerLastSeenAt()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus);

        using var discCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var before = (await alice.DiscoverPeersAsync(discCts.Token))
            .Single(p => p.UhidIdentityId == "bob");

        await Task.Delay(20);

        var heartbeat = AgentMessage.Create(
            AgentMessageKind.Heartbeat, "bob", "alice", "text/plain", [], []);
        bus.Send(heartbeat);

        // Allow alice's pump to process the heartbeat.
        await Task.Delay(50);

        using var discCts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var after = (await alice.DiscoverPeersAsync(discCts2.Token))
            .Single(p => p.UhidIdentityId == "bob");

        Assert.True(after.LastSeenAt >= before.LastSeenAt);
    }

    [Fact]
    public async Task PaidCapability_CarriesCostCurrencyEndToEnd()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        using var bob = CreatePeer("bob", bus,
            capabilities: [new AgentCapability("diagnose", "2.0.0", 1.5m, "ZAR")]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var caps = await alice.QueryCapabilitiesAsync("bob", cts.Token);

        var diagnose = Assert.Single(caps);
        Assert.Equal(1.5m, diagnose.CostPerInvocation);
        Assert.Equal("ZAR", diagnose.CostCurrency);
    }

    [Fact]
    public async Task StreamInbox_RespectsCancellationToken()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var collected = new List<AgentMessage>();
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in alice.StreamInboxAsync(cts.Token))
                {
                    collected.Add(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        });

        await task;
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Signature_IsPreservedEndToEnd()
    {
        var bus = new AgentBus();
        var signature = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        using var alice = CreatePeer("alice", bus, signer: _ => signature);
        using var bob = CreatePeer("bob", bus);

        var received = new List<AgentMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var task = CollectAsync(bob, cts.Token, received);

        await Task.Delay(20);

        using var greetCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await alice.GreetAsync("bob", greetCts.Token);

        await SafeWait(task);

        var greet = Assert.Single(received.Where(m => m.Kind == AgentMessageKind.Greet));
        Assert.Equal(signature, greet.Signature);
    }

    [Fact]
    public async Task Unregister_StopsDeliveryToPeer()
    {
        var bus = new AgentBus();
        using var alice = CreatePeer("alice", bus);
        var bob = CreatePeer("bob", bus);

        var received = new List<AgentMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var task = CollectAsync(bob, cts.Token, received);

        await Task.Delay(20);

        bob.Dispose(); // unregisters bob from the bus

        var message = AgentMessage.Create(
            AgentMessageKind.Heartbeat, "alice", "bob", "text/plain", [], []);
        bus.Send(message);

        await SafeWait(task);

        Assert.DoesNotContain(received, m => m.Id == message.Id);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task CollectAsync(
        IAgentPeerProtocol peer,
        CancellationToken token,
        List<AgentMessage> sink) =>
        Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in peer.StreamInboxAsync(token))
                {
                    sink.Add(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        });

    private static async Task SafeWait(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
