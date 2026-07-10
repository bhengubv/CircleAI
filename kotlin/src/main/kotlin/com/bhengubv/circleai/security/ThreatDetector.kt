// ThreatDetector.kt
//
// Kotlin port of src/CircleAI.Security/ThreatDetector.cs.
//
// Pure static threat logic — no state, no DI, fully testable in isolation.
//
// Two responsibilities:
//   1. computeDegradation: how much trust a single security event should cost.
//   2. detectIndicators:   which behavioural patterns are visible in a window.
//
// Transport-agnostic: operates on PeerSecurityEvent / PeerSecurityEventKind /
// PeerThreatLevel — no dependency on any specific transport package.

package com.bhengubv.circleai.security

import java.time.Duration
import java.time.Instant

/**
 * Stateless threat analysis helpers used by [SecurityLayerService] and
 * [PeerIntelligenceService].
 */
object ThreatDetector {

    // --- Degradation weights by event kind ----------------------------------

    private fun baseWeight(kind: PeerSecurityEventKind): Double = when (kind) {
        PeerSecurityEventKind.AuthAttempt -> 0.05
        PeerSecurityEventKind.RoutingAnomaly -> 0.10
        PeerSecurityEventKind.BehaviourChange -> 0.08
        PeerSecurityEventKind.EncryptionEvent -> 0.06
        PeerSecurityEventKind.IntrusionSignal -> 0.15
        PeerSecurityEventKind.PrivilegeAttempt -> 0.12
        PeerSecurityEventKind.ConnectionAnomaly -> 0.07
        PeerSecurityEventKind.DataExfiltration -> 0.14
        PeerSecurityEventKind.DenialOfService -> 0.13
        else -> 0.05
    }

    // --- Multipliers by threat level ----------------------------------------

    private fun threatMultiplier(level: PeerThreatLevel): Double = when (level) {
        PeerThreatLevel.None -> 0.0
        PeerThreatLevel.Low -> 0.5
        PeerThreatLevel.Medium -> 1.0
        PeerThreatLevel.High -> 2.0
        PeerThreatLevel.Critical -> 3.0
    }

    // --- Public API ---------------------------------------------------------

    /**
     * Returns the trust-score degradation amount for a security event,
     * calculated as `baseWeight(kind) * threatMultiplier(level)`.
     * Returns 0 when [PeerThreatLevel.None].
     */
    fun computeDegradation(e: PeerSecurityEvent): Double =
        baseWeight(e.kind) * threatMultiplier(e.threatLevel)

    /**
     * Derives human-readable threat indicator tags from a set of recent events
     * within the given [window]. Returns an empty list when no patterns are
     * detected.
     */
    fun detectIndicators(
        recentEvents: Iterable<PeerSecurityEvent>,
        window: Duration,
    ): List<String> {
        val cutoff = Instant.now().minus(window)
        val windowed = recentEvents.filter { !it.occurredAt.isBefore(cutoff) }

        if (windowed.isEmpty()) return emptyList()

        val indicators = ArrayList<String>(6)

        // >= 3 auth attempts within the window -> brute-force signal
        if (windowed.count { it.kind == PeerSecurityEventKind.AuthAttempt } >= 3) {
            indicators.add("repeated-auth-attempts")
        }

        // Any intrusion signal -> explicit probe or exploit
        if (windowed.any { it.kind == PeerSecurityEventKind.IntrusionSignal }) {
            indicators.add("intrusion-signal-detected")
        }

        // High or Critical event -> severity flag
        if (windowed.any {
                it.threatLevel == PeerThreatLevel.High ||
                    it.threatLevel == PeerThreatLevel.Critical
            }
        ) {
            indicators.add("high-severity-event")
        }

        // >= 3 distinct event kinds -> multi-vector activity
        if (windowed.map { it.kind }.distinct().size >= 3) {
            indicators.add("multi-vector-activity")
        }

        // Privilege escalation attempt
        if (windowed.any { it.kind == PeerSecurityEventKind.PrivilegeAttempt }) {
            indicators.add("privilege-escalation-attempt")
        }

        // Data exfiltration signal
        if (windowed.any { it.kind == PeerSecurityEventKind.DataExfiltration }) {
            indicators.add("data-exfiltration-signal")
        }

        return indicators
    }
}
