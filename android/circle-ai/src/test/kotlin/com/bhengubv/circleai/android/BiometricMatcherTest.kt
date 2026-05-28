package com.bhengubv.circleai.android

import com.bhengubv.circleai.android.identity.BiometricMatcher
import com.bhengubv.circleai.android.identity.BiometricProfile
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import kotlin.math.abs

/**
 * Cross-language fixture tests for BiometricMatcher (android sub-package).
 * Values sourced from fixtures/facex_biometric_vectors.json.
 * All comparisons use 1e-4 tolerance (cosine similarity vectors).
 */
class BiometricMatcherTest {

    private fun assertApprox(actual: Double, expected: Double, tol: Double, label: String = "") {
        val diff = abs(actual - expected)
        assertTrue("$label: expected $expected got $actual diff=$diff", diff <= tol)
    }

    private fun profile(embedding: FloatArray, threshold: Float = 0.85f) =
        BiometricProfile(identityId = "test", embeddingVector = embedding,
            matchThreshold = threshold, enrolledAt = Instant.now())

    @Test fun identicalUnitVectors2d() =
        assertApprox(BiometricMatcher.cosineSimilarity(floatArrayOf(0.6f, 0.8f), floatArrayOf(0.6f, 0.8f)), 1.0, 1e-5, "identical")

    @Test fun orthogonalVectors2d() =
        assertApprox(BiometricMatcher.cosineSimilarity(floatArrayOf(1f, 0f), floatArrayOf(0f, 1f)), 0.0, 1e-5, "orthogonal")

    @Test fun oppositeVectors2d() =
        assertApprox(BiometricMatcher.cosineSimilarity(floatArrayOf(1f, 0f), floatArrayOf(-1f, 0f)), -1.0, 1e-5, "opposite")

    @Test fun sameFaceHighSimilarity4d() {
        val a = floatArrayOf(0.5257f, 0.7236f, 0.2425f, 0.3780f)
        val b = floatArrayOf(0.5133f, 0.7340f, 0.2511f, 0.3692f)
        assertApprox(BiometricMatcher.cosineSimilarity(a, b), 0.999794, 1e-4, "same_face")
        assertTrue("isMatch at 0.85", BiometricMatcher.isMatch(a, profile(b)))
    }

    @Test fun differentFaceLowSimilarity4d() {
        val a = floatArrayOf(0.5257f, 0.7236f, 0.2425f, 0.3780f)
        val b = floatArrayOf(-0.3015f, 0.6547f, 0.5893f, -0.3812f)
        assertApprox(BiometricMatcher.cosineSimilarity(a, b), 0.311911, 1e-4, "different_face")
        assertFalse("isMatch=false at 0.85", BiometricMatcher.isMatch(a, profile(b)))
    }

    @Test fun marginalMatchAtThreshold() {
        val v = floatArrayOf(0.7071f, 0.7071f)
        assertTrue("sim=1.0 >= 0.85 is a match", BiometricMatcher.isMatch(v, profile(v, 0.85f)))
    }

    @Test fun resultBoundedToMinusOneOne() {
        val sim = BiometricMatcher.cosineSimilarity(floatArrayOf(1f, 0f), floatArrayOf(-1f, 0f))
        assertTrue("result must be >= -1", sim >= -1.0)
        assertTrue("result must be <= 1",  sim <=  1.0)
    }
}
