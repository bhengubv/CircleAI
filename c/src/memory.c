#include "circle_ai/memory.h"
#include <time.h>

static float clampf(float v, float lo, float hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

static float lerpf(float a, float b, float t) {
    t = clampf(t, 0.0f, 1.0f);
    return a + (b - a) * t;
}

ca_affect_state_t ca_affect_state_default(void) {
    ca_affect_state_t s;
    s.curiosity    = 0.5f;
    s.engagement   = 0.5f;
    s.uncertainty  = 0.2f;
    s.rapport      = 0.0f;
    s.energy       = 0.5f;
    s.last_updated_at = 0;
    return s;
}

void ca_affect_state_positive_signal(ca_affect_state_t* s) {
    s->engagement  = clampf(s->engagement  + 0.02f, 0.0f, 1.0f);
    s->rapport     = clampf(s->rapport     + 0.01f, 0.0f, 1.0f);
    s->uncertainty = clampf(s->uncertainty - 0.02f, 0.0f, 1.0f);
}

void ca_affect_state_negative_signal(ca_affect_state_t* s) {
    s->engagement  = clampf(s->engagement  - 0.03f, 0.0f, 1.0f);
    s->uncertainty = clampf(s->uncertainty + 0.03f, 0.0f, 1.0f);
}

void ca_affect_state_idle_decay(ca_affect_state_t* s, float idle_hours) {
    float decay = idle_hours * 0.02f;
    if (decay > 0.3f) decay = 0.3f;
    s->engagement = lerpf(s->engagement, 0.5f, decay);
    s->energy     = lerpf(s->energy,     0.5f, decay);
}
