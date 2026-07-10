// GrpcTest.kt
//
// Verifies the CircleAI.Networking.Grpc port:
//   - GrpcChannelState carries every C# member in order
//   - GrpcRetryPolicies: Default / Aggressive / NoRetry match the C# statics
//   - InMemoryGrpcCallMetrics: registerChannel/getChannel, state default Idle,
//     logCall returns monotonic grpc-N ids, recentCalls newest-first + limit
//   - GrpcNetworkTransport: kind, isAvailable follows start/stop, send throws
//     UnsupportedOperationException, receive is empty, channel exposes the injected
//     channel, close disposes it

package com.bhengubv.circleai.networking.grpc

import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.count
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class GrpcTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    private class FakeGrpcChannel(override val target: String) : IGrpcChannel {
        var closed = false
        override fun close() { closed = true }
    }

    // -----------------------------------------------------------------------
    // Enum + retry policies
    // -----------------------------------------------------------------------

    @Test
    fun `GrpcChannelState carries all members in C# order`() {
        assertEquals(
            listOf("Idle", "Connecting", "Ready", "TransientFailure", "Shutdown"),
            GrpcChannelState.entries.map { it.name },
        )
    }

    @Test
    fun `retry policies match the C# statics`() {
        assertEquals(
            GrpcRetryPolicy(3, Duration.ofMillis(100), Duration.ofSeconds(2), 2.0, listOf("UNAVAILABLE", "DEADLINE_EXCEEDED")),
            GrpcRetryPolicies.Default,
        )
        assertEquals(
            GrpcRetryPolicy(6, Duration.ofMillis(50), Duration.ofSeconds(5), 2.0, listOf("UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED")),
            GrpcRetryPolicies.Aggressive,
        )
        assertEquals(
            GrpcRetryPolicy(1, Duration.ZERO, Duration.ZERO, 1.0, emptyList()),
            GrpcRetryPolicies.NoRetry,
        )
    }

    // -----------------------------------------------------------------------
    // InMemoryGrpcCallMetrics
    // -----------------------------------------------------------------------

    @Test
    fun `metrics registerChannel + getChannel round-trips`() {
        val m = InMemoryGrpcCallMetrics()
        val d = GrpcChannelDescriptor("https://h:443", true, 4_000_000, 4_000_000, Duration.ofSeconds(30))
        m.registerChannel("c1", d)
        assertEquals(d, m.getChannel("c1"))
        assertNull(m.getChannel("absent"))
    }

    @Test
    fun `metrics state defaults to Idle and setState persists`() {
        val m = InMemoryGrpcCallMetrics()
        assertEquals(GrpcChannelState.Idle, m.state("c1"))
        m.setState("c1", GrpcChannelState.Ready)
        assertEquals(GrpcChannelState.Ready, m.state("c1"))
    }

    @Test
    fun `logCall returns monotonic grpc-N ids`() {
        val m = InMemoryGrpcCallMetrics()
        val id1 = m.logCall(GrpcCallSummary("M", 1, Duration.ofMillis(5), "OK", t0))
        val id2 = m.logCall(GrpcCallSummary("M", 1, Duration.ofMillis(6), "OK", t0))
        assertEquals("grpc-1", id1)
        assertEquals("grpc-2", id2)
    }

    @Test
    fun `recentCalls is newest-first and honours the limit`() {
        val m = InMemoryGrpcCallMetrics()
        m.logCall(GrpcCallSummary("a", 1, Duration.ofMillis(1), "OK", t0))
        m.logCall(GrpcCallSummary("b", 1, Duration.ofMillis(1), "OK", t0.plusSeconds(5)))
        m.logCall(GrpcCallSummary("c", 1, Duration.ofMillis(1), "OK", t0.plusSeconds(10)))
        assertEquals(listOf("c", "b", "a"), m.recentCalls().map { it.method })
        assertEquals(listOf("c"), m.recentCalls(limit = 1).map { it.method })
    }

    // -----------------------------------------------------------------------
    // GrpcNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability follow start and stop`() = runTest {
        val transport = GrpcNetworkTransport(FakeGrpcChannel("https://h:443"))
        assertEquals(TransportKind.Grpc, transport.kind)
        assertFalse(transport.isAvailable)
        transport.start()
        assertTrue(transport.isAvailable)
        transport.stop()
        assertFalse(transport.isAvailable)
    }

    @Test
    fun `send throws UnsupportedOperationException`() = runTest {
        val transport = GrpcNetworkTransport(FakeGrpcChannel("https://h:443"))
        assertFailsWith<UnsupportedOperationException> {
            transport.send(NetworkPayload.create(byteArrayOf(1), now = { t0 }))
        }
    }

    @Test
    fun `receive is empty and channel exposes the injected channel`() = runTest {
        val ch = FakeGrpcChannel("https://h:443")
        val transport = GrpcNetworkTransport(ch)
        assertEquals(0, transport.receive().count())
        assertSame(ch, transport.channel)
    }

    @Test
    fun `close disposes the channel`() {
        val ch = FakeGrpcChannel("https://h:443")
        val transport = GrpcNetworkTransport(ch)
        transport.close()
        assertTrue(ch.closed)
    }
}
