// ──────────────────────────────────────────────────────────────────────────
// AetherNetCompanionStateChannel
//
// Item 3 of the audit follow-up — the production transport for
// CircleAI.Memory.Sync.CompanionStateSyncEngine. Marshals SyncEnvelopes
// onto AetherNet.Messaging's MeshMessage pipeline.
//
//   ICompanionStateChannel.SendAsync(envelope)
//        ↓ JSON-serialize
//        ↓ wrap in MeshMessage with MessageType = "circleai.sync.v1"
//        ↓ for each peer UHID configured at construction:
//        ↓ IMessagingService.SendAsync(meshMessage, plaintext)
//
//   IMessagingService.MessageReceived
//        ↓ filter MessageType == "circleai.sync.v1"
//        ↓ skip self-loopback (SenderUhid == LocalNodeId)
//        ↓ JSON-deserialize MeshMessage.EncryptedContent
//        ↓ fire every Subscribe handler
//
// The plaintext crossing the bus is JSON. AetherNet.Messaging applies the
// usual Signal-Protocol E2E layer on top — this channel does not need to
// know about encryption.
// ──────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using CircleAI.Memory.Sync;

namespace CircleAI.AetherNet;

/// <summary>
/// AetherNet-backed implementation of <see cref="ICompanionStateChannel"/>.
/// </summary>
public sealed class AetherNetCompanionStateChannel : ICompanionStateChannel, IDisposable
{
    /// <summary>MessageType used to distinguish CircleAI sync envelopes from other mesh traffic.</summary>
    public const string SyncMessageType = "circleai.sync.v1";

    private readonly IMessagingService _messaging;
    private readonly IReadOnlyList<string> _peerUhids;
    private readonly List<Func<SyncEnvelope, CancellationToken, Task>> _handlers = new();
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Constructs the channel. Subscribes immediately to
    /// <see cref="IMessagingService.MessageReceived"/>.
    /// </summary>
    /// <param name="messaging">Live AetherNet messaging service.</param>
    /// <param name="localUhid">This node's mesh UHID.</param>
    /// <param name="peerUhids">
    /// UHIDs the channel should broadcast to. The sync engine converges via
    /// announce/request/push so the list does NOT need to include every peer
    /// on the mesh — only the user's own paired devices. An empty list is
    /// allowed; SendAsync is then a no-op (useful for single-device boot).
    /// </param>
    public AetherNetCompanionStateChannel(
        IMessagingService messaging,
        string localUhid,
        IEnumerable<string> peerUhids)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        if (string.IsNullOrWhiteSpace(localUhid))
            throw new ArgumentException("localUhid is required.", nameof(localUhid));
        ArgumentNullException.ThrowIfNull(peerUhids);

        _messaging = messaging;
        LocalNodeId = localUhid;
        _peerUhids = peerUhids.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        _messaging.MessageReceived += OnInbound;
    }

    /// <inheritdoc/>
    public string LocalNodeId { get; }

    /// <inheritdoc/>
    public async Task SendAsync(SyncEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_disposed) throw new ObjectDisposedException(nameof(AetherNetCompanionStateChannel));
        if (_peerUhids.Count == 0) return; // no peers configured

        var json = JsonSerializer.Serialize(envelope, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);

        foreach (var peer in _peerUhids)
        {
            ct.ThrowIfCancellationRequested();
            var meshMessage = new MeshMessage
            {
                Id = Guid.NewGuid(),
                SenderUhid = LocalNodeId,
                RecipientUhid = peer,
                MessageType = SyncMessageType,
                Priority = 5,
                EncryptedContent = Array.Empty<byte>(), // service encrypts the plaintext arg
                Status = MessageStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            await _messaging.SendAsync(meshMessage, plaintext, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<SyncEnvelope, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_disposed) throw new ObjectDisposedException(nameof(AetherNetCompanionStateChannel));
        lock (_lock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    private async void OnInbound(object? sender, MeshMessage msg)
    {
        if (msg is null) return;
        if (!string.Equals(msg.MessageType, SyncMessageType, StringComparison.Ordinal)) return;
        if (string.Equals(msg.SenderUhid, LocalNodeId, StringComparison.Ordinal)) return;
        if (msg.EncryptedContent is null || msg.EncryptedContent.Length == 0) return;

        SyncEnvelope? envelope;
        try
        {
            var json = Encoding.UTF8.GetString(msg.EncryptedContent);
            envelope = JsonSerializer.Deserialize<SyncEnvelope>(json, JsonOpts);
        }
        catch
        {
            // Malformed payload — drop silently. Sync converges next round.
            return;
        }
        if (envelope is null) return;

        List<Func<SyncEnvelope, CancellationToken, Task>> snapshot;
        lock (_lock) snapshot = _handlers.ToList();
        foreach (var h in snapshot)
        {
            try { await h(envelope, CancellationToken.None).ConfigureAwait(false); }
            catch { /* one handler's failure must not stop the others */ }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messaging.MessageReceived -= OnInbound;
        lock (_lock) _handlers.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly AetherNetCompanionStateChannel _owner;
        private readonly Func<SyncEnvelope, CancellationToken, Task> _handler;
        public Subscription(AetherNetCompanionStateChannel owner, Func<SyncEnvelope, CancellationToken, Task> handler)
        { _owner = owner; _handler = handler; }
        public void Dispose() { lock (_owner._lock) _owner._handlers.Remove(_handler); }
    }
}
