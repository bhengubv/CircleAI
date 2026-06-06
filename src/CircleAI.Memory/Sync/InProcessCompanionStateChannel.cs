// InProcessCompanionStateChannel.cs
//
// Loopback channel — wires N nodes in the same process so two
// CompanionStateSyncEngine instances can converge in tests without any real
// transport. Also useful for same-device simulation (two CircleAI instances
// sharing a hub).
//
// Pattern: every channel belongs to an InProcessSyncHub. SendAsync broadcasts
// to every peer channel on the hub EXCEPT the sender's own; receivers fire
// their subscribed handlers asynchronously.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Routes envelopes between every <see cref="InProcessCompanionStateChannel"/>
/// that has joined the hub. One hub per simulated "mesh".
/// </summary>
public sealed class InProcessSyncHub
{
    private readonly ConcurrentDictionary<string, InProcessCompanionStateChannel> _channels = new();

    internal void Join(InProcessCompanionStateChannel channel) =>
        _channels[channel.LocalNodeId] = channel;

    internal void Leave(string nodeId) => _channels.TryRemove(nodeId, out _);

    internal async Task BroadcastAsync(
        SyncEnvelope envelope, string senderNodeId, CancellationToken ct)
    {
        var peers = _channels.Values
            .Where(c => !string.Equals(c.LocalNodeId, senderNodeId, StringComparison.Ordinal))
            .ToList();
        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested();
            await peer.DeliverAsync(envelope, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Channels currently on this hub.</summary>
    public IReadOnlyCollection<string> ConnectedNodeIds =>
        _channels.Keys.ToList();
}

/// <summary>
/// In-process <see cref="ICompanionStateChannel"/>. Broadcasts via an
/// <see cref="InProcessSyncHub"/>.
/// </summary>
public sealed class InProcessCompanionStateChannel : ICompanionStateChannel, IDisposable
{
    private readonly InProcessSyncHub _hub;
    private readonly List<Func<SyncEnvelope, CancellationToken, Task>> _handlers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public InProcessCompanionStateChannel(InProcessSyncHub hub, string localNodeId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrWhiteSpace(localNodeId))
            throw new ArgumentException("localNodeId required", nameof(localNodeId));
        _hub = hub;
        LocalNodeId = localNodeId;
        _hub.Join(this);
    }

    /// <inheritdoc/>
    public string LocalNodeId { get; }

    /// <inheritdoc/>
    public Task SendAsync(SyncEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_disposed) throw new ObjectDisposedException(nameof(InProcessCompanionStateChannel));
        return _hub.BroadcastAsync(envelope, LocalNodeId, ct);
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Func<SyncEnvelope, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_disposed) throw new ObjectDisposedException(nameof(InProcessCompanionStateChannel));
        lock (_lock) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    internal async Task DeliverAsync(SyncEnvelope envelope, CancellationToken ct)
    {
        List<Func<SyncEnvelope, CancellationToken, Task>> snapshot;
        lock (_lock) snapshot = _handlers.ToList();
        foreach (var h in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await h(envelope, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Unregisters from the hub.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hub.Leave(LocalNodeId);
        lock (_lock) _handlers.Clear();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InProcessCompanionStateChannel _owner;
        private readonly Func<SyncEnvelope, CancellationToken, Task> _handler;
        public Subscription(InProcessCompanionStateChannel owner,
                            Func<SyncEnvelope, CancellationToken, Task> handler)
        {
            _owner = owner; _handler = handler;
        }
        public void Dispose()
        {
            lock (_owner._lock) _owner._handlers.Remove(_handler);
        }
    }
}
