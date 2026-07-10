// AetherTest.kt
//
// Verifies the Aether contract family and its in-memory implementations:
//   - record computed properties (health validity, directive target/permanence,
//     trust-update change/degrade, network congestion, security severity)
//   - AetherVersion comparison + parse semantics
//   - InMemoryAetherContext derived-property matrix
//   - InMemoryAuthChallenge minimum-method enforcement + OS toggle floor
//   - InMemoryAetherTelemetry fan-out + unsubscribe
//   - InMemoryAISecurityLayer telemetry-driven degradation, single-directive
//     threshold crossing, posture, and directive fan-out
//   - InMemoryAetherIntelligence health/threat/routing/stream over a ledger

package com.bhengubv.circleai.aether

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class AetherTest {

    // ── helpers ──────────────────────────────────────────────────────────────

    private fun secEvent(
        node: String,
        level: AetherThreatLevel,
        kind: AetherSecurityEventKind = AetherSecurityEventKind.IntrusionSignal,
        desc: String = "evt",
    ) = AetherSecurityEvent(node, kind, level, desc, emptyMap(), Instant.now())

    private class DirectiveRecorder : ISecurityDirectiveConsumer {
        val directives = CopyOnWriteArrayList<SecurityDirective>()
        override fun onDirective(directive: SecurityDirective) {
            directives.add(directive)
        }
    }

    // ── AetherVersion ──────────────────────────────────────────────────────────

    @Test
    fun `version compares component-wise`() {
        assertTrue(AetherVersion(1, 2, 3) < AetherVersion(1, 2, 4))
        assertTrue(AetherVersion(2, 0) > AetherVersion(1, 9, 9, 9))
        assertEquals(AetherVersion(1, 0, 0, 0), AetherVersion(1))
        assertTrue(AetherVersion(1, 2) >= AetherVersion(1, 2, 0, 0))
    }

    @Test
    fun `version parses dotted strings with defaulted components`() {
        assertEquals(AetherVersion(1, 0, 0, 0), AetherVersion.parse("1"))
        assertEquals(AetherVersion(1, 2, 0, 0), AetherVersion.parse("1.2"))
        assertEquals(AetherVersion(1, 2, 3, 4), AetherVersion.parse("1.2.3.4"))
        assertEquals("1.2.3.4", AetherVersion(1, 2, 3, 4).toString())
    }

    // ── record computed members ───────────────────────────────────────────────

    @Test
    fun `node health validity and exit`() {
        assertTrue(AetherNodeHealth(0.5, true, Duration.ZERO, 1).isValid)
        assertFalse(AetherNodeHealth(1.5, true, Duration.ZERO, 1).isValid)
        val leave = AetherNodeEvent("n", AetherNodeEventKind.Left, AetherNodeHealth(1.0, true, Duration.ZERO, 0), Instant.now())
        assertTrue(leave.isExit)
    }

    @Test
    fun `route hop count and failure, transport loss, network congestion`() {
        val route = AetherRouteEvent("a", "c", listOf("a", "b", "c"), AetherRouteEventKind.Failed, "lost", Instant.now())
        assertEquals(3, route.hopCount)
        assertTrue(route.isFailed)

        val transport = AetherTransportEvent("n", AetherTransportEventKind.PacketLoss, AetherTransportKind.WiFi, null, 0.9, Instant.now())
        assertTrue(transport.exceedsLoss(0.5))
        assertFalse(transport.exceedsLoss(0.95))

        val net = AetherNetworkEvent(AetherNetworkEventKind.CongestionDetected, 10, 4, 0.8, Instant.now())
        assertTrue(net.isHighCongestion)

        assertTrue(secEvent("n", AetherThreatLevel.Critical).isHighSeverity)
        assertFalse(secEvent("n", AetherThreatLevel.Low).isHighSeverity)
    }

    @Test
    fun `security directive target and permanence`() {
        val targeted = SecurityDirective(SecurityDirectiveKind.AvoidNode, "n1", null, AetherThreatLevel.High, "r", null, Instant.now())
        assertTrue(targeted.hasTarget)
        assertTrue(targeted.isPermanent)

        val untargeted = SecurityDirective(SecurityDirectiveKind.RequestReauth, null, null, AetherThreatLevel.Medium, "r", Duration.ofMinutes(5), Instant.now())
        assertFalse(untargeted.hasTarget)
        assertFalse(untargeted.isPermanent)
    }

    @Test
    fun `trust score update change and degrade`() {
        val degraded = TrustScoreUpdate("n", 0.9, 0.4, "r", Instant.now())
        assertTrue(degraded.hasChanged)
        assertTrue(degraded.isDegraded)

        val tiny = TrustScoreUpdate("n", 0.9, 0.9005, "r", Instant.now())
        assertFalse(tiny.hasChanged) // within 0.001
    }

    @Test
    fun `intelligence output validity ranges`() {
        assertTrue(NetworkHealthReport(0.5, 1, 0, "s", Instant.now()).isValid)
        assertFalse(NetworkHealthReport(1.2, 1, 0, "s", Instant.now()).isValid)
        assertTrue(ThreatAssessment("n", 0.3, AetherThreatLevel.Low, emptyList(), Instant.now()).isValid)
        assertFalse(ThreatAssessment("n", -0.1, AetherThreatLevel.Low, emptyList(), Instant.now()).isValid)
    }

    @Test
    fun `auth challenge result factory methods`() {
        val ok = AuthChallengeResult.success(AuthMethod.Biometric)
        assertTrue(ok.succeeded)
        assertNull(ok.failureReason)
        val bad = AuthChallengeResult.failure(AuthMethod.DeviceAdmin, "nope")
        assertFalse(bad.succeeded)
        assertEquals("nope", bad.failureReason)
        // strength ordering preserved
        assertTrue(AuthMethod.Custom.strength > AuthMethod.BiometricAndDeviceAdmin.strength)
    }

    // ── InMemoryAetherContext ─────────────────────────────────────────────────

    @Test
    fun `context absent reports unavailable`() {
        val ctx = InMemoryAetherContext.absent()
        assertEquals(AetherInstallLevel.None, ctx.installLevel)
        assertFalse(ctx.isAvailable)
        assertFalse(ctx.isEnabled)
        assertFalse(ctx.requiresAuth)
        assertTrue(ctx.isSufficient) // no minimum required
    }

    @Test
    fun `context OS level requires auth and honours enabled flag`() {
        val on = InMemoryAetherContext(AetherInstallLevel.OS, AetherVersion(2), enabled = true)
        assertTrue(on.requiresAuth)
        assertTrue(on.isEnabled)
        assertTrue(on.isAvailable)

        val off = InMemoryAetherContext(AetherInstallLevel.OS, AetherVersion(2), enabled = false)
        assertTrue(off.requiresAuth)
        assertFalse(off.isEnabled) // toggled off
        assertFalse(off.isAvailable)
    }

    @Test
    fun `context sufficiency compares runtime against minimum`() {
        val ok = InMemoryAetherContext(AetherInstallLevel.App, AetherVersion(2, 5), AetherVersion(2, 0))
        assertTrue(ok.isSufficient)
        val tooOld = InMemoryAetherContext(AetherInstallLevel.App, AetherVersion(1, 9), AetherVersion(2, 0))
        assertFalse(tooOld.isSufficient)
        val noRuntime = InMemoryAetherContext(AetherInstallLevel.App, null, AetherVersion(2, 0))
        assertFalse(noRuntime.isSufficient) // minimum set but no runtime
    }

    // ── InMemoryAuthChallenge ─────────────────────────────────────────────────

    @Test
    fun `auth challenge enforces biometric-and-device-admin floor when null`() = runTest {
        val captured = ArrayList<AuthMethod>()
        val challenge = InMemoryAuthChallenge { _, required, _ ->
            captured.add(required)
            AuthChallengeResult.success(required)
        }
        challenge.challenge(AuthChallengeReason.PrivilegedOperation, null, "prompt")
        assertEquals(AuthMethod.BiometricAndDeviceAdmin, captured.single())
    }

    @Test
    fun `auth challenge never drops below the floor`() = runTest {
        val captured = ArrayList<AuthMethod>()
        val challenge = InMemoryAuthChallenge { _, required, _ ->
            captured.add(required)
            AuthChallengeResult.success(required)
        }
        // Ask for a weaker method than the floor; the floor must still apply.
        challenge.challenge(AuthChallengeReason.ManualRequest, AuthMethod.Biometric, "p")
        assertEquals(AuthMethod.BiometricAndDeviceAdmin, captured.single())
    }

    @Test
    fun `auth challenge honours a stronger requested minimum`() = runTest {
        val captured = ArrayList<AuthMethod>()
        val challenge = InMemoryAuthChallenge { _, required, _ ->
            captured.add(required)
            AuthChallengeResult.success(required)
        }
        challenge.challenge(AuthChallengeReason.ThreatThresholdReached, AuthMethod.Custom, "p")
        assertEquals(AuthMethod.Custom, captured.single())
    }

    @Test
    fun `auth challenge fails when authenticator returns a too-weak method`() = runTest {
        // Authenticator claims success but with a weaker method than required.
        val challenge = InMemoryAuthChallenge { _, _, _ ->
            AuthChallengeResult.success(AuthMethod.Biometric)
        }
        val result = challenge.challenge(AuthChallengeReason.PrivilegedOperation, AuthMethod.Custom, "p")
        assertFalse(result.succeeded)
    }

    @Test
    fun `os toggle always requires the OS floor`() = runTest {
        val captured = ArrayList<AuthMethod>()
        val challenge = InMemoryAuthChallenge { _, required, _ ->
            captured.add(required)
            AuthChallengeResult.success(required)
        }
        val r = challenge.requestOsToggle(enable = true)
        assertTrue(r.succeeded)
        assertEquals(AuthMethod.BiometricAndDeviceAdmin, captured.single())
    }

    @Test
    fun `default authenticator approves with the required minimum`() = runTest {
        val challenge = InMemoryAuthChallenge()
        val r = challenge.challenge(AuthChallengeReason.PeriodicRevalidation, null, "p")
        assertTrue(r.succeeded)
        assertEquals(AuthMethod.BiometricAndDeviceAdmin, r.methodUsed)
    }

    // ── InMemoryAetherTelemetry ───────────────────────────────────────────────

    @Test
    fun `telemetry fans out to observers and unsubscribe stops delivery`() {
        val telemetry = InMemoryAetherTelemetry()
        val seen = CopyOnWriteArrayList<String>()
        val handle = telemetry.subscribe(object : IAetherTelemetryObserver {
            override fun onSecurityEvent(e: AetherSecurityEvent) { seen.add(e.nodeId) }
        })
        assertEquals(1, telemetry.observerCount)

        telemetry.emitSecurity(secEvent("n1", AetherThreatLevel.High))
        assertEquals(listOf("n1"), seen)

        handle.close()
        assertEquals(0, telemetry.observerCount)
        telemetry.emitSecurity(secEvent("n2", AetherThreatLevel.High))
        assertEquals(listOf("n1"), seen) // no delivery after close
    }

    // ── InMemoryAISecurityLayer ───────────────────────────────────────────────

    @Test
    fun `security layer degrades trust and issues single directive on threshold crossing`() = runTest {
        val telemetry = InMemoryAetherTelemetry()
        val layer = InMemoryAISecurityLayer()
        val rec = DirectiveRecorder()
        layer.subscribeToDirectives(rec)
        layer.start(telemetry)

        // High = 0.50 degradation: 1.0 -> 0.50, crosses avoid (<=0.50) but not
        // quarantine (<=0.25). Exactly one AvoidNode directive.
        telemetry.emitSecurity(secEvent("bad", AetherThreatLevel.High))
        assertEquals(1, rec.directives.size)
        assertEquals(SecurityDirectiveKind.AvoidNode, rec.directives[0].kind)
        assertEquals("bad", rec.directives[0].targetNodeId)
        assertEquals(0.5, layer.trustLedger.trustOf("bad"), 1e-9)

        layer.stop()
    }

    @Test
    fun `security layer quarantines on critical event`() = runTest {
        val telemetry = InMemoryAetherTelemetry()
        val layer = InMemoryAISecurityLayer()
        val rec = DirectiveRecorder()
        layer.subscribeToDirectives(rec)
        layer.start(telemetry)

        // Critical = 1.0 degradation: 1.0 -> 0.0, crosses quarantine. One directive.
        telemetry.emitSecurity(secEvent("evil", AetherThreatLevel.Critical))
        assertEquals(1, rec.directives.size)
        assertEquals(SecurityDirectiveKind.QuarantineNode, rec.directives[0].kind)
        assertEquals(AetherThreatLevel.Critical, rec.directives[0].threatLevel)

        val posture = layer.getPosture()
        assertEquals(1, posture.quarantinedNodeCount)
        assertTrue(posture.isActive)
        assertEquals(AetherThreatLevel.Critical, posture.overallThreatLevel)

        layer.stop()
        assertFalse(layer.getPosture().isActive)
    }

    @Test
    fun `none-level event neither degrades nor issues a directive`() = runTest {
        val telemetry = InMemoryAetherTelemetry()
        val layer = InMemoryAISecurityLayer()
        val rec = DirectiveRecorder()
        layer.subscribeToDirectives(rec)
        layer.start(telemetry)

        telemetry.emitSecurity(secEvent("calm", AetherThreatLevel.None))
        assertTrue(rec.directives.isEmpty())
        assertEquals(1.0, layer.trustLedger.trustOf("calm"), 1e-9)
        layer.stop()
    }

    @Test
    fun `unsubscribing a directive consumer stops delivery`() = runTest {
        val telemetry = InMemoryAetherTelemetry()
        val layer = InMemoryAISecurityLayer()
        val rec = DirectiveRecorder()
        val handle = layer.subscribeToDirectives(rec)
        layer.start(telemetry)
        handle.close()

        telemetry.emitSecurity(secEvent("x", AetherThreatLevel.Critical))
        assertTrue(rec.directives.isEmpty())
        layer.stop()
    }

    // ── InMemoryAetherIntelligence (shares the layer ledger) ──────────────────

    @Test
    fun `intelligence assesses unknown node as none with zero confidence`() = runTest {
        val intel = InMemoryAetherIntelligence()
        val a = intel.assessThreat("ghost")
        assertEquals(AetherThreatLevel.None, a.level)
        assertEquals(0.0, a.threatConfidence, 1e-9)
        assertTrue(a.indicators.isEmpty())
    }

    @Test
    fun `intelligence reports health and routing over shared ledger`() = runTest {
        val telemetry = InMemoryAetherTelemetry()
        val layer = InMemoryAISecurityLayer()
        val intel = InMemoryAetherIntelligence(layer.trustLedger)
        layer.start(telemetry)

        // Degrade "dest" to 0.0 (quarantined) and observe a clean node.
        telemetry.emitSecurity(secEvent("dest", AetherThreatLevel.Critical))
        layer.trustLedger.ensure("good")

        val advice = intel.getRoutingAdvice("dest")
        assertTrue(advice.recommendedPath.isEmpty())
        assertTrue(advice.avoidNodes.contains("dest"))
        assertTrue(advice.reasoning.contains("quarantined"))

        val threat = intel.assessThreat("dest")
        assertEquals(AetherThreatLevel.Critical, threat.level)
        assertTrue(threat.indicators.contains("intrusion-signal"))

        val health = intel.getNetworkHealth()
        // Two nodes: dest=0.0, good=1.0 -> overall 0.5
        assertEquals(0.5, health.overallScore, 1e-9)
        layer.stop()
    }

    @Test
    fun `intelligence stream relays ledger trust updates`() = runTest {
        val ledger = AetherTrustLedger()
        val intel = InMemoryAetherIntelligence(ledger)

        // The ledger's channel is unbounded and retains writes until read, so an
        // update emitted BEFORE collection is buffered and delivered on first().
        ledger.applyEvent(secEvent("p1", AetherThreatLevel.Medium))
        val update = intel.streamTrustScores().first()

        assertEquals("p1", update.nodeId)
        assertTrue(update.isDegraded)
    }

    @Test
    fun `ledger buffers updates emitted before the first collector attaches`() = runTest {
        // Retain-until-read: an update emitted before any collector attaches is
        // buffered (no lost writes), unlike a replay=0 SharedFlow.
        val ledger = AetherTrustLedger()
        ledger.applyEvent(secEvent("early", AetherThreatLevel.High))
        // First collection still sees the earlier emission.
        val first = ledger.updates.first()
        assertEquals("early", first.nodeId)
    }
}
