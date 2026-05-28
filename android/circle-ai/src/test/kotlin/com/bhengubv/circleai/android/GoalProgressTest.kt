package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.memory.Goal
import com.bhengubv.circleai.android.memory.GoalPriority
import com.bhengubv.circleai.android.memory.GoalStatus
import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.Instant
import kotlin.math.abs

/**
 * Cross-language fixture tests for Goal.advanceProgress (android sub-package).
 * Values sourced from fixtures/goal_progress.json.
 * All comparisons within 1e-5 tolerance.
 */
class GoalProgressTest {

    private fun assertApprox(actual: Float, expected: Float, label: String = "") {
        val diff = abs(actual - expected)
        assert(diff <= 1e-5f) { "$label: expected $expected got $actual diff=$diff" }
    }

    private fun goal(progress: Float) = Goal(
        id = "g1", userId = "u1", title = "Test", description = "Test goal",
        status = GoalStatus.Active, priority = GoalPriority.Normal,
        createdUtc = Instant.now(), progress = progress
    )

    @Test fun zeroInitial() =
        assertApprox(goal(0f).advanceProgress(0f).progress, 0f, "zero_initial")

    @Test fun partialAdvance() =
        assertApprox(goal(0f).advanceProgress(0.3f).progress, 0.3f, "partial_advance")

    @Test fun clampMax() =
        assertApprox(goal(0.9f).advanceProgress(0.5f).progress, 1.0f, "clamp_max")

    @Test fun clampMin() =
        assertApprox(goal(0.1f).advanceProgress(-0.5f).progress, 0.0f, "clamp_min")

    @Test fun zeroDelta() =
        assertApprox(goal(0.5f).advanceProgress(0f).progress, 0.5f, "zero_delta")

    @Test fun advanceToFull() =
        assertApprox(goal(0.75f).advanceProgress(0.25f).progress, 1.0f, "advance_to_full")

    @Test fun negativeDelta() =
        assertApprox(goal(0.6f).advanceProgress(-0.2f).progress, 0.4f, "negative_delta")

    @Test fun immutability() {
        val original = goal(0.5f)
        original.advanceProgress(0.3f)
        assertApprox(original.progress, 0.5f, "immutability")
    }
}
