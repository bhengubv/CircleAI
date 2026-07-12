// IAgentPeerProtocol.kt
//
// Kotlin port of CircleAI.Agents.Peer/IAgentPeerProtocol.cs.
//
// The contract a Circle AI device implements to talk directly to other Circle
// AI devices over the Aether mesh — no cloud, no relay.
//
// Implementations vary by transport (in-memory mock for tests; real BLE /
// Wi-Fi Direct / Aether router in production). Every method MUST be safe to
// call from any coroutine.
//
// C# -> Kotlin conventions:
//   Task<IReadOnlyList<T>>          -> suspend fun (): List<T>
//   IAsyncEnumerable<AgentMessage>  -> kotlinx.coroutines.flow.Flow<AgentMessage>
//   CancellationToken               -> structured concurrency (coroutine scope)

package com.bhengubv.circleai.agents.peer

import kotlinx.coroutines.flow.Flow

/**
 * Agent-to-agent protocol over the Aether mesh.
 */
interface IAgentPeerProtocol {
    /**
     * Listens for [AgentMessageKind.DISCOVER] broadcasts and any already-registered
     * peers for a short discovery window, returning every peer observed.
     */
    suspend fun discoverPeers(): List<PeerAgent>

    /**
     * Initiates a handshake with [targetUhid]. Returns the peer's identity record
     * on a successful greet, or `null` if the peer is unreachable or did not
     * respond.
     */
    suspend fun greet(targetUhid: String): PeerAgent?

    /**
     * Queries [targetUhid] for the capabilities it currently advertises.
     */
    suspend fun queryCapabilities(targetUhid: String): List<AgentCapability>

    /**
     * Invokes [capability] on [targetUhid] with [requestPayload]. Awaits a single
     * [AgentMessageKind.RESPONSE] envelope.
     *
     * @throws AgentInvocationException when the peer returns
     *   [AgentMessageKind.DECLINE] or when invocation otherwise fails.
     */
    suspend fun invoke(
        targetUhid: String,
        capability: AgentCapability,
        requestPayload: ByteArray,
    ): AgentMessage

    /**
     * Streams every inbound [AgentMessage] addressed to this agent (including
     * broadcasts where [AgentMessage.toUhid] is `"*"`). The flow terminates when
     * the collecting coroutine is cancelled.
     */
    fun streamInbox(): Flow<AgentMessage>
}
