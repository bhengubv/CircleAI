#ifndef CIRCLE_AI_MEMORY_H
#define CIRCLE_AI_MEMORY_H

#include <stdint.h>

/* AffectState - pure value type (no heap allocation) */
typedef struct {
    float curiosity;
    float engagement;
    float uncertainty;
    float rapport;
    float energy;
    int64_t last_updated_at; /* unix ms */
} ca_affect_state_t;

/* Construct with defaults */
ca_affect_state_t ca_affect_state_default(void);

/* Mutators - modify in-place */
void ca_affect_state_positive_signal(ca_affect_state_t* s);
void ca_affect_state_negative_signal(ca_affect_state_t* s);
void ca_affect_state_idle_decay(ca_affect_state_t* s, float idle_hours);

/* Feedback signal */
typedef enum { CA_FEEDBACK_POSITIVE = 0, CA_FEEDBACK_NEGATIVE, CA_FEEDBACK_NEUTRAL } ca_feedback_signal_t;

/* Verbosity + formality */
typedef enum { CA_VERBOSITY_BRIEF = 0, CA_VERBOSITY_BALANCED, CA_VERBOSITY_DETAILED } ca_verbosity_t;
typedef enum { CA_FORMALITY_CASUAL = 0, CA_FORMALITY_NEUTRAL, CA_FORMALITY_FORMAL } ca_formality_t;

typedef struct {
    ca_verbosity_t verbosity;
    ca_formality_t formality;
    const char*    preferred_locale; /* NULL = no preference */
} ca_persona_state_t;

/* Goal */
typedef enum { CA_GOAL_ACTIVE = 0, CA_GOAL_COMPLETED, CA_GOAL_ABANDONED } ca_goal_status_t;

typedef struct {
    char            id[37];          /* UUID string */
    const char*     description;
    ca_goal_status_t status;
    int64_t         created_at;      /* unix ms */
    int64_t         resolved_at;     /* 0 = unresolved */
} ca_goal_t;

#endif /* CIRCLE_AI_MEMORY_H */
