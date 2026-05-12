// Sync.kt
//
// Kotlin port of Circle.AI.Networking sync primitives.
//
// Covers:
//   SyncDeliveryMode   — enum: BestEffort | Reliable | Guaranteed
//   SyncDomainKeys     — well-known domain key constants
//   SyncDelta          — an incremental state change for cross-device sync
//   ISyncChannel       — the cross-device continuity primitive

package com.bhengubv.circleai.sync

import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant

// ---------------------------------------------------------------------------
// SyncDeliveryMode
// ---------------------------------------------------------------------------

/** Requested delivery guarantee for a [SyncDelta]. */
enum class SyncDeliveryMode {
    /** Fire and forget — no delivery confirmation required. */
    BestEffort,
    /** Delivered at least once; duplicates are possible. */
    Reliable,
    /** Delivered exactly once in order; higher overhead. */
    Guaranteed,
}

// ---------------------------------------------------------------------------
// SyncDomainKeys
// ---------------------------------------------------------------------------

/** Well-known [SyncDelta.domainKey] constants. */
object SyncDomainKeys {
    const val MemoryEpisodic = "memory.episodic"
    const val AffectState    = "affect.state"
    const val Persona        = "persona"
    const val Goals          = "goals"
    const val Identity       = "identity"
}

// ---------------------------------------------------------------------------
// SyncDelta
// ---------------------------------------------------------------------------

/**
 * An incremental state change that must reach every device owned by [ownerId].
 * This is the primitive that makes Circle AI cross-device continuous —
 * HER + JARVIS memory following the person.
 */
data class SyncDelta(
    /** Identity whose state this belongs to. */
    val ownerId: String,
    /** Origin device. */
    val sourceDeviceId: String,
    /** Destination device. "" = broadcast to all owned devices. */
    val targetDeviceId: String,
    /**
     * Domain key identifying the type of state carried.
     * Use [SyncDomainKeys] constants or a custom string.
     */
    val domainKey: String,
    /** Serialised state payload (e.g. JSON or protobuf bytes). */
    val payload: ByteArray,
    /** Monotonic sequence number per owner + domain. */
    val sequence: Long,
    val deliveryMode: SyncDeliveryMode,
    /** Optional time-to-live. null = live forever. */
    val ttl: Duration?,
    val createdAt: Instant
) {
    // ByteArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SyncDelta) return false
        return ownerId == other.ownerId &&
            sourceDeviceId == other.sourceDeviceId &&
            targetDeviceId == other.targetDeviceId &&
            domainKey == other.domainKey &&
            payload.contentEquals(other.payload) &&
            sequence == other.sequence &&
            deliveryMode == other.deliveryMode &&
            ttl == other.ttl &&
            createdAt == other.createdAt
    }

    override fun hashCode(): Int {
        var result = ownerId.hashCode()
        result = 31 * result + sourceDeviceId.hashCode()
        result = 31 * result + targetDeviceId.hashCode()
        result = 31 * result + domainKey.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + sequence.hashCode()
        result = 31 * result + deliveryMode.hashCode()
        result = 31 * result + (ttl?.hashCode() ?: 0)
        result = 31 * result + createdAt.hashCode()
        return result
    }
}

// ---------------------------------------------------------------------------
// ISyncChannel
// ---------------------------------------------------------------------------

/**
 * The cross-device continuity primitive.
 *
 * Pushes memory/state deltas across whatever transport is available:
 * gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
 * App code is identical in every case.
 *
 * This is the primitive that makes Circle AI HER + JARVIS:
 * memory follows the person, not the device.
 */
interface ISyncChannel {
    /**
     * Push a delta. Channel selects transport and handles retries.
     * Returns when accepted (not necessarily delivered for DTN/LocalStore).
     */
    suspend fun pushDeltaAsync(delta: SyncDelta)

    /**
     * Emits all incoming deltas for [ownerId] as a cold [Flow].
     * The flow completes when the channel is closed or the caller cancels.
     */
    fun receiveDeltasAsync(ownerId: String): Flow<SyncDelta>

    /**
     * Returns the last seen sequence number for [ownerId] / [domainKey].
     * Returns 0 when no delta has been received yet.
     */
    suspend fun getLastSequenceAsync(ownerId: String, domainKey: String): Long
}
