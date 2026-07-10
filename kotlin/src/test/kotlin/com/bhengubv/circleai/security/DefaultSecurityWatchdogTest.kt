// DefaultSecurityWatchdogTest.kt
//
// Verifies the graduated response policy of DefaultSecurityWatchdog and its
// signal stream (unbounded — buffers a pre-subscription emission).

package com.bhengubv.circleai.security

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class DefaultSecurityWatchdogTest {

    private fun signal(
        vector: ThreatVector,
        confidence: Float,
        module: String = "CircleAI.Companion",
    ) = AnomalySignal.create(vector, confidence, module, "anomaly")

    @Test
    fun `low confidence yields NoAction`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val r = wd.onAnomalyDetected(signal(ThreatVector.MemoryAnomaly, 0.20f))
        assertEquals(SecurityResponseKind.NoAction, r.kind)
        assertTrue(r.appliedActions.isEmpty())
        assertNull(r.restoredCheckpoint)
    }

    @Test
    fun `mid confidence yields KeyRotation`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val r = wd.onAnomalyDetected(signal(ThreatVector.MemoryAnomaly, 0.45f))
        assertEquals(SecurityResponseKind.KeyRotation, r.kind)
        assertTrue(r.description.contains("MemoryAnomaly"))
        assertTrue(r.description.contains("CircleAI.Companion"))
    }

    @Test
    fun `exactly rotation threshold is KeyRotation not NoAction`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val r = wd.onAnomalyDetected(signal(ThreatVector.MemoryAnomaly, 0.30f))
        assertEquals(SecurityResponseKind.KeyRotation, r.kind)
    }

    @Test
    fun `exactly composite threshold stays KeyRotation`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val r = wd.onAnomalyDetected(signal(ThreatVector.ControlFlowDrift, 0.60f))
        assertEquals(SecurityResponseKind.KeyRotation, r.kind)
    }

    @Test
    fun `high confidence yields Composite with rotation and mesh signal`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val r = wd.onAnomalyDetected(signal(ThreatVector.MemoryAnomaly, 0.80f))
        assertEquals(SecurityResponseKind.Composite, r.kind)
        assertTrue(r.appliedActions.contains(SecurityResponseKind.KeyRotation))
        assertTrue(r.appliedActions.contains(SecurityResponseKind.MeshIsolationSignal))
        // MemoryAnomaly is not a high-severity vector -> no rollback even with a checkpoint.
        assertTrue(!r.appliedActions.contains(SecurityResponseKind.StateRollback))
    }

    @Test
    fun `high confidence high-severity vector with verified checkpoint adds rollback`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Companion", "state".toByteArray())
        val r = wd.onAnomalyDetected(signal(ThreatVector.PrivilegeEscalation, 0.90f), cp)
        assertEquals(SecurityResponseKind.Composite, r.kind)
        assertTrue(r.appliedActions.contains(SecurityResponseKind.StateRollback))
        assertSame(cp, r.restoredCheckpoint)
    }

    @Test
    fun `high-severity vector but non-high-severity classification skips rollback`() = runTest {
        // BiometricSpoofAttempt is NOT in the high-severity set.
        val wd = DefaultSecurityWatchdog()
        val cp = SecurityCheckpoint.create("uhid-1", "mod", "s".toByteArray())
        val r = wd.onAnomalyDetected(signal(ThreatVector.BiometricSpoofAttempt, 0.95f), cp)
        assertTrue(!r.appliedActions.contains(SecurityResponseKind.StateRollback))
        assertNull(r.restoredCheckpoint)
    }

    @Test
    fun `tampered checkpoint is not used for rollback`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val payload = "state".toByteArray()
        val cp = SecurityCheckpoint.create("uhid-1", "mod", payload)
        payload[0] = 42 // corrupt -> verify() now fails
        val r = wd.onAnomalyDetected(signal(ThreatVector.StateCorruption, 0.90f), cp)
        assertTrue(!r.appliedActions.contains(SecurityResponseKind.StateRollback))
        assertNull(r.restoredCheckpoint)
    }

    @Test
    fun `response signalId echoes the anomaly id`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val s = signal(ThreatVector.MemoryAnomaly, 0.10f)
        val r = wd.onAnomalyDetected(s)
        assertEquals(s.id, r.signalId)
    }

    @Test
    fun `streamSignals buffers a signal emitted before subscription`() = runTest {
        val wd = DefaultSecurityWatchdog()
        val s = signal(ThreatVector.NetworkPivot, 0.10f)
        // Emit BEFORE collecting — unbounded channel must retain it.
        wd.onAnomalyDetected(s)
        val received = wd.streamSignals().first()
        assertEquals(s.id, received.id)
        assertEquals(ThreatVector.NetworkPivot, received.vector)
    }
}
