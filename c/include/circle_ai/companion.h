#ifndef CIRCLE_AI_COMPANION_H
#define CIRCLE_AI_COMPANION_H

/*
 * companion.h — CompanionContext, CompanionTurn, FaceAffectMapper,
 *               FaceCompanionBridge, and ProactiveEvent types.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>
#include <stdbool.h>
#include "memory.h"
#include "models.h"

/* ---------------------------------------------------------------------------
 * Interface kind (surface type)
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_IFACE_VOICE   = 0,
    CA_IFACE_TEXT    = 1,
    CA_IFACE_VISUAL  = 2,
    CA_IFACE_AMBIENT = 3
} ca_interface_kind_t;

/* ---------------------------------------------------------------------------
 * CompanionContext — identifies a live session
 * --------------------------------------------------------------------------- */

typedef struct {
    char                session_id[37];
    char                identity_id[37];
    ca_interface_kind_t interface_kind;
    const char         *locale;       /* BCP-47 or NULL; caller owns */
    int64_t             started_at;   /* Unix ms UTC */
} ca_companion_context_t;

/* ---------------------------------------------------------------------------
 * CompanionTurn — a single user/assistant exchange
 * --------------------------------------------------------------------------- */

typedef struct {
    char        turn_id[37];
    char        session_id[37];
    const char *user_input;           /* caller owns */
    const char *assistant_response;  /* caller owns */
    int64_t     created_at;          /* Unix ms UTC */
    int         turn_index;
} ca_companion_turn_t;

/* ---------------------------------------------------------------------------
 * ProactiveEvent — emitted by the companion bridge
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_PROACTIVE_IDLE_TOO_LONG   = 0,
    CA_PROACTIVE_TOPIC_SHIFT     = 1,
    CA_PROACTIVE_GOAL_COMPLETED  = 2,
    CA_PROACTIVE_GOAL_SUGGESTED  = 3,
    CA_PROACTIVE_MEMORY_RECALLED = 4
} ca_proactive_event_kind_t;

typedef struct {
    ca_proactive_event_kind_t kind;
    char                      session_id[64];
    char                      identity_id[64];
    ca_interface_kind_t       interface_kind;
    char                      message[256];
    char                      trigger_name[64];
    int64_t                   generated_at_ms;
    const char               *payload; /* caller owns; may be NULL */
} ca_proactive_event_t;

/* ---------------------------------------------------------------------------
 * FaceAffectMapper
 *
 * Maps a detected face expression + confidence score onto the AffectState.
 *
 * Threshold: confidence must be >= 0.5 to apply any delta.
 *
 * Expression deltas (all axes clamped to [0.0, 1.0] after mutation):
 *   HAPPY     (1): engagement += 0.03, energy     += 0.02
 *   SURPRISED (3): curiosity  += 0.04
 *   CONFUSED  (4): uncertainty+= 0.05
 *   STRESSED  (5): uncertainty+= 0.08, energy     -= 0.05
 *   ANGRY     (6): engagement -= 0.04, rapport    -= 0.02
 *   All others: no mutation (returns false)
 *
 * Returns true if the affect state was mutated, false otherwise.
 * --------------------------------------------------------------------------- */

bool ca_face_apply_affect(float confidence, ca_face_expression_t expression,
                          ca_affect_state_t *affect);

/* ---------------------------------------------------------------------------
 * FaceCompanionBridge
 *
 * Confusion threshold: uncertainty >= 0.70 triggers a proactive event.
 *
 * Returns 1 if a proactive event was emitted and *out_event was filled;
 * returns 0 otherwise.
 * --------------------------------------------------------------------------- */

#define CA_CONFUSION_THRESHOLD 0.70f

int ca_face_observe(float confidence, ca_face_expression_t expression,
                    ca_affect_state_t *affect,
                    const char *session_id, const char *identity_id,
                    ca_interface_kind_t surface,
                    ca_proactive_event_t *out_event);

#endif /* CIRCLE_AI_COMPANION_H */
