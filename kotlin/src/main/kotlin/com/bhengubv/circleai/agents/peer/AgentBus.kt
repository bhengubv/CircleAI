// AgentBus.kt
//
// Kotlin port of CircleAI.Agents.Peer/AgentBus.cs.
//
// In-process coordinator that lets several InMemoryAgentPeerProtocol instances
// behave like devices on a mesh, for tests and samples.
//
// AgentBus owns the peer registry and an unbounded channel per registered peer.
// send() routes a message to the right channel (or fans out on broadcast).
// receive() yields envelopes as they arrive.
//
// AgentBus is NOT a production transport. It exists so the protocol contract
// can be exercised without a real Aether router on the wire.
//
// C# -> Kotlin conventions:
//   ConcurrentDictionary            -> java.util.concurrent.ConcurrentHashMap
//   Channel.CreateUnbounded<T>()    -> kotlinx.coroutines.channels.Channel(UNLIMITED)
//   IAsyncEnumerable<AgentMessage>  -> kotlinx.coroutines.flow.Flow<AgentMessage>

package com.bhengubv.circleai.agents.peer

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.util.concurrent.ConcurrentHashMap

/**
 * In-process bus used to simulate a mesh of CircleAI peers for tests and
 * samples. Not a production transport.
 */
class AgentBus {
    private val peers = ConcurrentHashMap<String, PeerAgent>()
    private val inboxes = ConcurrentHashMap<String, Channel<AgentMessage>>()

    /** Snapshot of every peer currently registered on the bus. */
    val registeredPeers: List<PeerAgent> get() = peers.values.toList()

    /**
     * Registers [peer] on the bus. A subsequent [send] targeted at the peer's
     * UHID will deliver to its inbox. Re-registering with the same UHID replaces
     * the prior record.
     */
    fun register(peer: PeerAgent) {
        peers[peer.uhidIdentityId] = peer
        inboxes.computeIfAbsent(peer.uhidIdentityId) { Channel(Channel.UNLIMITED) }
    }

    /**
     * Removes [uhid] from the bus and completes its inbox so any active [receive]
     * flow terminates cleanly.
     */
    fun unregister(uhid: String) {
        require(uhid.isNotBlank()) { "uhid required" }
        peers.remove(uhid)
        inboxes.remove(uhid)?.close()
    }

    /** Tries to read the latest record for [uhid]. */
    fun tryGetPeer(uhid: String): PeerAgent? {
        require(uhid.isNotBlank()) { "uhid required" }
        return peers[uhid]
    }

    /**
     * Routes [message] to its recipient(s). When [AgentMessage.toUhid] is `"*"`
     * the envelope is delivered to every registered inbox except the sender's
     * own. Messages for an unknown UHID are dropped silently — the simulated
     * peer is considered offline.
     */
    fun send(message: AgentMessage) {
        if (message.toUhid == "*") {
            for ((key, inbox) in inboxes) {
                if (key == message.fromUhid) continue
                inbox.trySend(message)
            }
            return
        }
        inboxes[message.toUhid]?.trySend(message)
    }

    /**
     * Streams every envelope delivered to [uhid]'s inbox. The flow terminates
     * when the inbox is completed (via [unregister]) or when the collecting
     * coroutine is cancelled.
     */
    fun receive(uhid: String): Flow<AgentMessage> {
        require(uhid.isNotBlank()) { "uhid required" }
        val inbox = inboxes.computeIfAbsent(uhid) { Channel(Channel.UNLIMITED) }
        return flow {
            for (message in inbox) {
                emit(message)
            }
        }
    }
}
