// PeerIntelligenceService.kt
//
// Kotlin port of the PeerIntelligenceService type from
// src/CircleAI.Security/AetherIntelligenceService.cs.
//
// Transport-agnostic intelligence output — full implementation of
// IPeerIntelligence.
//
// Reads trust scores and event history from NodeTrustRegistry and packages them
// as the four intelligence outputs consumed by apps and the security layer:
//
//   PeerNetworkHealthReport   aggregate health (overall score, counts)
//   PeerThreatAssessment      per-peer confidence + level + indicators
//   PeerRoutingAdvice         trust-aware path with avoid-list
//   PeerTrustScoreUpdate      live Flow of every score change

package com.bhengubv.circleai.security

import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.Locale

/**
 * Reads [NodeTrustRegistry] state to produce transport-agnostic intelligence
 * outputs. Wires directly to the registry's
 * [NodeTrustRegistry.trustScoreUpdates] Flow for the streaming API.
 */
class PeerIntelligenceService(
    private val registry: NodeTrustRegistry,
    private val options: SecurityOptions,
) : IPeerIntelligence {

    // --- IPeerIntelligence --------------------------------------------------

    override suspend fun getNetworkHealth(): PeerNetworkHealthReport {
        val nodeIds = registry.allNodeIds.toList()

        if (nodeIds.isEmpty()) {
            return PeerNetworkHealthReport(
                overallScore = 1.0,
                trustedPeerCount = 0,
                suspiciousPeerCount = 0,
                summary = "No peers observed.",
                generatedAt = Instant.now(),
            )
        }

        val scores = nodeIds.map { registry.getTrustScore(it) }
        val overall = scores.average()
        val trusted = scores.count { it > options.avoidNodeThreshold }
        val suspicious = scores.count { it <= options.elevateMonitoringThreshold }

        val summary = when {
            overall > 0.90 -> "Network health is excellent."
            overall > 0.75 -> "Network health is good; minor anomalies detected."
            overall > 0.50 -> "Network health is degraded; elevated monitoring active."
            overall > 0.25 -> "Network health is poor; routing around compromised peers."
            else -> "Network health is critical; quarantine directives in effect."
        }

        return PeerNetworkHealthReport(
            overall,
            trusted,
            suspicious,
            summary,
            Instant.now(),
        )
    }

    override suspend fun assessThreat(nodeId: String): PeerThreatAssessment {
        val score = registry.getTrustScore(nodeId)
        val deficit = 1.0 - score // 0 = fully trusted, 1 = fully lost

        val indicators = ThreatDetector.detectIndicators(
            registry.getRecentEvents(nodeId),
            options.eventWindow,
        )

        val level = when {
            score <= 0.25 -> PeerThreatLevel.Critical
            score <= 0.50 -> PeerThreatLevel.High
            score <= 0.75 -> PeerThreatLevel.Medium
            score <= 0.90 -> PeerThreatLevel.Low
            else -> PeerThreatLevel.None
        }

        // Confidence is proportional to trust deficit, boosted by each indicator.
        val confidence = minOf(1.0, deficit + indicators.size * 0.1)

        return PeerThreatAssessment(
            nodeId,
            confidence,
            level,
            indicators,
            Instant.now(),
        )
    }

    override suspend fun getRoutingAdvice(destinationNodeId: String): PeerRoutingAdvice {
        val allNodes = registry.allNodeIds.toList()
        val avoidNodes = allNodes.filter {
            registry.getTrustScore(it) <= options.avoidNodeThreshold
        }

        val destScore = registry.getTrustScore(destinationNodeId)

        // Recommended path is direct only when destination is above avoid-threshold.
        val recommended: List<String> =
            if (destScore > options.avoidNodeThreshold) listOf(destinationNodeId)
            else emptyList()

        val reasoning = when {
            destScore > 0.75 ->
                "Direct path to $destinationNodeId is trusted (score ${f2(destScore)})."
            destScore > 0.50 ->
                "Destination $destinationNodeId is under monitoring; routing with caution."
            destScore > 0.25 ->
                "Destination $destinationNodeId has degraded trust; avoid recommended."
            else ->
                "Destination $destinationNodeId is quarantined; no safe path available."
        }

        return PeerRoutingAdvice(
            destinationNodeId,
            recommended,
            avoidNodes,
            confidence = destScore,
            reasoning,
            Instant.now(),
        )
    }

    override fun streamTrustScores(): Flow<PeerTrustScoreUpdate> =
        registry.trustScoreUpdates

    // --- Helpers ------------------------------------------------------------

    /** Formats a score with two fixed decimals, matching C# `{x:F2}`. */
    private fun f2(value: Double): String = String.format(Locale.ROOT, "%.2f", value)
}
