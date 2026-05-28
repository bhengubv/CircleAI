/*
 * models.c — Implementation of ca_goal_advance_progress().
 * All other model types are pure value types with no associated logic.
 * Pure C11, no OS-specific headers.
 */

#include "circle_ai/models.h"

float ca_goal_advance_progress(ca_goal_t *goal, float delta) {
    float next = goal->progress + delta;
    if (next < 0.0f) next = 0.0f;
    if (next > 1.0f) next = 1.0f;
    goal->progress = next;
    return next;
}
