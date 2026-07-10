// NetworkingTest.kt
//
// Verifies the CircleAI.Networking core abstraction port:
//   - enums carry every C# member in declaration order
//   - NetworkPayload.create defaults + value equality over the byte content
//   - NetworkContext.offline canonical snapshot
//   - DefaultNetworkPolicy permissive contract
//   - NetworkPolicyBuilder: allow / force / mesh-first / no-cloud / disable-queue
//   - DefaultTransportSelector: documented cascade, force short-circuit,
//     mesh-first reordering, cloud gating, context-availability filtering,
//     always-terminal LocalStore fallback
//   - LoopbackNetworkTransport: unicast + broadcast, source stamping, no self
//     delivery, message sent right after start() is not lost, stop() ends the
//     receive flow
//   - InMemoryMeshNetwork: peer set excludes self, derived + overridden health
//   - InMemoryMessageChannel: typed send/receive routed by type, pre-subscribe
//     buffering, non-matching types filtered out
//   - InMemoryConnectivityMonitor: snapshot replay to late subscriber + change
//     propagation

package com.bhengubv.circleai.networking

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class NetworkingTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    // -----------------------------------------------------------------------
    // Enums
    // -----------------------------------------------------------------------

    @Test
    fun `TransportKind carries all members in C# order`() {
        assertEquals(
            listOf(
                "Http", "WebSocket", "Grpc", "Mqtt", "Tcp", "Udp",
                "WiFi", "Bluetooth", "NearLink", "Aether", "Dtn", "LocalStore",
            ),
            TransportKind.entries.map { it.name },
        )
    }

    @Test
    fun `connectivity + priority + peer role enums match C#`() {
        assertEquals(
            listOf("Online", "LocalOnly", "MeshOnly", "Offline"),
            ConnectivityState.entries.map { it.name },
        )
        assertEquals(
            listOf("Low", "Normal", "High", "Urgent", "Emergency"),
            MessagePriority.entries.map { it.name },
        )
        assertEquals(
            listOf("Peer", "Relay", "Bridge", "Sink"),
            PeerRole.entries.map { it.name },
        )
    }

    // -----------------------------------------------------------------------
    // NetworkPayload
    // -----------------------------------------------------------------------

    @Test
    fun `NetworkPayload create applies defaults + injected clock`() {
        val p = NetworkPayload.create(
            data = byteArrayOf(1, 2, 3),
            destinationId = "node-b",
            now = { t0 },
        )
        assertEquals(32, p.id.length) // Guid "N" format, no dashes
        assertNull(p.sourceId)
        assertEquals("node-b", p.destinationId)
        assertEquals(MessagePriority.Normal, p.priority)
        assertEquals("application/octet-stream", p.contentType)
        assertNull(p.ttl)
        assertTrue(p.metadata.isEmpty())
        assertEquals(t0, p.createdAt)
    }

    @Test
    fun `NetworkPayload equality is value based over bytes`() {
        val a = NetworkPayload(
            "id", null, "d", byteArrayOf(9, 8, 7),
            MessagePriority.High, null, "text/plain", emptyMap(), t0,
        )
        val b = a.copy(data = byteArrayOf(9, 8, 7)) // different array, same content
        val c = a.copy(data = byteArrayOf(9, 8, 6)) // different content
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertFalse(a == c)
    }

    // -----------------------------------------------------------------------
    // NetworkContext / PeerInfo
    // -----------------------------------------------------------------------

    @Test
    fun `NetworkContext offline is the canonical no-connectivity snapshot`() {
        val ctx = NetworkContext.offline(now = { t0 })
        assertEquals(ConnectivityState.Offline, ctx.state)
        assertEquals(TransportKind.LocalStore, ctx.preferredTransport)
        assertTrue(ctx.availableTransports.isEmpty())
        assertEquals(0, ctx.nearbyPeerCount)
        assertEquals(t0, ctx.snapshotAt)
    }

    @Test
    fun `PeerInfo holds its fields`() {
        val peer = PeerInfo(
            nodeId = "n1",
            displayName = "Alice",
            supportedTransports = listOf(TransportKind.Aether, TransportKind.WiFi),
            role = PeerRole.Relay,
            signalStrengthDbm = -55,
            lastSeen = t0,
        )
        assertEquals("n1", peer.nodeId)
        assertEquals(PeerRole.Relay, peer.role)
        assertEquals(2, peer.supportedTransports.size)
    }

    // -----------------------------------------------------------------------
    // DefaultNetworkPolicy
    // -----------------------------------------------------------------------

    @Test
    fun `DefaultNetworkPolicy permits everything and enables the offline queue`() {
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        for (t in TransportKind.entries) {
            assertTrue(DefaultNetworkPolicy.permits(t, payload), "should permit $t")
        }
        assertNull(DefaultNetworkPolicy.forceTransport)
        assertFalse(DefaultNetworkPolicy.meshFirst)
        assertTrue(DefaultNetworkPolicy.offlineQueueEnabled)
        assertTrue(DefaultNetworkPolicy.allowCloudTransports)
    }

    // -----------------------------------------------------------------------
    // NetworkPolicyBuilder
    // -----------------------------------------------------------------------

    @Test
    fun `builder no-cloud rejects the four cloud transports and clears allowCloud`() {
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        val policy = NetworkPolicyBuilder().noCloud().build()
        assertFalse(policy.allowCloudTransports)
        for (cloud in listOf(TransportKind.Http, TransportKind.WebSocket, TransportKind.Grpc, TransportKind.Mqtt)) {
            assertFalse(policy.permits(cloud, payload), "$cloud must be blocked")
        }
        // Non-cloud still permitted (no allow-list set).
        assertTrue(policy.permits(TransportKind.Aether, payload))
        assertTrue(policy.permits(TransportKind.LocalStore, payload))
    }

    @Test
    fun `builder allow-list restricts to listed kinds`() {
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        val policy = NetworkPolicyBuilder().allow(TransportKind.WiFi, TransportKind.Aether).build()
        assertTrue(policy.permits(TransportKind.WiFi, payload))
        assertTrue(policy.permits(TransportKind.Aether, payload))
        assertFalse(policy.permits(TransportKind.Tcp, payload))
        assertFalse(policy.permits(TransportKind.Http, payload))
    }

    @Test
    fun `builder force + mesh-first + disable-queue flags surface`() {
        val policy = NetworkPolicyBuilder()
            .meshFirst()
            .disableQueue()
            .force(TransportKind.Bluetooth)
            .build()
        assertEquals(TransportKind.Bluetooth, policy.forceTransport)
        assertTrue(policy.meshFirst)
        assertFalse(policy.offlineQueueEnabled)
    }

    // -----------------------------------------------------------------------
    // DefaultTransportSelector
    // -----------------------------------------------------------------------

    private fun ctxWith(vararg available: TransportKind) = NetworkContext(
        state = ConnectivityState.Online,
        preferredTransport = available.firstOrNull() ?: TransportKind.LocalStore,
        availableTransports = available.toList(),
        signalStrengthDbm = null,
        estimatedBandwidthBps = null,
        latencyMs = null,
        nearbyPeerCount = 0,
        snapshotAt = t0,
    )

    @Test
    fun `selector follows the documented cascade when all transports available`() {
        val selector = DefaultTransportSelector()
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        val ctx = ctxWith(*TransportKind.entries.toTypedArray())
        assertEquals(
            listOf(
                TransportKind.Grpc, TransportKind.WebSocket, TransportKind.Http, TransportKind.Mqtt,
                TransportKind.Tcp, TransportKind.WiFi, TransportKind.Bluetooth, TransportKind.NearLink,
                TransportKind.Aether, TransportKind.Dtn, TransportKind.LocalStore,
            ),
            selector.getCascade(payload, ctx),
        )
        assertEquals(TransportKind.Grpc, selector.selectBest(payload, ctx))
    }

    @Test
    fun `selector filters to context-available transports but keeps LocalStore terminal`() {
        val selector = DefaultTransportSelector()
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        // Only WiFi + Bluetooth live; LocalStore must still terminate the cascade.
        val ctx = ctxWith(TransportKind.WiFi, TransportKind.Bluetooth)
        assertEquals(
            listOf(TransportKind.WiFi, TransportKind.Bluetooth, TransportKind.LocalStore),
            selector.getCascade(payload, ctx),
        )
        assertEquals(TransportKind.WiFi, selector.selectBest(payload, ctx))
    }

    @Test
    fun `selector force short-circuits with LocalStore fallback`() {
        val policy = NetworkPolicyBuilder().force(TransportKind.NearLink).build()
        val selector = DefaultTransportSelector(policy)
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        // Even though NearLink is NOT in the context, a force is an explicit override.
        assertEquals(
            listOf(TransportKind.NearLink, TransportKind.LocalStore),
            selector.getCascade(payload, ctxWith(TransportKind.Grpc)),
        )
    }

    @Test
    fun `selector mesh-first bubbles mesh transports ahead of cloud`() {
        val policy = NetworkPolicyBuilder().meshFirst().build()
        val selector = DefaultTransportSelector(policy)
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        val cascade = selector.getCascade(payload, ctxWith(*TransportKind.entries.toTypedArray()))
        // First mesh transport must come before the first cloud transport.
        val firstMesh = cascade.indexOfFirst { it in DefaultTransportSelector.MESH_TRANSPORTS }
        val firstCloud = cascade.indexOfFirst { it in NetworkPolicyBuilder.CLOUD_TRANSPORTS }
        assertTrue(firstMesh < firstCloud, "mesh should precede cloud: $cascade")
        assertEquals(TransportKind.WiFi, cascade.first()) // highest-priority mesh transport
    }

    @Test
    fun `selector no-cloud removes cloud transports from the cascade`() {
        val policy = NetworkPolicyBuilder().noCloud().build()
        val selector = DefaultTransportSelector(policy)
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        val cascade = selector.getCascade(payload, ctxWith(*TransportKind.entries.toTypedArray()))
        assertTrue(cascade.none { it in NetworkPolicyBuilder.CLOUD_TRANSPORTS }, "no cloud in $cascade")
        assertEquals(TransportKind.Tcp, cascade.first()) // first non-cloud in the base order
    }

    @Test
    fun `selector falls back to LocalStore when nothing else is available`() {
        val selector = DefaultTransportSelector()
        val payload = NetworkPayload.create(byteArrayOf(0), now = { t0 })
        // Empty context, nothing available → LocalStore only.
        assertEquals(listOf(TransportKind.LocalStore), selector.getCascade(payload, ctxWith()))
        assertEquals(TransportKind.LocalStore, selector.selectBest(payload, ctxWith()))
    }

    // -----------------------------------------------------------------------
    // LoopbackNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport unicast delivers only to the addressed node and stamps source`() = runTest {
        val bus = LoopbackNetworkBus()
        val a = LoopbackNetworkTransport("A", bus, TransportKind.Tcp)
        val b = LoopbackNetworkTransport("B", bus, TransportKind.Tcp)
        val c = LoopbackNetworkTransport("C", bus, TransportKind.Tcp)
        a.start(); b.start(); c.start()

        val payload = NetworkPayload.create(byteArrayOf(42), destinationId = "B", now = { t0 })
        // Collect B's first inbound before sending is impossible (cold flow); instead
        // launch a collector, yield so it subscribes, then send.
        val received = ArrayList<NetworkPayload>()
        val job = launch { b.receive().take(1).toList(received) }
        yield()
        a.send(payload)
        withTimeout(2_000) { job.join() }

        assertEquals(1, received.size)
        assertEquals("A", received.single().sourceId) // stamped by the sending transport
        assertEquals(byteArrayOf(42).toList(), received.single().data.toList())
        assertEquals(TransportKind.Tcp, a.kind)
        assertTrue(a.isAvailable)
    }

    @Test
    fun `transport broadcast reaches every peer except the sender`() = runTest {
        val bus = LoopbackNetworkBus()
        val a = LoopbackNetworkTransport("A", bus)
        val b = LoopbackNetworkTransport("B", bus)
        val c = LoopbackNetworkTransport("C", bus)
        a.start(); b.start(); c.start()

        val bGot = ArrayList<NetworkPayload>()
        val cGot = ArrayList<NetworkPayload>()
        val jb = launch { b.receive().take(1).toList(bGot) }
        val jc = launch { c.receive().take(1).toList(cGot) }
        yield()
        // No destinationId → broadcast.
        a.send(NetworkPayload.create(byteArrayOf(7), now = { t0 }))
        withTimeout(2_000) { jb.join(); jc.join() }

        assertEquals(1, bGot.size)
        assertEquals(1, cGot.size)
        assertEquals(7, bGot.single().data.single())
    }

    @Test
    fun `message sent immediately after start is buffered, not lost`() = runTest {
        // The subscribe-before-consume rule: the inbox is attached synchronously in
        // start(), so a send that happens before any collector subscribes still
        // lands in the UNBOUNDED inbox and is delivered when collection begins.
        val bus = LoopbackNetworkBus()
        val a = LoopbackNetworkTransport("A", bus)
        val b = LoopbackNetworkTransport("B", bus)
        a.start()
        b.start()
        a.send(NetworkPayload.create(byteArrayOf(1), destinationId = "B", now = { t0 })) // BEFORE b collects

        val got = withTimeout(2_000) { b.receive().first() }
        assertEquals(1, got.data.single())
    }

    @Test
    fun `stop closes the inbox so the receive flow completes`() = runTest {
        val bus = LoopbackNetworkBus()
        val a = LoopbackNetworkTransport("A", bus)
        a.start()
        assertTrue(a.isAvailable)
        val collected = ArrayList<NetworkPayload>()
        val job = launch { a.receive().toList(collected) }
        yield()
        a.stop() // closes inbox → flow completes, join returns
        withTimeout(2_000) { job.join() }
        assertFalse(a.isAvailable)
        assertTrue(collected.isEmpty())
    }

    @Test
    fun `transport rejects blank node id and send before start`() = runTest {
        val bus = LoopbackNetworkBus()
        assertFailsWith<IllegalArgumentException> { LoopbackNetworkTransport("  ", bus) }
        val t = LoopbackNetworkTransport("X", bus)
        assertFailsWith<IllegalStateException> {
            t.send(NetworkPayload.create(byteArrayOf(0), now = { t0 }))
        }
    }

    // -----------------------------------------------------------------------
    // InMemoryMeshNetwork
    // -----------------------------------------------------------------------

    @Test
    fun `mesh peer ids exclude self and health derives from peer count`() = runTest {
        val bus = LoopbackNetworkBus()
        val ta = LoopbackNetworkTransport("A", bus); ta.start()
        val tb = LoopbackNetworkTransport("B", bus); tb.start()
        val mesh = InMemoryMeshNetwork("A", bus, now = { t0 })

        assertEquals(listOf("B"), mesh.getPeerIds())
        val health = mesh.getMeshHealth()
        assertEquals(ConnectivityState.MeshOnly, health.state)
        assertEquals(TransportKind.Aether, health.preferredTransport)
        assertEquals(1, health.nearbyPeerCount)
    }

    @Test
    fun `mesh health falls back to offline with no peers and honours override`() = runTest {
        val bus = LoopbackNetworkBus()
        val mesh = InMemoryMeshNetwork("solo", bus, now = { t0 })
        assertTrue(mesh.getPeerIds().isEmpty())
        assertEquals(ConnectivityState.Offline, mesh.getMeshHealth().state)

        val custom = NetworkContext.offline(now = { t0 }).copy(state = ConnectivityState.Online)
        mesh.setHealth(custom)
        assertSame(custom, mesh.getMeshHealth())
    }

    // -----------------------------------------------------------------------
    // InMemoryMessageChannel
    // -----------------------------------------------------------------------

    private data class Ping(val n: Int)
    private data class Pong(val s: String)

    @Test
    fun `message channel routes typed messages to the matching receiver`() = runTest {
        val hub = MessageHub()
        val sender = InMemoryMessageChannel("sender", hub)
        val receiver = InMemoryMessageChannel("receiver", hub)

        sender.send("receiver", Ping(1))
        sender.send("receiver", Pong("ignored")) // different type — must NOT reach the Ping flow
        sender.send("receiver", Ping(2))

        // UNBOUNDED per-type buffer: the two Pings are retained and read in order;
        // the Pong lands on a different type channel and is never seen here.
        val pings = withTimeout(2_000) { receiver.receive<Ping>().take(2).toList() }
        assertEquals(listOf(Ping(1), Ping(2)), pings)
    }

    @Test
    fun `message channel buffers messages published before the subscriber attaches`() = runTest {
        // Retain-until-read: a message delivered before any collector attaches is
        // buffered in the per-type UNBOUNDED channel and delivered on first().
        val hub = MessageHub()
        val sender = InMemoryMessageChannel("s", hub)
        val receiver = InMemoryMessageChannel("r", hub)

        sender.send("r", Ping(99)) // BEFORE receive() is ever called
        val got = withTimeout(2_000) { receiver.receive<Ping>().first() }
        assertEquals(Ping(99), got)
    }

    @Test
    fun `message channel does not deliver to an absent destination`() = runTest {
        val hub = MessageHub()
        val sender = InMemoryMessageChannel("s", hub)
        // "ghost" never joined — send is a no-op, must not throw.
        sender.send("ghost", Ping(1))
        assertEquals(listOf("s"), hub.connectedNodeIds.toList())
    }

    @Test
    fun `message channel rejects blank destination and blank node id, and send after close`() = runTest {
        val hub = MessageHub()
        assertFailsWith<IllegalArgumentException> { InMemoryMessageChannel(" ", hub) }
        val ch = InMemoryMessageChannel("n", hub)
        assertFailsWith<IllegalArgumentException> { ch.send("  ", Ping(1)) }
        ch.close()
        assertFailsWith<IllegalStateException> { ch.send("other", Ping(1)) }
    }

    // -----------------------------------------------------------------------
    // InMemoryConnectivityMonitor
    // -----------------------------------------------------------------------

    @Test
    fun `connectivity monitor replays the current snapshot to a late subscriber`() = runTest {
        val start = NetworkContext.offline(now = { t0 }).copy(state = ConnectivityState.Online)
        val monitor = InMemoryConnectivityMonitor(start)
        assertEquals(ConnectivityState.Online, monitor.currentState)
        assertSame(start, monitor.getSnapshot())

        // A subscriber that joins now must see the current snapshot immediately.
        val first = withTimeout(2_000) { monitor.watch().first() }
        assertEquals(ConnectivityState.Online, first.state)
    }

    @Test
    fun `connectivity monitor pushes changes to subscribers`() = runTest {
        val start = NetworkContext.offline(now = { t0 })
        val monitor = InMemoryConnectivityMonitor(start)

        val seen = ArrayList<NetworkContext>()
        // Expect: replayed initial (Offline) + two pushed changes.
        val job = launch { monitor.watch().take(3).toList(seen) }
        yield()

        monitor.push(start.copy(state = ConnectivityState.MeshOnly))
        monitor.push(start.copy(state = ConnectivityState.Online))

        withTimeout(2_000) { job.join() }
        assertEquals(
            listOf(ConnectivityState.Offline, ConnectivityState.MeshOnly, ConnectivityState.Online),
            seen.map { it.state },
        )
        // currentState reflects the last push.
        assertEquals(ConnectivityState.Online, monitor.currentState)
    }
}
