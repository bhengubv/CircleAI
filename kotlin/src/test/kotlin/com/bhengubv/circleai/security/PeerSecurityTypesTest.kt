// PeerSecurityTypesTest.kt
//
// Verifies the cross-port wire contract for the peer security enums: value
// counts, PeerThreatLevel numeric levels (0..4), and declaration order for the
// ordinal-stable enums.

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals

class PeerSecurityTypesTest {

    @Test
    fun `PeerSecurityEventKind has 10 values in declared order`() {
        assertEquals(10, PeerSecurityEventKind.entries.size)
        assertEquals(
            listOf(
                "AuthAttempt",
                "RoutingAnomaly",
                "BehaviourChange",
                "EncryptionEvent",
                "IntrusionSignal",
                "PrivilegeAttempt",
                "ConnectionAnomaly",
                "DataExfiltration",
                "DenialOfService",
                "Unknown",
            ),
            PeerSecurityEventKind.entries.map { it.name },
        )
    }

    @Test
    fun `PeerThreatLevel values map to stable numeric levels`() {
        assertEquals(0, PeerThreatLevel.None.level)
        assertEquals(1, PeerThreatLevel.Low.level)
        assertEquals(2, PeerThreatLevel.Medium.level)
        assertEquals(3, PeerThreatLevel.High.level)
        assertEquals(4, PeerThreatLevel.Critical.level)
    }

    @Test
    fun `PeerThreatLevel ordinal equals its wire level`() {
        for (l in PeerThreatLevel.entries) {
            assertEquals(l.ordinal, l.level, "ordinal/level mismatch for $l")
        }
    }

    @Test
    fun `PeerDirectiveKind has 4 values in declared order`() {
        assertEquals(
            listOf("ElevateMonitoring", "AvoidNode", "QuarantineNode", "ReleaseNode"),
            PeerDirectiveKind.entries.map { it.name },
        )
    }

    @Test
    fun `AnomalyDispatchOutcome codes match the C-sharp contract`() {
        assertEquals(0, AnomalyDispatchOutcome.Dispatched.code)
        assertEquals(1, AnomalyDispatchOutcome.Duplicate.code)
        assertEquals(2, AnomalyDispatchOutcome.BelowThreshold.code)
        assertEquals(3, AnomalyDispatchOutcome.Unverified.code)
        assertEquals(4, AnomalyDispatchOutcome.Cancelled.code)
    }

    @Test
    fun `SecurityResponseKind has the six documented kinds`() {
        assertEquals(
            listOf(
                "NoAction",
                "KeyRotation",
                "SessionRevocation",
                "MeshIsolationSignal",
                "StateRollback",
                "Composite",
            ),
            SecurityResponseKind.entries.map { it.name },
        )
    }
}
