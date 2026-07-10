// RealtimeCloudTest.kt
//
// Verifies the CircleAI.Realtime.Cloud port:
//   - NullRealtimeTransportFactory throws the reference "no factory registered"
//     message on connect (intentional parity guard)
//   - InMemoryLoopbackTransport echoes text/binary frames back on the receive
//     streams, retains frames written before a consumer subscribes (UNBOUNDED
//     buffering — nothing lost), and closeAsync/disposeAsync flip isOpen and end
//     the streams; sending after close throws
//   - InMemoryLoopbackTransportFactory hands out open transports

package com.bhengubv.circleai.realtime.cloud

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RealtimeCloudTest {

    @Test
    fun `null factory throws the reference message`() = runTest {
        val f = NullRealtimeTransportFactory.Instance
        val ex = assertFailsWith<IllegalStateException> {
            f.connectAsync(URI.create("wss://example/realtime"), mapOf("Authorization" to "Bearer x"))
        }
        assertTrue(ex.message!!.contains("No IRealtimeTransportFactory is registered"))
    }

    @Test
    fun `loopback transport echoes text and retains pre-subscribe frames`() = runTest {
        val t = InMemoryLoopbackTransport()
        assertTrue(t.isOpen)

        // Write BEFORE any consumer subscribes — UNBOUNDED buffering must retain these.
        t.sendTextAsync("a")
        t.sendTextAsync("b")
        t.closeAsync() // completes the stream so toList() terminates

        assertEquals(listOf("a", "b"), t.receiveTextAsync().toList())
        assertFalse(t.isOpen)
    }

    @Test
    fun `loopback transport echoes binary frames`() = runTest {
        val t = InMemoryLoopbackTransport()
        val f1 = byteArrayOf(1, 2, 3)
        val f2 = byteArrayOf(9)
        t.sendBinaryAsync(f1)
        t.sendBinaryAsync(f2)
        t.disposeAsync()

        val got = t.receiveBinaryAsync().toList()
        assertEquals(2, got.size)
        assertTrue(got[0].contentEquals(f1))
        assertTrue(got[1].contentEquals(f2))
    }

    @Test
    fun `sending after close throws`() = runTest {
        val t = InMemoryLoopbackTransport()
        t.closeAsync()
        assertFailsWith<IllegalStateException> { t.sendTextAsync("x") }
        assertFailsWith<IllegalStateException> { t.sendBinaryAsync(byteArrayOf(1)) }
    }

    @Test
    fun `factory hands out open transports`() = runTest {
        val f = InMemoryLoopbackTransportFactory()
        val t = f.connectAsync(URI.create("wss://loop"), null)
        assertTrue(t.isOpen)
        t.sendTextAsync("hi")
        t.closeAsync()
        assertEquals(listOf("hi"), t.receiveTextAsync().toList())
    }
}
