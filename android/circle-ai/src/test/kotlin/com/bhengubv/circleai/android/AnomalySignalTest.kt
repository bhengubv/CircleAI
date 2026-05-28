// AnomalySignalTest.kt
//
// Verifies:
//   - ThreatVector ordinal stability (wire/storage contract)
//   - AnomalySignal.create assigns fresh UUID, stamps detectedAt, copies evidence
//   - AnomalySignal.create clamps confidence into [0f, 1f] for all
//     boundary vectors documented in the cross-port test plan.

package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.security.AnomalySignal
import com.bhengubv.circleai.android.security.ThreatVector
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant

class AnomalySignalTest {

    // ── ThreatVector ordinals (stable wire contract) ─────────────────────────

    @Test
    fun threatVectorHasExactlyEightValues() {
        assertEquals(8, ThreatVector.entries.size)
    }

    @Test
    fun threatVectorOrdinalsMatchCrossPortContract() {
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
    fun threatVectorEntriesInDeclaredOrder() {
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
    fun createClampsAboveMaxConfidence() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 1.5f,
            affectedModule = "mod",
            description = "above max",
        )
        assertEquals(1.0f, s.confidence, 0f)
    }

    @Test
    fun createClampsBelowMinConfidence() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = -0.3f,
            affectedModule = "mod",
            description = "below min",
        )
        assertEquals(0.0f, s.confidence, 0f)
    }

    @Test
    fun createPreservesAtMaxConfidence() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 1.0f,
            affectedModule = "mod",
            description = "at max",
        )
        assertEquals(1.0f, s.confidence, 0f)
    }

    @Test
    fun createPreservesAtMinConfidence() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 0.0f,
            affectedModule = "mod",
            description = "at min",
        )
        assertEquals(0.0f, s.confidence, 0f)
    }

    @Test
    fun createPreservesNominalConfidence() {
        val s = AnomalySignal.create(
            vector = ThreatVector.MemoryAnomaly,
            confidence = 0.7f,
            affectedModule = "mod",
            description = "nominal",
        )
        assertEquals(0.7f, s.confidence, 0f)
    }

    // ── AnomalySignal.create — id / timestamp / evidence ─────────────────────

    @Test
    fun createAssignsFreshIdPerCall() {
        val a = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "first")
        val b = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "second")
        assertNotEquals("Each create() call must produce a unique UUID", a.id, b.id)
    }

    @Test
    fun createStampsDetectedAtWithinTightWindowOfNow() {
        val before = Instant.now()
        val signal = AnomalySignal.create(ThreatVector.NetworkPivot, 0.5f, "net", "probe")
        val after = Instant.now()
        assertTrue(
            "detectedAt ${signal.detectedAt} was before window start $before",
            !signal.detectedAt.isBefore(before.minus(Duration.ofSeconds(1))),
        )
        assertTrue(
            "detectedAt ${signal.detectedAt} was after window end $after",
            !signal.detectedAt.isAfter(after.plus(Duration.ofSeconds(1))),
        )
    }

    @Test
    fun createDefaultsEvidenceToEmptyMapWhenNull() {
        val s = AnomalySignal.create(ThreatVector.Unknown, 0.5f, "mod", "no evidence")
        assertTrue("evidence should default to empty map", s.evidence.isEmpty())
    }

    @Test
    fun createCopiesEvidenceMapDefensively() {
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
        assertEquals("evidence must be defensively copied", 2, s.evidence.size)
        assertEquals("10.0.0.1", s.evidence["ip"])
    }

    @Test
    fun createPreservesVectorAffectedModuleAndDescription() {
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
