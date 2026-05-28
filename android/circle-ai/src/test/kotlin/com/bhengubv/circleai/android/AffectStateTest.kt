package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.memory.AffectState
import org.junit.Test
import kotlin.math.abs

/** Cross-language fixture tests for AffectState — fixtures/affect_state.json */
class AffectStateTest {

    private fun assertApprox(actual: Float, expected: Float, label: String = "", tol: Float = 1e-5f) {
        val diff = abs(actual - expected)
        assert(diff <= tol) { "$label: expected $expected got $actual diff=$diff" }
    }

    private fun state(e: Float = 0.5f, u: Float = 0.2f, r: Float = 0.0f, en: Float = 0.5f) =
        AffectState("test").also { it.engagement = e; it.uncertainty = u; it.rapport = r; it.energy = en }

    // ── positive_signal_once ──────────────────────────────────────────────────
    @Test fun positiveSignalOnce() {
        val s = AffectState("test")
        s.applyPositiveSignal()
        assertApprox(s.engagement,  0.52f, "engagement")
        assertApprox(s.rapport,     0.01f, "rapport")
        assertApprox(s.uncertainty, 0.18f, "uncertainty")
        assertApprox(s.curiosity,   0.5f,  "curiosity_unchanged")
        assertApprox(s.energy,      0.5f,  "energy_unchanged")
    }

    // ── positive_signal_twice ─────────────────────────────────────────────────
    @Test fun positiveSignalTwice() {
        val s = AffectState("test")
        s.applyPositiveSignal(); s.applyPositiveSignal()
        assertApprox(s.engagement,  0.54f, "engagement")
        assertApprox(s.rapport,     0.02f, "rapport")
        assertApprox(s.uncertainty, 0.16f, "uncertainty")
    }

    // ── negative_signal_once ──────────────────────────────────────────────────
    @Test fun negativeSignalOnce() {
        val s = AffectState("test")
        s.applyNegativeSignal()
        assertApprox(s.engagement,  0.47f, "engagement")
        assertApprox(s.uncertainty, 0.23f, "uncertainty")
        assertApprox(s.rapport,     0.0f,  "rapport_unchanged")
    }

    // ── negative_signal_twice ─────────────────────────────────────────────────
    @Test fun negativeSignalTwice() {
        val s = AffectState("test")
        s.applyNegativeSignal(); s.applyNegativeSignal()
        assertApprox(s.engagement,  0.44f, "engagement")
        assertApprox(s.uncertainty, 0.26f, "uncertainty")
    }

    // ── idle_decay_1h ─────────────────────────────────────────────────────────
    @Test fun idleDecay1h() {
        val s = AffectState("test"); s.engagement = 0.8f; s.energy = 0.7f
        s.applyIdleDecay(1f)
        assertApprox(s.engagement, 0.794f, "engagement")
        assertApprox(s.energy,     0.696f, "energy")
    }

    // ── idle_decay_8h ─────────────────────────────────────────────────────────
    @Test fun idleDecay8h() {
        val s = AffectState("test"); s.engagement = 0.8f; s.energy = 0.7f
        s.applyIdleDecay(8f)
        assertApprox(s.engagement, 0.752f, "engagement")
        assertApprox(s.energy,     0.668f, "energy")
    }

    // ── idle_decay_24h (capped at 0.3) ────────────────────────────────────────
    @Test fun idleDecay24h() {
        val s = AffectState("test"); s.engagement = 0.8f; s.energy = 0.7f
        s.applyIdleDecay(24f)
        assertApprox(s.engagement, 0.71f,  "engagement")
        assertApprox(s.energy,     0.64f,  "energy")
    }

    // ── clamp_max_positive ────────────────────────────────────────────────────
    @Test fun clampMaxPositive() {
        val s = AffectState("test"); s.engagement = 0.99f; s.rapport = 0.99f; s.uncertainty = 0.01f
        s.applyPositiveSignal()
        assertApprox(s.engagement,  1.0f, "engagement_clamped")
        assertApprox(s.rapport,     1.0f, "rapport_clamped")
        assertApprox(s.uncertainty, 0.0f, "uncertainty_clamped")
    }

    // ── clamp_min_negative ────────────────────────────────────────────────────
    @Test fun clampMinNegative() {
        val s = AffectState("test"); s.engagement = 0.01f; s.uncertainty = 0.98f
        s.applyNegativeSignal()
        assertApprox(s.engagement,  0.0f, "engagement_clamped")
        assertApprox(s.uncertainty, 1.0f, "uncertainty_clamped")
    }

    // ── idle_decay_neutral_no_change ──────────────────────────────────────────
    @Test fun idleDecayNeutralNoChange() {
        val s = AffectState("test")  // engagement=0.5, energy=0.5
        s.applyIdleDecay(8f)
        assertApprox(s.engagement, 0.5f, "engagement_neutral")
        assertApprox(s.energy,     0.5f, "energy_neutral")
    }
}
