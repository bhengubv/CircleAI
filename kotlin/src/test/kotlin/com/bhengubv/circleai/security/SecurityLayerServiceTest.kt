// SecurityLayerServiceTest.kt
//
// Verifies the transport-agnostic security layer: trust degradation on events,
// most-severe-wins single-directive-per-event threshold crossing, posture
// counts, no-op on None-level events, and start/stop lifecycle.

package com.bhengubv.circleai.security

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class SecurityLayerServiceTest {

    private class Recorder : IPeerDirectiveConsumer {
        val directives = CopyOnWriteArrayList<PeerDirective>()
        override fun onDirective(directive: PeerDirective) {
            directives.add(directive)
        }
    }

    private fun buildLayer(
        options: SecurityOptions = SecurityOptions(),
    ): Triple<SecurityLayerService, NodeTrustRegistry, Recorder> {
        val registry = NodeTrustRegistry(options)
        val publisher = DirectivePublisher()
        val recorder = Recorder()
        publisher.subscribe(recorder)
        val layer = SecurityLayerService(registry, options, publisher)
        return Triple(layer, registry, recorder)
    }

    private fun event(
        node: String,
        kind: PeerSecurityEventKind,
        level: PeerThreatLevel,
        at: Instant = Instant.now(),
    ) = PeerSecurityEvent(node, kind, level, "evt", "test", at)

    @Test
    fun `None-level event causes no degradation and no directive`() {
        val (layer, registry, rec) = buildLayer()
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.None))
        assertEquals(1.0, registry.getTrustScore("p1"), 1e-9)
        assertTrue(rec.directives.isEmpty())
    }

    @Test
    fun `crossing elevate threshold issues a single ElevateMonitoring directive`() {
        // elevate=0.75; one IntrusionSignal@Medium = 0.15 degradation -> 0.85 (no cross)
        // Use a High intrusion (0.15*2=0.30) -> 0.70 which crosses 0.75 only, not 0.50.
        val (layer, _, rec) = buildLayer()
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        assertEquals(1, rec.directives.size)
        assertEquals(PeerDirectiveKind.ElevateMonitoring, rec.directives[0].kind)
        assertEquals(PeerThreatLevel.Medium, rec.directives[0].threatLevel)
        assertEquals("p1", rec.directives[0].targetNodeId)
    }

    @Test
    fun `crossing avoid threshold issues AvoidNode`() {
        // Start at 1.0; drive to just below 0.50 in one Critical intrusion (0.15*3=0.45)
        // -> 0.55, still above 0.50. Need a second event. First push to 0.70 then to 0.25?
        // Instead: DataExfiltration@Critical = 0.14*3 = 0.42 -> 0.58 (crosses 0.75 only).
        // Second identical event -> 0.16 -> crosses both 0.50 and 0.25? 0.58-0.42=0.16
        // crosses 0.50 AND 0.25 in one step -> most-severe wins = Quarantine. So to get
        // AvoidNode we craft a single event that crosses 0.50 but not 0.25.
        val (layer, _, rec) = buildLayer()
        // First event: Intrusion@High 0.30 -> 0.70 (Elevate).
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        // Second event: Intrusion@High 0.30 -> 0.40 (crosses 0.50, not 0.25) => AvoidNode.
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        assertEquals(2, rec.directives.size)
        assertEquals(PeerDirectiveKind.ElevateMonitoring, rec.directives[0].kind)
        assertEquals(PeerDirectiveKind.AvoidNode, rec.directives[1].kind)
        assertEquals(PeerThreatLevel.High, rec.directives[1].threatLevel)
    }

    @Test
    fun `single large drop across quarantine issues only Quarantine (most severe wins)`() {
        val (layer, registry, rec) = buildLayer()
        // Three Critical intrusions: 0.45 each. 1.0 -> 0.55 -> 0.10.
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical))
        // After first: 0.55, crosses 0.75 -> Elevate.
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical))
        // After second: 0.10, crosses 0.50 and 0.25 in one step -> Quarantine only.
        assertTrue(registry.getTrustScore("p1") <= 0.25)
        assertEquals(PeerDirectiveKind.ElevateMonitoring, rec.directives[0].kind)
        assertEquals(PeerDirectiveKind.QuarantineNode, rec.directives[1].kind)
        assertEquals(PeerThreatLevel.Critical, rec.directives[1].threatLevel)
        // Exactly one directive per event -> 2 total.
        assertEquals(2, rec.directives.size)
    }

    @Test
    fun `getPosture counts quarantined and monitored peers`() = runTest {
        val (layer, _, _) = buildLayer()
        // p-quar -> below 0.25
        layer.handlePeerEvent(event("q", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical))
        layer.handlePeerEvent(event("q", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical))
        // p-mon -> between 0.25 and 0.75
        layer.handlePeerEvent(event("m", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        // p-ok -> stays high
        layer.handlePeerEvent(event("ok", PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low))

        val posture = layer.getPosture()
        assertEquals(1, posture.quarantinedPeerCount)
        assertEquals(1, posture.monitoredPeerCount)
        assertEquals(PeerThreatLevel.Critical, posture.overallThreatLevel)
        assertFalse(posture.isActive) // not started
    }

    @Test
    fun `empty posture is healthy`() = runTest {
        val (layer, _, _) = buildLayer()
        val posture = layer.getPosture()
        assertEquals(PeerThreatLevel.None, posture.overallThreatLevel)
        assertEquals(0, posture.quarantinedPeerCount)
        assertEquals(0, posture.monitoredPeerCount)
    }

    @Test
    fun `start then stop toggles active flag`() = runTest {
        val options = SecurityOptions()
        val registry = NodeTrustRegistry(options)
        val publisher = DirectivePublisher()
        // Use this test's scope for the recovery loop; short interval.
        val layer = SecurityLayerService(
            registry, options, publisher,
            scope = this,
            recoveryInterval = Duration.ofSeconds(30),
        )

        layer.start()
        assertTrue(layer.getPosture().isActive)

        layer.stop()
        assertFalse(layer.getPosture().isActive)
    }

    @Test
    fun `double start is idempotent`() = runTest {
        val options = SecurityOptions()
        val layer = SecurityLayerService(
            NodeTrustRegistry(options), options, DirectivePublisher(),
            scope = this,
        )
        layer.start()
        layer.start() // no-op
        assertTrue(layer.getPosture().isActive)
        layer.stop()
    }

    @Test
    fun `subscribeToDirectives handle unsubscribes`() {
        val (layer, _, _) = buildLayer()
        val rec = Recorder()
        val handle = layer.subscribeToDirectives(rec)
        layer.handlePeerEvent(event("p1", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        assertEquals(1, rec.directives.size)
        handle.close()
        layer.handlePeerEvent(event("p2", PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.High))
        assertEquals(1, rec.directives.size, "no directives after unsubscribe")
    }
}
