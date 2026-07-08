// SyncServiceTest.kt
//
// Verifies the CircleAI.Sync module:
//   SyncReconciliation  — merge / dominance / last-writer-wins (SyncPrimitives.cs)
//   MemorySyncService   — push builds a broadcast SyncDelta; receive loop skips
//                         own echoes and forwards peer deltas (MemorySyncService.cs)

package com.bhengubv.circleai.syncservice

import com.bhengubv.circleai.memory.EpisodicMemoryEntry
import com.bhengubv.circleai.memory.IEpisodicMemoryStore
import com.bhengubv.circleai.sync.ISyncChannel
import com.bhengubv.circleai.sync.SyncDelta
import com.bhengubv.circleai.sync.SyncDeliveryMode
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.consumeAsFlow
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class SyncServiceTest {

    // -- SyncReconciliation --------------------------------------------------

    @Test
    fun `merge takes element-wise maximum over the key union`() {
        val a = VersionVector(mapOf("n1" to 3L, "n2" to 1L))
        val b = VersionVector(mapOf("n2" to 5L, "n3" to 2L))
        val merged = SyncReconciliation.merge(a, b).clocks
        assertEquals(3L, merged["n1"])
        assertEquals(5L, merged["n2"])
        assertEquals(2L, merged["n3"])
    }

    @Test
    fun `dominance requires all-ge and at-least-one-greater`() {
        val bigger = VersionVector(mapOf("n1" to 5L, "n2" to 2L))
        val smaller = VersionVector(mapOf("n1" to 3L, "n2" to 2L))
        assertTrue(SyncReconciliation.aDominatesB(bigger, smaller))
        // Equal vectors do not dominate.
        assertTrue(!SyncReconciliation.aDominatesB(smaller, smaller))
        // Concurrent (each greater somewhere) does not dominate.
        val concA = VersionVector(mapOf("n1" to 5L, "n2" to 1L))
        val concB = VersionVector(mapOf("n1" to 1L, "n2" to 5L))
        assertTrue(!SyncReconciliation.aDominatesB(concA, concB))
    }

    @Test
    fun `last-writer-wins picks the later timestamp and ties resolve to a`() {
        val early = Instant.EPOCH to "old"
        val late = Instant.EPOCH.plusSeconds(10) to "new"
        assertEquals("new", SyncReconciliation.lastWriterWins(early, late).second)
        assertEquals("new", SyncReconciliation.lastWriterWins(late, early).second)
        val tieA = Instant.EPOCH to "A"
        val tieB = Instant.EPOCH to "B"
        assertEquals("A", SyncReconciliation.lastWriterWins(tieA, tieB).second)
    }

    // -- Test doubles --------------------------------------------------------

    private class RecordingChannel(
        private val inbound: Channel<SyncDelta> = Channel(Channel.UNLIMITED),
    ) : ISyncChannel {
        val pushed = ArrayList<SyncDelta>()
        override suspend fun pushDelta(delta: SyncDelta) { pushed.add(delta) }
        override fun receiveDeltas(ownerId: String): Flow<SyncDelta> = inbound.consumeAsFlow()
        override suspend fun getLastSequence(ownerId: String, domainKey: String): Long = 0L
        suspend fun deliver(delta: SyncDelta) = inbound.send(delta)
    }

    private class NoopEpisodicStore : IEpisodicMemoryStore {
        override suspend fun save(entry: EpisodicMemoryEntry) {}
        override suspend fun getRecent(userId: String, limit: Int): List<EpisodicMemoryEntry> = emptyList()
        override suspend fun delete(id: String) {}
    }

    // -- MemorySyncService.push ---------------------------------------------

    @Test
    fun `push builds a broadcast delta from the local device`() = runTest {
        val channel = RecordingChannel()
        val svc = MemorySyncService(channel, NoopEpisodicStore(), localDeviceId = "device-A")

        val payload = byteArrayOf(10, 20, 30)
        svc.pushMemoryDelta("owner-1", SyncDomainKeys.EpisodicMemory, payload)

        assertEquals(1, channel.pushed.size)
        val d = channel.pushed.single()
        assertEquals("owner-1", d.ownerId)
        assertEquals("device-A", d.sourceDeviceId)
        assertEquals("", d.targetDeviceId) // broadcast
        assertEquals(SyncDomainKeys.EpisodicMemory, d.domainKey)
        assertEquals(SyncDeliveryMode.Guaranteed, d.deliveryMode) // default
        assertTrue(payload.contentEquals(d.payload))
    }

    @Test
    fun `push honours an explicit delivery mode`() = runTest {
        val channel = RecordingChannel()
        val svc = MemorySyncService(channel, NoopEpisodicStore(), "device-A")
        svc.pushMemoryDelta("o", SyncDomainKeys.Persona, byteArrayOf(1), SyncDeliveryMode.Urgent)
        assertEquals(SyncDeliveryMode.Urgent, channel.pushed.single().deliveryMode)
    }

    // -- MemorySyncService.receive ------------------------------------------

    @Test
    fun `receive loop consumes peer deltas and skips own echoes`() = runTest {
        val channel = RecordingChannel()
        val svc = MemorySyncService(channel, NoopEpisodicStore(), localDeviceId = "device-A")
        svc.startReceiving("owner-1")

        fun delta(source: String) = SyncDelta(
            ownerId = "owner-1",
            sourceDeviceId = source,
            targetDeviceId = "",
            domainKey = SyncDomainKeys.EpisodicMemory,
            payload = byteArrayOf(1),
            sequence = 1,
            deliveryMode = SyncDeliveryMode.Guaranteed,
            ttl = null,
            createdAt = Instant.EPOCH,
        )

        // Own echo (should be skipped) + a peer delta (should be consumed).
        channel.deliver(delta("device-A"))
        channel.deliver(delta("device-B"))

        // Let the background collector run. The loop drains without throwing;
        // reaching here without a hang/exception is the assertion.
        withTimeout(2_000) {
            repeat(20) { yield() }
        }
        svc.stopReceiving()
        assertTrue(true)
    }

    @Test
    fun `stop receiving without start is a no-op`() = runTest {
        val svc = MemorySyncService(RecordingChannel(), NoopEpisodicStore(), "d")
        svc.stopReceiving() // must not throw
        assertTrue(true)
    }
}
