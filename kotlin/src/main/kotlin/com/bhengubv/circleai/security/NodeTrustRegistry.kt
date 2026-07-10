// NodeTrustRegistry.kt
//
// Kotlin port of src/CircleAI.Security/NodeTrustRegistry.cs.
//
// Thread-safe, per-peer trust store.
//
// - Each peer gets a score in [0, 1]. 1.0 = fully trusted; 0.0 = fully lost.
// - applyDegradation drops the score and records the triggering event.
// - applyRecovery heals all peers passively (called by a background timer).
// - trustScoreUpdates is an UNBOUNDED channel exposed as a Flow; it retains
//   writes until read, so an update published before any collector attaches is
//   buffered rather than lost (matches C# Channel.CreateUnbounded semantics).
//
// Transport-agnostic: stores PeerSecurityEvent, emits PeerTrustScoreUpdate.

package com.bhengubv.circleai.security

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** Per-peer mutable trust state. Exposed for diagnostics and tests. */
class NodeTrustEntry internal constructor(
    val nodeId: String,
    initialTrustScore: Double,
) {
    @Volatile
    var trustScore: Double = initialTrustScore
        internal set

    @Volatile
    var lastUpdated: Instant = Instant.now()
        internal set

    /** Bounded history of security events (oldest-first). */
    val recentEvents: MutableList<PeerSecurityEvent> = ArrayList()
}

/**
 * Maintains per-peer trust scores, event history, and a live channel of trust
 * score changes consumed by [PeerIntelligenceService].
 */
class NodeTrustRegistry(private val options: SecurityOptions) {

    private val nodes = ConcurrentHashMap<String, NodeTrustEntry>()

    // Unbounded so publish never blocks and writes are retained until read —
    // one background recovery loop plus event handlers write; intelligence
    // subscribers read via the Flow.
    private val channel = Channel<PeerTrustScoreUpdate>(Channel.UNLIMITED)

    /**
     * Stream of trust score changes; never completes during normal operation.
     * Callers cancel their collecting coroutine to break out. Because the
     * backing channel is unbounded, updates emitted before the first collector
     * attaches are buffered and delivered on first collection.
     */
    val trustScoreUpdates: Flow<PeerTrustScoreUpdate> = channel.receiveAsFlow()

    // --- Peer access --------------------------------------------------------

    /**
     * Returns the existing entry for [nodeId], or creates a new one initialised
     * to [SecurityOptions.initialTrustScore].
     */
    fun getOrCreate(nodeId: String): NodeTrustEntry =
        nodes.computeIfAbsent(nodeId) { id ->
            NodeTrustEntry(id, options.initialTrustScore)
        }

    /** All peer IDs currently tracked. */
    val allNodeIds: Collection<String>
        get() = nodes.keys.toList()

    /**
     * Returns the current trust score for [nodeId], or
     * [SecurityOptions.initialTrustScore] for unknown peers.
     */
    fun getTrustScore(nodeId: String): Double {
        val entry = nodes[nodeId] ?: return options.initialTrustScore
        return synchronized(entry) { entry.trustScore }
    }

    // --- Mutations ----------------------------------------------------------

    /**
     * Applies trust degradation for a security event. Score is clamped to
     * [0, 1]; the event is appended to the per-peer history; a
     * [PeerTrustScoreUpdate] is published on the channel when the score
     * actually moves. Returns `(previous, current)`.
     */
    fun applyDegradation(
        securityEvent: PeerSecurityEvent,
        degradationAmount: Double,
    ): Pair<Double, Double> {
        val entry = getOrCreate(securityEvent.nodeId)

        return synchronized(entry) {
            val previous = entry.trustScore
            entry.trustScore = (previous - degradationAmount).coerceIn(0.0, 1.0)
            entry.lastUpdated = securityEvent.occurredAt

            // Maintain bounded event list (oldest dropped first).
            entry.recentEvents.add(securityEvent)
            while (entry.recentEvents.size > options.maxEventsPerNode) {
                entry.recentEvents.removeAt(0)
            }

            val current = entry.trustScore

            if (kotlin.math.abs(current - previous) > 0.0001) {
                publish(
                    entry.nodeId,
                    previous,
                    current,
                    securityEvent.description,
                    securityEvent.occurredAt,
                )
            }

            previous to current
        }
    }

    /**
     * Passively heals all tracked peers by
     * `recoveryRatePerSecond * elapsed.seconds`. Peers already at 1.0 are
     * skipped. Called by the background recovery timer.
     */
    fun applyRecovery(elapsed: Duration) {
        val amount = options.recoveryRatePerSecond * (elapsed.toNanos() / 1_000_000_000.0)
        if (amount <= 0) return

        for (entry in nodes.values) {
            synchronized(entry) {
                if (entry.trustScore >= 1.0) return@synchronized

                val previous = entry.trustScore
                entry.trustScore = minOf(1.0, previous + amount)
                val now = Instant.now()
                entry.lastUpdated = now

                publish(entry.nodeId, previous, entry.trustScore, "passive-recovery", now)
            }
        }
    }

    // --- History queries ----------------------------------------------------

    /**
     * Returns events for [nodeId] that fall within
     * [SecurityOptions.eventWindow] of now. Returns an empty list for unknown
     * peers.
     */
    fun getRecentEvents(nodeId: String): List<PeerSecurityEvent> {
        val entry = nodes[nodeId] ?: return emptyList()
        val cutoff = Instant.now().minus(options.eventWindow)
        return synchronized(entry) {
            entry.recentEvents.filter { !it.occurredAt.isBefore(cutoff) }
        }
    }

    // --- Private ------------------------------------------------------------

    private fun publish(
        nodeId: String,
        previous: Double,
        current: Double,
        reason: String,
        at: Instant,
    ) {
        // UNLIMITED channel: trySend never fails or blocks and does not acquire
        // a lock that the collector's cleanup path would re-enter, so it is safe
        // to call while holding the per-entry monitor.
        channel.trySend(PeerTrustScoreUpdate(nodeId, previous, current, reason, at))
    }
}
