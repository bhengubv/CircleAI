/*
 * memory.c — AffectState and PersonaState implementations.
 *
 * AffectState math must match fixtures/affect_state.json within 1e-5.
 * PersonaState hint must match fixtures/persona_state.json exactly.
 *
 * Pure C11, no OS-specific headers.
 */

#include "circle_ai/memory.h"
#include <string.h>
#include <stdio.h>

/* ---------------------------------------------------------------------------
 * Internal helpers
 * --------------------------------------------------------------------------- */

static float ca_clampf(float v) {
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

/*
 * lerp(a, b, t) = a + (b - a) * clamp(t, 0, 1)
 *
 * When a == b (e.g. both are 0.5, the neutral point) the result is exactly a,
 * independent of t — ensuring idle_decay_neutral_no_change passes with 0.0 diff.
 */
static float ca_lerpf(float a, float b, float t) {
    if (t < 0.0f) t = 0.0f;
    if (t > 1.0f) t = 1.0f;
    return a + (b - a) * t;
}

/* ---------------------------------------------------------------------------
 * AffectState
 * --------------------------------------------------------------------------- */

ca_affect_state_t ca_affect_state_default(void) {
    ca_affect_state_t s;
    s.curiosity       = 0.5f;
    s.engagement      = 0.5f;
    s.uncertainty     = 0.2f;
    s.rapport         = 0.0f;
    s.energy          = 0.5f;
    s.last_updated_at = 0;
    return s;
}

void ca_affect_state_positive_signal(ca_affect_state_t *s) {
    s->engagement  = ca_clampf(s->engagement  + 0.02f);
    s->rapport     = ca_clampf(s->rapport     + 0.01f);
    s->uncertainty = ca_clampf(s->uncertainty - 0.02f);
}

void ca_affect_state_negative_signal(ca_affect_state_t *s) {
    s->engagement  = ca_clampf(s->engagement  - 0.03f);
    s->uncertainty = ca_clampf(s->uncertainty + 0.03f);
}

void ca_affect_state_idle_decay(ca_affect_state_t *s, float idle_hours) {
    float decay = idle_hours * 0.02f;
    if (decay > 0.3f) decay = 0.3f;
    s->engagement = ca_lerpf(s->engagement, 0.5f, decay);
    s->energy     = ca_lerpf(s->energy,     0.5f, decay);
}

/* ---------------------------------------------------------------------------
 * AffectVad — derived Russell PAD projection of AffectState.
 *
 * Formulas (cross-language contract, see fixtures/affect_vad_derivation.json):
 *   valence   = (engagement + rapport + (1 - uncertainty)) / 3
 *   arousal   = (energy * 2 + curiosity + uncertainty) / 4
 *   dominance = (engagement + (1 - uncertainty)) / 2
 * All outputs clamped to [0.0, 1.0].
 * --------------------------------------------------------------------------- */

void ca_affect_vad_from(const ca_affect_state_t *state, ca_affect_vad_t *out_vad) {
    if (!state || !out_vad) return;
    float v = (state->engagement + state->rapport + (1.0f - state->uncertainty)) / 3.0f;
    float a = (state->energy * 2.0f + state->curiosity + state->uncertainty) / 4.0f;
    float d = (state->engagement + (1.0f - state->uncertainty)) / 2.0f;
    if (v < 0.0f) v = 0.0f; if (v > 1.0f) v = 1.0f;
    if (a < 0.0f) a = 0.0f; if (a > 1.0f) a = 1.0f;
    if (d < 0.0f) d = 0.0f; if (d > 1.0f) d = 1.0f;
    out_vad->valence   = v;
    out_vad->arousal   = a;
    out_vad->dominance = d;
}

/* ---------------------------------------------------------------------------
 * PersonaState hint
 *
 * Rules (fixtures/persona_state.json):
 *   - balanced + neutral + no locale  => ""
 *   - Otherwise start with "[User preferences]\n" then append lines:
 *       brief:    "Keep responses brief.\n"
 *       detailed: "Keep responses detailed.\n"
 *       casual:   "Use a casual, friendly tone.\n"
 *       formal:   "Maintain a formal, professional tone.\n"
 *       locale:   "Respond in the language appropriate for locale <tag>.\n"
 * --------------------------------------------------------------------------- */

char *ca_persona_state_to_hint(const ca_persona_state_t *p, char *buf, int buf_size) {
    int has_verbosity = (p->verbosity != CA_VERBOSITY_BALANCED);
    int has_formality = (p->formality != CA_FORMALITY_NEUTRAL);
    int has_locale    = (p->preferred_locale != NULL && p->preferred_locale[0] != '\0');

    if (!has_verbosity && !has_formality && !has_locale) {
        if (buf_size > 0) buf[0] = '\0';
        return buf;
    }

    /* Build into buf using snprintf to avoid overflow */
    int pos = 0;
    int rem = buf_size;

#define APPEND(str)                                         \
    do {                                                    \
        int _n = snprintf(buf + pos, (size_t)rem, "%s", (str)); \
        if (_n > 0) { pos += _n; rem -= _n; }              \
    } while(0)

    APPEND("[User preferences]\n");

    if (p->verbosity == CA_VERBOSITY_BRIEF) {
        APPEND("Keep responses brief.\n");
    } else if (p->verbosity == CA_VERBOSITY_DETAILED) {
        APPEND("Keep responses detailed.\n");
    }

    if (p->formality == CA_FORMALITY_CASUAL) {
        APPEND("Use a casual, friendly tone.\n");
    } else if (p->formality == CA_FORMALITY_FORMAL) {
        APPEND("Maintain a formal, professional tone.\n");
    }

    if (has_locale) {
        int _n = snprintf(buf + pos, (size_t)rem,
                          "Respond in the language appropriate for locale %s.\n",
                          p->preferred_locale);
        if (_n > 0) { pos += _n; rem -= _n; }
    }

#undef APPEND

    (void)pos;
    (void)rem;
    return buf;
}
