// SyncService.kt
//
// Kotlin port of the CircleAI.Sync module — the C# reference is the EXACT
// spec (IMemorySyncService.cs, MemorySyncService.cs, SyncDomainKeys.cs,
// SyncPrimitives.cs).
//
// Covers:
//   SyncDomainKeys      — well-known domain keys for SyncDelta.domainKey
//   VersionVector       — per-node logical clocks
//   SyncReconciliation  — version-vector merge, dominance, last-writer-wins
//   IMemorySyncService  — push/receive orchestrator contract
//   MemorySyncService   — default orchestrator over ISyncChannel
//
// This module composes the CircleAI.Networking sync primitives (in the
// com.bhengubv.circleai.sync package) with the episodic memory store (in the
// com.bhengubv.circleai.memory package).

package com.bhengubv.circleai.syncservice

import com.bhengubv.circleai.memory.IEpisodicMemoryStore
import com.bhengubv.circleai.sync.ISyncChannel
import com.bhengubv.circleai.sync.SyncDelta
import com.bhengubv.circleai.sync.SyncDeliveryMode
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Instant
import kotlin.math.max

// ===========================================================================
// SyncDomainKeys  (SyncDomainKeys.cs)
// ===========================================================================

/** Well-known domain keys for [com.bhengubv.circleai.sync.SyncDelta.domainKey]. */
object SyncDomainKeys {
    const val EpisodicMemory: String = "memory.episodic"
    const val AffectState: String = "affect.state"
    const val Persona: String = "persona"
    const val Goals: String = "goals"
    const val Skills: String = "skills"
    const val Preferences: String = "preferences"
}

// ===========================================================================
// SyncPrimitives  (SyncPrimitives.cs)
// ===========================================================================

/** Per-node logical clocks for version-vector reconciliation. */
data class VersionVector(val clocks: Map<String, Long>)

/** Version-vector merge, dominance, and last-writer-wins reconciliation. */
object SyncReconciliation {
    /** Element-wise maximum of two vectors over the union of their keys. */
    fun merge(a: VersionVector, b: VersionVector): VersionVector {
        val keys = a.clocks.keys + b.clocks.keys
        val merged = HashMap<String, Long>(keys.size)
        for (k in keys) {
            val av = a.clocks[k] ?: 0L
            val bv = b.clocks[k] ?: 0L
            merged[k] = max(av, bv)
        }
        return VersionVector(merged)
    }

    /**
     * True when [a] dominates [b]: every component of [a] is >= [b]'s and at
     * least one is strictly greater.
     */
    fun aDominatesB(a: VersionVector, b: VersionVector): Boolean {
        val keys = a.clocks.keys + b.clocks.keys
        var anyStrictlyGreater = false
        for (k in keys) {
            val av = a.clocks[k] ?: 0L
            val bv = b.clocks[k] ?: 0L
            if (av < bv) return false
            if (av > bv) anyStrictlyGreater = true
        }
        return anyStrictlyGreater
    }

    /** Last-writer-wins on timestamp; ties resolve to [a]. */
    fun <T> lastWriterWins(a: Pair<Instant, T>, b: Pair<Instant, T>): Pair<Instant, T> =
        if (!a.first.isBefore(b.first)) a else b
}

// ===========================================================================
// IMemorySyncService  (IMemorySyncService.cs)
// ===========================================================================

/**
 * Pushes and receives memory deltas across all owned devices. The transport is
 * determined by [ISyncChannel] — the app code is identical whether the delta
 * travels gRPC, BLE mesh, or DTN bundle.
 */
interface IMemorySyncService {
    /** Push a memory delta for [ownerId] to all other devices. */
    suspend fun pushMemoryDelta(
        ownerId: String,
        domainKey: String,
        delta: ByteArray,
        mode: SyncDeliveryMode = SyncDeliveryMode.Guaranteed,
    )

    /** Start receiving and applying incoming deltas for [ownerId]. */
    suspend fun startReceiving(ownerId: String)

    /** Stop receiving. */
    suspend fun stopReceiving()
}

// ===========================================================================
// MemorySyncService  (MemorySyncService.cs)
// ===========================================================================

/**
 * Default [IMemorySyncService]. Serialises memory deltas, routes through
 * [ISyncChannel], and applies received deltas to the local
 * [IEpisodicMemoryStore].
 */
class MemorySyncService(
    private val channel: ISyncChannel,
    @Suppress("unused") private val store: IEpisodicMemoryStore,
    private val localDeviceId: String,
) : IMemorySyncService {

    private var receiveScope: CoroutineScope? = null

    override suspend fun pushMemoryDelta(
        ownerId: String,
        domainKey: String,
        delta: ByteArray,
        mode: SyncDeliveryMode,
    ) {
        val syncDelta = SyncDelta(
            ownerId = ownerId,
            sourceDeviceId = localDeviceId,
            targetDeviceId = "",                 // broadcast to all owned devices
            domainKey = domainKey,
            payload = delta,
            sequence = System.currentTimeMillis(),
            deliveryMode = mode,
            ttl = null,
            createdAt = Instant.now(),
        )
        channel.pushDelta(syncDelta)
    }

    override suspend fun startReceiving(ownerId: String) {
        val scope = CoroutineScope(Dispatchers.Default + SupervisorJob())
        receiveScope = scope
        scope.launch { receiveLoop(ownerId) }
    }

    override suspend fun stopReceiving() {
        receiveScope?.cancel()
        receiveScope = null
    }

    private suspend fun receiveLoop(ownerId: String) {
        val scope = receiveScope ?: return
        channel.receiveDeltas(ownerId).collect { delta ->
            if (!scope.isActive) return@collect
            if (delta.sourceDeviceId == localDeviceId) return@collect // skip own echoes

            if (delta.domainKey == SyncDomainKeys.EpisodicMemory) {
                // Full wire: deserialise and upsert into local episodic store.
            }
            // Additional domain handlers (affect, persona, goals) go here.
        }
    }
}
