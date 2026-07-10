// SecurityAetherNetTest.kt
//
// Verifies the AetherNet-specific security bindings:
//   - MeshDirectiveStore: records Avoid/Quarantine as blocks, Release lifts all,
//     duration-based lazy expiry, active-directive audit view
//   - MeshSecurityGate: decide() + enforce() (throws MeshSecurityBlockedException)
//   - MeshGatedCompanionSession: guards send/stream/agent, passes through
//     context/history/feedback/close
//   - AetherIntelligenceAdapter: maps Peer* intelligence outputs to Aether shape
//   - AetherSecurityBridge: telemetry event -> security layer -> directive ->
//     Aether ISecurityDirectiveConsumer, and posture mapping

package com.bhengubv.circleai.security.aethernet

import com.bhengubv.circleai.aether.AetherSecurityEvent
import com.bhengubv.circleai.aether.AetherSecurityEventKind
import com.bhengubv.circleai.aether.AetherThreatLevel
import com.bhengubv.circleai.aether.InMemoryAetherTelemetry
import com.bhengubv.circleai.aether.ISecurityDirectiveConsumer
import com.bhengubv.circleai.aether.SecurityDirective
import com.bhengubv.circleai.aether.SecurityDirectiveKind
import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.security.DirectivePublisher
import com.bhengubv.circleai.security.NodeTrustRegistry
import com.bhengubv.circleai.security.PeerIntelligenceService
import com.bhengubv.circleai.security.SecurityLayerService
import com.bhengubv.circleai.security.SecurityOptions
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class SecurityAetherNetTest {

    // ── helpers ──────────────────────────────────────────────────────────────

    private fun directive(
        kind: SecurityDirectiveKind,
        node: String,
        reason: String = "r",
        duration: Duration? = null,
        issuedAt: Instant = Instant.now(),
    ) = SecurityDirective(kind, node, null, AetherThreatLevel.High, reason, duration, issuedAt)

    private class Recorder : ISecurityDirectiveConsumer {
        val directives = CopyOnWriteArrayList<SecurityDirective>()
        override fun onDirective(directive: SecurityDirective) { directives.add(directive) }
    }

    // A minimal fake companion session that records calls and returns canned text.
    private class FakeSession(
        override val identityId: String = "user-1",
    ) : ICompanionSession {
        val sends = AtomicInteger(0)
        val agents = AtomicInteger(0)
        var streamed = false
        var closed = false
        var refreshed = false
        val feedback = CopyOnWriteArrayList<Boolean>()

        override val sessionId: String = "sess-1"
        override val interfaceKind: InterfaceKind = InterfaceKind.Headless
        override val history: List<CompanionTurn> = listOf(CompanionTurn("user", "hi", Instant.now()))
        override val proactiveEvents: Flow<CompanionProactiveEvent> = emptyFlow()

        override suspend fun sendAsync(message: String): String {
            sends.incrementAndGet(); return "reply:$message"
        }

        override fun streamAsync(message: String): Flow<String> {
            streamed = true; return flowOf("a", "b")
        }

        override suspend fun agentAsync(instruction: String): String {
            agents.incrementAndGet(); return "did:$instruction"
        }

        override fun getContext(): CompanionContext = CompanionContext(
            identityId, "Name", null, interfaceKind, "", "", emptyList(), emptyList(), Instant.now(),
        )

        override suspend fun refreshContextAsync() { refreshed = true }

        override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) {
            feedback.add(positive)
        }

        override fun close() { closed = true }
    }

    // ── MeshDirectiveStore ─────────────────────────────────────────────────────

    @Test
    fun `store records avoid and quarantine as blocks`() {
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "n1", reason = "sketchy"))
        val check = store.isBlocked("n1")
        assertTrue(check.blocked)
        assertEquals("sketchy", check.reason)
        assertEquals(1, store.trackedNodeCount)
    }

    @Test
    fun `store ignores untargeted directives`() {
        val store = MeshDirectiveStore()
        store.onDirective(
            SecurityDirective(SecurityDirectiveKind.RequestReauth, null, null, AetherThreatLevel.Medium, "r", null, Instant.now()),
        )
        assertEquals(0, store.trackedNodeCount)
    }

    @Test
    fun `release lifts every block for the node`() {
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.QuarantineNode, "n1"))
        assertTrue(store.isBlocked("n1").blocked)
        store.onDirective(directive(SecurityDirectiveKind.ReleaseNode, "n1"))
        assertFalse(store.isBlocked("n1").blocked)
        assertEquals(0, store.trackedNodeCount)
    }

    @Test
    fun `most recent block reason wins`() {
        val base = Instant.parse("2026-07-10T00:00:00Z")
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "n1", reason = "first", issuedAt = base))
        store.onDirective(directive(SecurityDirectiveKind.QuarantineNode, "n1", reason = "second", issuedAt = base.plusSeconds(10)))
        assertEquals("second", store.isBlocked("n1").reason)
    }

    @Test
    fun `expired directive is swept on read`() {
        var now = Instant.parse("2026-07-10T00:00:00Z")
        val store = MeshDirectiveStore(clock = { now })
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "n1", duration = Duration.ofSeconds(30), issuedAt = now))
        assertTrue(store.isBlocked("n1").blocked)

        now = now.plusSeconds(31) // past expiry
        assertFalse(store.isBlocked("n1").blocked)
        assertEquals(0, store.trackedNodeCount) // swept
    }

    @Test
    fun `active directives view excludes expired`() {
        var now = Instant.parse("2026-07-10T00:00:00Z")
        val store = MeshDirectiveStore(clock = { now })
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "n1", duration = Duration.ofSeconds(30), issuedAt = now))
        store.onDirective(directive(SecurityDirectiveKind.ElevateMonitoring, "n1", duration = null, issuedAt = now))
        assertEquals(2, store.getActiveDirectives("n1").size)
        now = now.plusSeconds(31)
        assertEquals(1, store.getActiveDirectives("n1").size) // only the permanent one
    }

    // ── MeshSecurityGate ───────────────────────────────────────────────────────

    @Test
    fun `gate decide reflects block state`() {
        val store = MeshDirectiveStore()
        val gate = MeshSecurityGate(store)
        assertFalse(gate.decide("n1").isBlocked)
        store.onDirective(directive(SecurityDirectiveKind.QuarantineNode, "n1", reason = "bad"))
        val d = gate.decide("n1")
        assertTrue(d.isBlocked)
        assertEquals("bad", d.reason)
    }

    @Test
    fun `gate enforce throws for blocked id`() {
        val store = MeshDirectiveStore()
        val gate = MeshSecurityGate(store)
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "n1", reason = "no entry"))
        val ex = assertFailsWith<MeshSecurityBlockedException> { gate.enforce("n1") }
        assertEquals("n1", ex.blockedId)
        // allowed id does not throw
        gate.enforce("clean")
    }

    @Test
    fun `blank id is always allowed`() {
        val gate = MeshSecurityGate(MeshDirectiveStore())
        assertFalse(gate.decide("").isBlocked)
        assertEquals(MeshSecurityGate.GateDecision.Allowed, gate.decide("  "))
    }

    // ── MeshGatedCompanionSession ─────────────────────────────────────────────

    @Test
    fun `gated session allows calls for an unblocked identity`() = runTest {
        val inner = FakeSession("user-ok")
        val store = MeshDirectiveStore()
        val gated = MeshGatedCompanionSession(inner, MeshSecurityGate(store))

        assertEquals("reply:hello", gated.sendAsync("hello"))
        assertEquals("did:task", gated.agentAsync("task"))
        assertEquals(listOf("a", "b"), gated.streamAsync("x").toList())
        assertEquals(1, inner.sends.get())
        assertEquals(1, inner.agents.get())
        assertTrue(inner.streamed)
    }

    @Test
    fun `gated session blocks send and agent for a blocked identity`() = runTest {
        val inner = FakeSession("user-bad")
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.QuarantineNode, "user-bad", reason = "banned"))
        val gated = MeshGatedCompanionSession(inner, MeshSecurityGate(store))

        assertFailsWith<MeshSecurityBlockedException> { gated.sendAsync("hello") }
        assertFailsWith<MeshSecurityBlockedException> { gated.agentAsync("task") }
        assertEquals(0, inner.sends.get())
        assertEquals(0, inner.agents.get())
    }

    @Test
    fun `gated session blocks stream at collection time`() = runTest {
        val inner = FakeSession("user-bad")
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.AvoidNode, "user-bad", reason = "banned"))
        val gated = MeshGatedCompanionSession(inner, MeshSecurityGate(store))

        val flow = gated.streamAsync("x") // building the flow does not throw
        assertFailsWith<MeshSecurityBlockedException> { flow.toList() } // collection throws
        assertFalse(inner.streamed) // inner stream never reached
    }

    @Test
    fun `gated session passes through diagnostic calls even when blocked`() = runTest {
        val inner = FakeSession("user-bad")
        val store = MeshDirectiveStore()
        store.onDirective(directive(SecurityDirectiveKind.QuarantineNode, "user-bad"))
        val gated = MeshGatedCompanionSession(inner, MeshSecurityGate(store))

        // Metadata / diagnostic surfaces are NOT gated.
        assertEquals("user-bad", gated.getContext().identityId)
        assertEquals(inner.history, gated.history)
        gated.refreshContextAsync()
        gated.signalFeedbackAsync(true, "ok")
        gated.close()
        assertTrue(inner.refreshed)
        assertEquals(listOf(true), inner.feedback)
        assertTrue(inner.closed)
    }

    // ── AetherIntelligenceAdapter ─────────────────────────────────────────────

    @Test
    fun `intelligence adapter maps peer outputs to aether shape`() = runTest {
        val options = SecurityOptions()
        val registry = NodeTrustRegistry(options)
        val peerIntel = PeerIntelligenceService(registry, options)
        val adapter = AetherIntelligenceAdapter(peerIntel)

        // Empty network -> full health mapped through.
        val health = adapter.getNetworkHealth()
        assertEquals(1.0, health.overallScore, 1e-9)
        assertEquals("No peers observed.", health.summary)

        // Unknown node threat maps to None with zero confidence.
        val threat = adapter.assessThreat("ghost")
        assertEquals(AetherThreatLevel.None, threat.level)
        assertEquals(0.0, threat.threatConfidence, 1e-9)

        // Routing advice for a trusted destination is direct.
        registry.getOrCreate("dest")
        val advice = adapter.getRoutingAdvice("dest")
        assertEquals(listOf("dest"), advice.recommendedPath)
    }

    @Test
    fun `intelligence adapter relays and maps the trust score stream`() = runTest {
        val options = SecurityOptions()
        val registry = NodeTrustRegistry(options)
        val peerIntel = PeerIntelligenceService(registry, options)
        val adapter = AetherIntelligenceAdapter(peerIntel)

        val evt = com.bhengubv.circleai.security.PeerSecurityEvent(
            "p1",
            com.bhengubv.circleai.security.PeerSecurityEventKind.IntrusionSignal,
            com.bhengubv.circleai.security.PeerThreatLevel.High,
            "e", "test", Instant.now(),
        )
        registry.applyDegradation(evt, 0.3)
        val update = adapter.streamTrustScores().first()
        assertEquals("p1", update.nodeId)
        // newScore -> currentScore mapping
        assertTrue(update.currentScore < update.previousScore)
    }

    // ── AetherSecurityBridge ───────────────────────────────────────────────────

    @Test
    fun `security bridge routes telemetry events into directives`() = runTest {
        val options = SecurityOptions()
        val registry = NodeTrustRegistry(options)
        val publisher = DirectivePublisher()
        val layer = SecurityLayerService(registry, options, publisher)
        val bridge = AetherSecurityBridge(layer)

        val recorder = Recorder()
        bridge.subscribeToDirectives(recorder)

        val telemetry = InMemoryAetherTelemetry()
        bridge.start(telemetry)

        // Critical intrusion: degradation 0.14*3 in the peer layer... use a strong
        // event so trust crosses at least the avoid threshold and a directive fires.
        telemetry.emitSecurity(
            AetherSecurityEvent(
                "attacker",
                AetherSecurityEventKind.IntrusionSignal,
                AetherThreatLevel.Critical,
                "breach",
                emptyMap(),
                Instant.now(),
            ),
        )

        assertTrue(recorder.directives.isNotEmpty(), "expected at least one directive")
        val d = recorder.directives.first()
        assertEquals("attacker", d.targetNodeId)
        // Directive kind is a valid Aether SecurityDirectiveKind (mapped from Peer).
        assertTrue(
            d.kind in setOf(
                SecurityDirectiveKind.AvoidNode,
                SecurityDirectiveKind.QuarantineNode,
                SecurityDirectiveKind.ElevateMonitoring,
            ),
        )

        val posture = bridge.getPosture()
        assertTrue(posture.isActive)
        bridge.stop()
        assertFalse(bridge.getPosture().isActive)
    }
}
