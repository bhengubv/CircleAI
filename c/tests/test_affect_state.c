#include <stdio.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define EPSILON 1e-5f

static void check(const char* id, float got, float expected) {
    float diff = fabsf(got - expected);
    if (diff > EPSILON) {
        fprintf(stderr, "FAIL [%s]: got %.8f, expected %.8f (diff %.8f)\n", id, got, expected, diff);
        assert(0);
    }
}

int main(void) {
    ca_affect_state_t s;

    /* positive_signal_once */
    s = ca_affect_state_default();
    ca_affect_state_positive_signal(&s);
    check("pos_once engagement",  s.engagement,  0.52f);
    check("pos_once uncertainty", s.uncertainty, 0.18f);
    check("pos_once rapport",     s.rapport,     0.01f);
    check("pos_once energy",      s.energy,      0.5f);
    check("pos_once curiosity",   s.curiosity,   0.5f);

    /* positive_signal_twice */
    s = ca_affect_state_default();
    ca_affect_state_positive_signal(&s);
    ca_affect_state_positive_signal(&s);
    check("pos_twice engagement",  s.engagement,  0.54f);
    check("pos_twice uncertainty", s.uncertainty, 0.16f);
    check("pos_twice rapport",     s.rapport,     0.02f);
    check("pos_twice energy",      s.energy,      0.5f);

    /* negative_signal_once */
    s = ca_affect_state_default();
    ca_affect_state_negative_signal(&s);
    check("neg_once engagement",  s.engagement,  0.47f);
    check("neg_once uncertainty", s.uncertainty, 0.23f);
    check("neg_once rapport",     s.rapport,     0.0f);
    check("neg_once energy",      s.energy,      0.5f);

    /* negative_signal_twice */
    s = ca_affect_state_default();
    ca_affect_state_negative_signal(&s);
    ca_affect_state_negative_signal(&s);
    check("neg_twice engagement",  s.engagement,  0.44f);
    check("neg_twice uncertainty", s.uncertainty, 0.26f);
    check("neg_twice rapport",     s.rapport,     0.0f);
    check("neg_twice energy",      s.energy,      0.5f);

    /* positive_then_negative
     * Start default, positive: engagement=0.52, uncertainty=0.18, rapport=0.01
     * Then negative: engagement=0.52-0.03=0.49, uncertainty=0.18+0.03=0.21, rapport stays 0.01
     */
    s = ca_affect_state_default();
    ca_affect_state_positive_signal(&s);
    ca_affect_state_negative_signal(&s);
    check("pos_neg engagement",  s.engagement,  0.49f);
    check("pos_neg uncertainty", s.uncertainty, 0.21f);
    check("pos_neg rapport",     s.rapport,     0.01f);
    check("pos_neg energy",      s.energy,      0.5f);

    /* negative_then_positive
     * Start default, negative: engagement=0.47, uncertainty=0.23, rapport=0.0
     * Then positive: engagement=0.47+0.02=0.49, uncertainty=0.23-0.02=0.21, rapport=0.0+0.01=0.01
     */
    s = ca_affect_state_default();
    ca_affect_state_negative_signal(&s);
    ca_affect_state_positive_signal(&s);
    check("neg_pos engagement",  s.engagement,  0.49f);
    check("neg_pos uncertainty", s.uncertainty, 0.21f);
    check("neg_pos rapport",     s.rapport,     0.01f);
    check("neg_pos energy",      s.energy,      0.5f);

    /* idle_decay_1h
     * Input: engagement=0.8, energy=0.7, rapport=0.4 (curiosity=0.5, uncertainty=0.2)
     * decay = min(0.3, 1*0.02) = 0.02
     * engagement = 0.8 + (0.5-0.8)*0.02 = 0.8 - 0.006 = 0.794
     * energy     = 0.7 + (0.5-0.7)*0.02 = 0.7 - 0.004 = 0.696
     */
    s = ca_affect_state_default();
    s.engagement = 0.8f; s.energy = 0.7f; s.rapport = 0.4f;
    ca_affect_state_idle_decay(&s, 1.0f);
    check("decay_1h engagement", s.engagement, 0.794f);
    check("decay_1h energy",     s.energy,     0.696f);
    check("decay_1h rapport",    s.rapport,    0.4f);
    check("decay_1h uncertainty",s.uncertainty,0.2f);

    /* idle_decay_8h
     * Input: engagement=0.8, energy=0.7, rapport=0.4
     * decay = min(0.3, 8*0.02) = 0.16
     * engagement = 0.8 + (0.5-0.8)*0.16 = 0.8 - 0.048 = 0.752
     * energy     = 0.7 + (0.5-0.7)*0.16 = 0.7 - 0.032 = 0.668
     */
    s = ca_affect_state_default();
    s.engagement = 0.8f; s.energy = 0.7f; s.rapport = 0.4f;
    ca_affect_state_idle_decay(&s, 8.0f);
    check("decay_8h engagement", s.engagement, 0.752f);
    check("decay_8h energy",     s.energy,     0.668f);
    check("decay_8h rapport",    s.rapport,    0.4f);
    check("decay_8h uncertainty",s.uncertainty,0.2f);

    /* idle_decay_24h
     * Input: engagement=0.8, energy=0.7, rapport=0.4
     * decay = min(0.3, 24*0.02) = 0.3 (capped)
     * engagement = 0.8 + (0.5-0.8)*0.3 = 0.8 - 0.09 = 0.71
     * energy     = 0.7 + (0.5-0.7)*0.3 = 0.7 - 0.06 = 0.64
     */
    s = ca_affect_state_default();
    s.engagement = 0.8f; s.energy = 0.7f; s.rapport = 0.4f;
    ca_affect_state_idle_decay(&s, 24.0f);
    check("decay_24h engagement", s.engagement, 0.71f);
    check("decay_24h energy",     s.energy,     0.64f);
    check("decay_24h rapport",    s.rapport,    0.4f);
    check("decay_24h uncertainty",s.uncertainty,0.2f);

    /* clamp_max_positive
     * Input: engagement=0.99, uncertainty=0.01, rapport=0.99
     * positive: engagement = min(1.0, 0.99+0.02) = 1.0
     *           uncertainty= max(0.0, 0.01-0.02) = 0.0
     *           rapport    = min(1.0, 0.99+0.01) = 1.0
     */
    s = ca_affect_state_default();
    s.engagement = 0.99f; s.uncertainty = 0.01f; s.rapport = 0.99f;
    ca_affect_state_positive_signal(&s);
    check("clamp_max engagement",  s.engagement,  1.0f);
    check("clamp_max uncertainty", s.uncertainty, 0.0f);
    check("clamp_max rapport",     s.rapport,     1.0f);
    check("clamp_max energy",      s.energy,      0.5f);

    /* clamp_min_negative
     * Input: engagement=0.01, uncertainty=0.98
     * negative: engagement = max(0.0, 0.01-0.03) = 0.0
     *           uncertainty= min(1.0, 0.98+0.03) = 1.0
     */
    s = ca_affect_state_default();
    s.engagement = 0.01f; s.uncertainty = 0.98f;
    ca_affect_state_negative_signal(&s);
    check("clamp_min engagement",  s.engagement,  0.0f);
    check("clamp_min uncertainty", s.uncertainty, 1.0f);
    check("clamp_min rapport",     s.rapport,     0.0f);
    check("clamp_min energy",      s.energy,      0.5f);

    /* idle_decay_neutral_no_change
     * Input: default state (engagement=0.5, energy=0.5), apply 8h decay
     * lerpf(0.5, 0.5, t) = 0.5 regardless of t
     */
    s = ca_affect_state_default();
    ca_affect_state_idle_decay(&s, 8.0f);
    check("neutral_decay engagement", s.engagement, 0.5f);
    check("neutral_decay energy",     s.energy,     0.5f);
    check("neutral_decay uncertainty",s.uncertainty,0.2f);
    check("neutral_decay rapport",    s.rapport,    0.0f);

    printf("All affect state tests passed.\n");
    return 0;
}
