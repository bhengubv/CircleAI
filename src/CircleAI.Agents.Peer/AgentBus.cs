// AgentBus.cs
//
// In-process coordinator that lets several InMemoryAgentPeerProtocol
// instances behave like devices on a mesh, for tests and samples.
//
// AgentBus owns the peer registry and an unbounded channel per registered
// peer. Send routes a message to the right channel (or fans out on
// broadcast). Receive yields envelopes as they arrive.
//
// AgentBus is NOT a production transport. It exists so the protocol
// contract can be exercised without a real Aether router on the wire.

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CircleAI.Agents.Peer;

/// <summary>
/// In-process bus used to simulate a mesh of CircleAI peers for tests and
/// samples. Not a production transport.
/// </summary>
public sealed class AgentBus
{
    private readonly ConcurrentDictionary<string, PeerAgent> _peers = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Channel<AgentMessage>> _inboxes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshot of every peer currently registered on the bus.
    /// </summary>
    public IReadOnlyCollection<PeerAgent> RegisteredPeers => [.. _peers.Values];

    /// <summary>
    /// Registers <paramref name="peer"/> on the bus. A subsequent
    /// <see cref="Send"/> targeted at the peer's UHID will deliver to its
    /// inbox. Re-registering with the same UHID replaces the prior record.
    /// </summary>
    public void Register(PeerAgent peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _peers[peer.UhidIdentityId] = peer;
        _ = _inboxes.GetOrAdd(
            peer.UhidIdentityId,
            static _ => Channel.CreateUnbounded<AgentMessage>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));
    }

    /// <summary>
    /// Removes <paramref name="uhid"/> from the bus and completes its inbox
    /// so any active <see cref="Receive"/> enumerator terminates cleanly.
    /// </summary>
    public void Unregister(string uhid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uhid);
        _peers.TryRemove(uhid, out _);
        if (_inboxes.TryRemove(uhid, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Tries to read the latest record for <paramref name="uhid"/>.
    /// </summary>
    public bool TryGetPeer(string uhid, out PeerAgent peer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uhid);
        if (_peers.TryGetValue(uhid, out var found))
        {
            peer = found;
            return true;
        }
        peer = default!;
        return false;
    }

    /// <summary>
    /// Routes <paramref name="message"/> to its recipient(s). When
    /// <see cref="AgentMessage.ToUhid"/> is <c>"*"</c> the envelope is
    /// delivered to every registered inbox except the sender's own. Messages
    /// for an unknown UHID are dropped silently — the simulated peer is
    /// considered offline.
    /// </summary>
    public void Send(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ToUhid == "*")
        {
            foreach (var kv in _inboxes)
            {
                if (string.Equals(kv.Key, message.FromUhid, StringComparison.Ordinal))
                {
                    continue;
                }
                kv.Value.Writer.TryWrite(message);
            }
            return;
        }

        if (_inboxes.TryGetValue(message.ToUhid, out var inbox))
        {
            inbox.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// Streams every envelope delivered to <paramref name="uhid"/>'s inbox.
    /// The sequence terminates when the inbox is completed (via
    /// <see cref="Unregister"/>) or when
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async IAsyncEnumerable<AgentMessage> Receive(
        string uhid,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uhid);
        var inbox = _inboxes.GetOrAdd(
            uhid,
            static _ => Channel.CreateUnbounded<AgentMessage>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));

        while (await inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (inbox.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }
}
