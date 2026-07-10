// ModelAlignmentTest.kt
//
// Verifies the ModelAlignment port against the C# reference: the in-memory
// toolkit only accepts reversible profiles, tracks them per-model, reverts
// correctly, and the auditor refuses to publish a model that carries any
// applied profile. Also covers the fail-closed Null implementations.

package com.bhengubv.circleai.modelalignment

import kotlinx.coroutines.test.runTest
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ModelAlignmentTest {

    private fun profile(id: String, reversible: Boolean = true) = AlignmentProfile(
        profileId = id,
        description = "test $id",
        refusalCategoriesRemoved = listOf("cat-a", "cat-b"),
        createdAtUtc = Instant.parse("2026-01-01T00:00:00Z"),
        isReversible = reversible,
    )

    // ── InMemoryAlignmentToolkit ───────────────────────────────────────────

    @Test
    fun `toolkit backend id is in-memory`() {
        assertEquals("in-memory", InMemoryAlignmentToolkit().backendId)
    }

    @Test
    fun `apply accepts a reversible profile`() = runTest {
        val t = InMemoryAlignmentToolkit()
        val res = t.applyAsync("model-1", profile("p1"))
        assertTrue(res.success)
        assertEquals("p1", res.profileId)
        assertNull(res.failureReason)
        assertEquals(listOf("p1"), t.listAppliedAsync("model-1").map { it.profileId })
    }

    @Test
    fun `apply refuses a non-reversible profile`() = runTest {
        val t = InMemoryAlignmentToolkit()
        val res = t.applyAsync("model-1", profile("bad", reversible = false))
        assertFalse(res.success)
        assertEquals("Non-reversible alignment refused by InMemoryAlignmentToolkit", res.failureReason)
        assertTrue(t.listAppliedAsync("model-1").isEmpty())
    }

    @Test
    fun `apply rejects a blank model id`() = runTest {
        assertFailsWith<IllegalArgumentException> {
            InMemoryAlignmentToolkit().applyAsync("  ", profile("p"))
        }
    }

    @Test
    fun `list is isolated per model`() = runTest {
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("model-a", profile("pa"))
        t.applyAsync("model-b", profile("pb"))
        assertEquals(listOf("pa"), t.listAppliedAsync("model-a").map { it.profileId })
        assertEquals(listOf("pb"), t.listAppliedAsync("model-b").map { it.profileId })
    }

    @Test
    fun `list returns empty for an unknown model`() = runTest {
        assertTrue(InMemoryAlignmentToolkit().listAppliedAsync("never-touched").isEmpty())
    }

    @Test
    fun `revert removes an applied profile`() = runTest {
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("model-1", profile("p1"))
        val res = t.revertAsync("model-1", "p1")
        assertTrue(res.success)
        assertNull(res.failureReason)
        assertTrue(t.listAppliedAsync("model-1").isEmpty())
    }

    @Test
    fun `revert on an unknown model reports so`() = runTest {
        val res = InMemoryAlignmentToolkit().revertAsync("ghost", "p1")
        assertFalse(res.success)
        assertEquals("Unknown model", res.failureReason)
    }

    @Test
    fun `revert of a profile not applied reports so`() = runTest {
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("model-1", profile("p1"))
        val res = t.revertAsync("model-1", "does-not-exist")
        assertFalse(res.success)
        assertEquals("Profile not applied to this model", res.failureReason)
    }

    @Test
    fun `revert rejects blank ids`() = runTest {
        val t = InMemoryAlignmentToolkit()
        assertFailsWith<IllegalArgumentException> { t.revertAsync("", "p") }
        assertFailsWith<IllegalArgumentException> { t.revertAsync("m", " ") }
    }

    @Test
    fun `applying the same profile id twice records two entries`() = runTest {
        // Mirrors the C# List.Add semantics (no de-dup on apply).
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("m", profile("dup"))
        t.applyAsync("m", profile("dup"))
        assertEquals(2, t.listAppliedAsync("m").size)
        // A single revert removes BOTH (RemoveAll by predicate).
        val res = t.revertAsync("m", "dup")
        assertTrue(res.success)
        assertTrue(t.listAppliedAsync("m").isEmpty())
    }

    // ── RefuseAlignedPublishAuditor ────────────────────────────────────────

    @Test
    fun `auditor backend id is refuse-aligned`() {
        assertEquals("refuse-aligned", RefuseAlignedPublishAuditor(InMemoryAlignmentToolkit()).backendId)
    }

    @Test
    fun `auditor allows publishing a clean model`() = runTest {
        val t = InMemoryAlignmentToolkit()
        // No throw == pass.
        RefuseAlignedPublishAuditor(t).assertOkToPublishAsync("clean-model")
    }

    @Test
    fun `auditor refuses publishing a model with applied profiles`() = runTest {
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("aligned", profile("p1"))
        val ex = assertFailsWith<IllegalStateException> {
            RefuseAlignedPublishAuditor(t).assertOkToPublishAsync("aligned")
        }
        assertTrue(ex.message!!.contains("Cannot publish 'aligned'"))
        assertTrue(ex.message!!.contains("1 alignment profile"))
    }

    @Test
    fun `auditor allows publishing again after all profiles reverted`() = runTest {
        val t = InMemoryAlignmentToolkit()
        t.applyAsync("m", profile("p1"))
        t.revertAsync("m", "p1")
        RefuseAlignedPublishAuditor(t).assertOkToPublishAsync("m")
    }

    @Test
    fun `auditor rejects a blank model id`() = runTest {
        assertFailsWith<IllegalArgumentException> {
            RefuseAlignedPublishAuditor(InMemoryAlignmentToolkit()).assertOkToPublishAsync(" ")
        }
    }

    // ── Null implementations ───────────────────────────────────────────────

    @Test
    fun `null toolkit refuses everything and lists nothing`() = runTest {
        val t = NullAlignmentToolkit
        assertEquals("null", t.backendId)
        val a = t.applyAsync("m", profile("p"))
        assertFalse(a.success)
        assertEquals("NullAlignmentToolkit: no real backend wired.", a.failureReason)
        val r = t.revertAsync("m", "p")
        assertFalse(r.success)
        assertEquals("NullAlignmentToolkit: nothing to revert.", r.failureReason)
        assertTrue(t.listAppliedAsync("m").isEmpty())
    }

    @Test
    fun `null auditor always passes`() = runTest {
        assertEquals("null", NullAlignmentAuditor.backendId)
        NullAlignmentAuditor.assertOkToPublishAsync("anything")
    }
}
