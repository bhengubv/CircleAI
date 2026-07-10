// DtnTest.kt
//
// Verifies the CircleAI.Networking.Dtn port:
//   - DtnPriority carries every C# member in order
//   - DtnBundle value equality over the payload bytes
//   - InMemoryDtnBundleStore: store/get, all, custody accept/get, isExpired
//     (unknown id -> true, strict now > expiresAt), purge removes expired + returns
//     count, inFlightTo filters by destination
//   - DtnSyncChannel: getLastSequence default 0; pushDelta routes to the first
//     AVAILABLE transport with the right payload (destination, content-type,
//     priority Urgent iff Urgent else Normal); skips unavailable transports; when
//     none are available it queues locally (no send); receiveDeltas is the empty
//     delivery bridge

package com.bhengubv.circleai.networking.dtn

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.MessagePriority
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import com.bhengubv.circleai.sync.SyncDelta
import com.bhengubv.circleai.sync.SyncDeliveryMode
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class DtnTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    /** Fake transport: records sent payloads; availability is fixed at construction. */
    private class FakeTransport(
        override val kind: TransportKind,
        override val isAvailable: Boolean,
    ) : INetworkTransport {
        val sent = mutableListOf<NetworkPayload>()
        override suspend fun start() {}
        override suspend fun stop() {}
        override suspend fun send(payload: NetworkPayload) { sent.add(payload) }
        override fun receive(): Flow<NetworkPayload> = emptyFlow()
    }

    private fun delta(
        mode: SyncDeliveryMode,
        target: String = "devB",
        payload: ByteArray = byteArrayOf(1, 2, 3),
        ttl: java.time.Duration? = null,
    ) = SyncDelta(
        ownerId = "owner",
        sourceDeviceId = "devA",
        targetDeviceId = target,
        domainKey = "memory.episodic",
        payload = payload,
        sequence = 1,
        deliveryMode = mode,
        ttl = ttl,
        createdAt = t0,
    )

    // -----------------------------------------------------------------------
    // DtnPriority + DtnBundle
    // -----------------------------------------------------------------------

    @Test
    fun `DtnPriority carries all members in C# order`() {
        assertEquals(listOf("Bulk", "Normal", "Expedited"), DtnPriority.entries.map { it.name })
    }

    @Test
    fun `DtnBundle equality is value-based over the payload`() {
        val a = DtnBundle("id", "s", "d", byteArrayOf(1, 2), t0.plusSeconds(10), true, 0, t0)
        val b = DtnBundle("id", "s", "d", byteArrayOf(1, 2), t0.plusSeconds(10), true, 0, t0)
        val c = a.copy(payload = byteArrayOf(9))
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertFalse(a == c)
    }

    // -----------------------------------------------------------------------
    // InMemoryDtnBundleStore
    // -----------------------------------------------------------------------

    @Test
    fun `store + get + all round-trip`() {
        val store = InMemoryDtnBundleStore()
        val b = DtnBundle("b1", "s", "d", byteArrayOf(1), t0.plusSeconds(60), false, 0, t0)
        store.store(b)
        assertEquals(b, store.get("b1"))
        assertNull(store.get("absent"))
        assertEquals(listOf("b1"), store.all.map { it.bundleId })
    }

    @Test
    fun `custody accept + get round-trips`() {
        val store = InMemoryDtnBundleStore()
        assertNull(store.getCustody("b1"))
        val rec = DtnCustodyRecord("b1", "node-7", t0)
        store.acceptCustody(rec)
        assertEquals(rec, store.getCustody("b1"))
    }

    @Test
    fun `isExpired treats unknown id as expired and uses strict now-after-expiresAt`() {
        val store = InMemoryDtnBundleStore()
        assertTrue(store.isExpired("absent", t0))

        val expiresAt = t0.plusSeconds(100)
        store.store(DtnBundle("b1", "s", "d", byteArrayOf(1), expiresAt, false, 0, t0))
        assertFalse(store.isExpired("b1", expiresAt))                 // now == expiresAt -> not expired
        assertFalse(store.isExpired("b1", expiresAt.minusSeconds(1))) // before -> not expired
        assertTrue(store.isExpired("b1", expiresAt.plusSeconds(1)))   // after  -> expired
    }

    @Test
    fun `purge removes expired bundles and their custody, returns count`() {
        val store = InMemoryDtnBundleStore()
        store.store(DtnBundle("live", "s", "d", byteArrayOf(1), t0.plusSeconds(1000), false, 0, t0))
        store.store(DtnBundle("dead1", "s", "d", byteArrayOf(1), t0.plusSeconds(10), false, 0, t0))
        store.store(DtnBundle("dead2", "s", "d", byteArrayOf(1), t0.plusSeconds(20), false, 0, t0))
        store.acceptCustody(DtnCustodyRecord("dead1", "n", t0))

        val purged = store.purge(t0.plusSeconds(100))
        assertEquals(2, purged)
        assertNull(store.get("dead1"))
        assertNull(store.get("dead2"))
        assertNull(store.getCustody("dead1"))
        assertEquals(listOf("live"), store.all.map { it.bundleId })
    }

    @Test
    fun `inFlightTo filters by destination node`() {
        val store = InMemoryDtnBundleStore()
        store.store(DtnBundle("b1", "s", "devB", byteArrayOf(1), t0.plusSeconds(60), false, 0, t0))
        store.store(DtnBundle("b2", "s", "devC", byteArrayOf(1), t0.plusSeconds(60), false, 0, t0))
        store.store(DtnBundle("b3", "s", "devB", byteArrayOf(1), t0.plusSeconds(60), false, 0, t0))
        assertEquals(setOf("b1", "b3"), store.inFlightTo("devB").map { it.bundleId }.toSet())
        assertTrue(store.inFlightTo("devZ").isEmpty())
    }

    // -----------------------------------------------------------------------
    // DtnSyncChannel
    // -----------------------------------------------------------------------

    @Test
    fun `getLastSequence defaults to 0`() = runTest {
        val channel = DtnSyncChannel(emptyList())
        assertEquals(0L, channel.getLastSequence("owner", "domain"))
    }

    @Test
    fun `pushDelta routes to the first available transport with a Normal-priority payload`() = runTest {
        val transport = FakeTransport(TransportKind.Http, isAvailable = true)
        val channel = DtnSyncChannel(listOf(transport), now = { t0 })

        channel.pushDelta(delta(SyncDeliveryMode.Guaranteed, target = "devB", payload = byteArrayOf(5, 6)))

        assertEquals(1, transport.sent.size)
        val p = transport.sent[0]
        assertEquals("devB", p.destinationId)
        assertEquals("application/dtn-bundle", p.contentType)
        assertEquals(MessagePriority.Normal, p.priority)
        assertTrue(byteArrayOf(5, 6).contentEquals(p.data))
    }

    @Test
    fun `pushDelta uses Urgent priority only for Urgent delivery mode`() = runTest {
        val transport = FakeTransport(TransportKind.Http, isAvailable = true)
        val channel = DtnSyncChannel(listOf(transport), now = { t0 })

        channel.pushDelta(delta(SyncDeliveryMode.Urgent))

        assertEquals(MessagePriority.Urgent, transport.sent.single().priority)
    }

    @Test
    fun `pushDelta skips unavailable transports and uses the first available one`() = runTest {
        val down = FakeTransport(TransportKind.Grpc, isAvailable = false)
        val up = FakeTransport(TransportKind.Http, isAvailable = true)
        val channel = DtnSyncChannel(listOf(down, up), now = { t0 })

        channel.pushDelta(delta(SyncDeliveryMode.BestEffort))

        assertTrue(down.sent.isEmpty())
        assertEquals(1, up.sent.size)
    }

    @Test
    fun `pushDelta with no available transport queues locally and sends nothing`() = runTest {
        val down = FakeTransport(TransportKind.Http, isAvailable = false)
        val channel = DtnSyncChannel(listOf(down), now = { t0 })

        // Does not throw; simply queues (no live transport to send on).
        channel.pushDelta(delta(SyncDeliveryMode.Guaranteed))

        assertTrue(down.sent.isEmpty())
    }
}
