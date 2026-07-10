// WiFiTest.kt
//
// Verifies the CircleAI.Networking.WiFi port:
//   - Port constants (DiscoveryPort 47890 / DataPort 47891) and BeaconMagic
//   - parseIp: dotted-decimal IPv4 + IPv6 literal parse; bare hostnames / junk -> null
//   - InMemoryUdpNetwork: unicast reaches the bound socket; broadcast fans out to all
//     bound sockets except the sender
//   - WiFiNetworkTransport: kind WiFi, isAvailable once started, send unicasts to a
//     parseable IP destination else broadcasts to DataPort, an inbound datagram
//     surfaces via receive, stop closes + ends the flow
//   - WiFiPeerDiscovery: announce -> discover yields a PeerInfo for the beacon; a
//     non-beacon datagram is ignored

package com.bhengubv.circleai.networking.wifi

import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.PeerInfo
import com.bhengubv.circleai.networking.PeerRole
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class WiFiTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    // -----------------------------------------------------------------------
    // Constants + parseIp
    // -----------------------------------------------------------------------

    @Test
    fun `port constants and beacon magic match the C# reference`() {
        assertEquals(47890, WiFiNetworkTransport.DiscoveryPort)
        assertEquals(47891, WiFiNetworkTransport.DataPort)
        assertEquals("CIRCLEAI:BEACON:", WiFiPeerDiscovery.BeaconMagic)
    }

    @Test
    fun `parseIp accepts IPv4 and IPv6 literals and rejects hostnames`() {
        assertNotNull(WiFiNetworkTransport.parseIp("192.168.1.10"))
        assertNotNull(WiFiNetworkTransport.parseIp("10.0.0.1"))
        assertNotNull(WiFiNetworkTransport.parseIp("::1"))
        // Bare hostnames and junk must not parse (they fall through to broadcast).
        assertNull(WiFiNetworkTransport.parseIp("device-name"))
        assertNull(WiFiNetworkTransport.parseIp("example.com"))
        assertNull(WiFiNetworkTransport.parseIp("999.1.1.1")) // out-of-range octet
        assertNull(WiFiNetworkTransport.parseIp(""))
    }

    // -----------------------------------------------------------------------
    // InMemoryUdpNetwork
    // -----------------------------------------------------------------------

    @Test
    fun `unicast reaches the bound socket only`() = runTest {
        val net = InMemoryUdpNetwork()
        val receiver = net.openBound(5000, "10.0.0.2")
        val sender = net.openUnbound("10.0.0.9")
        withTimeout(2_000) {
            sender.sendTo(byteArrayOf(1, 2), "10.0.0.2", 5000)
            val d = receiver.receive()
            assertTrue(byteArrayOf(1, 2).contentEquals(d.data))
            assertEquals("10.0.0.9", d.fromAddress)
        }
    }

    @Test
    fun `broadcast fans out to all bound sockets except the sender`() = runTest {
        val net = InMemoryUdpNetwork()
        val r1 = net.openBound(6000, "10.0.0.2")
        val r2 = net.openBound(6000, "10.0.0.3")
        val senderAlsoBound = net.openBound(6000, "10.0.0.9")
        withTimeout(2_000) {
            senderAlsoBound.sendTo(byteArrayOf(7), IUdpNetwork.BROADCAST_ADDRESS, 6000)
            assertTrue(byteArrayOf(7).contentEquals(r1.receive().data))
            assertTrue(byteArrayOf(7).contentEquals(r2.receive().data))
            // The sender does not receive its own broadcast: nothing else is queued.
        }
    }

    // -----------------------------------------------------------------------
    // WiFiNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability once started`() = runTest {
        val transport = WiFiNetworkTransport(InMemoryUdpNetwork(), scope = this)
        assertEquals(TransportKind.WiFi, transport.kind)
        assertFalse(transport.isAvailable)
        transport.start()
        assertTrue(transport.isAvailable)
        transport.stop()
        assertFalse(transport.isAvailable)
    }

    @Test
    fun `send unicasts to a parseable IP destination on DataPort`() = runTest {
        val net = InMemoryUdpNetwork()
        // A peer bound on the DataPort at the destination IP receives the unicast.
        val peer = net.openBound(WiFiNetworkTransport.DataPort, "192.168.1.50")
        val transport = WiFiNetworkTransport(net, sourceAddress = "192.168.1.9", scope = this)
        transport.start()
        withTimeout(2_000) {
            transport.send(NetworkPayload.create(byteArrayOf(4, 4), destinationId = "192.168.1.50", now = { t0 }))
            // Two sockets are bound to DataPort now (peer + transport receiver). Unicast
            // hits the first bound → the peer (bound before the transport started).
            assertTrue(byteArrayOf(4, 4).contentEquals(peer.receive().data))
        }
        transport.stop()
    }

    @Test
    fun `send broadcasts when destination is not an IP`() = runTest {
        val net = InMemoryUdpNetwork()
        val listener = net.openBound(WiFiNetworkTransport.DataPort, "192.168.1.77")
        val transport = WiFiNetworkTransport(net, sourceAddress = "192.168.1.9", scope = this)
        transport.start()
        withTimeout(2_000) {
            transport.send(NetworkPayload.create(byteArrayOf(5, 5, 5), destinationId = "some-host-name", now = { t0 }))
            // Broadcast reaches every socket bound to DataPort except the sender's own
            // receiver → the external listener sees it.
            assertTrue(byteArrayOf(5, 5, 5).contentEquals(listener.receive().data))
        }
        transport.stop()
    }

    @Test
    fun `an inbound datagram surfaces via receive`() = runTest {
        val net = InMemoryUdpNetwork()
        val transport = WiFiNetworkTransport(net, sourceAddress = "192.168.1.9", scope = this)
        transport.start()
        val sender = net.openUnbound("192.168.1.200")
        withTimeout(2_000) {
            // Unicast to the transport's DataPort receiver.
            sender.sendTo(byteArrayOf(6, 6), "192.168.1.9", WiFiNetworkTransport.DataPort)
            val received = transport.receive().first()
            assertTrue(byteArrayOf(6, 6).contentEquals(received.data))
        }
        transport.stop()
    }

    @Test
    fun `stop closes sockets and ends the receive flow`() = runTest {
        val transport = WiFiNetworkTransport(InMemoryUdpNetwork(), scope = this)
        transport.start()
        withTimeout(2_000) {
            val collected = launch { transport.receive().toList() }
            yield()
            transport.stop()
            collected.join()
        }
        assertFalse(transport.isAvailable)
    }

    // -----------------------------------------------------------------------
    // WiFiPeerDiscovery
    // -----------------------------------------------------------------------

    @Test
    fun `announce then discover yields a PeerInfo for the beacon`() = runTest {
        val net = InMemoryUdpNetwork()
        val discovery = WiFiPeerDiscovery(net, sourceAddress = "10.1.1.1", now = { t0 })

        withTimeout(3_000) {
            // Start collecting first so the discovery socket is bound before the
            // beacon is broadcast (UDP: a datagram sent before the receiver binds is
            // lost — mirroring the real transport).
            val peers = mutableListOf<PeerInfo>()
            val job = launch {
                discovery.discover().collect { peers.add(it); throw kotlinx.coroutines.CancellationException("got one") }
            }
            // Give the collector a moment to bind the discovery port.
            yield()
            yield()

            val announcer = WiFiPeerDiscovery(net, sourceAddress = "10.1.1.2", now = { t0 })
            announcer.announce(
                PeerInfo("nodeB", null, listOf(TransportKind.WiFi), PeerRole.Peer, null, t0),
            )
            job.join()

            val p = peers.single()
            assertEquals("nodeB", p.nodeId)
            assertEquals(listOf(TransportKind.WiFi), p.supportedTransports)
            assertEquals(PeerRole.Peer, p.role)
            assertEquals("WiFi/10.1.1.2", p.displayName)
            assertEquals(t0, p.lastSeen)
        }
    }

    @Test
    fun `a non-beacon datagram is ignored by discover`() = runTest {
        val net = InMemoryUdpNetwork()
        val discovery = WiFiPeerDiscovery(net, sourceAddress = "10.1.1.1", now = { t0 })

        withTimeout(3_000) {
            val peers = mutableListOf<PeerInfo>()
            val beaconSeen = kotlinx.coroutines.CompletableDeferred<Unit>()
            val job = launch {
                discovery.discover().collect {
                    peers.add(it)
                    beaconSeen.complete(Unit)
                }
            }
            yield(); yield()

            val sender = net.openUnbound("10.1.1.9").apply { enableBroadcast = true }
            // A junk datagram (no beacon magic) must be ignored.
            sender.sendTo("not-a-beacon".toByteArray(), IUdpNetwork.BROADCAST_ADDRESS, WiFiNetworkTransport.DiscoveryPort)
            // A real beacon after it must be the ONLY thing surfaced.
            sender.sendTo("${WiFiPeerDiscovery.BeaconMagic}realNode".toByteArray(), IUdpNetwork.BROADCAST_ADDRESS, WiFiNetworkTransport.DiscoveryPort)

            beaconSeen.await()
            job.cancel()
            assertEquals(listOf("realNode"), peers.map { it.nodeId })
        }
    }
}
