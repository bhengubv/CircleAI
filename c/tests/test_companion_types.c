/*
 * test_companion_types.c — Companion types, FaceAffectMapper, and
 *                          FaceCompanionBridge tests.
 *
 * Expression deltas match fixtures/facex_biometric_vectors.json
 * affect_mapper_vectors within epsilon=1e-5.
 * Returns 0 on all-pass, calls assert() on first failure.
 */

#include <stdio.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define EPSILON 1e-5f

static void check(const char *id, float got, float expected) {
    float diff = fabsf(got - expected);
    if (diff > EPSILON) {
        fprintf(stderr, "FAIL [%s]: got %.8f, expected %.8f (diff %.8f)\n",
                id, (double)got, (double)expected, (double)diff);
        assert(0);
    }
}

int main(void) {
    /* ------------------------------------------------------------------
     * Companion struct construction
     * ------------------------------------------------------------------ */
    ca_companion_context_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    strncpy(ctx.session_id,  "550e8400-e29b-41d4-a716-446655440000", 36);
    strncpy(ctx.identity_id, "550e8400-e29b-41d4-a716-446655440001", 36);
    ctx.interface_kind = CA_IFACE_VOICE;
    ctx.locale = "en-US";
    ctx.started_at = 1704067200000LL;

    assert(ctx.interface_kind == CA_IFACE_VOICE);
    assert(strcmp(ctx.locale, "en-US") == 0);
    assert(ctx.started_at == 1704067200000LL);

    /* All interface kinds */
    assert(CA_IFACE_VOICE   == 0);
    assert(CA_IFACE_TEXT    == 1);
    assert(CA_IFACE_VISUAL  == 2);
    assert(CA_IFACE_AMBIENT == 3);

    ca_companion_turn_t turn;
    memset(&turn, 0, sizeof(turn));
    strncpy(turn.turn_id,    "550e8400-e29b-41d4-a716-446655440000", 36);
    strncpy(turn.session_id, "550e8400-e29b-41d4-a716-446655440001", 36);
    turn.user_input          = "Hello";
    turn.assistant_response  = "Hi there!";
    turn.turn_index          = 0;

    assert(turn.turn_index == 0);
    assert(strcmp(turn.user_input, "Hello") == 0);
    assert(strcmp(turn.assistant_response, "Hi there!") == 0);

    /* Proactive event kinds */
    assert(CA_PROACTIVE_IDLE_TOO_LONG   == 0);
    assert(CA_PROACTIVE_TOPIC_SHIFT     == 1);
    assert(CA_PROACTIVE_GOAL_COMPLETED  == 2);
    assert(CA_PROACTIVE_GOAL_SUGGESTED  == 3);
    assert(CA_PROACTIVE_MEMORY_RECALLED == 4);

    ca_proactive_event_t event;
    memset(&event, 0, sizeof(event));
    event.kind    = CA_PROACTIVE_GOAL_SUGGESTED;
    event.payload = "{\"goal\":\"test\"}";
    assert(event.kind == CA_PROACTIVE_GOAL_SUGGESTED);
    assert(strcmp(event.payload, "{\"goal\":\"test\"}") == 0);

    /* ------------------------------------------------------------------
     * FaceAffectMapper — fixture: affect_mapper_vectors
     * ------------------------------------------------------------------ */

    ca_affect_state_t s;
    bool mutated;

    /* happy_from_neutral: engagement += 0.03, energy += 0.02 */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.92f, CA_FACE_HAPPY, &s);
    assert(mutated == true);
    check("happy engagement", s.engagement, 0.53f);
    check("happy energy",     s.energy,     0.52f);
    check("happy curiosity",  s.curiosity,  0.5f);
    check("happy uncertainty",s.uncertainty,0.2f);
    check("happy rapport",    s.rapport,    0.0f);

    /* surprised_from_neutral: curiosity += 0.04 */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.88f, CA_FACE_SURPRISED, &s);
    assert(mutated == true);
    check("surprised curiosity",   s.curiosity,   0.54f);
    check("surprised engagement",  s.engagement,  0.5f);
    check("surprised uncertainty", s.uncertainty, 0.2f);

    /* confused_from_neutral: uncertainty += 0.05 */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.79f, CA_FACE_CONFUSED, &s);
    assert(mutated == true);
    check("confused uncertainty", s.uncertainty, 0.25f);
    check("confused engagement",  s.engagement,  0.5f);
    check("confused curiosity",   s.curiosity,   0.5f);

    /* stressed_from_neutral: uncertainty += 0.08, energy -= 0.05 */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.85f, CA_FACE_STRESSED, &s);
    assert(mutated == true);
    check("stressed uncertainty", s.uncertainty, 0.28f);
    check("stressed energy",      s.energy,      0.45f);
    check("stressed engagement",  s.engagement,  0.5f);

    /* angry_from_neutral (rapport=0.3): engagement -= 0.04, rapport -= 0.02 */
    s = ca_affect_state_default();
    s.rapport = 0.3f;
    mutated = ca_face_apply_affect(0.91f, CA_FACE_ANGRY, &s);
    assert(mutated == true);
    check("angry engagement", s.engagement, 0.46f);
    check("angry rapport",    s.rapport,    0.28f);
    check("angry uncertainty",s.uncertainty,0.2f);

    /* neutral_expression_no_change */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.95f, CA_FACE_NEUTRAL, &s);
    assert(mutated == false);
    check("neutral engagement",  s.engagement,  0.5f);
    check("neutral curiosity",   s.curiosity,   0.5f);
    check("neutral uncertainty", s.uncertainty, 0.2f);
    check("neutral rapport",     s.rapport,     0.0f);
    check("neutral energy",      s.energy,      0.5f);

    /* low_confidence_discarded: confidence < 0.5 => no change */
    s = ca_affect_state_default();
    mutated = ca_face_apply_affect(0.49f, CA_FACE_STRESSED, &s);
    assert(mutated == false);
    check("low_conf uncertainty", s.uncertainty, 0.2f);
    check("low_conf energy",      s.energy,      0.5f);

    /* clamp_max_engagement: near-max + happy delta => 1.0 */
    s = ca_affect_state_default();
    s.engagement = 0.99f;
    mutated = ca_face_apply_affect(0.95f, CA_FACE_HAPPY, &s);
    assert(mutated == true);
    check("clamp_max_engagement", s.engagement, 1.0f);
    check("clamp_max_energy",     s.energy,     0.52f);

    /* ------------------------------------------------------------------
     * FaceCompanionBridge — confusion threshold
     * ------------------------------------------------------------------ */

    /* uncertainty well below threshold => no event */
    s = ca_affect_state_default(); /* uncertainty = 0.2 */
    ca_proactive_event_t out;
    memset(&out, 0, sizeof(out));
    int fired = ca_face_observe(0.80f, CA_FACE_HAPPY, &s,
                                "sess-001", "id-001", CA_IFACE_TEXT, &out);
    assert(fired == 0);

    /* Build up uncertainty above threshold (0.70) via multiple confused signals */
    s = ca_affect_state_default();
    s.uncertainty = 0.66f; /* 0.66 + 0.05 = 0.71 >= 0.70 */
    memset(&out, 0, sizeof(out));
    fired = ca_face_observe(0.80f, CA_FACE_CONFUSED, &s,
                            "sess-002", "id-002", CA_IFACE_TEXT, &out);
    assert(fired == 1);
    assert(s.uncertainty >= CA_CONFUSION_THRESHOLD);
    assert(strcmp(out.session_id,  "sess-002") == 0);
    assert(strcmp(out.identity_id, "id-002")   == 0);

    /* Exactly at threshold: 0.70 >= 0.70 fires if a confused signal just pushed it */
    s = ca_affect_state_default();
    s.uncertainty = 0.65f; /* 0.65 + 0.05 = 0.70 exactly */
    memset(&out, 0, sizeof(out));
    fired = ca_face_observe(0.80f, CA_FACE_CONFUSED, &s,
                            "sess-003", "id-003", CA_IFACE_VOICE, &out);
    assert(fired == 1);
    check("threshold_exact", s.uncertainty, CA_CONFUSION_THRESHOLD);

    printf("All companion type tests passed.\n");
    return 0;
}
