// WebSocketTest.kt
//
// Verifies the CircleAI.Networking.WebSocket port:
//   - WebSocketLinkState (incl. Closed_Error) + WebSocketMessageType carry every C#
//     member in order
//   - InMemoryWebSocketSessionRegistry: register/get, state default Closed + persists,
//     totalBytes, frameCount by type
//   - InMemoryWebSocket: loopback duplex round-trips a binary frame; closing an end
//     surfaces a Close frame on the peer
//   - WebSocketTransport: kind WebSocket, isAvailable == Open, start connects, send is
//     a binary frame, a peer binary frame surfaces via receive, a peer close ends the
//     receive flow, stop closes + ends the flow

package com.bhengubv.circleai.networking.websocket

import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class WebSocketTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    // -----------------------------------------------------------------------
    // Enums
    // -----------------------------------------------------------------------

    @Test
    fun `enums carry all members in C# order incl underscored Closed_Error`() {
        assertEquals(
            listOf("Closed", "Connecting", "Open", "CloseSent", "CloseReceived", "Closed_Error"),
            WebSocketLinkState.entries.map { it.name },
        )
        assertEquals(
            listOf("Text", "Binary", "Ping", "Pong", "Close"),
            WebSocketMessageType.entries.map { it.name },
        )
    }

    // -----------------------------------------------------------------------
    // InMemoryWebSocketSessionRegistry
    // -----------------------------------------------------------------------

    @Test
    fun `registry register + get + state default + totals`() {
        val reg = InMemoryWebSocketSessionRegistry()
        val d = WebSocketEndpointDescriptor(URI("wss://h/ws"), mapOf("A" to "B"), Duration.ofSeconds(30), listOf("v1"))
        reg.register("s1", d)
        assertEquals(d, reg.get("s1"))
        assertNull(reg.get("absent"))

        assertEquals(WebSocketLinkState.Closed, reg.state("s1"))
        reg.setState("s1", WebSocketLinkState.Open)
        assertEquals(WebSocketLinkState.Open, reg.state("s1"))

        reg.recordFrame(WebSocketFrameSummary("s1", WebSocketMessageType.Binary, 100, t0))
        reg.recordFrame(WebSocketFrameSummary("s1", WebSocketMessageType.Binary, 40, t0))
        reg.recordFrame(WebSocketFrameSummary("s1", WebSocketMessageType.Ping, 4, t0))
        reg.recordFrame(WebSocketFrameSummary("other", WebSocketMessageType.Binary, 9, t0))
        assertEquals(144L, reg.totalBytes("s1"))
        assertEquals(2, reg.frameCount("s1", WebSocketMessageType.Binary))
        assertEquals(1, reg.frameCount("s1", WebSocketMessageType.Ping))
    }

    // -----------------------------------------------------------------------
    // InMemoryWebSocket
    // -----------------------------------------------------------------------

    @Test
    fun `loopback duplex round-trips a binary frame`() = runTest {
        val (a, b) = InMemoryWebSocket.pair()
        a.connect(URI("ws://x"))
        b.connect(URI("ws://x"))
        withTimeout(2_000) {
            a.sendBinary(byteArrayOf(1, 2, 3))
            val frame = b.receive()
            assertEquals(WebSocketMessageType.Binary, frame.type)
            assertTrue(byteArrayOf(1, 2, 3).contentEquals(frame.payload))
        }
    }

    @Test
    fun `closing an end surfaces a Close frame on the peer`() = runTest {
        val (a, b) = InMemoryWebSocket.pair()
        a.connect(URI("ws://x"))
        b.connect(URI("ws://x"))
        withTimeout(2_000) {
            a.close("bye")
            assertEquals(WebSocketMessageType.Close, b.receive().type)
        }
    }

    // -----------------------------------------------------------------------
    // WebSocketTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability track Open state`() = runTest {
        val (sock, _peer) = InMemoryWebSocket.pair()
        val transport = WebSocketTransport(sock, "ws://host/ws", scope = this)
        assertEquals(TransportKind.WebSocket, transport.kind)
        assertFalse(transport.isAvailable) // not connected yet
        transport.start()
        assertTrue(transport.isAvailable)
        transport.stop()
    }

    @Test
    fun `send emits a binary frame to the peer`() = runTest {
        val (sock, peer) = InMemoryWebSocket.pair()
        peer.connect(URI("ws://x"))
        val transport = WebSocketTransport(sock, "ws://host/ws", scope = this)
        transport.start()
        withTimeout(2_000) {
            transport.send(NetworkPayload.create(byteArrayOf(7, 8, 9), now = { t0 }))
            val frame = peer.receive()
            assertEquals(WebSocketMessageType.Binary, frame.type)
            assertTrue(byteArrayOf(7, 8, 9).contentEquals(frame.payload))
        }
        transport.stop()
    }

    @Test
    fun `a peer binary frame surfaces via receive`() = runTest {
        val (sock, peer) = InMemoryWebSocket.pair()
        peer.connect(URI("ws://x"))
        val transport = WebSocketTransport(sock, "ws://host/ws", scope = this)
        transport.start()
        withTimeout(2_000) {
            peer.sendBinary(byteArrayOf(3, 1, 4, 1, 5))
            val received = transport.receive().first()
            assertTrue(byteArrayOf(3, 1, 4, 1, 5).contentEquals(received.data))
        }
        transport.stop()
    }

    @Test
    fun `a peer close ends the receive flow`() = runTest {
        val (sock, peer) = InMemoryWebSocket.pair()
        peer.connect(URI("ws://x"))
        val transport = WebSocketTransport(sock, "ws://host/ws", scope = this)
        transport.start()
        withTimeout(2_000) {
            val collected = launch { transport.receive().toList() }
            yield()
            peer.close("done") // sends a Close frame → pump breaks → inbox completes
            collected.join()
        }
    }

    @Test
    fun `stop closes the socket and ends the receive flow`() = runTest {
        val (sock, _peer) = InMemoryWebSocket.pair()
        val transport = WebSocketTransport(sock, "ws://host/ws", scope = this)
        transport.start()
        withTimeout(2_000) {
            val collected = launch { transport.receive().toList() }
            yield()
            transport.stop()
            collected.join()
        }
        assertFalse(transport.isAvailable)
    }
}
