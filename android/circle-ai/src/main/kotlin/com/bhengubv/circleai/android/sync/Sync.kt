// Sync.kt
//
// Android/Kotlin port of Circle.AI.Networking sync primitives.
//
// Covers:
//   SyncDeliveryMode   — enum: Immediate | Batched | BestEffort
//   SchedulingHint     — peer/window suggestions for a sync delta
//   SyncDelta          — an incremental state change for cross-device sync
//   SyncDomainKeys     — well-known domain key constants
//   ISyncChannel       — the cross-device continuity primitive

package com.bhengubv.circleai.android.sync

import kotlinx.coroutines.flow.Flow
import java.time.Instant

// ---------------------------------------------------------------------------
// SyncDeliveryMode
// ---------------------------------------------------------------------------

/** Requested delivery mode for a [SyncDelta]. */
enum class SyncDeliveryMode {
    /** Deliver as soon as a path is available. */
    Immediate,
    /** Accumulate into a batch for efficient bulk transfer. */
    Batched,
    /** Fire and forget — no delivery confirmation required. */
    BestEffort,
}

// ---------------------------------------------------------------------------
// SchedulingHint
// ---------------------------------------------------------------------------

/**
 * Optional scheduling metadata attached to a [SyncDelta].
 * Allows the sender to suggest preferred recipients and delivery windows.
 */
data class SchedulingHint(
    /** Preferred peer identity IDs to route this delta through. */
    val preferredPeerIds: List<String>,
    /** Suggested UTC delivery window start, or null for immediate. */
    val suggestedWindowAt: Instant?,
    /** Confidence in the scheduling hint, 0.0–1.0. */
    val confidenceScore: Float
)

// ---------------------------------------------------------------------------
// SyncDelta
// ---------------------------------------------------------------------------

/**
 * An incremental state change that must reach every device owned by the identity.
 * This is the primitive that makes Circle AI cross-device continuous —
 * HER + JARVIS memory following the person.
 */
data class SyncDelta(
    /**
     * Domain key identifying the type of state carried.
     * Use [SyncDomainKeys] constants or a custom string.
     */
    val domainKey: String,
    /** Unique entity identifier within the domain. */
    val entityId: String,
    /** Serialised state payload (e.g. JSON or protobuf bytes). */
    val payload: ByteArray,
    /** UTC timestamp when this delta was created. */
    val timestamp: Instant,
    /** Requested delivery mode. */
    val deliveryMode: SyncDeliveryMode,
    /** Optional scheduling hint. null = no preference. */
    val schedulingHint: SchedulingHint? = null
) {
    // ByteArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SyncDelta) return false
        return domainKey == other.domainKey &&
            entityId == other.entityId &&
            payload.contentEquals(other.payload) &&
            timestamp == other.timestamp &&
            deliveryMode == other.deliveryMode &&
            schedulingHint == other.schedulingHint
    }

    override fun hashCode(): Int {
        var result = domainKey.hashCode()
        result = 31 * result + entityId.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + timestamp.hashCode()
        result = 31 * result + deliveryMode.hashCode()
        result = 31 * result + (schedulingHint?.hashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// SyncDomainKeys
// ---------------------------------------------------------------------------

/** Well-known [SyncDelta.domainKey] constants. */
object SyncDomainKeys {
    const val MemoryEpisodic    = "memory.episodic"
    const val AffectState       = "affect.state"
    const val Persona           = "persona"
    const val Goals             = "goals"
    const val Identity          = "identity"
    const val Feedback          = "feedback"
    const val BiometricProfile  = "biometric.profile"
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
 */
interface ISyncChannel {
    /**
     * Push a delta. Channel selects transport and handles retries.
     * Returns when accepted (not necessarily delivered).
     */
    suspend fun send(delta: SyncDelta)

    /**
     * Emits all incoming deltas as a cold [Flow].
     * The flow completes when the channel is closed or the caller cancels.
     */
    fun receive(): Flow<SyncDelta>
}
