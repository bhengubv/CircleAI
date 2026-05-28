/*
 * identity.c — BiometricMatcher implementation.
 *
 * cosine_similarity uses double accumulators for cross-platform reproducibility.
 * NO SIMD intrinsics, no __builtin_ia32_*, no platform-specific extensions.
 *
 * Pure C11.  Links against -lm for sqrt().
 */

#include "circle_ai/identity.h"
#include <math.h>

double ca_biometric_cosine_similarity(const float *a, const float *b, int n) {
    double dot   = 0.0;
    double mag_a = 0.0;
    double mag_b = 0.0;

    for (int i = 0; i < n; i++) {
        double ai = (double)a[i];
        double bi = (double)b[i];
        dot   += ai * bi;
        mag_a += ai * ai;
        mag_b += bi * bi;
    }

    mag_a = sqrt(mag_a);
    mag_b = sqrt(mag_b);

    if (mag_a < 1e-10 || mag_b < 1e-10) return 0.0;

    double sim = dot / (mag_a * mag_b);

    /* Clamp to [-1.0, 1.0] to guard against floating-point rounding beyond bounds */
    if (sim >  1.0) return  1.0;
    if (sim < -1.0) return -1.0;
    return sim;
}

bool ca_biometric_is_match(const float *candidate, const ca_biometric_profile_t *stored) {
    double sim = ca_biometric_cosine_similarity(
        candidate, stored->embedding_vector, stored->embedding_dim);
    return sim >= (double)stored->match_threshold;
}
