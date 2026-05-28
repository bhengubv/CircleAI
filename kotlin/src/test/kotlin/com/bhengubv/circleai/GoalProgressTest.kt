// GoalProgressTest.kt
//
// Verifies Goal.advanceProgress(delta) against all vectors from
// fixtures/goal_progress.json.
// Vectors are hardcoded for portability — they exactly match the fixture values.

package com.bhengubv.circleai

import com.bhengubv.circleai.memory.Goal
import com.bhengubv.circleai.memory.GoalPriority
import com.bhengubv.circleai.memory.GoalStatus
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class GoalProgressTest {

    // ── Helpers ───────────────────────────────────────────────────────────────

    private val EPSILON = 1e-6f

    /** Build a minimal [Goal] with the given initial progress. */
    private fun goal(progress: Float): Goal = Goal(
        id          = "test-goal",
        userId      = "test-user",
        title       = "Test Goal",
        description = "Unit test goal",
        status      = GoalStatus.Active,
        priority    = GoalPriority.Normal,
        createdUtc  = Instant.EPOCH,
        progress    = progress
    )

    private fun assertApprox(expected: Float, actual: Float, message: String = "") {
        assertTrue(
            abs(actual - expected) <= EPSILON,
            "${if (message.isNotEmpty()) "[$message] " else ""}expected $expected ± $EPSILON, got $actual"
        )
    }

    // ── Fixture vectors ───────────────────────────────────────────────────────

    @Test
    fun `zero_initial — advance by 0 from 0 yields 0`() {
        // initial=0.0, delta=0.0, expected=0.0
        assertApprox(0.0f, goal(0.0f).advanceProgress(0.0f).progress, "zero_initial")
    }

    @Test
    fun `partial_advance — advance 30 percent from zero`() {
        // initial=0.0, delta=0.3, expected=0.3
        assertApprox(0.3f, goal(0.0f).advanceProgress(0.3f).progress, "partial_advance")
    }

    @Test
    fun `clamp_max — advance past 1_0 clamps to 1_0`() {
        // initial=0.9, delta=0.5, expected=1.0
        assertApprox(1.0f, goal(0.9f).advanceProgress(0.5f).progress, "clamp_max")
    }

    @Test
    fun `clamp_min — negative delta past 0_0 clamps to 0_0`() {
        // initial=0.1, delta=-0.5, expected=0.0
        assertApprox(0.0f, goal(0.1f).advanceProgress(-0.5f).progress, "clamp_min")
    }

    @Test
    fun `zero_delta — zero delta mid-progress is unchanged`() {
        // initial=0.5, delta=0.0, expected=0.5
        assertApprox(0.5f, goal(0.5f).advanceProgress(0.0f).progress, "zero_delta")
    }

    @Test
    fun `advance_to_full — exact advance to 1_0 is not clamped`() {
        // initial=0.75, delta=0.25, expected=1.0
        assertApprox(1.0f, goal(0.75f).advanceProgress(0.25f).progress, "advance_to_full")
    }

    @Test
    fun `negative_delta — regression without hitting floor`() {
        // initial=0.6, delta=-0.2, expected=0.4
        assertApprox(0.4f, goal(0.6f).advanceProgress(-0.2f).progress, "negative_delta")
    }

    // ── Immutability checks ───────────────────────────────────────────────────

    @Test
    fun `advanceProgress returns new Goal, does not mutate original`() {
        val original = goal(0.5f)
        val updated  = original.advanceProgress(0.2f)
        assertApprox(0.5f, original.progress, "original unchanged")
        assertApprox(0.7f, updated.progress,  "updated value")
    }

    @Test
    fun `advanceProgress preserves all other Goal fields`() {
        val original = goal(0.3f).copy(title = "My Goal", status = GoalStatus.Active)
        val updated  = original.advanceProgress(0.1f)
        assertEquals(original.id,       updated.id)
        assertEquals(original.userId,   updated.userId)
        assertEquals(original.title,    updated.title)
        assertEquals(original.status,   updated.status)
        assertEquals(original.priority, updated.priority)
    }

    // ── Boundary / clamping ───────────────────────────────────────────────────

    @Test
    fun `already at 1_0 stays at 1_0 with positive delta`() {
        assertApprox(1.0f, goal(1.0f).advanceProgress(0.1f).progress)
    }

    @Test
    fun `already at 0_0 stays at 0_0 with negative delta`() {
        assertApprox(0.0f, goal(0.0f).advanceProgress(-0.1f).progress)
    }

    @Test
    fun `large positive delta clamps to 1_0`() {
        assertApprox(1.0f, goal(0.0f).advanceProgress(Float.MAX_VALUE).progress)
    }

    @Test
    fun `large negative delta clamps to 0_0`() {
        assertApprox(0.0f, goal(1.0f).advanceProgress(-Float.MAX_VALUE).progress)
    }
}
