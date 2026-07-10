// SecurityResponseTest.kt
//
// Verifies the SecurityResponse factory helpers set the right kind, actions,
// and restored-checkpoint fields.

package com.bhengubv.circleai.security

import org.junit.jupiter.api.Test
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class SecurityResponseTest {

    private val id = UUID.randomUUID()

    @Test
    fun `noAction sets NoAction kind and empty actions`() {
        val r = SecurityResponse.noAction(id, "low confidence")
        assertEquals(SecurityResponseKind.NoAction, r.kind)
        assertEquals(id, r.signalId)
        assertTrue(r.appliedActions.isEmpty())
        assertNull(r.restoredCheckpoint)
        assertEquals("low confidence", r.description)
    }

    @Test
    fun `forKeyRotation sets KeyRotation kind`() {
        val r = SecurityResponse.forKeyRotation(id, "rotate")
        assertEquals(SecurityResponseKind.KeyRotation, r.kind)
        assertTrue(r.appliedActions.isEmpty())
        assertNull(r.restoredCheckpoint)
    }

    @Test
    fun `forRollback records restored checkpoint and describes it`() {
        val cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", "x".toByteArray())
        val r = SecurityResponse.forRollback(id, cp)
        assertEquals(SecurityResponseKind.StateRollback, r.kind)
        assertSame(cp, r.restoredCheckpoint)
        assertTrue(r.description.contains(cp.id.toString()))
        assertTrue(r.description.contains("CircleAI.Memory"))
    }

    @Test
    fun `composite carries the action list`() {
        val actions = listOf(
            SecurityResponseKind.KeyRotation,
            SecurityResponseKind.MeshIsolationSignal,
        )
        val r = SecurityResponse.composite(id, actions, "composite")
        assertEquals(SecurityResponseKind.Composite, r.kind)
        assertEquals(actions, r.appliedActions)
        assertNull(r.restoredCheckpoint)
    }

    @Test
    fun `composite can carry a restored checkpoint`() {
        val cp = SecurityCheckpoint.create("uhid-1", "mod", "x".toByteArray())
        val r = SecurityResponse.composite(
            id,
            listOf(SecurityResponseKind.KeyRotation, SecurityResponseKind.StateRollback),
            "composite+rollback",
            cp,
        )
        assertSame(cp, r.restoredCheckpoint)
        assertTrue(r.appliedActions.contains(SecurityResponseKind.StateRollback))
    }
}
