// IAgentPeerProtocol.cs
//
// The contract a Circle AI device implements to talk directly to other
// Circle AI devices over the Aether mesh — no cloud, no relay.
//
// Implementations vary by transport (in-memory mock for tests; real BLE /
// Wi-Fi Direct / Aether router in production). Every method MUST be safe to
// call from any thread.

namespace Circle.AI.Agents.Peer;

/// <summary>
/// Agent-to-agent protocol over the Aether mesh.
/// </summary>
public interface IAgentPeerProtocol
{
    /// <summary>
    /// Listens for <see cref="AgentMessageKind.Discover"/> broadcasts and any
    /// already-registered peers for a short discovery window, returning every
    /// peer observed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the discovery window.</param>
    /// <returns>The peers observed during the discovery window.</returns>
    Task<IReadOnlyList<PeerAgent>> DiscoverPeersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Initiates a handshake with <paramref name="targetUhid"/>. Returns the
    /// peer's identity record on a successful greet, or <c>null</c> if the
    /// peer is unreachable or did not respond.
    /// </summary>
    /// <param name="targetUhid">Hashed UHID identity of the desired peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PeerAgent?> GreetAsync(string targetUhid, CancellationToken cancellationToken);

    /// <summary>
    /// Queries <paramref name="targetUhid"/> for the capabilities it currently
    /// advertises.
    /// </summary>
    /// <param name="targetUhid">Hashed UHID identity of the desired peer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AgentCapability>> QueryCapabilitiesAsync(
        string targetUhid,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invokes <paramref name="capability"/> on <paramref name="targetUhid"/>
    /// with <paramref name="requestPayload"/>. Awaits a single
    /// <see cref="AgentMessageKind.Response"/> envelope.
    /// </summary>
    /// <exception cref="AgentInvocationException">
    /// Thrown when the peer returns <see cref="AgentMessageKind.Decline"/> or
    /// when invocation otherwise fails.
    /// </exception>
    Task<AgentMessage> InvokeAsync(
        string targetUhid,
        AgentCapability capability,
        byte[] requestPayload,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams every inbound <see cref="AgentMessage"/> addressed to this
    /// agent (including broadcasts where <see cref="AgentMessage.ToUhid"/> is
    /// <c>"*"</c>). The sequence terminates when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<AgentMessage> StreamInboxAsync(CancellationToken cancellationToken);
}
