// AetherNetTest.kt
//
// Verifies the CircleAI.Networking.AetherNet port:
//   - AetherPeerKind carries every C# member in declaration order
//   - InMemoryAetherNetRegistry: register/getPeer, peers ordered by id, recentPackets
//     newest-first + limit, avgRoundTripMs (0.0 when empty), totalBytesBetween
//   - AetherNetworkTransport: kind, isAvailable tracks IAetherContext, send accepts,
//     stop ends the receive flow
//   - AetherPeerDiscovery: discover yields nothing (bridge), announce accepts
//   - AetherSyncChannel: getLastSequence default 0, pushDelta accepts, receiveDeltas
//     is an empty bridge flow

package com.bhengubv.circleai.networking.aethernet

import com.bhengubv.circleai.aether.AetherInstallLevel
import com.bhengubv.circleai.aether.InMemoryAetherContext
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import com.bhengubv.circleai.sync.SyncDelta
import com.bhengubv.circleai.sync.SyncDeliveryMode
import kotlinx.coroutines.flow.count
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class AetherNetTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    // -----------------------------------------------------------------------
    // AetherPeerKind
    // -----------------------------------------------------------------------

    @Test
    fun `AetherPeerKind carries all members in C# order`() {
        assertEquals(
            listOf("Phone", "Tablet", "Laptop", "Desktop", "Edge", "Vehicle", "Iot"),
            AetherPeerKind.entries.map { it.name },
        )
    }

    // -----------------------------------------------------------------------
    // InMemoryAetherNetRegistry
    // -----------------------------------------------------------------------

    @Test
    fun `registry register + getPeer round-trips and peers is ordered by id`() {
        val reg = InMemoryAetherNetRegistry()
        reg.register(AetherPeer("zebra", AetherPeerKind.Phone, "Z", listOf("chat")))
        reg.register(AetherPeer("alpha", AetherPeerKind.Laptop, null, emptyList()))
        reg.register(AetherPeer("mike", AetherPeerKind.Edge, "M", listOf("relay")))

        assertEquals(AetherPeerKind.Laptop, reg.getPeer("alpha")?.kind)
        assertNull(reg.getPeer("absent"))
        assertEquals(listOf("alpha", "mike", "zebra"), reg.peers.map { it.peerId })
    }

    @Test
    fun `registry register replaces by peer id`() {
        val reg = InMemoryAetherNetRegistry()
        reg.register(AetherPeer("p1", AetherPeerKind.Phone, "old", emptyList()))
        reg.register(AetherPeer("p1", AetherPeerKind.Tablet, "new", listOf("x")))
        assertEquals(1, reg.peers.size)
        assertEquals("new", reg.getPeer("p1")?.friendlyName)
        assertEquals(AetherPeerKind.Tablet, reg.getPeer("p1")?.kind)
    }

    @Test
    fun `recentPackets is newest-first and honours the limit`() {
        val reg = InMemoryAetherNetRegistry()
        reg.recordPacket(AetherPacketSummary("a", "p1", "p2", 10, "data", t0))
        reg.recordPacket(AetherPacketSummary("b", "p1", "p2", 20, "data", t0.plusSeconds(5)))
        reg.recordPacket(AetherPacketSummary("c", "p1", "p2", 30, "data", t0.plusSeconds(10)))

        assertEquals(listOf("c", "b", "a"), reg.recentPackets().map { it.packetId })
        assertEquals(listOf("c", "b"), reg.recentPackets(limit = 2).map { it.packetId })
    }

    @Test
    fun `avgRoundTripMs averages samples and is 0 when empty`() {
        val reg = InMemoryAetherNetRegistry()
        assertEquals(0.0, reg.avgRoundTripMs("p1"), 1e-9)
        reg.recordHop(AetherHopTelemetry("p1", 2, 10.0, t0))
        reg.recordHop(AetherHopTelemetry("p1", 3, 30.0, t0))
        reg.recordHop(AetherHopTelemetry("other", 1, 100.0, t0))
        assertEquals(20.0, reg.avgRoundTripMs("p1"), 1e-9)
    }

    @Test
    fun `totalBytesBetween sums only matching from-to packets`() {
        val reg = InMemoryAetherNetRegistry()
        reg.recordPacket(AetherPacketSummary("a", "p1", "p2", 10, "data", t0))
        reg.recordPacket(AetherPacketSummary("b", "p1", "p2", 15, "data", t0))
        reg.recordPacket(AetherPacketSummary("c", "p2", "p1", 99, "data", t0))
        assertEquals(25, reg.totalBytesBetween("p1", "p2"))
        assertEquals(0, reg.totalBytesBetween("p1", "p3"))
    }

    // -----------------------------------------------------------------------
    // AetherNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport reports Aether kind and tracks context availability`() = runTest {
        val available = AetherNetworkTransport(InMemoryAetherContext(AetherInstallLevel.App, enabled = true))
        val offline = AetherNetworkTransport(InMemoryAetherContext(AetherInstallLevel.App, enabled = false))

        assertEquals(TransportKind.Aether, available.kind)
        assertTrue(available.isAvailable)
        assertFalse(offline.isAvailable)
    }

    @Test
    fun `transport send accepts and start is a no-op`() = runTest {
        val transport = AetherNetworkTransport(InMemoryAetherContext(AetherInstallLevel.App))
        transport.start()
        // Bridge accept — does not throw, mirrors C# Task.CompletedTask.
        transport.send(NetworkPayload.create(byteArrayOf(1, 2, 3), destinationId = "peer", now = { t0 }))
    }

    @Test
    fun `transport stop ends the receive flow`() = runTest {
        val transport = AetherNetworkTransport(InMemoryAetherContext(AetherInstallLevel.App))
        transport.start()
        withTimeout(2_000) {
            val collected = launch { transport.receive().toList() }
            yield()
            transport.stop() // completes the inbound bridge → receive() flow ends
            collected.join()
        }
    }

    // -----------------------------------------------------------------------
    // AetherPeerDiscovery
    // -----------------------------------------------------------------------

    @Test
    fun `discovery yields nothing and announce accepts`() = runTest {
        val discovery = AetherPeerDiscovery(InMemoryAetherContext(AetherInstallLevel.App))
        assertEquals(0, discovery.discover().count())
        // announce is a bridge accept — does not throw.
        discovery.announce(
            com.bhengubv.circleai.networking.PeerInfo(
                nodeId = "me",
                displayName = "Me",
                supportedTransports = listOf(TransportKind.Aether),
                role = com.bhengubv.circleai.networking.PeerRole.Peer,
                signalStrengthDbm = null,
                lastSeen = t0,
            ),
        )
    }

    // -----------------------------------------------------------------------
    // AetherSyncChannel
    // -----------------------------------------------------------------------

    @Test
    fun `sync channel getLastSequence defaults to 0 and pushDelta accepts`() = runTest {
        val channel = AetherSyncChannel(InMemoryAetherContext(AetherInstallLevel.App))
        assertEquals(0L, channel.getLastSequence("owner", "memory.episodic"))

        channel.pushDelta(
            SyncDelta(
                ownerId = "owner",
                sourceDeviceId = "devA",
                targetDeviceId = "devB",
                domainKey = "memory.episodic",
                payload = byteArrayOf(9),
                sequence = 1,
                deliveryMode = SyncDeliveryMode.Guaranteed,
                createdAt = t0,
            ),
        )
        // Still 0 — the bridge does not mutate the sequence table (mirrors C#).
        assertEquals(0L, channel.getLastSequence("owner", "memory.episodic"))
    }

    @Test
    fun `sync channel receiveDeltas is an empty bridge flow`() = runTest {
        val channel = AetherSyncChannel(InMemoryAetherContext(AetherInstallLevel.App))
        assertEquals(emptyList(), channel.receiveDeltas("owner").toList())
    }
}
