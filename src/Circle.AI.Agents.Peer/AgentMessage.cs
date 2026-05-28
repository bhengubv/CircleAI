// AgentMessage.cs
//
// Wire format for every exchange in the agent-to-agent protocol.
// Messages are content-typed so peers can negotiate JSON / plain text /
// CBOR payloads without protocol churn, and every message is signed with the
// sender's UhidKeyRing so a recipient can prove provenance offline.

namespace Circle.AI.Agents.Peer;

/// <summary>
/// Discriminator for the kind of agent-to-agent exchange a message represents.
/// </summary>
public enum AgentMessageKind
{
    /// <summary>Broadcast announcement — "I am here, here is my UHID and capabilities."</summary>
    Discover,

    /// <summary>Targeted greeting / handshake initiation.</summary>
    Greet,

    /// <summary>Request for a peer's currently advertised capability list.</summary>
    CapabilityQuery,

    /// <summary>Request that the peer execute a named capability with a payload.</summary>
    Invoke,

    /// <summary>Successful response to a prior <see cref="Invoke"/> or <see cref="CapabilityQuery"/>.</summary>
    Response,

    /// <summary>Peer refused the invocation (cost, policy, capacity, unknown capability).</summary>
    Decline,

    /// <summary>Keep-alive — updates <see cref="PeerAgent.LastSeenAt"/> on the recipient side.</summary>
    Heartbeat
}

/// <summary>
/// Signed, content-typed envelope exchanged between two CircleAI agents.
/// </summary>
/// <param name="Id">Globally unique message identifier.</param>
/// <param name="Kind">Kind of exchange this message represents.</param>
/// <param name="FromUhid">Sender's hashed UHID identity reference.</param>
/// <param name="ToUhid">
/// Recipient's hashed UHID identity, or <c>"*"</c> for a broadcast.
/// </param>
/// <param name="ContentType">
/// IANA-style media type for <paramref name="Payload"/> —
/// e.g. <c>"application/json"</c>, <c>"text/plain"</c>, <c>"application/cbor"</c>.
/// </param>
/// <param name="Payload">Opaque body. Interpretation is determined by <paramref name="ContentType"/>.</param>
/// <param name="Signature">
/// ECDSA-SHA256 signature over <paramref name="Payload"/> produced by the sender's UhidKeyRing.
/// </param>
/// <param name="SentAt">UTC timestamp stamped by the sender at envelope creation.</param>
public sealed record AgentMessage(
    Guid Id,
    AgentMessageKind Kind,
    string FromUhid,
    string ToUhid,
    string ContentType,
    byte[] Payload,
    byte[] Signature,
    DateTimeOffset SentAt
)
{
    /// <summary>
    /// Creates a new <see cref="AgentMessage"/> with a freshly-generated
    /// <see cref="Id"/> and a <see cref="SentAt"/> stamped at UTC now.
    /// </summary>
    /// <param name="kind">Kind of exchange.</param>
    /// <param name="fromUhid">Sender's hashed UHID identity.</param>
    /// <param name="toUhid">Recipient UHID, or <c>"*"</c> for broadcast.</param>
    /// <param name="contentType">Media type for <paramref name="payload"/>.</param>
    /// <param name="payload">Opaque payload bytes.</param>
    /// <param name="signature">Signature over <paramref name="payload"/>.</param>
    public static AgentMessage Create(
        AgentMessageKind kind,
        string fromUhid,
        string toUhid,
        string contentType,
        byte[] payload,
        byte[] signature) =>
        new(Guid.NewGuid(), kind, fromUhid, toUhid, contentType, payload, signature, DateTimeOffset.UtcNow);
}
