// SecurityLayerService.kt
//
// Kotlin port of the SecurityLayerService type from
// src/CircleAI.Security/AISecurityLayerService.cs.
//
// Transport-agnostic AI Security Layer — full implementation of
// IPeerSecurityLayer.
//
// Lifecycle:
//   start  -> launches the background trust-recovery loop.
//   (running) -> security events arrive via handlePeerEvent(PeerSecurityEvent).
//               Each event degrades the peer's trust score; threshold
//               evaluation decides which PeerDirective (if any) to issue.
//   stop   -> cancels the recovery loop, cleans up.
//
// Any transport (Aether, WiFi, BLE, NearLink, HTTP, ...) calls handlePeerEvent
// after translating its own event type to PeerSecurityEvent.
//
// Directives issued (most-severe wins per event):
//   QuarantineNode    trust <= quarantineThreshold
//   AvoidNode         trust <= avoidNodeThreshold
//   ElevateMonitoring trust <= elevateMonitoringThreshold
//   ReleaseNode       not issued automatically — requires explicit operator action

package com.bhengubv.circleai.security

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant

/**
 * Transport-agnostic AI Security Layer. Degrades per-peer trust scores via
 * [ThreatDetector] and issues [PeerDirective] recommendations to all registered
 * [IPeerDirectiveConsumer] subscribers.
 *
 * @param registry Per-peer trust store.
 * @param options Threshold configuration.
 * @param publisher Directive fan-out.
 * @param scope Parent scope for the background recovery loop. Defaults to a
 *   dedicated [SupervisorJob] on [Dispatchers.Default]; inject a test scope to
 *   control timing.
 * @param recoveryInterval Cadence of the passive-recovery tick. Default 30s,
 *   matching the C# reference.
 */
class SecurityLayerService(
    private val registry: NodeTrustRegistry,
    private val options: SecurityOptions,
    private val publisher: DirectivePublisher,
    private val scope: CoroutineScope =
        CoroutineScope(SupervisorJob() + Dispatchers.Default),
    private val recoveryInterval: Duration = Duration.ofSeconds(30),
) : IPeerSecurityLayer {

    private val lock = Any()
    private var recoveryJob: Job? = null

    @Volatile
    private var active: Boolean = false

    // --- IPeerSecurityLayer -------------------------------------------------

    override suspend fun start() {
        synchronized(lock) {
            if (active) return
            active = true
            recoveryJob = scope.launch { runRecoveryLoop() }
        }
    }

    override suspend fun stop() {
        val job = synchronized(lock) {
            active = false
            val j = recoveryJob
            recoveryJob = null
            j
        }
        job?.cancelAndJoin()
    }

    /**
     * Feed a security event from any transport into the security layer. Call
     * this from any transport adapter after translating its native event type
     * to [PeerSecurityEvent]. Thread-safe.
     */
    override fun handlePeerEvent(e: PeerSecurityEvent) {
        val degradation = ThreatDetector.computeDegradation(e)
        if (degradation <= 0) return // PeerThreatLevel.None — no trust impact

        val (previous, current) = registry.applyDegradation(e, degradation)
        evaluateThresholds(e.nodeId, previous, current, e.description)
    }

    /**
     * Notify the security layer that a peer has left. Trust entry is preserved
     * for historical queries; no directive is issued.
     */
    @Suppress("UNUSED_PARAMETER")
    fun handlePeerLeft(nodeId: String) {
        // Trust entry retained for forensic queries; no action on departure.
    }

    override fun subscribeToDirectives(consumer: IPeerDirectiveConsumer): AutoCloseable =
        publisher.subscribe(consumer)

    override suspend fun getPosture(): PeerSecurityPosture {
        val nodeIds = registry.allNodeIds.toList()
        val quarantined = nodeIds.count {
            registry.getTrustScore(it) <= options.quarantineThreshold
        }
        val monitored = nodeIds.count {
            val s = registry.getTrustScore(it)
            s <= options.elevateMonitoringThreshold && s > options.quarantineThreshold
        }

        val worstScore =
            if (nodeIds.isEmpty()) 1.0
            else nodeIds.minOf { registry.getTrustScore(it) }
        val overallThreat = scoreToThreatLevel(worstScore)

        return PeerSecurityPosture(
            overallThreat,
            quarantined,
            monitored,
            active,
            Instant.now(),
        )
    }

    // --- Threshold evaluation ----------------------------------------------

    private fun evaluateThresholds(
        nodeId: String,
        previous: Double,
        current: Double,
        reason: String,
    ) {
        // Evaluate from most-severe to least; issue at most one directive per event.

        if (previous > options.quarantineThreshold && current <= options.quarantineThreshold) {
            issueDirective(
                PeerDirectiveKind.QuarantineNode, nodeId, current, reason,
                PeerThreatLevel.Critical,
            )
            return
        }

        if (previous > options.avoidNodeThreshold && current <= options.avoidNodeThreshold) {
            issueDirective(
                PeerDirectiveKind.AvoidNode, nodeId, current, reason,
                PeerThreatLevel.High,
            )
            return
        }

        if (previous > options.elevateMonitoringThreshold &&
            current <= options.elevateMonitoringThreshold
        ) {
            issueDirective(
                PeerDirectiveKind.ElevateMonitoring, nodeId, current, reason,
                PeerThreatLevel.Medium,
            )
        }
    }

    private fun issueDirective(
        kind: PeerDirectiveKind,
        nodeId: String,
        trustScore: Double,
        reason: String,
        threatLevel: PeerThreatLevel,
    ) {
        publisher.publish(
            PeerDirective(
                kind = kind,
                targetNodeId = nodeId,
                trustScore = trustScore,
                threatLevel = threatLevel,
                reason = reason,
                duration = null, // permanent until ReleaseNode
                issuedAt = Instant.now(),
            ),
        )
    }

    // --- Background recovery loop ------------------------------------------

    private suspend fun runRecoveryLoop() {
        val intervalMillis = recoveryInterval.toMillis()
        while (scope.isActive) {
            delay(intervalMillis)
            registry.applyRecovery(recoveryInterval)
        }
    }

    // --- Helpers ------------------------------------------------------------

    private fun scoreToThreatLevel(score: Double): PeerThreatLevel = when {
        score <= 0.25 -> PeerThreatLevel.Critical
        score <= 0.50 -> PeerThreatLevel.High
        score <= 0.75 -> PeerThreatLevel.Medium
        score <= 0.90 -> PeerThreatLevel.Low
        else -> PeerThreatLevel.None
    }
}
