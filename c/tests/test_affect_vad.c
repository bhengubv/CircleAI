/*
 * test_affect_vad.c — AffectVad derivation tests.
 *
 * Vectors and expected values mirror fixtures/affect_vad_derivation.json
 * with epsilon 1e-5. All language ports must produce byte-identical math.
 *
 * Returns 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <math.h>
#include <stdio.h>

#include "circle_ai/circle_ai.h"

#define EPSILON 1e-5f

static void check(const char *id, const char *axis, float got, float expected) {
    float diff = fabsf(got - expected);
    if (diff > EPSILON) {
        fprintf(stderr, "FAIL [%s.%s]: got %.8f, expected %.8f (diff %.8f)\n",
                id, axis, (double)got, (double)expected, (double)diff);
        assert(0);
    }
}

static void run_case(
    const char *id,
    float curiosity, float engagement, float uncertainty,
    float rapport,   float energy,
    float v_exp,     float a_exp,      float d_exp
) {
    ca_affect_state_t s = ca_affect_state_default();
    s.curiosity   = curiosity;
    s.engagement  = engagement;
    s.uncertainty = uncertainty;
    s.rapport     = rapport;
    s.energy      = energy;

    ca_affect_vad_t vad;
    ca_affect_vad_from(&s, &vad);

    check(id, "valence",   vad.valence,   v_exp);
    check(id, "arousal",   vad.arousal,   a_exp);
    check(id, "dominance", vad.dominance, d_exp);
}

int main(void) {
    /* default_state */
    run_case("default",
        0.5f, 0.5f, 0.2f, 0.0f, 0.5f,
        0.43333333f, 0.425f, 0.65f);

    /* all_max */
    run_case("all_max",
        1.0f, 1.0f, 0.0f, 1.0f, 1.0f,
        1.0f, 0.75f, 1.0f);

    /* all_min_high_uncertainty */
    run_case("all_min",
        0.0f, 0.0f, 1.0f, 0.0f, 0.0f,
        0.0f, 0.25f, 0.0f);

    /* high_engagement_warm */
    run_case("warm",
        0.6f, 0.9f, 0.1f, 0.8f, 0.7f,
        0.86666667f, 0.525f, 0.9f);

    /* stressed_low_energy */
    run_case("stressed",
        0.3f, 0.2f, 0.8f, 0.0f, 0.2f,
        0.13333333f, 0.375f, 0.2f);

    /* energetic_curious */
    run_case("energetic",
        0.9f, 0.6f, 0.3f, 0.4f, 0.9f,
        0.56666667f, 0.75f, 0.65f);

    /* NULL-safety — both pointer args required, no-op on NULL */
    ca_affect_vad_t vad;
    vad.valence = vad.arousal = vad.dominance = -1.0f;
    ca_affect_vad_from(NULL, &vad);
    assert(vad.valence   == -1.0f);
    assert(vad.arousal   == -1.0f);
    assert(vad.dominance == -1.0f);

    ca_affect_state_t s = ca_affect_state_default();
    ca_affect_vad_from(&s, NULL); /* must not crash */

    printf("All affect VAD tests passed.\n");
    return 0;
}
