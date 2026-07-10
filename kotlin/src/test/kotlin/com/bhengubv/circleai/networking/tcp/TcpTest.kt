// TcpTest.kt
//
// Verifies the CircleAI.Networking.Tcp port:
//   - TcpConnectionState carries every C# member in order
//   - TcpKnownPorts constants match the C# statics
//   - InMemoryTcpConnectionRegistry: register/get, state default Disconnected +
//     persists, throughput totalBytesSent
//   - InMemoryTcpConnection: loopback duplex round-trips bytes, readExactly throws on
//     close before enough bytes
//   - TcpNetworkTransport (client mode): kind Tcp, isAvailable tracks the connection,
//     send frames with a 4-byte little-endian length prefix, a peer write surfaces via
//     receive as one payload per frame, stop closes + ends the flow
//   - TcpNetworkTransport (server mode): start binds the injected listener

package com.bhengubv.circleai.networking.tcp

import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TcpTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    private val remoteDesc = TcpEndpointDescriptor("host", 9000, noDelay = true, keepAlive = false, connectTimeout = Duration.ofSeconds(5))

    /** Connector that hands back a pre-made connection. */
    private class FixedConnector(private val conn: ITcpConnection) : ITcpConnector {
        var connectedTo: TcpEndpointDescriptor? = null
        override suspend fun connect(descriptor: TcpEndpointDescriptor): ITcpConnection {
            connectedTo = descriptor
            return conn
        }
    }

    /** Listener recording bind/stop + serving queued connections. */
    private class FakeListener : ITcpListener {
        var started = false
        var stopped = false
        val queue = Channel<ITcpConnection>(Channel.UNLIMITED)
        override fun start() { started = true }
        override suspend fun accept(): ITcpConnection? = queue.receiveCatching().getOrNull()
        override fun stop() { stopped = true; queue.close() }
    }

    // -----------------------------------------------------------------------
    // Commons
    // -----------------------------------------------------------------------

    @Test
    fun `TcpConnectionState carries all members in C# order`() {
        assertEquals(
            listOf("Disconnected", "Connecting", "Connected", "Closing", "Failed"),
            TcpConnectionState.entries.map { it.name },
        )
    }

    @Test
    fun `known ports match the C# statics`() {
        assertEquals(80, TcpKnownPorts.HTTP)
        assertEquals(443, TcpKnownPorts.HTTPS)
        assertEquals(22, TcpKnownPorts.SSH)
        assertEquals(25, TcpKnownPorts.SMTP)
        assertEquals(143, TcpKnownPorts.IMAP)
        assertEquals(993, TcpKnownPorts.IMAP_SSL)
        assertEquals(110, TcpKnownPorts.POP3)
        assertEquals(995, TcpKnownPorts.POP3_SSL)
        assertEquals(1883, TcpKnownPorts.MQTT)
        assertEquals(8883, TcpKnownPorts.MQTT_SSL)
    }

    @Test
    fun `registry register + get + state default + throughput`() {
        val reg = InMemoryTcpConnectionRegistry()
        reg.register("e1", remoteDesc)
        assertEquals(remoteDesc, reg.get("e1"))
        assertNull(reg.get("absent"))

        assertEquals(TcpConnectionState.Disconnected, reg.state("e1"))
        reg.setState("e1", TcpConnectionState.Connected)
        assertEquals(TcpConnectionState.Connected, reg.state("e1"))

        reg.recordSample(TcpThroughputSample("e1", 100, 10, t0))
        reg.recordSample(TcpThroughputSample("e1", 250, 20, t0))
        reg.recordSample(TcpThroughputSample("other", 999, 1, t0))
        assertEquals(350L, reg.totalBytesSent("e1"))
    }

    // -----------------------------------------------------------------------
    // InMemoryTcpConnection
    // -----------------------------------------------------------------------

    @Test
    fun `loopback duplex round-trips bytes`() = runTest {
        val (a, b) = InMemoryTcpConnection.pair()
        withTimeout(2_000) {
            a.write(byteArrayOf(1, 2, 3, 4))
            assertTrue(byteArrayOf(1, 2, 3, 4).contentEquals(b.readExactly(4)))
        }
    }

    @Test
    fun `readExactly throws when closed before enough bytes`() = runTest {
        val (a, b) = InMemoryTcpConnection.pair()
        withTimeout(2_000) {
            a.write(byteArrayOf(1))
            a.close()
            assertFailsWith<java.io.EOFException> { b.readExactly(4) }
        }
    }

    // -----------------------------------------------------------------------
    // TcpNetworkTransport — client mode
    // -----------------------------------------------------------------------

    @Test
    fun `client transport kind + availability track the connection`() = runTest {
        val (client, _peer) = InMemoryTcpConnection.pair()
        val transport = TcpNetworkTransport(
            connector = FixedConnector(client),
            remote = remoteDesc,
            scope = this,
        )
        assertEquals(TransportKind.Tcp, transport.kind)
        assertFalse(transport.isAvailable)
        transport.start()
        assertTrue(transport.isAvailable)
        transport.stop()
    }

    @Test
    fun `send writes a 4-byte little-endian length prefix then the data`() = runTest {
        val (client, peer) = InMemoryTcpConnection.pair()
        val transport = TcpNetworkTransport(connector = FixedConnector(client), remote = remoteDesc, scope = this)
        transport.start()

        withTimeout(2_000) {
            transport.send(NetworkPayload.create(byteArrayOf(10, 20, 30), now = { t0 }))
            val lenBuf = peer.readExactly(4)
            val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
            assertEquals(3, len)
            // Little-endian encoding of 3 is 03 00 00 00.
            assertTrue(byteArrayOf(3, 0, 0, 0).contentEquals(lenBuf))
            assertTrue(byteArrayOf(10, 20, 30).contentEquals(peer.readExactly(len)))
        }
        transport.stop()
    }

    @Test
    fun `peer-framed bytes surface via receive as one payload per frame`() = runTest {
        val (client, peer) = InMemoryTcpConnection.pair()
        val transport = TcpNetworkTransport(connector = FixedConnector(client), remote = remoteDesc, scope = this)
        transport.start()

        // Peer writes a framed message: LE length prefix + data.
        val data = byteArrayOf(5, 6, 7, 8, 9)
        val len = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(data.size).array()
        withTimeout(2_000) {
            peer.write(len)
            peer.write(data)
            val received = transport.receive().first()
            assertTrue(data.contentEquals(received.data))
        }
        transport.stop()
    }

    @Test
    fun `send throws when not connected`() = runTest {
        val transport = TcpNetworkTransport(connector = FixedConnector(InMemoryTcpConnection.pair().first), remote = remoteDesc, scope = this)
        // Not started → no connection.
        assertFailsWith<IllegalStateException> { transport.send(NetworkPayload.create(byteArrayOf(1), now = { t0 })) }
    }

    @Test
    fun `stop closes the connection and ends the receive flow`() = runTest {
        val (client, _peer) = InMemoryTcpConnection.pair()
        val transport = TcpNetworkTransport(connector = FixedConnector(client), remote = remoteDesc, scope = this)
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
    // TcpNetworkTransport — server mode
    // -----------------------------------------------------------------------

    @Test
    fun `server transport start binds the listener and stop unbinds it`() = runTest {
        val listener = FakeListener()
        val transport = TcpNetworkTransport(listener = listener, listenPort = 9000, scope = this)
        transport.start()
        assertTrue(listener.started)
        transport.stop()
        assertTrue(listener.stopped)
    }
}
