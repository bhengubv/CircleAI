package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.companion.FaceAffectMapper
import com.bhengubv.circleai.android.companion.FaceCompanionBridge
import com.bhengubv.circleai.android.companion.InterfaceKind
import com.bhengubv.circleai.android.memory.AffectState
import com.bhengubv.circleai.android.tools.FaceExpressionClassification
import com.bhengubv.circleai.android.tools.FaceBoundingBox
import com.bhengubv.circleai.android.tools.FacialMetricMatrix
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Instant
import kotlin.math.abs

/** Cross-language fixture tests for FaceAffectMapper + FaceCompanionBridge.
 *  Values sourced from fixtures/facex_biometric_vectors.json. */
class CompanionTypesTest {

    private fun assertApprox(actual: Float, expected: Float, label: String = "", tol: Float = 1e-5f) {
        val diff = abs(actual - expected)
        assert(diff <= tol) { "$label: expected $expected got $actual diff=$diff" }
    }

    private fun affect(c: Float=0.5f, e: Float=0.5f, u: Float=0.2f, r: Float=0.0f, en: Float=0.5f) =
        AffectState("test").also { it.curiosity=c; it.engagement=e; it.uncertainty=u; it.rapport=r; it.energy=en }

    private fun matrix(expr: FaceExpressionClassification, conf: Float) = FacialMetricMatrix(
        landmarks = FloatArray(136), boundingBox = FaceBoundingBox(0f,0f,1f,1f),
        expression = expr, confidenceScore = conf, capturedAt = Instant.now()
    )

    // ── happy_from_neutral ────────────────────────────────────────────────────
    @Test fun happyFromNeutral() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Happy, 0.92f), s)
        assertApprox(s.engagement, 0.53f, "engagement")
        assertApprox(s.energy,     0.52f, "energy")
        assertApprox(s.curiosity,  0.5f,  "curiosity_unchanged")
        assertApprox(s.uncertainty, 0.2f, "uncertainty_unchanged")
    }

    // ── surprised_from_neutral ────────────────────────────────────────────────
    @Test fun surprisedFromNeutral() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Surprised, 0.88f), s)
        assertApprox(s.curiosity,  0.54f, "curiosity")
        assertApprox(s.engagement, 0.5f,  "engagement_unchanged")
    }

    // ── confused_from_neutral ─────────────────────────────────────────────────
    @Test fun confusedFromNeutral() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Confused, 0.79f), s)
        assertApprox(s.uncertainty, 0.25f, "uncertainty")
        assertApprox(s.engagement,  0.5f,  "engagement_unchanged")
    }

    // ── stressed_from_neutral ─────────────────────────────────────────────────
    @Test fun stressedFromNeutral() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Stressed, 0.85f), s)
        assertApprox(s.uncertainty, 0.28f, "uncertainty")
        assertApprox(s.energy,      0.45f, "energy")
    }

    // ── angry_from_neutral ────────────────────────────────────────────────────
    @Test fun angryFromNeutral() {
        val s = affect(r=0.3f); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Angry, 0.91f), s)
        assertApprox(s.engagement, 0.46f, "engagement")
        assertApprox(s.rapport,    0.28f, "rapport")
    }

    // ── neutral_expression_no_change ──────────────────────────────────────────
    @Test fun neutralExpressionNoChange() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Neutral, 0.95f), s)
        assertApprox(s.curiosity,   0.5f, "curiosity")
        assertApprox(s.engagement,  0.5f, "engagement")
        assertApprox(s.uncertainty, 0.2f, "uncertainty")
        assertApprox(s.rapport,     0.0f, "rapport")
        assertApprox(s.energy,      0.5f, "energy")
    }

    // ── low_confidence_discarded ──────────────────────────────────────────────
    @Test fun lowConfidenceDiscarded() {
        val s = affect(); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Stressed, 0.49f), s)
        assertApprox(s.uncertainty, 0.2f, "uncertainty_unchanged")
        assertApprox(s.energy,      0.5f, "energy_unchanged")
    }

    // ── clamp_max_engagement ──────────────────────────────────────────────────
    @Test fun clampMaxEngagement() {
        val s = affect(e=0.99f); FaceAffectMapper.apply(matrix(FaceExpressionClassification.Happy, 0.95f), s)
        assertApprox(s.engagement, 1.0f, "engagement_clamped")
    }

    // ── FaceCompanionBridge — threshold NOT crossed ───────────────────────────
    @Test fun bridgeNoEventBelowThreshold() {
        val s = affect(u = 0.2f)
        val m = matrix(FaceExpressionClassification.Confused, 0.79f)
        // After confused: uncertainty = 0.2 + 0.05 = 0.25 < 0.70
        assertNull(FaceCompanionBridge.observe(m, s, "s1", "i1", InterfaceKind.Mobile))
    }

    // ── FaceCompanionBridge — threshold crossed ───────────────────────────────
    @Test fun bridgeEventAboveThreshold() {
        val s = affect(u = 0.67f)
        val m = matrix(FaceExpressionClassification.Confused, 0.79f)
        // After confused: uncertainty = 0.67 + 0.05 = 0.72 >= 0.70
        val event = FaceCompanionBridge.observe(m, s, "s2", "i2", InterfaceKind.Mobile)
        assertNotNull(event)
        assertEquals("face.confusion_detected", event!!.triggerName)
        assertEquals("s2", event.sessionId)
    }

    // ── FaceCompanionBridge — stressed also triggers ──────────────────────────
    @Test fun bridgeStressedTriggers() {
        val s = affect(u = 0.64f)
        val m = matrix(FaceExpressionClassification.Stressed, 0.85f)
        // After stressed: uncertainty = 0.64 + 0.08 = 0.72 >= 0.70
        val event = FaceCompanionBridge.observe(m, s, "s3", "i3", InterfaceKind.Wearable)
        assertNotNull(event)
        assertEquals("face.confusion_detected", event!!.triggerName)
    }

    // ── FaceCompanionBridge — happy never triggers ────────────────────────────
    @Test fun bridgeHappyNoConfusionEvent() {
        val s = affect(u = 0.9f)  // uncertainty already very high
        val m = matrix(FaceExpressionClassification.Happy, 0.95f)
        // Happy raises engagement/energy but is NOT a confusion expression
        assertNull(FaceCompanionBridge.observe(m, s, "s4", "i4", InterfaceKind.Mobile))
    }

    // ── InterfaceKind ordinals ────────────────────────────────────────────────
    @Test fun interfaceKindValues() {
        assertEquals(0, InterfaceKind.Mobile.ordinal)
        assertEquals(1, InterfaceKind.Wearable.ordinal)
        assertEquals(2, InterfaceKind.Desktop.ordinal)
        assertEquals(3, InterfaceKind.Web.ordinal)
        assertEquals(6, InterfaceKind.Headless.ordinal)
    }
}
