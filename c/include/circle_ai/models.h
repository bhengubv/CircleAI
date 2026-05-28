#ifndef CIRCLE_AI_MODELS_H
#define CIRCLE_AI_MODELS_H

/*
 * models.h — Core value types shared across the CircleAI SDK.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>
#include <stdbool.h>

/* ---------------------------------------------------------------------------
 * Chat message
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_ROLE_USER      = 0,
    CA_ROLE_ASSISTANT = 1,
    CA_ROLE_SYSTEM    = 2
} ca_role_t;

typedef struct {
    ca_role_t   role;
    const char *content;    /* UTF-8, caller owns */
    int64_t     created_at; /* Unix ms UTC */
} ca_chat_message_t;

/* ---------------------------------------------------------------------------
 * Download / transfer progress
 * --------------------------------------------------------------------------- */

typedef struct {
    int64_t bytes_received;
    int64_t bytes_total;  /* 0 = unknown */
    float   progress;     /* [0.0, 1.0] */
} ca_download_progress_t;

/* ---------------------------------------------------------------------------
 * FaceExpression classification
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_FACE_NEUTRAL   = 0,
    CA_FACE_HAPPY     = 1,
    CA_FACE_SAD       = 2,
    CA_FACE_SURPRISED = 3,
    CA_FACE_CONFUSED  = 4,
    CA_FACE_STRESSED  = 5,
    CA_FACE_ANGRY     = 6,
    CA_FACE_UNKNOWN   = 7
} ca_face_expression_t;

/* ---------------------------------------------------------------------------
 * Facial metric matrix (68-point landmark model)
 * --------------------------------------------------------------------------- */

typedef struct {
    float x;      /* normalised [0.0, 1.0] relative to image width  */
    float y;      /* normalised [0.0, 1.0] relative to image height */
    float width;
    float height;
} ca_face_bounding_box_t;

typedef struct {
    float                  landmarks[136];  /* 68 (x,y) pairs, flat float array */
    ca_face_bounding_box_t bounding_box;
    ca_face_expression_t   expression;
    float                  confidence_score; /* [0.0, 1.0] */
    int64_t                captured_at_ms;   /* Unix ms UTC */
} ca_facial_metric_matrix_t;

/* ---------------------------------------------------------------------------
 * Goal
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_GOAL_ACTIVE    = 0,
    CA_GOAL_COMPLETED = 1,
    CA_GOAL_ABANDONED = 2
} ca_goal_status_t;

typedef struct {
    char             id[37];        /* UUID string (null-terminated) */
    const char      *description;   /* caller owns */
    ca_goal_status_t status;
    float            progress;      /* [0.0, 1.0] */
    int64_t          created_at;    /* Unix ms UTC */
    int64_t          resolved_at;   /* 0 = unresolved */
} ca_goal_t;

/* Advance progress by delta, clamped to [0.0, 1.0]. Returns new progress. */
float ca_goal_advance_progress(ca_goal_t *goal, float delta);

/* ---------------------------------------------------------------------------
 * Feedback signal
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_FEEDBACK_POSITIVE = 0,
    CA_FEEDBACK_NEGATIVE = 1,
    CA_FEEDBACK_NEUTRAL  = 2
} ca_feedback_signal_t;

#endif /* CIRCLE_AI_MODELS_H */
