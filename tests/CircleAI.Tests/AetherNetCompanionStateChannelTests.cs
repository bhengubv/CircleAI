// AetherNetCompanionStateChannelTests.cs
//
// Item 3 audit follow-up: AetherNetCompanionStateChannel verifies that the
// SyncEnvelope round-trips correctly through a fake AetherNet messaging
// service. The real messaging runtime is mocked — we only need to validate
// the channel's protocol behaviour:
//   • SendAsync emits ONE MeshMessage per configured peer
//   • The MessageType filter ignores unrelated traffic
//   • Self-loopback is dropped
//   • Subscribed handlers fire on inbound; unsubscribed handlers don't
//   • Dispose unhooks the MessageReceived subscription

using System.Text;
using System.Text.Json;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Protocol;
using CircleAI.AetherNet;
using CircleAI.Memory.Sync;
using Xunit;

namespace CircleAI.Tests;

public sealed class AetherNetCompanionStateChannelTests
{
    // ── Fake IMessagingService — only the bits the channel touches ────────

    private sealed class FakeMessagingService : IMessagingService
    {
        public List<(MeshMessage Message, byte[] Plaintext)> Sent { get; } = new();
        public event EventHandler<MeshMessage>? MessageReceived;
        public event EventHandler<DeliveryReceipt>? DeliveryConfirmed;
        public event EventHandler<string>? SessionRequired;

        public Task<bool> SendAsync(MeshMessage message, byte[] plaintext, CancellationToken cancellationToken)
        {
            Sent.Add((message, plaintext));
            return Task.FromResult(true);
        }

        public Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> ProcessOutboxAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<MeshMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MeshMessage>>(Array.Empty<MeshMessage>());
        public Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MeshMessage>>(Array.Empty<MeshMessage>());

        public void RaiseReceived(MeshMessage m) => MessageReceived?.Invoke(this, m);

        // Suppress unused-event warnings.
        public void Bump()
        {
            DeliveryConfirmed?.Invoke(this, null!);
            SessionRequired?.Invoke(this, "");
        }
    }

    private static SyncEnvelope MakeAnnounce(string from) => new(
        Kind: SyncEnvelopeKind.Announce,
        FromNodeId: from,
        StateVector: new[] { new StateVectorEntry("PersonaState", 100L) },
        Requests: null,
        Entries: null);

    // ══════════════════════════════════════════════════════════════════════
    // SendAsync
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SendAsync_NoPeers_IsNoOp()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", Array.Empty<string>());
        await ch.SendAsync(MakeAnnounce("uhid-A"));
        Assert.Empty(msg.Sent);
    }

    [Fact]
    public async Task SendAsync_OnePeer_EmitsOneMeshMessage()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        await ch.SendAsync(MakeAnnounce("uhid-A"));

        Assert.Single(msg.Sent);
        var (sent, plaintext) = msg.Sent[0];
        Assert.Equal("uhid-A", sent.SenderUhid);
        Assert.Equal("uhid-B", sent.RecipientUhid);
        Assert.Equal(AetherNetCompanionStateChannel.SyncMessageType, sent.MessageType);
        Assert.NotEmpty(plaintext);

        // JSON payload must round-trip.
        var json = Encoding.UTF8.GetString(plaintext);
        var envelope = JsonSerializer.Deserialize<SyncEnvelope>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(envelope);
        Assert.Equal(SyncEnvelopeKind.Announce, envelope!.Kind);
        Assert.Equal("uhid-A", envelope.FromNodeId);
    }

    [Fact]
    public async Task SendAsync_ManyPeers_OneMessagePerPeer()
    {
        var msg = new FakeMessagingService();
        var peers = new[] { "uhid-B", "uhid-C", "uhid-D" };
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", peers);
        await ch.SendAsync(MakeAnnounce("uhid-A"));

        Assert.Equal(3, msg.Sent.Count);
        Assert.Equal(peers, msg.Sent.Select(s => s.Message.RecipientUhid).ToArray());
    }

    [Fact]
    public async Task SendAsync_DeduplicatesPeers()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(
            msg, "uhid-A",
            new[] { "uhid-B", "uhid-B", "uhid-C", "" });
        await ch.SendAsync(MakeAnnounce("uhid-A"));
        Assert.Equal(2, msg.Sent.Count);
        Assert.Contains(msg.Sent, s => s.Message.RecipientUhid == "uhid-B");
        Assert.Contains(msg.Sent, s => s.Message.RecipientUhid == "uhid-C");
    }

    [Fact]
    public async Task SendAsync_AfterDispose_Throws()
    {
        var msg = new FakeMessagingService();
        var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        ch.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ch.SendAsync(MakeAnnounce("uhid-A")));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Inbound
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Inbound_DeliveredEnvelope_FiresHandler()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });

        SyncEnvelope? received = null;
        ch.Subscribe((e, _) => { received = e; return Task.CompletedTask; });

        var envelope = MakeAnnounce("uhid-B");
        msg.RaiseReceived(new MeshMessage
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-B",
            RecipientUhid = "uhid-A",
            MessageType = AetherNetCompanionStateChannel.SyncMessageType,
            EncryptedContent = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        });

        await Task.Delay(50); // event invocation is async void
        Assert.NotNull(received);
        Assert.Equal(SyncEnvelopeKind.Announce, received!.Kind);
        Assert.Equal("uhid-B", received.FromNodeId);
    }

    [Fact]
    public async Task Inbound_UnrelatedMessageType_Ignored()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        bool fired = false;
        ch.Subscribe((_, _) => { fired = true; return Task.CompletedTask; });

        msg.RaiseReceived(new MeshMessage
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-B",
            RecipientUhid = "uhid-A",
            MessageType = "some.other.type",
            EncryptedContent = Encoding.UTF8.GetBytes("{}"),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        });

        await Task.Delay(50);
        Assert.False(fired);
    }

    [Fact]
    public async Task Inbound_SelfLoopback_Ignored()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        bool fired = false;
        ch.Subscribe((_, _) => { fired = true; return Task.CompletedTask; });

        msg.RaiseReceived(new MeshMessage
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-A", // self
            RecipientUhid = "uhid-A",
            MessageType = AetherNetCompanionStateChannel.SyncMessageType,
            EncryptedContent = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(MakeAnnounce("uhid-A"))),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        });

        await Task.Delay(50);
        Assert.False(fired);
    }

    [Fact]
    public async Task Inbound_MalformedJson_DoesNotThrow()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        ch.Subscribe((_, _) => Task.CompletedTask);

        msg.RaiseReceived(new MeshMessage
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-B",
            RecipientUhid = "uhid-A",
            MessageType = AetherNetCompanionStateChannel.SyncMessageType,
            EncryptedContent = Encoding.UTF8.GetBytes("not json {"),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        });
        await Task.Delay(50);
        // no exception
    }

    [Fact]
    public async Task Subscribe_DisposeHandle_UnregistersHandler()
    {
        var msg = new FakeMessagingService();
        using var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });

        int callCount = 0;
        var sub = ch.Subscribe((_, _) => { callCount++; return Task.CompletedTask; });

        MeshMessage Build() => new()
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-B",
            RecipientUhid = "uhid-A",
            MessageType = AetherNetCompanionStateChannel.SyncMessageType,
            EncryptedContent = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(MakeAnnounce("uhid-B"))),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        };

        msg.RaiseReceived(Build()); await Task.Delay(50);
        Assert.Equal(1, callCount);

        sub.Dispose();
        msg.RaiseReceived(Build()); await Task.Delay(50);
        Assert.Equal(1, callCount); // not incremented
    }

    [Fact]
    public async Task Dispose_UnhooksMessageReceived()
    {
        var msg = new FakeMessagingService();
        var ch = new AetherNetCompanionStateChannel(msg, "uhid-A", new[] { "uhid-B" });
        int callCount = 0;
        ch.Subscribe((_, _) => { callCount++; return Task.CompletedTask; });
        ch.Dispose();

        msg.RaiseReceived(new MeshMessage
        {
            Id = Guid.NewGuid(),
            SenderUhid = "uhid-B",
            RecipientUhid = "uhid-A",
            MessageType = AetherNetCompanionStateChannel.SyncMessageType,
            EncryptedContent = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(MakeAnnounce("uhid-B"))),
            Status = MessageStatus.Delivered,
            CreatedAt = DateTime.UtcNow,
            Priority = 5,
        });
        await Task.Delay(50);
        Assert.Equal(0, callCount);
    }
}
