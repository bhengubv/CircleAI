// AffectVadTest.kt
//
// Verifies AffectVad.from / AffectState.toVad against the six canonical
// cross-port vectors. Math must be byte-identical to the C# / Swift /
// Python / TS / Go ports — epsilon 1e-5f.

package com.bhengubv.circleai

import com.bhengubv.circleai.memory.AffectState
import com.bhengubv.circleai.memory.AffectVad
import com.bhengubv.circleai.memory.toVad
import org.junit.jupiter.api.Test
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class AffectVadTest {

    private val EPSILON = 1e-5f

    // ── Helpers ───────────────────────────────────────────────────────────────

    private fun state(
        curiosity: Float,
        engagement: Float,
        uncertainty: Float,
        rapport: Float,
        energy: Float,
    ): AffectState = AffectState(userId = "test-user").apply {
        this.curiosity   = curiosity
        this.engagement  = engagement
        this.uncertainty = uncertainty
        this.rapport     = rapport
        this.energy      = energy
    }

    private fun assertClose(label: String, expected: Float, actual: Float) {
        assertTrue(
            abs(actual - expected) < EPSILON,
            "[$label] expected $expected but was $actual (delta=${abs(actual - expected)}, epsilon=$EPSILON)",
        )
    }

    private fun assertVad(
        label: String,
        s: AffectState,
        expectedV: Float,
        expectedA: Float,
        expectedD: Float,
    ) {
        val vad = AffectVad.from(s)
        assertClose("$label.valence",   expectedV, vad.valence)
        assertClose("$label.arousal",   expectedA, vad.arousal)
        assertClose("$label.dominance", expectedD, vad.dominance)
    }

    // ── Cross-port canonical vectors ─────────────────────────────────────────

    @Test
    fun `default — neutral state`() {
        // c=0.5, e=0.5, u=0.2, r=0.0, en=0.5
        val s = state(curiosity = 0.5f, engagement = 0.5f, uncertainty = 0.2f, rapport = 0.0f, energy = 0.5f)
        assertVad("default", s, expectedV = 0.43333333f, expectedA = 0.425f, expectedD = 0.65f)
    }

    @Test
    fun `all_max — every dimension saturated positively`() {
        // c=1, e=1, u=0, r=1, en=1
        val s = state(curiosity = 1f, engagement = 1f, uncertainty = 0f, rapport = 1f, energy = 1f)
        assertVad("all_max", s, expectedV = 1.0f, expectedA = 0.75f, expectedD = 1.0f)
    }

    @Test
    fun `all_min — every dimension saturated negatively`() {
        // c=0, e=0, u=1, r=0, en=0
        val s = state(curiosity = 0f, engagement = 0f, uncertainty = 1f, rapport = 0f, energy = 0f)
        assertVad("all_min", s, expectedV = 0.0f, expectedA = 0.25f, expectedD = 0.0f)
    }

    @Test
    fun `warm — high rapport and engagement, low uncertainty`() {
        // c=0.6, e=0.9, u=0.1, r=0.8, en=0.7
        val s = state(curiosity = 0.6f, engagement = 0.9f, uncertainty = 0.1f, rapport = 0.8f, energy = 0.7f)
        assertVad("warm", s, expectedV = 0.86666667f, expectedA = 0.525f, expectedD = 0.9f)
    }

    @Test
    fun `stressed — low engagement, high uncertainty`() {
        // c=0.3, e=0.2, u=0.8, r=0.0, en=0.2
        val s = state(curiosity = 0.3f, engagement = 0.2f, uncertainty = 0.8f, rapport = 0.0f, energy = 0.2f)
        assertVad("stressed", s, expectedV = 0.13333333f, expectedA = 0.375f, expectedD = 0.2f)
    }

    @Test
    fun `energetic — high curiosity and energy`() {
        // c=0.9, e=0.6, u=0.3, r=0.4, en=0.9
        val s = state(curiosity = 0.9f, engagement = 0.6f, uncertainty = 0.3f, rapport = 0.4f, energy = 0.9f)
        assertVad("energetic", s, expectedV = 0.56666667f, expectedA = 0.75f, expectedD = 0.65f)
    }

    // ── Extension parity ─────────────────────────────────────────────────────

    @Test
    fun `toVad extension matches AffectVad_from for default state`() {
        val s = state(curiosity = 0.5f, engagement = 0.5f, uncertainty = 0.2f, rapport = 0.0f, energy = 0.5f)
        val viaCompanion = AffectVad.from(s)
        val viaExtension = s.toVad()
        assertEquals(viaCompanion, viaExtension)
    }

    @Test
    fun `toVad extension matches AffectVad_from for warm state`() {
        val s = state(curiosity = 0.6f, engagement = 0.9f, uncertainty = 0.1f, rapport = 0.8f, energy = 0.7f)
        assertEquals(AffectVad.from(s), s.toVad())
    }

    // ── Clamp behaviour ──────────────────────────────────────────────────────

    @Test
    fun `components are clamped into 0 to 1`() {
        // Even with all-max inputs the formulas can exceed 1.0 for arousal
        // (e.g. en=1, c=1, u=1 → (2 + 1 + 1)/4 = 1.0). Confirm the clamp holds.
        val s = state(curiosity = 1f, engagement = 1f, uncertainty = 1f, rapport = 1f, energy = 1f)
        val vad = s.toVad()
        assertTrue(vad.valence   in 0f..1f, "valence out of range: ${vad.valence}")
        assertTrue(vad.arousal   in 0f..1f, "arousal out of range: ${vad.arousal}")
        assertTrue(vad.dominance in 0f..1f, "dominance out of range: ${vad.dominance}")
    }
}
