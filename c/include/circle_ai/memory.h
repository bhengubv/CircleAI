#ifndef CIRCLE_AI_MEMORY_H
#define CIRCLE_AI_MEMORY_H

/*
 * memory.h — AffectState (5-axis emotional model) and PersonaState.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>
#include <stdbool.h>

/* ---------------------------------------------------------------------------
 * AffectState — 5-axis float model, all axes clamped to [0.0, 1.0]
 *
 * Defaults: curiosity=0.5, engagement=0.5, uncertainty=0.2, rapport=0.0, energy=0.5
 * --------------------------------------------------------------------------- */

typedef struct {
    float   curiosity;       /* default 0.5 */
    float   engagement;      /* default 0.5 */
    float   uncertainty;     /* default 0.2 */
    float   rapport;         /* default 0.0 */
    float   energy;          /* default 0.5 */
    int64_t last_updated_at; /* Unix ms UTC  */
} ca_affect_state_t;

/* Construct with defaults */
ca_affect_state_t ca_affect_state_default(void);

/* Mutators — modify in-place */

/*
 * ApplyPositiveSignal:
 *   engagement  += 0.02
 *   rapport     += 0.01
 *   uncertainty -= 0.02
 * All axes clamped to [0.0, 1.0].
 */
void ca_affect_state_positive_signal(ca_affect_state_t *s);

/*
 * ApplyNegativeSignal:
 *   engagement  -= 0.03
 *   uncertainty += 0.03
 * All axes clamped to [0.0, 1.0].
 */
void ca_affect_state_negative_signal(ca_affect_state_t *s);

/*
 * ApplyIdleDecay:
 *   decay = min(0.3, idle_hours * 0.02)
 *   engagement = lerp(engagement, 0.5, decay)
 *   energy     = lerp(energy,     0.5, decay)
 * (curiosity, uncertainty, rapport are NOT decayed)
 */
void ca_affect_state_idle_decay(ca_affect_state_t *s, float idle_hours);

/* ---------------------------------------------------------------------------
 * AffectVad — derived Russell PAD (Valence / Arousal / Dominance) projection.
 *
 * AffectVad is a DERIVED 3-axis view of AffectState; it does not replace the
 * 5-axis model. Derivation (output clamped to [0.0, 1.0]):
 *
 *   valence   = (engagement + rapport + (1 - uncertainty)) / 3
 *   arousal   = (energy * 2 + curiosity + uncertainty) / 4
 *   dominance = (engagement + (1 - uncertainty)) / 2
 *
 * This math is the cross-language fixture contract — see
 * fixtures/affect_vad_derivation.json. Must match every port byte-identically.
 * --------------------------------------------------------------------------- */

typedef struct {
    float valence;   /* pleasure ↔ displeasure, [0, 1] */
    float arousal;   /* activation ↔ calm,       [0, 1] */
    float dominance; /* in-control ↔ submissive, [0, 1] */
} ca_affect_vad_t;

/*
 * Project an AffectState into its derived VAD view.
 * Both pointers must be non-NULL; the function is a no-op when either is NULL.
 */
void ca_affect_vad_from(const ca_affect_state_t *state, ca_affect_vad_t *out_vad);

/* ---------------------------------------------------------------------------
 * PersonaState — verbosity, formality, locale preference
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_VERBOSITY_BRIEF    = 0,
    CA_VERBOSITY_BALANCED = 1,
    CA_VERBOSITY_DETAILED = 2
} ca_verbosity_t;

typedef enum {
    CA_FORMALITY_CASUAL  = 0,
    CA_FORMALITY_NEUTRAL = 1,
    CA_FORMALITY_FORMAL  = 2
} ca_formality_t;

typedef struct {
    ca_verbosity_t verbosity;
    ca_formality_t formality;
    const char    *preferred_locale; /* BCP-47 tag or NULL */
} ca_persona_state_t;

/*
 * Render persona state as a system-prompt hint.
 *
 * Rules (matches fixtures/persona_state.json exactly):
 *   - If all defaults (balanced + neutral + no locale): returns ""
 *   - Otherwise starts with "[User preferences]\n" then appends:
 *       brief:    "Keep responses brief.\n"
 *       detailed: "Keep responses detailed.\n"
 *       casual:   "Use a casual, friendly tone.\n"
 *       formal:   "Maintain a formal, professional tone.\n"
 *       locale:   "Respond in the language appropriate for locale <tag>.\n"
 *
 * Returns pointer to caller-supplied buffer (size >= 512 recommended).
 */
char *ca_persona_state_to_hint(const ca_persona_state_t *p, char *buf, int buf_size);

#endif /* CIRCLE_AI_MEMORY_H */
