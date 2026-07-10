// ThreatDetectorTest.kt
//
// Verifies the stateless threat logic: degradation = baseWeight × multiplier,
// and indicator derivation over an event window.

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.assertFalse

class ThreatDetectorTest {

    private fun event(
        kind: PeerSecurityEventKind,
        level: PeerThreatLevel,
        at: Instant = Instant.now(),
        node: String = "peer-1",
    ) = PeerSecurityEvent(node, kind, level, "desc", "test", at)

    // -- computeDegradation --------------------------------------------------

    @Test
    fun `None threat level yields zero degradation`() {
        val d = ThreatDetector.computeDegradation(
            event(PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.None),
        )
        assertEquals(0.0, d, 1e-9)
    }

    @Test
    fun `intrusion signal at critical is base 0_15 times 3`() {
        val d = ThreatDetector.computeDegradation(
            event(PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical),
        )
        assertEquals(0.15 * 3.0, d, 1e-9)
    }

    @Test
    fun `auth attempt at medium is base 0_05 times 1`() {
        val d = ThreatDetector.computeDegradation(
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Medium),
        )
        assertEquals(0.05, d, 1e-9)
    }

    @Test
    fun `data exfiltration at high is base 0_14 times 2`() {
        val d = ThreatDetector.computeDegradation(
            event(PeerSecurityEventKind.DataExfiltration, PeerThreatLevel.High),
        )
        assertEquals(0.14 * 2.0, d, 1e-9)
    }

    @Test
    fun `unknown kind falls back to base 0_05`() {
        val d = ThreatDetector.computeDegradation(
            event(PeerSecurityEventKind.Unknown, PeerThreatLevel.Low),
        )
        assertEquals(0.05 * 0.5, d, 1e-9)
    }

    @Test
    fun `all base weights match the C-sharp reference table`() {
        // multiplier at Medium is 1.0 so degradation == base weight
        val expected = mapOf(
            PeerSecurityEventKind.AuthAttempt to 0.05,
            PeerSecurityEventKind.RoutingAnomaly to 0.10,
            PeerSecurityEventKind.BehaviourChange to 0.08,
            PeerSecurityEventKind.EncryptionEvent to 0.06,
            PeerSecurityEventKind.IntrusionSignal to 0.15,
            PeerSecurityEventKind.PrivilegeAttempt to 0.12,
            PeerSecurityEventKind.ConnectionAnomaly to 0.07,
            PeerSecurityEventKind.DataExfiltration to 0.14,
            PeerSecurityEventKind.DenialOfService to 0.13,
            PeerSecurityEventKind.Unknown to 0.05,
        )
        for ((kind, w) in expected) {
            val d = ThreatDetector.computeDegradation(event(kind, PeerThreatLevel.Medium))
            assertEquals(w, d, 1e-9, "weight mismatch for $kind")
        }
    }

    // -- detectIndicators ----------------------------------------------------

    @Test
    fun `empty events yields empty indicators`() {
        val result = ThreatDetector.detectIndicators(emptyList(), Duration.ofMinutes(5))
        assertTrue(result.isEmpty())
    }

    @Test
    fun `three auth attempts flags repeated-auth-attempts`() {
        val events = listOf(
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
        )
        val result = ThreatDetector.detectIndicators(events, Duration.ofMinutes(5))
        assertTrue(result.contains("repeated-auth-attempts"))
    }

    @Test
    fun `two auth attempts does not flag brute-force`() {
        val events = listOf(
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
        )
        val result = ThreatDetector.detectIndicators(events, Duration.ofMinutes(5))
        assertFalse(result.contains("repeated-auth-attempts"))
    }

    @Test
    fun `intrusion signal flags intrusion-signal-detected`() {
        val result = ThreatDetector.detectIndicators(
            listOf(event(PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Low)),
            Duration.ofMinutes(5),
        )
        assertTrue(result.contains("intrusion-signal-detected"))
    }

    @Test
    fun `high severity event flags high-severity-event`() {
        val result = ThreatDetector.detectIndicators(
            listOf(event(PeerSecurityEventKind.BehaviourChange, PeerThreatLevel.High)),
            Duration.ofMinutes(5),
        )
        assertTrue(result.contains("high-severity-event"))
    }

    @Test
    fun `three distinct kinds flags multi-vector-activity`() {
        val events = listOf(
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.RoutingAnomaly, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.EncryptionEvent, PeerThreatLevel.Low),
        )
        val result = ThreatDetector.detectIndicators(events, Duration.ofMinutes(5))
        assertTrue(result.contains("multi-vector-activity"))
    }

    @Test
    fun `privilege attempt and exfiltration flag their indicators`() {
        val events = listOf(
            event(PeerSecurityEventKind.PrivilegeAttempt, PeerThreatLevel.Low),
            event(PeerSecurityEventKind.DataExfiltration, PeerThreatLevel.Low),
        )
        val result = ThreatDetector.detectIndicators(events, Duration.ofMinutes(5))
        assertTrue(result.contains("privilege-escalation-attempt"))
        assertTrue(result.contains("data-exfiltration-signal"))
    }

    @Test
    fun `events outside the window are ignored`() {
        val old = Instant.now().minus(Duration.ofMinutes(10))
        val events = listOf(
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
            event(PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
        )
        val result = ThreatDetector.detectIndicators(events, Duration.ofMinutes(5))
        assertTrue(result.isEmpty(), "stale events must not produce indicators")
    }
}
