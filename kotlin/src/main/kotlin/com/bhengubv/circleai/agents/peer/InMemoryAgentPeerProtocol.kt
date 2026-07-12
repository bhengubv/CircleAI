// InMemoryAgentPeerProtocol.kt
//
// Kotlin port of CircleAI.Agents.Peer/InMemoryAgentPeerProtocol.cs.
//
// Reference implementation of IAgentPeerProtocol that uses an in-process
// AgentBus as its transport. Multiple instances sharing one bus simulate a
// small mesh of CircleAI devices.
//
// Real implementations (BLE, Wi-Fi Direct, Aether router) live in
// CircleAI.Aether and follow the same contract.
//
// C# -> Kotlin conventions:
//   Task.Run(PumpInboxAsync)              -> scope.launch { pumpInbox() }
//   TaskCompletionSource<AgentMessage>    -> kotlinx.coroutines.CompletableDeferred
//   CancellationTokenSource + CancelAfter -> withTimeoutOrNull
//   ConcurrentDictionary                  -> java.util.concurrent.ConcurrentHashMap
//   Guid <-> 16 bytes                     -> uuidToBytes / bytesToUuid helpers

package com.bhengubv.circleai.agents.peer

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.math.BigDecimal
import java.nio.ByteBuffer
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

/**
 * In-memory reference implementation of [IAgentPeerProtocol]. Backed by an
 * [AgentBus] so multiple instances can simulate a mesh of CircleAI peers in
 * tests and samples.
 *
 * Not transport-backed — use an Aether-backed [IAgentPeerProtocol] in
 * production.
 *
 * @param ownUhid Hashed UHID identity owned by this agent.
 * @param bus Shared bus standing in for the Aether transport.
 * @param ownCapabilities Capabilities this agent advertises to peers.
 * @param ownPublicKey DER-encoded public key from the agent's UhidKeyRing.
 * @param signer Optional function that signs outbound payloads. When `null`,
 *   outbound messages carry an empty [AgentMessage.signature].
 * @param capabilityHandler Optional function invoked when a peer sends
 *   [AgentMessageKind.INVOKE]. Returning a non-null [ByteArray] sends a
 *   [AgentMessageKind.RESPONSE]; returning `null` sends a
 *   [AgentMessageKind.DECLINE].
 */
class InMemoryAgentPeerProtocol(
    private val ownUhid: String,
    private val bus: AgentBus,
    private val ownCapabilities: List<AgentCapability>,
    private val ownPublicKey: ByteArray,
    private val signer: ((ByteArray) -> ByteArray)? = null,
    private val capabilityHandler: ((AgentCapability, ByteArray) -> ByteArray?)? = null,
) : IAgentPeerProtocol, AutoCloseable {

    init {
        require(ownUhid.isNotBlank()) { "ownUhid required" }
    }

    val componentName: String get() = "InMemoryAgentPeerProtocol"

    private val lastSeen = ConcurrentHashMap<String, Instant>()
    private val pendingInvocations = ConcurrentHashMap<UUID, CompletableDeferred<AgentMessage>>()
    private val externalInbox = kotlinx.coroutines.channels.Channel<AgentMessage>(
        kotlinx.coroutines.channels.Channel.UNLIMITED,
    )
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val disposed = AtomicBoolean(false)

    /** The UHID identity owned by this agent. */
    val ownUhidValue: String get() = ownUhid

    init {
        bus.register(
            PeerAgent(
                id = UUID.randomUUID(),
                uhidIdentityId = ownUhid,
                displayName = ownUhid,
                capabilities = ownCapabilities,
                publicKeyDer = ownPublicKey,
                currentTransportId = "in-memory",
                lastSeenAt = Instant.now(),
            ),
        )
        scope.launch { pumpInbox() }
    }

    override suspend fun discoverPeers(): List<PeerAgent> {
        // Broadcast a Discover so peers can refresh their view of us.
        val announcement = AgentMessage.create(
            AgentMessageKind.DISCOVER,
            ownUhid,
            "*",
            "application/json",
            payload = ByteArray(0),
            signature = sign(ByteArray(0)),
        )
        bus.send(announcement)

        // Brief listen window so any registered peer's responses can land.
        delay(DEFAULT_DISCOVERY_WINDOW_MS)

        return bus.registeredPeers
            .filter { it.uhidIdentityId != ownUhid }
            .map { withLastSeen(it) }
    }

    override suspend fun greet(targetUhid: String): PeerAgent? {
        require(targetUhid.isNotBlank()) { "targetUhid required" }
        val peer = bus.tryGetPeer(targetUhid) ?: return null

        val greet = AgentMessage.create(
            AgentMessageKind.GREET,
            ownUhid,
            targetUhid,
            "application/json",
            payload = ByteArray(0),
            signature = sign(ByteArray(0)),
        )
        bus.send(greet)

        return withLastSeen(peer)
    }

    override suspend fun queryCapabilities(targetUhid: String): List<AgentCapability> {
        require(targetUhid.isNotBlank()) { "targetUhid required" }
        val peer = bus.tryGetPeer(targetUhid) ?: return emptyList()
        return peer.capabilities
    }

    override suspend fun invoke(
        targetUhid: String,
        capability: AgentCapability,
        requestPayload: ByteArray,
    ): AgentMessage {
        require(targetUhid.isNotBlank()) { "targetUhid required" }

        bus.tryGetPeer(targetUhid)
            ?: throw AgentInvocationException(
                "Peer '$targetUhid' is not reachable on the current transport.", targetUhid,
            )

        val invokeMsg = AgentMessage.create(
            AgentMessageKind.INVOKE,
            ownUhid,
            targetUhid,
            "application/octet-stream",
            payload = requestPayload,
            signature = sign(requestPayload),
        )

        val deferred = CompletableDeferred<AgentMessage>()
        pendingInvocations[invokeMsg.id] = deferred

        bus.send(invokeMsg)

        val reply = withTimeoutOrNull(DEFAULT_INVOKE_TIMEOUT_MS) { deferred.await() }
        pendingInvocations.remove(invokeMsg.id)

        if (reply == null) {
            throw AgentInvocationException(
                "Invocation of '${capability.name}' on peer '$targetUhid' timed out.", targetUhid,
            )
        }

        if (reply.kind == AgentMessageKind.DECLINE) {
            throw AgentInvocationException(
                "Peer '$targetUhid' declined '${capability.name}'.", targetUhid, reply,
            )
        }

        return reply
    }

    override fun streamInbox(): Flow<AgentMessage> = flow {
        for (message in externalInbox) {
            emit(message)
        }
    }

    /**
     * Tears down the protocol, unregisters from the bus, and stops the inbox
     * pump.
     */
    override fun close() {
        if (!disposed.compareAndSet(false, true)) return
        scope.cancel()
        bus.unregister(ownUhid)
        externalInbox.close()
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private suspend fun pumpInbox() {
        try {
            bus.receive(ownUhid).collect { message ->
                lastSeen[message.fromUhid] = message.sentAt
                handleIncoming(message)
            }
        } catch (_: CancellationException) {
            // Shutdown path.
        }
    }

    private suspend fun handleIncoming(message: AgentMessage) {
        when (message.kind) {
            AgentMessageKind.RESPONSE, AgentMessageKind.DECLINE -> completePending(message)
            AgentMessageKind.INVOKE -> routeInvoke(message)
            else -> {}
        }

        // Every inbound message is also surfaced to external consumers.
        externalInbox.trySend(message)
    }

    private fun completePending(message: AgentMessage) {
        // Convention: Response/Decline carry the original Invoke's Id in the
        // first 16 bytes of the payload when generated by routeInvoke.
        if (message.payload.size < 16) return
        val correlationId = bytesToUuid(message.payload, 0)
        pendingInvocations[correlationId]?.complete(message)
    }

    private fun routeInvoke(invoke: AgentMessage) {
        val handler = capabilityHandler ?: return

        // Best-effort: a real implementation negotiates which capability is being
        // invoked by carrying its name in the payload. The in-memory mock simply
        // hands the first advertised capability to the handler.
        val capability = ownCapabilities.firstOrNull()
            ?: AgentCapability("unknown", "0.0.0", BigDecimal.ZERO, "SDPKT")

        val result: ByteArray? = try {
            handler(capability, invoke.payload)
        } catch (_: Exception) {
            null
        }

        val correlationPrefix = uuidToBytes(invoke.id)

        if (result == null) {
            val decline = AgentMessage.create(
                AgentMessageKind.DECLINE,
                ownUhid,
                invoke.fromUhid,
                "application/octet-stream",
                payload = correlationPrefix,
                signature = sign(correlationPrefix),
            )
            bus.send(decline)
            return
        }

        val responsePayload = ByteArray(correlationPrefix.size + result.size)
        System.arraycopy(correlationPrefix, 0, responsePayload, 0, correlationPrefix.size)
        System.arraycopy(result, 0, responsePayload, correlationPrefix.size, result.size)

        val response = AgentMessage.create(
            AgentMessageKind.RESPONSE,
            ownUhid,
            invoke.fromUhid,
            "application/octet-stream",
            payload = responsePayload,
            signature = sign(responsePayload),
        )
        bus.send(response)
    }

    private fun sign(data: ByteArray): ByteArray = signer?.invoke(data) ?: ByteArray(0)

    private fun withLastSeen(peer: PeerAgent): PeerAgent {
        val ts = lastSeen[peer.uhidIdentityId] ?: peer.lastSeenAt
        return peer.copy(lastSeenAt = ts)
    }

    companion object {
        private const val DEFAULT_DISCOVERY_WINDOW_MS = 50L
        private const val DEFAULT_INVOKE_TIMEOUT_MS = 5_000L

        /** Serialises a [UUID] into 16 big-endian bytes (msb || lsb). */
        internal fun uuidToBytes(uuid: UUID): ByteArray =
            ByteBuffer.allocate(16)
                .putLong(uuid.mostSignificantBits)
                .putLong(uuid.leastSignificantBits)
                .array()

        /** Reads a [UUID] from 16 big-endian bytes starting at [offset]. */
        internal fun bytesToUuid(bytes: ByteArray, offset: Int): UUID {
            val bb = ByteBuffer.wrap(bytes, offset, 16)
            val msb = bb.long
            val lsb = bb.long
            return UUID(msb, lsb)
        }
    }
}
