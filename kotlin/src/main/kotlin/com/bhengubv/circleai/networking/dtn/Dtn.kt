// Dtn.kt
//
// Kotlin port of CircleAI.Networking.Dtn (src/CircleAI.Networking.Dtn/*.cs is the
// EXACT spec). Delay-tolerant networking: self-contained bundles with TTL + custody
// semantics, an in-memory bundle store, and an [ISyncChannel] that store-and-forwards
// deltas over whatever [INetworkTransport] is available. TTL = 72 hours; expired
// bundles are discarded.
//
// Covers (C# → Kotlin):
//   DtnBundle.cs           → DtnBundle (record → data class, value bytes)
//   DtnTransportCommons.cs → DtnPriority (enum), DtnCustodyRecord (record →
//                            data class), InMemoryDtnBundleStore
//   DtnSyncChannel.cs      → DtnSyncChannel (ISyncChannel)
//
// C# → Kotlin conventions:
//   record                        → data class
//   ReadOnlyMemory<byte>          → ByteArray (value equals/hashCode)
//   ConcurrentDictionary          → ConcurrentHashMap
//   Guid.NewGuid().ToString("N")  → UUID.randomUUID() sans dashes (32-char hex)
//   DateTimeOffset.UtcNow         → injectable now: () -> Instant (deterministic)
//   Task / IAsyncEnumerable<T>    → suspend fun / Flow<T>
//   Channel.CreateUnbounded       → kotlinx.coroutines Channel(UNLIMITED)
//
// DETERMINISM: C# reads DateTimeOffset.UtcNow and Guid.NewGuid() directly inside
// PushDeltaAsync. To keep the Kotlin port testable + deterministic (RULES), both
// are injected as constructor lambdas (default to the real clock / random UUID),
// exactly as the networking core does for NetworkPayload.create(now = ...).
package com.bhengubv.circleai.networking.dtn

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.MessagePriority
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.sync.ISyncChannel
import com.bhengubv.circleai.sync.SyncDelta
import com.bhengubv.circleai.sync.SyncDeliveryMode
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Duration
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// DtnBundle  (DtnBundle.cs)
// ===========================================================================

/**
 * A DTN bundle: a self-contained delivery unit with TTL and custody semantics.
 * [expiresAt] defaults to createdAt + 72h at the call site (see [DtnSyncChannel]).
 * [payload] is opaque bytes; equals/hashCode use value semantics over the byte
 * content (C# records give this for free; Kotlin [ByteArray] is reference-equal by
 * default so the overrides are explicit).
 */
data class DtnBundle(
    val bundleId: String,
    val sourceNodeId: String,
    val destinationNodeId: String,
    val payload: ByteArray,
    /** default: createdAt + 72h */
    val expiresAt: Instant,
    /** request custody transfer at each hop */
    val custodyRequired: Boolean,
    val hopCount: Int,
    val createdAt: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DtnBundle) return false
        return bundleId == other.bundleId &&
            sourceNodeId == other.sourceNodeId &&
            destinationNodeId == other.destinationNodeId &&
            payload.contentEquals(other.payload) &&
            expiresAt == other.expiresAt &&
            custodyRequired == other.custodyRequired &&
            hopCount == other.hopCount &&
            createdAt == other.createdAt
    }

    override fun hashCode(): Int {
        var result = bundleId.hashCode()
        result = 31 * result + sourceNodeId.hashCode()
        result = 31 * result + destinationNodeId.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + expiresAt.hashCode()
        result = 31 * result + custodyRequired.hashCode()
        result = 31 * result + hopCount
        result = 31 * result + createdAt.hashCode()
        return result
    }
}

// ===========================================================================
// DtnPriority  (DtnTransportCommons.cs)
// ===========================================================================

/** Relative forwarding priority of a DTN bundle. */
enum class DtnPriority { Bulk, Normal, Expedited }

// ===========================================================================
// DtnCustodyRecord  (DtnTransportCommons.cs)
// ===========================================================================

/** Records which node accepted custody of a bundle, and when. */
data class DtnCustodyRecord(
    val bundleId: String,
    val custodianNode: String,
    val acceptedAtUtc: Instant,
)

// ===========================================================================
// InMemoryDtnBundleStore  (DtnTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of DTN bundles + custody records. Mirrors the C#
 * [ConcurrentDictionary] pair. [purge] removes every bundle (and its custody
 * record) whose [DtnBundle.expiresAt] is strictly before [now] and returns the
 * count removed; [isExpired] treats an unknown bundle id as expired, matching C#.
 */
class InMemoryDtnBundleStore {
    private val bundles = ConcurrentHashMap<String, DtnBundle>()
    private val custody = ConcurrentHashMap<String, DtnCustodyRecord>()

    /** Store (or replace) a bundle by its id. */
    fun store(b: DtnBundle) {
        bundles[b.bundleId] = b
    }

    /** The bundle with [bundleId], or null if unknown. */
    fun get(bundleId: String): DtnBundle? = bundles[bundleId]

    /** Every stored bundle (unordered, matching C# `.Values.ToArray()`). */
    val all: List<DtnBundle>
        get() = bundles.values.toList()

    /** Accept custody of a bundle. */
    fun acceptCustody(r: DtnCustodyRecord) {
        custody[r.bundleId] = r
    }

    /** The custody record for [bundleId], or null if none. */
    fun getCustody(bundleId: String): DtnCustodyRecord? = custody[bundleId]

    /**
     * Whether [bundleId] is expired as of [now]. An unknown bundle id is treated as
     * expired (C#: `if (!TryGetValue) return true`). Expiry is `now > expiresAt`
     * (strictly after), matching C#.
     */
    fun isExpired(bundleId: String, now: Instant): Boolean {
        val b = bundles[bundleId] ?: return true
        return now.isAfter(b.expiresAt)
    }

    /**
     * Remove every bundle (and its custody record) that has expired as of [now].
     * Returns the number of bundles removed.
     */
    fun purge(now: Instant): Int {
        val dead = bundles.entries.filter { now.isAfter(it.value.expiresAt) }.map { it.key }
        for (id in dead) {
            bundles.remove(id)
            custody.remove(id)
        }
        return dead.size
    }

    /** Every stored bundle destined for [destinationNodeId]. */
    fun inFlightTo(destinationNodeId: String): List<DtnBundle> =
        bundles.values.filter { it.destinationNodeId == destinationNodeId }
}

// ===========================================================================
// DtnSyncChannel  (DtnSyncChannel.cs)
// ===========================================================================

/**
 * [ISyncChannel] backed by DTN store-and-forward. Bundles are persisted locally and
 * forwarded whenever any transport becomes available. Works over HTTP, WiFi,
 * Bluetooth, NearLink — any [INetworkTransport]. TTL = 72 hours; expired bundles are
 * discarded.
 *
 * [pushDelta] builds a [DtnBundle] (custody required iff the delivery mode is
 * [SyncDeliveryMode.Guaranteed]), then tries the first available transport, sending
 * a [NetworkPayload] (priority Urgent iff the delivery mode is
 * [SyncDeliveryMode.Urgent], else Normal; content-type `application/dtn-bundle`). If
 * no transport is available the bundle is queued locally (the full impl persists to
 * SQLite and retries on transport-up events).
 *
 * @param now injectable clock (defaults to the real UTC clock) — C# reads
 *   DateTimeOffset.UtcNow directly; injected here for deterministic tests.
 * @param newBundleId injectable id generator (defaults to a fresh 32-char hex UUID,
 *   matching C# Guid.NewGuid().ToString("N")).
 */
class DtnSyncChannel(
    transports: Iterable<INetworkTransport>,
    private val now: () -> Instant = { Instant.now() },
    private val newBundleId: () -> String = { UUID.randomUUID().toString().replace("-", "") },
) : ISyncChannel {

    private val transports: List<INetworkTransport> = transports.toList()
    private val delivered = Channel<SyncDelta>(Channel.UNLIMITED)
    private val sequences = HashMap<Pair<String, String>, Long>()
    private val lock = Any()

    override suspend fun pushDelta(delta: SyncDelta) {
        val nowTs = now()
        @Suppress("UNUSED_VARIABLE")
        val bundle = DtnBundle(
            bundleId = newBundleId(),
            sourceNodeId = delta.sourceDeviceId,
            destinationNodeId = delta.targetDeviceId,
            payload = delta.payload,
            expiresAt = nowTs.plus(delta.ttl ?: DEFAULT_TTL),
            custodyRequired = delta.deliveryMode == SyncDeliveryMode.Guaranteed,
            hopCount = 0,
            createdAt = nowTs,
        )

        // Try live transports first; if none available, queue for later delivery.
        val available = transports.filter { it.isAvailable }
        if (available.isNotEmpty()) {
            val payload = NetworkPayload.create(
                data = delta.payload,
                destinationId = delta.targetDeviceId,
                priority = if (delta.deliveryMode == SyncDeliveryMode.Urgent) {
                    MessagePriority.Urgent
                } else {
                    MessagePriority.Normal
                },
                contentType = "application/dtn-bundle",
                now = now,
            )
            available[0].send(payload)
        }
        // else: bundle is queued locally (full impl: persist to SQLite) and retried
        // on transport-up events.
    }

    override fun receiveDeltas(ownerId: String): Flow<SyncDelta> = flow {
        for (d in delivered) emit(d)
    }

    override suspend fun getLastSequence(ownerId: String, domainKey: String): Long =
        synchronized(lock) { sequences[ownerId to domainKey] ?: 0L }

    companion object {
        /** Default bundle time-to-live: 72 hours. */
        val DEFAULT_TTL: Duration = Duration.ofHours(72)
    }
}
