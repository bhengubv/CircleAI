#ifndef CIRCLE_AI_COMPANION_REASON_H
#define CIRCLE_AI_COMPANION_REASON_H

/*
 * companion_reason.h — CircleAI companion reasoning core (C11 port).
 *
 * The four HER/Jarvis reasoning contracts plus their in-memory, deterministic
 * implementations, ported 1:1 from the C# reference:
 *
 *   IWorldModel        → FrequencyWorldModel  (frequency P(outcome|obs))
 *                        BayesianWorldModel   (online Naive-Bayes, Laplace smoothing)
 *   IPredictiveEngine  → HistogramPredictiveEngine (time-of-day 24x7 histogram)
 *                        SequencePredictiveEngine  (variable-order Markov chain)
 *   IInnerMonologue    → TemplateInnerMonologue    (narrative-template reflection)
 *                        ReasoningLoopInnerMonologue(reasoning-LLM stream capture)
 *   ITheoryOfMind      → BeliefTrackerTheoryOfMind (bag-of-belief inference)
 *
 * Source files:
 *   HerJarvisContracts.cs          (interfaces + record types 5,10,13,14)
 *   HerJarvisRealImplementations.cs(FrequencyWorldModel, HistogramPredictiveEngine,
 *                                   TemplateInnerMonologue, BeliefTrackerTheoryOfMind)
 *   BayesianWorldModel.cs / SequencePredictiveEngine.cs / ReasoningLoopInnerMonologue.cs
 *
 * Memory ownership follows the SDK contract: owning structs hold strdup'd copies
 * with a matching *_free; returned arrays are deep copies the caller frees.
 * Errors on array-returning calls are signalled with NULL + *out_count == SIZE_MAX
 * (distinct from an empty result of NULL + 0).
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "models.h"     /* ca_chat_message_t */
#include "inference.h"  /* ca_chat_generator_t, ca_chat_fragment_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Record types (contracts 5, 10, 13, 14)
 * =========================================================================== */

/* CausalPrediction(Outcome, Probability, SupportingFactors). */
typedef struct {
    char   *outcome;             /* owned */
    double  probability;
    char  **supporting_factors;  /* owned array of owned strings, or NULL */
    size_t  factor_count;
} ca_causal_prediction_t;

void ca_causal_prediction_free(ca_causal_prediction_t *p);

/* AnticipatedNeed(Description, ExpectedByUtc, Probability).
 * ExpectedByUtc is Unix ms UTC (DateTimeOffset in the C# spec). */
typedef struct {
    char   *description;      /* owned */
    int64_t expected_by_ms;
    double  probability;
} ca_anticipated_need_t;

void ca_anticipated_need_free(ca_anticipated_need_t *n);
void ca_anticipated_need_free_array(ca_anticipated_need_t *arr, size_t count);

/* SelfReflection(Thought, At). At is Unix ms UTC. */
typedef struct {
    char   *thought;   /* owned */
    int64_t at_ms;
} ca_self_reflection_t;

void ca_self_reflection_free(ca_self_reflection_t *r);

/* OtherMindEstimate(TargetIdentifier, LikelyBeliefJson, Confidence). */
typedef struct {
    char   *target_identifier;  /* owned */
    char   *likely_belief_json;  /* owned (JSON object of belief -> weight) */
    double  confidence;
} ca_other_mind_estimate_t;

void ca_other_mind_estimate_free(ca_other_mind_estimate_t *e);

/* ===========================================================================
 * 5a. FrequencyWorldModel — learn P(outcome|observation) from evidence.
 * ===========================================================================
 *
 * Observe(observations, outcome) tallies, per observation (case-insensitive),
 * how often each outcome co-occurred. Predict extracts observations from the
 * scenario JSON object (each property rendered "name=value"), sums the tallies
 * of matching observations, and returns the argmax outcome with probability
 * top/total and the matched observations as supporting factors. With no matched
 * observations it returns ("unknown", 0.5, matched-observation list).
 */

typedef struct ca_frequency_world_model ca_frequency_world_model_t;

ca_frequency_world_model_t *ca_frequency_world_model_create(void);
void ca_frequency_world_model_destroy(ca_frequency_world_model_t *m);

/* Record: when these observations happen, this outcome was seen. observations is
 * an array of count UTF-8 strings. Blank outcome or NULL observations is a no-op
 * (the C# throws; the C port ignores the malformed call). */
void ca_frequency_world_model_observe(ca_frequency_world_model_t *m,
                                      const char *const *observations, size_t count,
                                      const char *outcome);

/* Predict from a scenario JSON object. Writes *out on success and returns true.
 * Returns false only on a NULL model/out. A malformed / non-object scenario
 * yields ("unknown", 0.5, empty). Caller frees *out with ca_causal_prediction_free. */
bool ca_frequency_world_model_predict(const ca_frequency_world_model_t *m,
                                      const char *scenario_json,
                                      ca_causal_prediction_t *out);

/* ===========================================================================
 * 5b. BayesianWorldModel — online Naive-Bayes with Laplace smoothing.
 * ===========================================================================
 *
 * P(outcome|obs) ∝ P(outcome) · ∏ P(obs_i|outcome), Laplace-smoothed by alpha.
 * At predict time every seen outcome is scored by log-posterior; the argmax is
 * returned with a softmax-normalised probability and the extracted observations
 * as supporting factors. Empty observations or an untrained model → ("unknown",
 * 0.5, empty).
 */

typedef struct ca_bayesian_world_model ca_bayesian_world_model_t;

/* laplace_alpha must be > 0 (default 1.0); NULL is returned for alpha <= 0. */
ca_bayesian_world_model_t *ca_bayesian_world_model_create(double laplace_alpha);
void ca_bayesian_world_model_destroy(ca_bayesian_world_model_t *m);

/* One (observations → outcome) training example. Blank observations are skipped;
 * a blank outcome or NULL observations is a no-op. */
void ca_bayesian_world_model_observe(ca_bayesian_world_model_t *m,
                                     const char *const *observations, size_t count,
                                     const char *outcome);

/* Predict. Writes *out and returns true on success, false on NULL model/out.
 * Caller frees *out with ca_causal_prediction_free. */
bool ca_bayesian_world_model_predict(const ca_bayesian_world_model_t *m,
                                     const char *scenario_json,
                                     ca_causal_prediction_t *out);

/* ===========================================================================
 * 14a. HistogramPredictiveEngine — time-of-day 24x7 histogram of needs.
 * ===========================================================================
 *
 * Observe(description, at_ms) bumps the (dayOfWeek*24 + hourUtc) slot for that
 * description. Anticipate sums, per description, the slots reachable within the
 * horizon (stepping 30 min from now, inclusive) and emits needs with probability
 * upcoming/total, expected at now + horizon/2, sorted by probability descending.
 */

typedef struct ca_histogram_predictive_engine ca_histogram_predictive_engine_t;

ca_histogram_predictive_engine_t *ca_histogram_predictive_engine_create(void);
void ca_histogram_predictive_engine_destroy(ca_histogram_predictive_engine_t *e);

/* Record that this need occurred at this Unix-ms UTC time. Blank description is a
 * no-op. */
void ca_histogram_predictive_engine_observe(ca_histogram_predictive_engine_t *e,
                                            const char *description, int64_t at_ms);

/* Anticipate needs over the next horizon_minutes. Returns a fresh array (caller
 * frees with ca_anticipated_need_free_array); *out_count set. Returns NULL + 0
 * for no predictions, or NULL + SIZE_MAX on an error (NULL engine, or
 * horizon_minutes <= 0). now_ms is the "current" Unix-ms UTC instant. */
ca_anticipated_need_t *ca_histogram_predictive_engine_anticipate(
    const ca_histogram_predictive_engine_t *e,
    int horizon_minutes, int64_t now_ms, size_t *out_count);

/* ===========================================================================
 * 14b. SequencePredictiveEngine — variable-order Markov chain over events.
 * ===========================================================================
 *
 * Observe(event, at_ms) appends to the timeline and updates n-gram transition
 * counts up to `order`, plus per-event mean inter-arrival for repeats. Anticipate
 * takes the most-recent `order` events as context, backs off from longest to
 * shortest context weighting longer contexts by 2^k, normalises, and forecasts
 * each candidate at now + its mean inter-arrival (dropping events whose mean
 * interval exceeds the horizon).
 */

typedef struct ca_sequence_predictive_engine ca_sequence_predictive_engine_t;

/* order in [1,6] (default 3); NULL for out-of-range order. */
ca_sequence_predictive_engine_t *ca_sequence_predictive_engine_create(int order);
void ca_sequence_predictive_engine_destroy(ca_sequence_predictive_engine_t *e);

/* Append one event to the user timeline. Blank event is a no-op. */
void ca_sequence_predictive_engine_observe(ca_sequence_predictive_engine_t *e,
                                           const char *event, int64_t at_ms);

/* Anticipate. Same return contract as the histogram engine: NULL+0 empty,
 * NULL+SIZE_MAX error (NULL engine or horizon_minutes <= 0). */
ca_anticipated_need_t *ca_sequence_predictive_engine_anticipate(
    const ca_sequence_predictive_engine_t *e,
    int horizon_minutes, int64_t now_ms, size_t *out_count);

/* ===========================================================================
 * 13a. TemplateInnerMonologue — narrative-template reflection (stateless).
 * ===========================================================================
 *
 * Summarise strips JSON punctuation and keeps the first 12 tokens; InferDirection
 * keys off "error"/"goal"/"user" substrings (case-insensitive, checked in that
 * order); a deterministic hash of the context selects one of three frames. The
 * chosen frame's {summary}/{direction} placeholders are filled.
 *
 * Note: the C# selects the frame via String.GetHashCode(), which is randomised
 * per-process, so its frame choice is non-deterministic. This port uses a fixed
 * FNV-1a hash so the C behaviour is deterministic (all three frames yield the
 * same summary + direction, so the observable reflection differs only in phrasing).
 */

/* Reflect on the context JSON. Writes *out and returns true; returns false only
 * on NULL context/out. Caller frees *out with ca_self_reflection_free. at_ms is
 * stamped onto the reflection. */
bool ca_template_inner_monologue_reflect(const char *context_json, int64_t at_ms,
                                         ca_self_reflection_t *out);

/* ===========================================================================
 * 13b. ReasoningLoopInnerMonologue — reasoning-LLM stream capture.
 * ===========================================================================
 *
 * Drives a fragment-streaming chat generator (ca_chat_generator_t) with the
 * reasoning system prompt + the context turn, accumulating REASONING fragments
 * as the inner monologue and CONTENT fragments as the visible conclusion. The
 * thought prefers the trimmed reasoning trace, else the trimmed content, else
 * "(no inner state)".
 *
 * The generator seam: the caller supplies a driver that, given the built message
 * list and options, invokes the fragment callback once per fragment (kind+text)
 * and returns. This mirrors IChatGenerator.StreamFragmentsAsync without threads.
 */

/* Fragment-stream driver. Invoke `emit(fragment, sink)` once per produced
 * fragment. `user` is the driver's own context. */
typedef void (*ca_reasoning_stream_fn)(
    void *user,
    const ca_chat_message_t *messages, size_t message_count,
    const ca_generation_options_t *options,
    ca_stream_fragment_callback emit, void *sink);

/* The reasoning system prompt used to prime the monologue (borrowed constant). */
const char *ca_reasoning_inner_monologue_system_prompt(void);

/* Reflect using the reasoning stream. Writes *out and returns true; returns false
 * only on NULL driver/context/out. A driver that emits nothing yields the
 * "(no inner state)" fallback. Caller frees *out with ca_self_reflection_free. */
bool ca_reasoning_inner_monologue_reflect(ca_reasoning_stream_fn driver, void *driver_user,
                                          const char *context_json, int64_t at_ms,
                                          ca_self_reflection_t *out);

/* ===========================================================================
 * 10. BeliefTrackerTheoryOfMind — bag-of-belief inference (stateless).
 * ===========================================================================
 *
 * Scans the interaction history for "(thinks|believes|wants|fears|hopes) <claim>"
 * (case-insensitive; claim runs to the next . ; ! or ?). Each match contributes
 * weight (1.0 for believe*, else 0.7) times a positional decay 1/(1+idx*0.1) to
 * its "verb:claim" key. The belief map is serialised to JSON; confidence is
 * min(1, sum/5) (0 when empty).
 */

/* Estimate the target's beliefs. Writes *out and returns true; returns false only
 * on a blank target or NULL history/out. Caller frees *out with
 * ca_other_mind_estimate_free. */
bool ca_belief_tracker_theory_of_mind_estimate(const char *target,
                                               const char *interaction_history_json,
                                               ca_other_mind_estimate_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPANION_REASON_H */
