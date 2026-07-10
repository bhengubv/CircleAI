// NearLinkTest.kt
//
// Verifies the CircleAI.Networking.NearLink port:
//   - NearLinkPairingState / NearLinkPowerProfile carry every C# member in order
//   - InMemoryNearLinkRegistry: register/getDevice, allDevices ordered by friendly
//     name, pairingState default Unpaired + persists, session open/get/close +
//     activeSessions, avgRssi (-127.0 when empty, mean otherwise)
//   - NearLinkTransport: kind NearLink, isAvailable tracks adapter, start hands the
//     inbound channel to the adapter, send delegates to adapter.send, adapter-pushed
//     payloads surface via receive, stop stops the adapter + ends the receive flow

package com.bhengubv.circleai.networking.nearlink

import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.channels.SendChannel
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
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class NearLinkTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    /** Fake adapter: captures the inbound channel + sent payloads; can push inbound. */
    private class FakeNearLinkAdapter(override var isAvailable: Boolean = true) : INearLinkAdapter {
        var inbound: SendChannel<NetworkPayload>? = null
        val sent = mutableListOf<NetworkPayload>()
        var stopped = false

        override suspend fun start(inbound: SendChannel<NetworkPayload>) {
            this.inbound = inbound
        }

        override suspend fun stop() {
            stopped = true
        }

        override suspend fun send(payload: NetworkPayload) {
            sent.add(payload)
        }
    }

    // -----------------------------------------------------------------------
    // Enums
    // -----------------------------------------------------------------------

    @Test
    fun `enums carry all members in C# order`() {
        assertEquals(
            listOf("Unpaired", "Pairing", "Paired", "PairingFailed"),
            NearLinkPairingState.entries.map { it.name },
        )
        assertEquals(
            listOf("LowEnergy", "Balanced", "HighThroughput"),
            NearLinkPowerProfile.entries.map { it.name },
        )
    }

    // -----------------------------------------------------------------------
    // InMemoryNearLinkRegistry
    // -----------------------------------------------------------------------

    @Test
    fun `register + getDevice round-trips and allDevices ordered by friendly name`() {
        val reg = InMemoryNearLinkRegistry()
        reg.register(NearLinkDevice("d1", "Zephyr", "HW", "1.0"))
        reg.register(NearLinkDevice("d2", "Aria", "HW", "2.0"))

        assertEquals("Zephyr", reg.getDevice("d1")?.friendlyName)
        assertNull(reg.getDevice("absent"))
        assertEquals(listOf("Aria", "Zephyr"), reg.allDevices.map { it.friendlyName })
    }

    @Test
    fun `pairingState defaults to Unpaired and setPairingState persists`() {
        val reg = InMemoryNearLinkRegistry()
        assertEquals(NearLinkPairingState.Unpaired, reg.pairingState("d1"))
        reg.setPairingState("d1", NearLinkPairingState.Paired)
        assertEquals(NearLinkPairingState.Paired, reg.pairingState("d1"))
    }

    @Test
    fun `session open + get + close and activeSessions reflects live sessions`() {
        val reg = InMemoryNearLinkRegistry()
        val s = NearLinkSession("s1", "d1", NearLinkPowerProfile.Balanced, t0)
        reg.openSession(s)
        assertEquals(s, reg.getSession("s1"))
        assertEquals(listOf("s1"), reg.activeSessions.map { it.sessionId })
        reg.closeSession("s1")
        assertNull(reg.getSession("s1"))
        assertTrue(reg.activeSessions.isEmpty())
    }

    @Test
    fun `avgRssi averages samples and is -127 when empty`() {
        val reg = InMemoryNearLinkRegistry()
        assertEquals(-127.0, reg.avgRssi("d1"), 1e-9)
        reg.recordThroughput(NearLinkThroughputSample("d1", 100.0, 50.0, -40, t0))
        reg.recordThroughput(NearLinkThroughputSample("d1", 200.0, 60.0, -60, t0))
        reg.recordThroughput(NearLinkThroughputSample("other", 1.0, 1.0, -99, t0))
        assertEquals(-50.0, reg.avgRssi("d1"), 1e-9)
    }

    // -----------------------------------------------------------------------
    // NearLinkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability track the adapter`() {
        val up = NearLinkTransport(FakeNearLinkAdapter(isAvailable = true))
        val down = NearLinkTransport(FakeNearLinkAdapter(isAvailable = false))
        assertEquals(TransportKind.NearLink, up.kind)
        assertTrue(up.isAvailable)
        assertFalse(down.isAvailable)
    }

    @Test
    fun `start hands the inbound channel to the adapter and send delegates`() = runTest {
        val adapter = FakeNearLinkAdapter()
        val transport = NearLinkTransport(adapter)
        transport.start()
        assertTrue(adapter.inbound != null)

        val p = NetworkPayload.create(byteArrayOf(1), destinationId = "peer", now = { t0 })
        transport.send(p)
        assertEquals(1, adapter.sent.size)
        assertSame(p, adapter.sent[0])
    }

    @Test
    fun `adapter-pushed payloads surface via receive`() = runTest {
        val adapter = FakeNearLinkAdapter()
        val transport = NearLinkTransport(adapter)
        transport.start()

        val p = NetworkPayload.create(byteArrayOf(7, 8), destinationId = "peer", now = { t0 })
        withTimeout(2_000) {
            adapter.inbound!!.send(p)
            assertEquals(p, transport.receive().first())
        }
    }

    @Test
    fun `stop stops the adapter and ends the receive flow`() = runTest {
        val adapter = FakeNearLinkAdapter()
        val transport = NearLinkTransport(adapter)
        transport.start()
        withTimeout(2_000) {
            val collected = launch { transport.receive().toList() }
            yield()
            transport.stop()
            collected.join()
        }
        assertTrue(adapter.stopped)
    }
}
