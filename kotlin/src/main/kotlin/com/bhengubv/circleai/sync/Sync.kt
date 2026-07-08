// Sync.kt
//
// Kotlin port of the CircleAI.Networking cross-device sync primitives —
// the C# reference (SyncDelta.cs, ISyncChannel.cs, NetworkTypes.cs) is the
// EXACT spec.
//
// Covers:
//   SyncDeliveryMode — enum: BestEffort | Guaranteed | Urgent
//   SchedulingHint   — optional AI-layer routing advisory on a delta
//   SyncDelta        — an incremental state change for cross-device sync
//   ISyncChannel     — the cross-device continuity primitive

package com.bhengubv.circleai.sync

import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant

// ---------------------------------------------------------------------------
// SyncDeliveryMode  (NetworkTypes.cs)
// ---------------------------------------------------------------------------

/** Requested delivery semantics for a [SyncDelta]. */
enum class SyncDeliveryMode {
    /** Fire and forget — no delivery confirmation required. */
    BestEffort,

    /** Deliver reliably; retry until accepted (store-and-forward if needed). */
    Guaranteed,

    /** Deliver reliably AND ahead of lower-priority traffic. */
    Urgent,
}

// ---------------------------------------------------------------------------
// SchedulingHint
// ---------------------------------------------------------------------------

/**
 * Optional AI-layer routing advisory attached to a [SyncDelta]. Allows the
 * sender to suggest preferred peers and a delivery window.
 */
data class SchedulingHint(
    /** Preferred peer identity IDs to route this delta through. */
    val preferredPeerIds: List<String> = emptyList(),
    /** Suggested UTC delivery window start, or null for immediate. */
    val suggestedWindowUtc: Instant? = null,
    /** Confidence in the scheduling hint, 0.0–1.0. */
    val confidenceScore: Float = 0f,
)

// ---------------------------------------------------------------------------
// SyncDelta  (SyncDelta.cs)
// ---------------------------------------------------------------------------

/**
 * An incremental state change that must reach every device owned by
 * [ownerId]. This is the primitive that makes Circle AI cross-device
 * continuous — HER + JARVIS memory following the person.
 *
 * The [payload] is opaque bytes; [equals]/[hashCode] use value semantics over
 * the byte content so two deltas carrying identical bytes compare equal.
 */
data class SyncDelta(
    /** Identity whose state this belongs to. */
    val ownerId: String,
    /** Origin device. */
    val sourceDeviceId: String,
    /** Target device — "" = broadcast to all owned devices. */
    val targetDeviceId: String,
    /** Domain key — "memory.episodic" | "affect.state" | "persona" | custom. */
    val domainKey: String,
    /** Serialised state payload. */
    val payload: ByteArray,
    /** Monotonic sequence per owner+domain. */
    val sequence: Long,
    /** Requested delivery mode. */
    val deliveryMode: SyncDeliveryMode,
    /** Optional time-to-live; null = no expiry. */
    val ttl: Duration? = null,
    /** UTC creation timestamp. */
    val createdAt: Instant,
    /** Optional AI-layer routing advisory. null = no preference. */
    val schedulingHint: SchedulingHint? = null,
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
            createdAt == other.createdAt &&
            schedulingHint == other.schedulingHint
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
        result = 31 * result + (schedulingHint?.hashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// ISyncChannel  (ISyncChannel.cs)
// ---------------------------------------------------------------------------

/**
 * The cross-device continuity primitive.
 *
 * Pushes memory/state deltas across whatever transport is available: gRPC over
 * 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later. App code is
 * identical in every case. This is the primitive that makes Circle AI HER +
 * JARVIS: memory follows the person, not the device.
 */
interface ISyncChannel {
    /**
     * Push a delta. Channel selects transport and handles retries. Returns
     * when accepted (not necessarily delivered for DTN/LocalStore).
     */
    suspend fun pushDelta(delta: SyncDelta)

    /**
     * Emits all incoming deltas for [ownerId] as a cold [Flow]. The flow
     * completes when the channel is closed or the caller cancels.
     */
    fun receiveDeltas(ownerId: String): Flow<SyncDelta>

    /** Returns the last observed sequence number for [ownerId] + [domainKey], or 0 if none. */
    suspend fun getLastSequence(ownerId: String, domainKey: String): Long
}
