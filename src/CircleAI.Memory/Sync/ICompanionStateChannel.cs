// ICompanionStateChannel.cs
//
// Transport seam for the sync engine. Implementations:
//   • InProcessCompanionStateChannel — loopback for tests + same-device sim
//   • (Phase 3.1) AetherNetCompanionStateChannel — over the live mesh
//   • Any other transport the host wants (TCP, WebSockets, etc.)

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Transport that moves <see cref="SyncEnvelope"/> messages between peers.
/// </summary>
public interface ICompanionStateChannel
{
    /// <summary>
    /// Stable identifier of THIS node on this channel. Stamped onto every
    /// envelope as <see cref="SyncEnvelope.FromNodeId"/>.
    /// </summary>
    string LocalNodeId { get; }

    /// <summary>
    /// Sends an envelope to peers. Channel decides whether this is broadcast
    /// (to every peer) or routed (to a specific destination embedded in the
    /// envelope's content). For v0.1 every channel implements broadcast
    /// semantics — Phase 3.1 can route by FromNodeId targeting.
    /// </summary>
    Task SendAsync(SyncEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to inbound envelopes. The returned disposable unsubscribes.
    /// </summary>
    IDisposable Subscribe(Func<SyncEnvelope, CancellationToken, Task> handler);
}
