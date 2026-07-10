// PeerIntelligenceServiceTest.kt
//
// Verifies the intelligence outputs derived from the trust registry: network
// health aggregation, per-peer threat assessment (level + confidence +
// indicators), routing advice (avoid-list + direct/no path), and that the
// trust-score stream is the registry's live Flow.

package com.bhengubv.circleai.security

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class PeerIntelligenceServiceTest {

    private fun build(
        options: SecurityOptions = SecurityOptions(),
    ): Pair<PeerIntelligenceService, NodeTrustRegistry> {
        val registry = NodeTrustRegistry(options)
        return PeerIntelligenceService(registry, options) to registry
    }

    private fun event(
        node: String,
        kind: PeerSecurityEventKind = PeerSecurityEventKind.AuthAttempt,
        level: PeerThreatLevel = PeerThreatLevel.Medium,
    ) = PeerSecurityEvent(node, kind, level, "e", "test", Instant.now())

    // -- getNetworkHealth ----------------------------------------------------

    @Test
    fun `empty network reports full health`() = runTest {
        val (svc, _) = build()
        val report = svc.getNetworkHealth()
        assertEquals(1.0, report.overallScore, 1e-9)
        assertEquals(0, report.trustedPeerCount)
        assertEquals(0, report.suspiciousPeerCount)
        assertEquals("No peers observed.", report.summary)
    }

    @Test
    fun `health averages scores and counts trusted vs suspicious`() = runTest {
        val (svc, reg) = build()
        reg.getOrCreate("a") // 1.0 trusted, not suspicious
        reg.applyDegradation(event("b"), 0.4) // 0.6 trusted (>0.50), not suspicious (>0.75? no 0.6<=0.75 -> suspicious)
        reg.applyDegradation(event("c"), 0.8) // 0.2 suspicious, not trusted

        val report = svc.getNetworkHealth()
        assertEquals((1.0 + 0.6 + 0.2) / 3.0, report.overallScore, 1e-9)
        // trusted = score > avoid(0.50): a(1.0), b(0.6) => 2
        assertEquals(2, report.trustedPeerCount)
        // suspicious = score <= elevate(0.75): b(0.6), c(0.2) => 2
        assertEquals(2, report.suspiciousPeerCount)
    }

    @Test
    fun `health summary reflects overall band`() = runTest {
        val (svc, reg) = build()
        reg.applyDegradation(event("x"), 0.6) // -> 0.4 overall -> "poor"
        val report = svc.getNetworkHealth()
        assertTrue(report.summary.contains("poor"), "got: ${report.summary}")
    }

    // -- assessThreat --------------------------------------------------------

    @Test
    fun `assessThreat of unknown peer is none with zero confidence`() = runTest {
        val (svc, _) = build()
        val a = svc.assessThreat("ghost")
        assertEquals(PeerThreatLevel.None, a.threatLevel)
        assertEquals(0.0, a.confidence, 1e-9) // deficit 0, no indicators
        assertTrue(a.indicators.isEmpty())
    }

    @Test
    fun `assessThreat maps trust score to threat level`() = runTest {
        val (svc, reg) = build()
        reg.applyDegradation(event("crit"), 0.8) // -> 0.2 -> Critical
        assertEquals(PeerThreatLevel.Critical, svc.assessThreat("crit").threatLevel)

        reg.applyDegradation(event("hi"), 0.6) // -> 0.4 -> High
        assertEquals(PeerThreatLevel.High, svc.assessThreat("hi").threatLevel)
    }

    @Test
    fun `assessThreat confidence combines deficit and indicators`() = runTest {
        val (svc, reg) = build()
        // Three auth attempts -> repeated-auth-attempts indicator (+0.1).
        repeat(3) { reg.applyDegradation(event("p", PeerSecurityEventKind.AuthAttempt), 0.1) }
        // score ~ 0.7 -> deficit ~0.3; one indicator => confidence ~0.4.
        val a = svc.assessThreat("p")
        assertTrue(a.indicators.contains("repeated-auth-attempts"))
        val actualScore = reg.getTrustScore("p")
        val expected = minOf(1.0, (1.0 - actualScore) + a.indicators.size * 0.1)
        assertEquals(expected, a.confidence, 1e-9)
    }

    @Test
    fun `assessThreat confidence is capped at 1_0`() = runTest {
        val (svc, reg) = build()
        // Drive score to 0 (deficit 1.0) plus multiple indicators.
        reg.applyDegradation(event("p", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 5.0)
        repeat(3) { reg.applyDegradation(event("p", PeerSecurityEventKind.AuthAttempt), 0.0) }
        val a = svc.assessThreat("p")
        assertTrue(a.confidence <= 1.0)
        assertEquals(1.0, a.confidence, 1e-9)
    }

    // -- getRoutingAdvice ----------------------------------------------------

    @Test
    fun `routing advice gives direct path to a trusted destination`() = runTest {
        val (svc, reg) = build()
        reg.getOrCreate("dest") // 1.0
        val advice = svc.getRoutingAdvice("dest")
        assertEquals(listOf("dest"), advice.recommendedPath)
        assertTrue(advice.avoidNodeIds.isEmpty())
        assertEquals(1.0, advice.confidence, 1e-9)
        assertTrue(advice.reasoning.contains("trusted"))
    }

    @Test
    fun `routing advice avoids low-trust nodes and gives no path to quarantined dest`() = runTest {
        val (svc, reg) = build()
        reg.applyDegradation(event("dest"), 0.9) // -> 0.1 quarantined
        reg.applyDegradation(event("bad"), 0.7)  // -> 0.3 (<=0.50 avoid)
        reg.getOrCreate("good")                  // 1.0

        val advice = svc.getRoutingAdvice("dest")
        assertTrue(advice.recommendedPath.isEmpty(), "no path to quarantined dest")
        assertTrue(advice.avoidNodeIds.contains("dest"))
        assertTrue(advice.avoidNodeIds.contains("bad"))
        assertTrue(!advice.avoidNodeIds.contains("good"))
        assertTrue(advice.reasoning.contains("quarantined"))
    }

    @Test
    fun `routing reasoning includes two-decimal score for trusted dest`() = runTest {
        val (svc, reg) = build()
        reg.applyDegradation(event("dest"), 0.1) // -> 0.90 (>0.75 trusted)
        val advice = svc.getRoutingAdvice("dest")
        // f2(0.90) == "0.90"
        assertTrue(advice.reasoning.contains("0.90"), "got: ${advice.reasoning}")
    }

    // -- streamTrustScores ---------------------------------------------------

    @Test
    fun `streamTrustScores relays registry updates`() = runTest {
        val (svc, reg) = build()
        reg.applyDegradation(event("p1"), 0.2)
        val update = svc.streamTrustScores().first()
        assertEquals("p1", update.nodeId)
    }
}
