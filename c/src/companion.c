/*
 * companion.c — FaceAffectMapper and FaceCompanionBridge implementations.
 *
 * Expression deltas match fixtures/facex_biometric_vectors.json affect_mapper_vectors.
 * Pure C11, no OS-specific headers.  Links against -lm.
 */

#include "circle_ai/companion.h"
#include <string.h>
#include <stdio.h>

/* ---------------------------------------------------------------------------
 * Internal: clamp float to [0.0, 1.0]
 * --------------------------------------------------------------------------- */

static float ca_clampf(float v) {
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

/* ---------------------------------------------------------------------------
 * ca_face_apply_affect
 *
 * Deltas (all axes clamped after mutation):
 *   HAPPY     (CA_FACE_HAPPY=1):     engagement += 0.03, energy     += 0.02
 *   SURPRISED (CA_FACE_SURPRISED=3): curiosity  += 0.04
 *   CONFUSED  (CA_FACE_CONFUSED=4):  uncertainty+= 0.05
 *   STRESSED  (CA_FACE_STRESSED=5):  uncertainty+= 0.08, energy     -= 0.05
 *   ANGRY     (CA_FACE_ANGRY=6):     engagement -= 0.04, rapport    -= 0.02
 *   All others: no mutation, returns false
 * --------------------------------------------------------------------------- */

bool ca_face_apply_affect(float confidence, ca_face_expression_t expression,
                          ca_affect_state_t *affect) {
    if (confidence < 0.5f) return false;

    switch (expression) {
        case CA_FACE_HAPPY:
            affect->engagement = ca_clampf(affect->engagement + 0.03f);
            affect->energy     = ca_clampf(affect->energy     + 0.02f);
            return true;

        case CA_FACE_SURPRISED:
            affect->curiosity  = ca_clampf(affect->curiosity  + 0.04f);
            return true;

        case CA_FACE_CONFUSED:
            affect->uncertainty = ca_clampf(affect->uncertainty + 0.05f);
            return true;

        case CA_FACE_STRESSED:
            affect->uncertainty = ca_clampf(affect->uncertainty + 0.08f);
            affect->energy      = ca_clampf(affect->energy      - 0.05f);
            return true;

        case CA_FACE_ANGRY:
            affect->engagement = ca_clampf(affect->engagement - 0.04f);
            affect->rapport    = ca_clampf(affect->rapport    - 0.02f);
            return true;

        default:
            return false;
    }
}

/* ---------------------------------------------------------------------------
 * ca_face_observe
 *
 * 1. Apply expression to affect state (via ca_face_apply_affect).
 * 2. If affect->uncertainty >= CA_CONFUSION_THRESHOLD (0.70), emit a
 *    proactive CONFUSED event.
 * Returns 1 if *out_event was filled, 0 otherwise.
 * --------------------------------------------------------------------------- */

int ca_face_observe(float confidence, ca_face_expression_t expression,
                    ca_affect_state_t *affect,
                    const char *session_id, const char *identity_id,
                    ca_interface_kind_t surface,
                    ca_proactive_event_t *out_event) {
    ca_face_apply_affect(confidence, expression, affect);

    /* Both conditions must hold: post-mutation uncertainty crosses the threshold
     * AND the observed expression was Confused or Stressed.
     * A high Uncertainty score alone (from prior interactions) does not trigger
     * a face-driven proactive event. */
    int is_confusion_expr = (expression == CA_FACE_CONFUSED || expression == CA_FACE_STRESSED);
    if (affect->uncertainty >= CA_CONFUSION_THRESHOLD && is_confusion_expr) {
        memset(out_event, 0, sizeof(*out_event));
        out_event->interface_kind = surface;

        if (session_id) {
            strncpy(out_event->session_id,  session_id,  sizeof(out_event->session_id)  - 1);
        }
        if (identity_id) {
            strncpy(out_event->identity_id, identity_id, sizeof(out_event->identity_id) - 1);
        }

        strncpy(out_event->trigger_name, "face.confusion_detected",
                sizeof(out_event->trigger_name) - 1);

        strncpy(out_event->message,
                "I notice you might be finding this a bit tricky. "
                "Would you like me to slow down or explain it differently?",
                sizeof(out_event->message) - 1);

        return 1;
    }

    return 0;
}
