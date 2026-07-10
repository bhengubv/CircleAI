// BluetoothTest.kt
//
// Verifies the CircleAI.Networking.Bluetooth port:
//   - BluetoothConnectionState carries every C# member in order
//   - BluetoothCapabilityProfiles: Le5 / Le4 / Classic constants match the C# statics
//   - InMemoryBluetoothTransportRegistry: register/getEndpoint, allEndpoints ordered
//     by name, state default Disconnected, avgKbpsRead (0.0 when empty)
//   - BluetoothNetworkTransport: kind, isAvailable tracks adapter, start hands the
//     inbound channel to the adapter, send delegates to adapter.write, adapter-pushed
//     payloads surface via receive, stop stops the adapter + ends the receive flow

package com.bhengubv.circleai.networking.bluetooth

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

class BluetoothTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    /** Fake adapter: captures the inbound channel + written payloads; can push inbound. */
    private class FakeBleAdapter(override var isAvailable: Boolean = true) : IBleGattAdapter {
        var inbound: SendChannel<NetworkPayload>? = null
        val written = mutableListOf<NetworkPayload>()
        var stopped = false

        override suspend fun start(inbound: SendChannel<NetworkPayload>) {
            this.inbound = inbound
        }

        override suspend fun stop() {
            stopped = true
        }

        override suspend fun write(payload: NetworkPayload) {
            written.add(payload)
        }
    }

    // -----------------------------------------------------------------------
    // Enum + capability profiles
    // -----------------------------------------------------------------------

    @Test
    fun `BluetoothConnectionState carries all members in C# order`() {
        assertEquals(
            listOf("Disconnected", "Discovering", "Connecting", "Connected", "Failed"),
            BluetoothConnectionState.entries.map { it.name },
        )
    }

    @Test
    fun `capability profiles match the C# statics`() {
        assertEquals(
            BluetoothCapabilityProfile(247, true, true, listOf("GATT", "L2CAP")),
            BluetoothCapabilityProfiles.Le5,
        )
        assertEquals(
            BluetoothCapabilityProfile(23, true, false, listOf("GATT")),
            BluetoothCapabilityProfiles.Le4,
        )
        assertEquals(
            BluetoothCapabilityProfile(1024, true, false, listOf("SPP", "RFCOMM")),
            BluetoothCapabilityProfiles.Classic,
        )
    }

    // -----------------------------------------------------------------------
    // InMemoryBluetoothTransportRegistry
    // -----------------------------------------------------------------------

    @Test
    fun `registry register + getEndpoint round-trips and allEndpoints ordered by name`() {
        val reg = InMemoryBluetoothTransportRegistry()
        reg.register(BluetoothEndpointDescriptor("d1", "Zephyr", "AA:BB", listOf("s1")))
        reg.register(BluetoothEndpointDescriptor("d2", "Aria", "CC:DD", emptyList()))

        assertEquals("Zephyr", reg.getEndpoint("d1")?.name)
        assertNull(reg.getEndpoint("absent"))
        assertEquals(listOf("Aria", "Zephyr"), reg.allEndpoints.map { it.name })
    }

    @Test
    fun `state defaults to Disconnected and setState persists`() {
        val reg = InMemoryBluetoothTransportRegistry()
        assertEquals(BluetoothConnectionState.Disconnected, reg.state("d1"))
        reg.setState("d1", BluetoothConnectionState.Connected)
        assertEquals(BluetoothConnectionState.Connected, reg.state("d1"))
    }

    @Test
    fun `avgKbpsRead averages samples and is 0 when empty`() {
        val reg = InMemoryBluetoothTransportRegistry()
        assertEquals(0.0, reg.avgKbpsRead("d1"), 1e-9)
        reg.recordThroughput(BluetoothThroughputSample("d1", 100.0, 50.0, t0))
        reg.recordThroughput(BluetoothThroughputSample("d1", 200.0, 60.0, t0))
        reg.recordThroughput(BluetoothThroughputSample("other", 999.0, 1.0, t0))
        assertEquals(150.0, reg.avgKbpsRead("d1"), 1e-9)
    }

    // -----------------------------------------------------------------------
    // BluetoothNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability track the adapter`() {
        val up = BluetoothNetworkTransport(FakeBleAdapter(isAvailable = true))
        val down = BluetoothNetworkTransport(FakeBleAdapter(isAvailable = false))
        assertEquals(TransportKind.Bluetooth, up.kind)
        assertTrue(up.isAvailable)
        assertFalse(down.isAvailable)
    }

    @Test
    fun `start hands the inbound channel to the adapter and send delegates to write`() = runTest {
        val adapter = FakeBleAdapter()
        val transport = BluetoothNetworkTransport(adapter)
        transport.start()
        assertTrue(adapter.inbound != null)

        val p = NetworkPayload.create(byteArrayOf(1), destinationId = "peer", now = { t0 })
        transport.send(p)
        assertEquals(1, adapter.written.size)
        assertSame(p, adapter.written[0])
    }

    @Test
    fun `adapter-pushed payloads surface via receive`() = runTest {
        val adapter = FakeBleAdapter()
        val transport = BluetoothNetworkTransport(adapter)
        transport.start()

        val p = NetworkPayload.create(byteArrayOf(7, 8), destinationId = "peer", now = { t0 })
        withTimeout(2_000) {
            adapter.inbound!!.send(p)
            val received = transport.receive().first()
            assertEquals(p, received)
        }
    }

    @Test
    fun `stop stops the adapter and ends the receive flow`() = runTest {
        val adapter = FakeBleAdapter()
        val transport = BluetoothNetworkTransport(adapter)
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
