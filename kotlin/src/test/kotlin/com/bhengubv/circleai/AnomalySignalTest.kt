// AnomalySignalTest.kt
//
// Verifies:
//   - ThreatVector ordinal stability (wire/storage contract)
//   - AnomalySignal.create assigns fresh UUID, stamps detectedAt, copies evidence
//   - AnomalySignal.create clamps confidence into [0f, 1f] for all
//     boundary vectors documented in the cross-port test plan.

package com.bhengubv.circleai

import com.bhengubv.circleai.security.AnomalySignal
import com.bhengubv.circleai.security.ThreatVector
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class AnomalySignalTest {

    // ── ThreatVector ordinals (stable wire contract) ─────────────────────────

    @Test
    fun `ThreatVector has exactly 8 values`() {
        assertEquals(8, ThreatVector.entries.size)
    }

    @Test
    fun `ThreatVector ordinals match the cross-port contract`() {
        assertEquals(0, ThreatVector.MemoryAnomaly.ordinal)
        assertEquals(1, ThreatVector.ControlFlowDrift.ordinal)
        assertEquals(2, ThreatVector.PrivilegeEscalation.ordinal)
        assertEquals(3, ThreatVector.BiometricSpoofAttempt.ordinal)
        assertEquals(4, ThreatVector.NetworkPivot.ordinal)
        assertEquals(5, ThreatVector.StateCorruption.ordinal)
        assertEquals(6, ThreatVector.AgentPatchRejected.ordinal)
        assertEquals(7, ThreatVector.Unknown.ordinal)
    }

    @Test
    fun `ThreatVector entries are in declared order`() {
        val names = ThreatVector.entries.map { it.name }
        assertEquals(
            listOf(
                "MemoryAnomaly",
                "ControlFlowDrift",
                "PrivilegeEscalation",
                "BiometricSpoofAttempt",
                "NetworkPivot",
                "StateCorruption",
                "AgentPatchRejected",
                "Unknown",
            ),
            names,
        )
    }

    // ── AnomalySignal.create — confidence clamp vectors ──────────────────────

    @Test
    fun `create clamps above_max confidence 1_5 to 1_0`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 1.5f,
            affectedModule = "mod",
            description = "above max",
        )
        assertEquals(1.0f, s.confidence)
    }

    @Test
    fun `create clamps below_min confidence -0_3 to 0_0`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = -0.3f,
            affectedModule = "mod",
            description = "below min",
        )
        assertEquals(0.0f, s.confidence)
    }

    @Test
    fun `create preserves at_max confidence 1_0`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 1.0f,
            affectedModule = "mod",
            description = "at max",
        )
        assertEquals(1.0f, s.confidence)
    }

    @Test
    fun `create preserves at_min confidence 0_0`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 0.0f,
            affectedModule = "mod",
            description = "at min",
        )
        assertEquals(0.0f, s.confidence)
    }

    @Test
    fun `create preserves nominal confidence 0_7`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 0.7f,
            affectedModule = "mod",
            description = "nominal",
        )
        assertEquals(0.7f, s.confidence)
    }

    // ── AnomalySignal.create — id / timestamp / evidence ─────────────────────

    @Test
    fun `create assigns a fresh id per call`() {
        val a = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "first")
        val b = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "second")
        assertNotEquals(a.id, b.id, "Each create() call must produce a unique UUID")
    }

    @Test
    fun `create stamps detectedAt within a tight window of now`() {
        val before = Instant.now()
        val signal = AnomalySignal.create(ThreatVector.NetworkPivot, 0.5f, "net", "probe")
        val after = Instant.now()
        assertTrue(
            !signal.detectedAt.isBefore(before.minus(Duration.ofSeconds(1))),
            "detectedAt ${signal.detectedAt} was before window start $before",
        )
        assertTrue(
            !signal.detectedAt.isAfter(after.plus(Duration.ofSeconds(1))),
            "detectedAt ${signal.detectedAt} was after window end $after",
        )
    }

    @Test
    fun `create defaults evidence to empty map when null`() {
        val s = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "no evidence")
        assertTrue(s.evidence.isEmpty(), "evidence should default to empty map")
    }

    @Test
    fun `create copies the evidence map defensively`() {
        val src = mutableMapOf("ip" to "10.0.0.1", "port" to "443")
        val s = AnomalySignal.create(
            vector = ThreatVector.NetworkPivot,
            confidence = 0.9f,
            affectedModule = "net",
            description = "pivot",
            evidence = src,
        )
        // Snapshot captured at construction time.
        assertEquals(2, s.evidence.size)
        assertEquals("10.0.0.1", s.evidence["ip"])
        assertEquals("443", s.evidence["port"])

        // Mutating the source must not mutate the stored signal.
        src["new"] = "value"
        src.remove("ip")
        assertEquals(2, s.evidence.size, "evidence must be defensively copied")
        assertEquals("10.0.0.1", s.evidence["ip"])
    }

    @Test
    fun `create preserves vector affectedModule and description`() {
        val s = AnomalySignal.create(
            vector = ThreatVector.PrivilegeEscalation,
            confidence = 0.5f,
            affectedModule = "auth",
            description = "sudo without challenge",
        )
        assertEquals(ThreatVector.PrivilegeEscalation, s.vector)
        assertEquals("auth", s.affectedModule)
        assertEquals("sudo without challenge", s.description)
    }
}
