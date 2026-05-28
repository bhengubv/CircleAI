// PeerAgent.cs
//
// Identity record for a remote agent reachable over the Aether peer mesh.
// PeerAgent describes WHO another CircleAI is and HOW to reach them; it does
// not own the connection. Connections live on the protocol implementation.

namespace Circle.AI.Agents.Peer;

/// <summary>
/// A peer Circle AI agent discoverable over the Aether mesh.
/// </summary>
/// <param name="Id">Local handle for this peer (stable per discovery session).</param>
/// <param name="UhidIdentityId">
/// Hashed UHID identity reference — never raw PII. Used as the routing key
/// in <see cref="AgentMessage.ToUhid"/>.
/// </param>
/// <param name="DisplayName">User-chosen display label (e.g. "Sipho's Circle").</param>
/// <param name="Capabilities">Capabilities this peer advertises.</param>
/// <param name="PublicKeyDer">DER-encoded P-256 public key from the peer's UhidKeyRing.</param>
/// <param name="CurrentTransportId">
/// Transport currently carrying this peer — <c>"aether"</c>, <c>"wifi-direct"</c>,
/// <c>"ble"</c>, <c>"https-relay"</c>, or <c>null</c> when the peer is offline.
/// </param>
/// <param name="LastSeenAt">UTC timestamp of the last message or heartbeat from this peer.</param>
public sealed record PeerAgent(
    Guid Id,
    string UhidIdentityId,
    string DisplayName,
    IReadOnlyList<AgentCapability> Capabilities,
    byte[] PublicKeyDer,
    string? CurrentTransportId,
    DateTimeOffset LastSeenAt
);

/// <summary>
/// A capability advertised by a <see cref="PeerAgent"/>.
/// </summary>
/// <param name="Name">
/// Canonical capability name — e.g. <c>"translate"</c>, <c>"summarise"</c>,
/// <c>"navigate"</c>, <c>"diagnose"</c>.
/// </param>
/// <param name="Version">Semantic version of the capability contract.</param>
/// <param name="CostPerInvocation">Cost in <paramref name="CostCurrency"/>. <c>0</c> means free.</param>
/// <param name="CostCurrency">
/// Currency code. Defaults to <c>"SDPKT"</c> within the CircleAI ecosystem;
/// other codes are allowed for interoperability with external agents.
/// </param>
public sealed record AgentCapability(
    string Name,
    string Version,
    decimal CostPerInvocation,
    string CostCurrency
);
