// BiometricMatcherTest.kt
//
// Verifies BiometricMatcher.cosineSimilarity and isMatch against the vectors
// in fixtures/facex_biometric_vectors.json.
// Vectors are hardcoded here for speed and portability — they match the fixture exactly.

package com.bhengubv.circleai

import com.bhengubv.circleai.identity.BiometricMatcher
import com.bhengubv.circleai.identity.BiometricProfile
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.math.abs
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class BiometricMatcherTest {

    // ── Helpers ───────────────────────────────────────────────────────────────

    /** Assert that [actual] is within [tolerance] of [expected]. */
    private fun assertApprox(
        expected: Double,
        actual: Double,
        tolerance: Double,
        message: String = ""
    ) {
        assertTrue(
            abs(actual - expected) <= tolerance,
            "${if (message.isNotEmpty()) "[$message] " else ""}expected $expected ± $tolerance, got $actual (delta=${abs(actual - expected)})"
        )
    }

    /** Build a minimal [BiometricProfile] with the given embedding and threshold. */
    private fun profile(embedding: FloatArray, threshold: Float = 0.85f): BiometricProfile =
        BiometricProfile(
            identityId      = "test",
            embeddingVector = embedding,
            matchThreshold  = threshold,
            enrolledAt      = Instant.EPOCH
        )

    // ── cosine similarity — canonical 2-element vectors ──────────────────────

    @Test
    fun `identical unit vectors have similarity 1_0`() {
        // fixtures: identical_unit_vectors_2d
        val a = floatArrayOf(0.6f, 0.8f)
        val b = floatArrayOf(0.6f, 0.8f)
        assertApprox(1.0, BiometricMatcher.cosineSimilarity(a, b), 1e-5)
    }

    @Test
    fun `orthogonal unit vectors have similarity 0_0`() {
        // fixtures: orthogonal_vectors_2d
        val a = floatArrayOf(1.0f, 0.0f)
        val b = floatArrayOf(0.0f, 1.0f)
        assertApprox(0.0, BiometricMatcher.cosineSimilarity(a, b), 1e-5)
    }

    @Test
    fun `opposite unit vectors have similarity -1_0`() {
        // fixtures: opposite_vectors_2d
        val a = floatArrayOf(1.0f, 0.0f)
        val b = floatArrayOf(-1.0f, 0.0f)
        assertApprox(-1.0, BiometricMatcher.cosineSimilarity(a, b), 1e-5)
    }

    // ── cosine similarity — 4-element face embeddings ─────────────────────────

    @Test
    fun `same face 4d embeddings have high similarity`() {
        // fixtures: same_face_high_similarity_4d  expected=0.9993  tolerance=1e-4
        val a = floatArrayOf(0.5257f, 0.7236f, 0.2425f, 0.3780f)
        val b = floatArrayOf(0.5133f, 0.7340f, 0.2511f, 0.3692f)
        val sim = BiometricMatcher.cosineSimilarity(a, b)
        assertApprox(0.9993, sim, 1e-4, "same_face_4d similarity")
    }

    @Test
    fun `same face 4d isMatch returns true at threshold 0_85`() {
        val a = floatArrayOf(0.5257f, 0.7236f, 0.2425f, 0.3780f)
        val b = floatArrayOf(0.5133f, 0.7340f, 0.2511f, 0.3692f)
        assertTrue(BiometricMatcher.isMatch(a, profile(b, 0.85f)),
            "Same-face 4D vectors should match at threshold 0.85")
    }

    @Test
    fun `different face 4d embeddings have low similarity`() {
        // fixtures: different_face_low_similarity_4d  expected=0.3421  tolerance=1e-4
        val a = floatArrayOf(0.5257f,  0.7236f, 0.2425f,  0.3780f)
        val b = floatArrayOf(-0.3015f, 0.6547f, 0.5893f, -0.3812f)
        val sim = BiometricMatcher.cosineSimilarity(a, b)
        assertApprox(0.3421, sim, 1e-4, "different_face_4d similarity")
    }

    @Test
    fun `different face 4d isMatch returns false at threshold 0_85`() {
        val a = floatArrayOf(0.5257f,  0.7236f, 0.2425f,  0.3780f)
        val b = floatArrayOf(-0.3015f, 0.6547f, 0.5893f, -0.3812f)
        assertFalse(BiometricMatcher.isMatch(a, profile(b, 0.85f)),
            "Different-face 4D vectors should not match at threshold 0.85")
    }

    // ── marginal: at-threshold boundary ───────────────────────────────────────

    @Test
    fun `marginal match exactly at threshold is accepted`() {
        // fixtures: marginal_match_exactly_at_threshold  expected_sim=1.0, threshold=0.85
        val a = floatArrayOf(0.7071f, 0.7071f)
        val b = floatArrayOf(0.7071f, 0.7071f)
        val sim = BiometricMatcher.cosineSimilarity(a, b)
        assertApprox(1.0, sim, 1e-5, "marginal_match sim")
        assertTrue(BiometricMatcher.isMatch(a, profile(b, 0.85f)),
            "At-threshold identical vectors should match")
    }

    // ── edge cases ────────────────────────────────────────────────────────────

    @Test
    fun `zero vector returns similarity 0_0`() {
        val a = floatArrayOf(0.0f, 0.0f, 0.0f)
        val b = floatArrayOf(1.0f, 0.0f, 0.0f)
        assertApprox(0.0, BiometricMatcher.cosineSimilarity(a, b), 1e-10,
            "zero vector should return 0.0")
    }

    @Test
    fun `result is coerced to minus1 to 1`() {
        // Numerically stable: result should always be in range even with denormals.
        val a = floatArrayOf(Float.MIN_VALUE, 0.0f)
        val b = floatArrayOf(Float.MIN_VALUE, 0.0f)
        val sim = BiometricMatcher.cosineSimilarity(a, b)
        assertTrue(sim >= -1.0 && sim <= 1.0,
            "Result out of range [-1, 1]: $sim")
    }

    @Test
    fun `single element vectors work correctly`() {
        val a = floatArrayOf(1.0f)
        val b = floatArrayOf(1.0f)
        assertApprox(1.0, BiometricMatcher.cosineSimilarity(a, b), 1e-10)
    }

    @Test
    fun `mismatched lengths throw IllegalArgumentException`() {
        val a = floatArrayOf(1.0f, 0.0f)
        val b = floatArrayOf(1.0f, 0.0f, 0.0f)
        try {
            BiometricMatcher.cosineSimilarity(a, b)
            assertTrue(false, "Expected IllegalArgumentException for mismatched lengths")
        } catch (e: IllegalArgumentException) {
            // expected
        }
    }
}
