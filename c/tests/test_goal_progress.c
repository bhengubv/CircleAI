/*
 * test_goal_progress.c — Goal.AdvanceProgress fixture tests.
 *
 * All expected values match fixtures/goal_progress.json.
 * Formula: new_progress = clamp(progress + delta, 0.0, 1.0)
 * Returns 0 on all-pass, calls assert() on first failure.
 */

#include <stdio.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define EPSILON 1e-6f

static void check(const char *id, float got, float expected) {
    float diff = fabsf(got - expected);
    if (diff > EPSILON) {
        fprintf(stderr, "FAIL [%s]: got %.8f, expected %.8f (diff %.8f)\n",
                id, (double)got, (double)expected, (double)diff);
        assert(0);
    }
}

static float advance(float initial, float delta) {
    ca_goal_t g;
    g.progress = initial;
    g.status   = CA_GOAL_ACTIVE;
    g.id[0]    = '\0';
    g.description    = NULL;
    g.created_at     = 0;
    g.resolved_at    = 0;
    return ca_goal_advance_progress(&g, delta);
}

int main(void) {
    /* zero_initial: 0 + 0 = 0 */
    check("zero_initial", advance(0.0f, 0.0f), 0.0f);

    /* partial_advance: 0 + 0.3 = 0.3 */
    check("partial_advance", advance(0.0f, 0.3f), 0.3f);

    /* clamp_max: 0.9 + 0.5 = 1.0 (clamped) */
    check("clamp_max", advance(0.9f, 0.5f), 1.0f);

    /* clamp_min: 0.1 - 0.5 = 0.0 (clamped) */
    check("clamp_min", advance(0.1f, -0.5f), 0.0f);

    /* zero_delta: 0.5 + 0 = 0.5 */
    check("zero_delta", advance(0.5f, 0.0f), 0.5f);

    /* advance_to_full: 0.75 + 0.25 = 1.0 (exact, not clamped) */
    check("advance_to_full", advance(0.75f, 0.25f), 1.0f);

    /* negative_delta: 0.6 - 0.2 = 0.4 */
    check("negative_delta", advance(0.6f, -0.2f), 0.4f);

    printf("All goal progress tests passed.\n");
    return 0;
}
