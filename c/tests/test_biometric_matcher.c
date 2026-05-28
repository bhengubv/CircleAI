/*
 * test_biometric_matcher.c — BiometricMatcher cross-language fixture tests.
 *
 * All expected values match fixtures/facex_biometric_vectors.json
 * cosine_similarity_vectors within the tolerances specified there.
 * Returns 0 on all-pass, calls assert() on first failure.
 */

#include <stdio.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define TOL_STRICT 1e-5
#define TOL_LOOSE  1e-4

static void check_sim(const char *id, double got, double expected, double tol) {
    double diff = fabs(got - expected);
    if (diff > tol) {
        fprintf(stderr, "FAIL [%s]: got %.10f, expected %.10f (diff %.10f, tol %.1e)\n",
                id, got, expected, diff, tol);
        assert(0);
    }
}

int main(void) {
    double sim;
    ca_biometric_profile_t profile;

    /* ------------------------------------------------------------------
     * cosine_similarity_vectors
     * ------------------------------------------------------------------ */

    /* identical_unit_vectors_2d: [0.6, 0.8] vs [0.6, 0.8] => 1.0 */
    {
        float a[] = { 0.6f, 0.8f };
        float b[] = { 0.6f, 0.8f };
        sim = ca_biometric_cosine_similarity(a, b, 2);
        check_sim("identical_2d", sim, 1.0, TOL_STRICT);
    }

    /* orthogonal_vectors_2d: [1, 0] vs [0, 1] => 0.0 */
    {
        float a[] = { 1.0f, 0.0f };
        float b[] = { 0.0f, 1.0f };
        sim = ca_biometric_cosine_similarity(a, b, 2);
        check_sim("orthogonal_2d", sim, 0.0, TOL_STRICT);
    }

    /* opposite_vectors_2d: [1, 0] vs [-1, 0] => -1.0 */
    {
        float a[] = {  1.0f, 0.0f };
        float b[] = { -1.0f, 0.0f };
        sim = ca_biometric_cosine_similarity(a, b, 2);
        check_sim("opposite_2d", sim, -1.0, TOL_STRICT);
    }

    /* same_face_high_similarity_4d => ~0.999794, tol 1e-4 */
    {
        float a[] = { 0.5257f, 0.7236f, 0.2425f, 0.3780f };
        float b[] = { 0.5133f, 0.7340f, 0.2511f, 0.3692f };
        sim = ca_biometric_cosine_similarity(a, b, 4);
        check_sim("same_face_4d", sim, 0.999794, TOL_LOOSE);

        /* is_match at default threshold 0.85 => true */
        profile.embedding_vector = b;
        profile.embedding_dim    = 4;
        profile.match_threshold  = 0.85f;
        assert(ca_biometric_is_match(a, &profile) == true);
    }

    /* different_face_low_similarity_4d => ~0.311911, tol 1e-4 */
    {
        float a[] = {  0.5257f,  0.7236f, 0.2425f,  0.3780f };
        float b[] = { -0.3015f,  0.6547f, 0.5893f, -0.3812f };
        sim = ca_biometric_cosine_similarity(a, b, 4);
        check_sim("different_face_4d", sim, 0.311911, TOL_LOOSE);

        /* is_match at default threshold 0.85 => false */
        profile.embedding_vector = b;
        profile.embedding_dim    = 4;
        profile.match_threshold  = 0.85f;
        assert(ca_biometric_is_match(a, &profile) == false);
    }

    /* marginal_match_exactly_at_threshold: identical vectors => 1.0 >= 0.85 => true */
    {
        float a[] = { 0.7071f, 0.7071f };
        float b[] = { 0.7071f, 0.7071f };
        sim = ca_biometric_cosine_similarity(a, b, 2);
        check_sim("marginal_match", sim, 1.0, TOL_STRICT);

        profile.embedding_vector = b;
        profile.embedding_dim    = 2;
        profile.match_threshold  = 0.85f;
        assert(ca_biometric_is_match(a, &profile) == true);
    }

    /* zero-magnitude guard: should return 0.0, not NaN */
    {
        float a[] = { 0.0f, 0.0f };
        float b[] = { 1.0f, 0.0f };
        sim = ca_biometric_cosine_similarity(a, b, 2);
        check_sim("zero_magnitude", sim, 0.0, TOL_STRICT);
    }

    printf("All biometric matcher tests passed.\n");
    return 0;
}
