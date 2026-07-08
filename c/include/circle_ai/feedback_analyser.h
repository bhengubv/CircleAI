#ifndef CIRCLE_AI_FEEDBACK_ANALYSER_H
#define CIRCLE_AI_FEEDBACK_ANALYSER_H

/*
 * feedback_analyser.h — persona-adaptation deltas from a window of feedback
 * signals (C11 port).
 *
 * Ported from CircleAI.Memory.FeedbackAnalyser (C#) and mirroring the verified
 * TypeScript reference (memory/feedback_analyser.ts) 1:1:
 *   - FeedbackPolarity (Positive/Negative/Correction)
 *   - FeedbackSignal (recorded-at + polarity — the minimal in-memory record)
 *   - InMemoryFeedbackStore (FIFO-capped, newest-first recall, positive ratio)
 *   - PersonaAdaptation (float deltas)
 *   - FeedbackAnalyser (window default 20, >70% negative → -0.1f, >70% positive
 *     → +0.05f, else 0; formality always 0; topics always empty)
 *
 * The C# PersonaAdaptation holds `float` deltas; we use C `float` so the FP32
 * constants (-0.1f, +0.05f) are byte-identical to every other SDK language.
 *
 * In-memory only: dynamic arrays + linear search. Every owning struct holds
 * strdup'd copies with a matching *_free / *_destroy (NULL-safe, no leaks).
 *
 * Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * FeedbackPolarity + FeedbackSignal
 * =========================================================================== */

/* Matches the C# FeedbackPolarity numeric values (Positive=1, Negative=-1,
 * Correction=0). NB: distinct from models.h's affect-era ca_feedback_signal_rec_t
 * enum — this polarity + the signal RECORD below belong to the feedback
 * analyser subsystem. */
typedef enum {
    CA_FEEDBACK_POLARITY_POSITIVE   = 1,
    CA_FEEDBACK_POLARITY_NEGATIVE   = -1,
    CA_FEEDBACK_POLARITY_CORRECTION = 0
} ca_feedback_polarity_t;

/* A single user-feedback event (the C# FeedbackSignal record). The analyser only
 * reads recorded_at + polarity; the optional id/user_text/assistant_text mirror
 * the C# fields for parity but are not required. Owns its string copies. Named
 * *_rec_t because models.h already owns the identifier ca_feedback_signal_rec_t for
 * an unrelated affect enum. */
typedef struct {
    char                  *id;              /* owned, or NULL */
    int64_t                recorded_at_ms;  /* Unix ms UTC */
    char                  *user_text;       /* owned, or NULL */
    char                  *assistant_text;  /* owned, or NULL */
    ca_feedback_polarity_t polarity;
} ca_feedback_signal_rec_t;

/* Deep-free the contents of a signal (not the struct). NULL-safe. */
void ca_feedback_signal_free(ca_feedback_signal_rec_t *sig);
void ca_feedback_signal_free_array(ca_feedback_signal_rec_t *sigs, size_t count);

/* ===========================================================================
 * InMemoryFeedbackStore — FIFO-capped signal store
 * =========================================================================== */

typedef struct ca_feedback_store ca_feedback_store_t;

/* Create a store capped at max_signals (FIFO eviction). max_signals must be > 0,
 * else returns NULL. */
ca_feedback_store_t *ca_feedback_store_create(size_t max_signals);
void                 ca_feedback_store_destroy(ca_feedback_store_t *store);

/* Append a deep copy of sig, evicting the oldest once over capacity. Returns
 * false on a NULL store/sig. */
bool ca_feedback_store_add(ca_feedback_store_t *store, const ca_feedback_signal_rec_t *sig);

size_t ca_feedback_store_count(const ca_feedback_store_t *store);

/* Most-recent count signals, newest-first by recorded_at. Returns a fresh
 * deep-copied array (caller frees with ca_feedback_signal_free_array); *out_count
 * set (0 → NULL). */
ca_feedback_signal_rec_t *ca_feedback_store_get_recent(const ca_feedback_store_t *store,
                                                   int count, size_t *out_count);

/* Fraction of stored signals that are Positive, in [0,1]. Returns false (and
 * leaves *out untouched) when the store is empty — mirrors the C#/TS
 * positiveRatio == null. */
bool ca_feedback_store_positive_ratio(const ca_feedback_store_t *store, double *out);

/* ===========================================================================
 * PersonaAdaptation + FeedbackAnalyser
 * =========================================================================== */

/* Deltas to apply to persona after analysing feedback. verbosity/formality are
 * FP32 to match the C# `float` record fields. preferred_topics is an owned array
 * of owned strings (always empty here — FeedbackSignal carries no topics). */
typedef struct {
    float   verbosity_delta;
    float   formality_delta;
    char  **preferred_topics;  /* owned array of owned strings, or NULL */
    size_t  topic_count;
} ca_persona_adaptation_t;

/* Deep-free the contents of an adaptation (not the struct). NULL-safe. */
void ca_persona_adaptation_free(ca_persona_adaptation_t *a);

typedef struct ca_feedback_analyser ca_feedback_analyser_t;

/* Create an analyser over the most-recent window_size signals. window_size must
 * be >= 1, else returns NULL. */
ca_feedback_analyser_t *ca_feedback_analyser_create(int window_size);
void                    ca_feedback_analyser_destroy(ca_feedback_analyser_t *a);

/*
 * Analyse a set of signals (passed by pointer + count; copied internally so the
 * caller keeps ownership). Fills *out with the adaptation the caller frees with
 * ca_persona_adaptation_free.
 *
 * Rule: take the most-recent window_size signals by recorded_at desc.
 *   >70% negative → verbosity_delta = -0.1f
 *   >70% positive → verbosity_delta = +0.05f
 *   else            verbosity_delta = 0
 * formality_delta always 0; preferred_topics always empty (count 0, NULL).
 * An empty signal set yields all-zero deltas.
 */
void ca_feedback_analyser_analyse(const ca_feedback_analyser_t *a,
                                  const ca_feedback_signal_rec_t *signals, size_t count,
                                  ca_persona_adaptation_t *out);

/* Convenience: analyse the signals currently held by a store. */
void ca_feedback_analyser_analyse_store(const ca_feedback_analyser_t *a,
                                        const ca_feedback_store_t *store,
                                        ca_persona_adaptation_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FEEDBACK_ANALYSER_H */
