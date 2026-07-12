// PeerAgent.kt
//
// Kotlin port of CircleAI.Agents.Peer/PeerAgent.cs.
//
// Identity record for a remote agent reachable over the Aether peer mesh.
// PeerAgent describes WHO another CircleAI is and HOW to reach them; it does
// not own the connection. Connections live on the protocol implementation.

package com.bhengubv.circleai.agents.peer

import java.math.BigDecimal
import java.time.Instant
import java.util.UUID

/**
 * A peer Circle AI agent discoverable over the Aether mesh.
 *
 * @property id Local handle for this peer (stable per discovery session).
 * @property uhidIdentityId Hashed UHID identity reference — never raw PII. Used
 *   as the routing key in [AgentMessage.toUhid].
 * @property displayName User-chosen display label (e.g. "Sipho's Circle").
 * @property capabilities Capabilities this peer advertises.
 * @property publicKeyDer DER-encoded P-256 public key from the peer's UhidKeyRing.
 * @property currentTransportId Transport currently carrying this peer —
 *   `"aether"`, `"wifi-direct"`, `"ble"`, `"https-relay"`, or `null` when the
 *   peer is offline.
 * @property lastSeenAt UTC timestamp of the last message or heartbeat.
 */
data class PeerAgent(
    val id: UUID,
    val uhidIdentityId: String,
    val displayName: String,
    val capabilities: List<AgentCapability>,
    val publicKeyDer: ByteArray,
    val currentTransportId: String?,
    val lastSeenAt: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PeerAgent) return false
        return id == other.id &&
            uhidIdentityId == other.uhidIdentityId &&
            displayName == other.displayName &&
            capabilities == other.capabilities &&
            publicKeyDer.contentEquals(other.publicKeyDer) &&
            currentTransportId == other.currentTransportId &&
            lastSeenAt == other.lastSeenAt
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + uhidIdentityId.hashCode()
        result = 31 * result + displayName.hashCode()
        result = 31 * result + capabilities.hashCode()
        result = 31 * result + publicKeyDer.contentHashCode()
        result = 31 * result + (currentTransportId?.hashCode() ?: 0)
        result = 31 * result + lastSeenAt.hashCode()
        return result
    }
}

/**
 * A capability advertised by a [PeerAgent].
 *
 * @property name Canonical capability name — e.g. `"translate"`, `"summarise"`,
 *   `"navigate"`, `"diagnose"`.
 * @property version Semantic version of the capability contract.
 * @property costPerInvocation Cost in [costCurrency]. `0` means free.
 * @property costCurrency Currency code. Defaults to `"SDPKT"` within the
 *   CircleAI ecosystem; other codes are allowed for interoperability.
 */
data class AgentCapability(
    val name: String,
    val version: String,
    val costPerInvocation: BigDecimal,
    val costCurrency: String,
)
