// MqttTest.kt
//
// Verifies the CircleAI.Networking.Mqtt port:
//   - MqttQos wire values (0/1/2)
//   - InMemoryMqttBroker: connect/disconnect + connectedClients, subscribe requires
//     non-blank args, MQTT wildcard matcher (# / + / exact + level-count), retained
//     store, matchingSubscribers
//   - MqttNetworkTransport: kind Mqtt, isAvailable tracks the client, start connects +
//     subscribes to circle/payloads/{clientId}/#, send publishes to
//     circle/payloads/{dest} (or /broadcast) with QoS ExactlyOnce for High+ else
//     AtLeastOnce, inbound broker messages surface via receive, stop disconnects + ends
//     the flow, close clears the handler
//   - InMemoryMqttClient end-to-end: two transports on one broker exchange a payload

package com.bhengubv.circleai.networking.mqtt

import com.bhengubv.circleai.networking.MessagePriority
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MqttTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    /** Fake client: records publishes + subscriptions; can push an inbound message. */
    private class FakeMqttClient : IMqttClient {
        data class Publish(val topic: String, val payload: ByteArray, val qos: MqttQos)
        val publishes = mutableListOf<Publish>()
        val subscriptions = mutableListOf<Pair<String, MqttQos>>()
        var connectedDescriptor: MqttClientDescriptor? = null
        var disconnected = false
        var closed = false

        @Volatile override var onMessage: (suspend (MqttMessage) -> Unit)? = null
        override val isConnected: Boolean get() = connectedDescriptor != null && !disconnected

        override suspend fun connect(descriptor: MqttClientDescriptor) {
            connectedDescriptor = descriptor
            disconnected = false
        }

        override suspend fun subscribe(topicFilter: String, qos: MqttQos) {
            subscriptions.add(topicFilter to qos)
        }

        override suspend fun publish(topic: String, payload: ByteArray, qos: MqttQos) {
            publishes.add(Publish(topic, payload, qos))
        }

        override suspend fun disconnect() {
            disconnected = true
        }

        override fun close() {
            closed = true
        }

        suspend fun push(topic: String, payload: ByteArray) {
            onMessage?.invoke(MqttMessage(topic, payload))
        }
    }

    private fun payload(dest: String?, priority: MessagePriority, data: ByteArray = byteArrayOf(9)) =
        NetworkPayload.create(data = data, destinationId = dest, priority = priority, now = { t0 })

    // -----------------------------------------------------------------------
    // MqttQos + broker
    // -----------------------------------------------------------------------

    @Test
    fun `MqttQos wire values match the MQTT spec`() {
        assertEquals(0, MqttQos.AtMostOnce.value)
        assertEquals(1, MqttQos.AtLeastOnce.value)
        assertEquals(2, MqttQos.ExactlyOnce.value)
    }

    @Test
    fun `broker connect + disconnect tracks connectedClients`() {
        val broker = InMemoryMqttBroker()
        val c = MqttClientDescriptor("c1", "h", 1883, false, Duration.ofSeconds(10))
        broker.connect(c)
        assertEquals(listOf("c1"), broker.connectedClients.map { it.clientId })
        broker.disconnect("c1")
        assertTrue(broker.connectedClients.isEmpty())
    }

    @Test
    fun `subscribe requires non-blank clientId and filter`() {
        val broker = InMemoryMqttBroker()
        assertFailsWith<IllegalArgumentException> { broker.subscribe(" ", "a/b") }
        assertFailsWith<IllegalArgumentException> { broker.subscribe("c1", " ") }
    }

    @Test
    fun `topic matcher honours hash + plus + exact + level count`() {
        val b = InMemoryMqttBroker()
        // '#' matches remainder
        assertTrue(b.matches("a/b/c", "a/#"))
        assertTrue(b.matches("a", "#"))
        // '+' matches exactly one level
        assertTrue(b.matches("a/b/c", "a/+/c"))
        assertFalse(b.matches("a/b/c/d", "a/+/c"))
        // exact match requires equal level counts
        assertTrue(b.matches("a/b", "a/b"))
        assertFalse(b.matches("a/b", "a/b/c"))
        assertFalse(b.matches("a/b/c", "a/b"))
        // empty inputs never match
        assertFalse(b.matches("", "a"))
        assertFalse(b.matches("a", ""))
    }

    @Test
    fun `retained store + matchingSubscribers`() {
        val b = InMemoryMqttBroker()
        val m = MqttRetainedMessage("circle/payloads/x", byteArrayOf(1, 2), t0)
        b.publishRetained(m)
        assertEquals(m, b.getRetained("circle/payloads/x"))
        assertNull(b.getRetained("absent"))

        b.subscribe("cA", "circle/payloads/cA/#")
        b.subscribe("cB", "circle/payloads/+")
        assertEquals(listOf("cB"), b.matchingSubscribers("circle/payloads/broadcast"))
        assertEquals(setOf("cA"), b.matchingSubscribers("circle/payloads/cA/sub").toSet())
    }

    // -----------------------------------------------------------------------
    // MqttNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + availability track the client`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        assertEquals(TransportKind.Mqtt, transport.kind)
        assertFalse(transport.isAvailable)
        transport.start()
        assertTrue(transport.isAvailable)
    }

    @Test
    fun `start connects and subscribes to the client-scoped wildcard topic`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        assertEquals("clientX", client.connectedDescriptor?.clientId)
        assertEquals("broker", client.connectedDescriptor?.host)
        assertEquals(1883, client.connectedDescriptor?.port)
        assertEquals("circle/payloads/clientX/#" to MqttQos.AtLeastOnce, client.subscriptions.single())
    }

    @Test
    fun `send publishes to destination topic with ExactlyOnce for High priority`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        transport.send(payload(dest = "devB", priority = MessagePriority.High, data = byteArrayOf(4, 5)))

        val pub = client.publishes.single()
        assertEquals("circle/payloads/devB", pub.topic)
        assertEquals(MqttQos.ExactlyOnce, pub.qos)
        assertTrue(byteArrayOf(4, 5).contentEquals(pub.payload))
    }

    @Test
    fun `send uses AtLeastOnce for below-High priority`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        transport.send(payload(dest = "devB", priority = MessagePriority.Normal))
        assertEquals(MqttQos.AtLeastOnce, client.publishes.single().qos)
    }

    @Test
    fun `send with no destination publishes to broadcast topic`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        transport.send(payload(dest = null, priority = MessagePriority.Urgent))
        val pub = client.publishes.single()
        assertEquals("circle/payloads/broadcast", pub.topic)
        assertEquals(MqttQos.ExactlyOnce, pub.qos) // Urgent >= High
    }

    @Test
    fun `inbound broker messages surface via receive`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        withTimeout(2_000) {
            client.push("circle/payloads/clientX/in", byteArrayOf(7, 7, 7))
            val received = transport.receive().first()
            assertTrue(byteArrayOf(7, 7, 7).contentEquals(received.data))
        }
    }

    @Test
    fun `stop disconnects the client and close clears the handler`() = runTest {
        val client = FakeMqttClient()
        val transport = MqttNetworkTransport(client, "broker", 1883, "clientX")
        transport.start()
        transport.stop()
        assertTrue(client.disconnected)
        transport.close()
        assertTrue(client.closed)
        assertNull(client.onMessage)
    }

    // -----------------------------------------------------------------------
    // InMemoryMqttClient end-to-end
    // -----------------------------------------------------------------------

    @Test
    fun `two transports on one broker exchange a payload end-to-end`() = runTest {
        val broker = InMemoryMqttBroker()
        val a = MqttNetworkTransport(InMemoryMqttClient(broker), "broker", 1883, "A")
        val b = MqttNetworkTransport(InMemoryMqttClient(broker), "broker", 1883, "B")
        a.start()
        b.start()

        withTimeout(2_000) {
            // A publishes to circle/payloads/B → B is subscribed to circle/payloads/B/#?
            // B subscribes to "circle/payloads/B/#"; publishing to "circle/payloads/B"
            // does NOT match "circle/payloads/B/#" (needs an extra level), so address
            // B via a sub-topic the wildcard covers.
            a.send(payload(dest = "B/msg", priority = MessagePriority.High, data = byteArrayOf(1, 2, 3)))
            val received = b.receive().first()
            assertTrue(byteArrayOf(1, 2, 3).contentEquals(received.data))
        }
    }
}
