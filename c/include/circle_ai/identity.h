#ifndef CIRCLE_AI_IDENTITY_H
#define CIRCLE_AI_IDENTITY_H

/*
 * identity.h — CircleIdentity, RegisteredDevice, and BiometricProfile.
 * Pure C11, no OS-specific headers.
 *
 * BiometricMatcher uses double accumulators for cross-platform reproducibility.
 * NO SIMD intrinsics, no __builtin_ia32_*, no platform-specific extensions.
 */

#include <stdint.h>
#include <stdbool.h>

/* ---------------------------------------------------------------------------
 * Identity tier
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_IDENTITY_ANONYMOUS     = 0,
    CA_IDENTITY_PSEUDONYMOUS  = 1,
    CA_IDENTITY_VERIFIED      = 2
} ca_identity_tier_t;

/* ---------------------------------------------------------------------------
 * Registered device
 * --------------------------------------------------------------------------- */

#define CA_MAX_DEVICES 32

typedef struct {
    char        device_id[37];    /* UUID string (null-terminated) */
    const char *device_name;      /* human-readable label, caller owns; may be NULL */
    char        platform[16];     /* "android", "ios", "windows", "macos", etc. */
    char        identity_id[37];  /* back-reference to parent identity */
    int64_t     registered_at;    /* Unix ms UTC */
    int64_t     last_active_at;   /* Unix ms UTC */
    bool        is_primary;
} ca_registered_device_t;

/* ---------------------------------------------------------------------------
 * CircleIdentity
 * --------------------------------------------------------------------------- */

typedef struct {
    char                   identity_id[37];  /* UUID string */
    ca_identity_tier_t     tier;
    const char            *display_name;     /* caller owns; may be NULL for anonymous */
    char                   preferred_language[16]; /* BCP-47 or empty string */
    int64_t                created_at;       /* Unix ms UTC */
    int64_t                last_seen_at;     /* Unix ms UTC */
    ca_registered_device_t devices[CA_MAX_DEVICES];
    int                    device_count;
} ca_circle_identity_t;

/* ---------------------------------------------------------------------------
 * BiometricProfile
 * --------------------------------------------------------------------------- */

typedef struct {
    char    identity_id[37];     /* UUID string */
    float  *embedding_vector;    /* L2-normalised; caller owns */
    int     embedding_dim;       /* length of embedding_vector */
    float   match_threshold;     /* default 0.85f */
    int64_t enrolled_at_ms;      /* Unix ms UTC */
    int64_t last_match_at_ms;    /* 0 = never matched */
} ca_biometric_profile_t;

/* ---------------------------------------------------------------------------
 * BiometricMatcher
 *
 * cosine_similarity uses double accumulators for cross-platform reproducibility.
 * Do NOT use SSE/AVX/NEON intrinsics here.
 * --------------------------------------------------------------------------- */

/*
 * Compute cosine similarity between two float vectors of length n.
 * Uses double accumulators to avoid float cancellation error.
 * Returns value in [-1.0, 1.0]; returns 0.0 if either magnitude < 1e-10.
 */
double ca_biometric_cosine_similarity(const float *a, const float *b, int n);

/*
 * Returns true if cosine_similarity(candidate, stored->embedding_vector) >= stored->match_threshold.
 */
bool ca_biometric_is_match(const float *candidate, const ca_biometric_profile_t *stored);

#endif /* CIRCLE_AI_IDENTITY_H */
