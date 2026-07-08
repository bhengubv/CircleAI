#ifndef CIRCLE_AI_CONSOLIDATION_H
#define CIRCLE_AI_CONSOLIDATION_H

/*
 * consolidation.h — Hierarchical memory consolidation, the "sleep cycle" engine
 * (C11 port).
 *
 * Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
 * core, and enforces retention. Ported from CircleAI.Memory.Consolidation (C#)
 * and mirroring the verified TypeScript reference (memory/consolidation.ts) 1:1:
 * SleepKind, CoreMemoryKind, the four tier records, the four in-memory stores,
 * a FULL cosine (dot/(‖a‖·‖b‖) — distinct from the episodic dot-only), the
 * HeuristicSummarizer, and the MemoryConsolidator orchestration engine.
 *
 * In-memory only: dynamic arrays (malloc/realloc) + linear search (test data is
 * tiny). Every owning struct holds strdup'd copies with a matching *_free /
 * *_destroy (NULL-safe, no leaks); returned arrays are deep copies the caller
 * frees with the documented helper.
 *
 * C# DateOnly is represented as ca_civil_date_t {year,month,day} with UTC
 * proleptic-Gregorian arithmetic. Time decisions go through an injectable
 * ca_clock_fn (epoch-ms UTC) so tests are deterministic.
 *
 * Reuses ca_episodic_entry_t + ca_episodic_store_t (memory_brain.h) as the
 * episodic source and adds a minimal in-memory persona store.
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "memory_brain.h"   /* ca_episodic_entry_t, ca_episodic_store_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Injectable clock — epoch-milliseconds UTC
 * =========================================================================== */

/* Returns the current time as Unix milliseconds UTC. The user pointer is passed
 * through untouched; a fixed-clock test double ignores it. */
typedef int64_t (*ca_clock_fn)(void *user);

/* The default real-time clock (time(NULL)*1000). user is ignored. */
int64_t ca_clock_real(void *user);

/* ===========================================================================
 * Civil date — C# DateOnly, UTC proleptic Gregorian
 * =========================================================================== */

typedef struct {
    int year;   /* e.g. 2026 */
    int month;  /* 1..12 */
    int day;    /* 1..31 */
} ca_civil_date_t;

/* The UTC calendar day of an epoch-ms instant. */
ca_civil_date_t ca_civil_date_from_ms(int64_t epoch_ms);

/* Lexicographic-equivalent compare: <0 if a<b, 0 if equal, >0 if a>b. */
int  ca_civil_date_compare(ca_civil_date_t a, ca_civil_date_t b);

/* a with `days` (may be negative) added. */
ca_civil_date_t ca_civil_date_add_days(ca_civil_date_t a, int days);

/* The Monday of the week containing d. Monday = d - ((weekday+6)%7) days,
 * with Sunday=0..Saturday=6. */
ca_civil_date_t ca_civil_date_monday_of(ca_civil_date_t d);

/* First day of the month containing d (day set to 1). */
ca_civil_date_t ca_civil_date_month_first(ca_civil_date_t d);

/* Render as "YYYY-MM-DD" into buf (needs >= 11 bytes). Returns buf. */
char *ca_civil_date_to_string(ca_civil_date_t d, char *buf, size_t buf_len);

/* ===========================================================================
 * SleepKind + CoreMemoryKind
 * =========================================================================== */

typedef enum {
    CA_SLEEP_DAILY    = 0,
    CA_SLEEP_WEEKLY   = 1,
    CA_SLEEP_MONTHLY  = 2,
    CA_SLEEP_ONDEMAND = 3
} ca_sleep_kind_t;

typedef enum {
    CA_CORE_USER_ASSERTED   = 0,
    CA_CORE_PATTERN_INFERRED = 1,
    CA_CORE_HIGH_SALIENCE    = 2,
    CA_CORE_HOST_PROVIDED    = 3
} ca_core_memory_kind_t;

/* ===========================================================================
 * Topic-weight map — (label → weight), linear search. Owns its label strings.
 * =========================================================================== */

typedef struct {
    char  **labels;   /* owned array of owned strings */
    double *weights;
    size_t  count, cap;
} ca_topic_weights_t;

/* ===========================================================================
 * Tier records
 * =========================================================================== */

/* Tier-5: a core memory the AI will not forget. */
typedef struct {
    char                 *id;                 /* owned */
    int64_t               created_at_ms;
    int64_t               last_reinforced_ms; /* mutable */
    char                 *statement;          /* owned */
    ca_core_memory_kind_t kind;
    char                 *topic;              /* owned, or NULL */
    float                *embedding;          /* owned, or NULL */
    size_t                embedding_len;
    int                   reinforcement_count; /* mutable */
    char                 *source_memory_id;   /* owned, or NULL */
} ca_core_memory_t;

/* Tier-2: a compressed single-day summary. */
typedef struct {
    char                *id;             /* owned */
    ca_civil_date_t      day;
    int64_t              generated_at_ms;
    char                *summary;        /* owned */
    ca_episodic_entry_t *highlights;     /* owned array (deep copies) */
    size_t               highlight_count;
    int                  episode_count;
    ca_topic_weights_t   topic_weights;  /* owned */
    double               topic_dispersion;
    double               salience;
} ca_daily_summary_t;

/* Tier-3: a topic-coherent cluster of daily summaries. */
typedef struct {
    char           *id;                  /* owned */
    int64_t         generated_at_ms;
    ca_civil_date_t week_starting_monday;
    char           *topic;               /* owned */
    char           *summary;             /* owned */
    float          *centroid_embedding;  /* owned, or NULL */
    size_t          centroid_len;
    char          **source_daily_ids;    /* owned array of owned strings */
    size_t          source_daily_count;
    double          topic_weight;
    double          salience;
} ca_semantic_cluster_t;

/* Tier-4: a persona diff over a consolidation period. */
typedef struct {
    char              *id;                 /* owned */
    int64_t            generated_at_ms;
    ca_civil_date_t    period_start;
    ca_civil_date_t    period_end;
    char              *user_id;            /* owned */
    char              *verbosity_before;   /* owned */
    char              *verbosity_after;    /* owned */
    char              *formality_before;   /* owned */
    char              *formality_after;    /* owned */
    ca_topic_weights_t new_topics;         /* owned */
    ca_topic_weights_t strengthened_topics;/* owned */
    char             **newly_disfavoured;  /* owned array of owned strings */
    size_t             newly_disfavoured_count;
    int                net_signal_delta;
    int                interactions_in_period;
    char              *narrative;          /* owned */
} ca_persona_delta_t;

/* Deep-free the contents of a record (not the struct pointer). NULL-safe. */
void ca_core_memory_free(ca_core_memory_t *m);
void ca_daily_summary_free(ca_daily_summary_t *d);
void ca_semantic_cluster_free(ca_semantic_cluster_t *c);
void ca_persona_delta_free(ca_persona_delta_t *p);

void ca_core_memory_free_array(ca_core_memory_t *arr, size_t count);
void ca_daily_summary_free_array(ca_daily_summary_t *arr, size_t count);
void ca_semantic_cluster_free_array(ca_semantic_cluster_t *arr, size_t count);
void ca_persona_delta_free_array(ca_persona_delta_t *arr, size_t count);

/* Borrowed lookup of a topic weight by label (case-insensitive). Returns true
 * and sets *out when present. */
bool ca_topic_weights_get(const ca_topic_weights_t *tw, const char *label, double *out);
size_t ca_topic_weights_count(const ca_topic_weights_t *tw);

/* ===========================================================================
 * PersonaState — minimal in-memory persona the consolidator reads
 * ===========================================================================
 *
 * Mirrors the fields the C#/TS PersonaState exposes that consolidation touches.
 */

/* NB: distinct from memory.h's minimal ca_persona_state_t (verbosity/formality
 * enums). This is the rich consolidation persona mirroring the C#/TS PersonaState
 * class — topic weights, signal counts, disfavoured topics. */
typedef struct {
    char              *user_id;            /* owned */
    int64_t            last_updated_ms;
    char              *verbosity;          /* owned; default "balanced" */
    char              *formality;          /* owned; default "neutral" */
    char              *preferred_locale;   /* owned, or NULL */
    ca_topic_weights_t topic_weights;      /* owned */
    char             **disfavoured_topics; /* owned array of owned strings */
    size_t             disfavoured_count;
    int                total_interactions;
    int                positive_signals;
    int                negative_signals;
} ca_consolidation_persona_t;

/* Allocate a fresh persona with C#/TS defaults (verbosity "balanced",
 * formality "neutral", the given user id — NULL → "default"). Caller frees with
 * ca_consolidation_persona_destroy. */
ca_consolidation_persona_t *ca_consolidation_persona_create(const char *user_id);
void                        ca_consolidation_persona_destroy(ca_consolidation_persona_t *p);

/* Set a topic weight (upsert, case-insensitive label). */
void ca_consolidation_persona_set_topic(ca_consolidation_persona_t *p, const char *label, double weight);
/* Append a disfavoured topic (no dedup — mirrors the sets built in tests). */
void ca_consolidation_persona_add_disfavoured(ca_consolidation_persona_t *p, const char *label);

/* Minimal in-memory persona store keyed by user id. */
typedef struct ca_persona_store ca_persona_store_t;

ca_persona_store_t *ca_persona_store_create(void);
void                ca_persona_store_destroy(ca_persona_store_t *store);

/* Save a deep copy of persona (replaces any existing entry for its user id). */
void ca_persona_store_save(ca_persona_store_t *store, const ca_consolidation_persona_t *persona);

/* Load the persona for user_id as a fresh deep copy (caller frees with
 * ca_consolidation_persona_destroy). Returns a fresh DEFAULT persona when none stored. */
ca_consolidation_persona_t *ca_persona_store_load(const ca_persona_store_t *store, const char *user_id);

/* ===========================================================================
 * ConsolidationOutcome + options
 * =========================================================================== */

typedef struct {
    ca_sleep_kind_t kind;
    int             daily_summaries_produced;
    int             semantic_clusters_produced;
    int             persona_deltas_produced;
    int             core_promotions;
    int             episodes_pruned;
    int             dailies_pruned;
    int             semantics_pruned;
    int64_t         ran_at_ms;
} ca_consolidation_outcome_t;

typedef struct {
    int    episodic_retention_days;       /* 0 → default 7 */
    int    daily_retention_days;          /* 0 → default 30 */
    int    semantic_retention_days;       /* 0 → default 365 */
    double daily_core_promotion_threshold;/* 0 → default 0.80 */
    double weekly_core_promotion_threshold;/* 0 → default 0.75 */
} ca_consolidation_options_t;

/* ===========================================================================
 * Full cosine — dot/(‖a‖·‖b‖). 0 on length mismatch or near-zero denominator.
 * Distinct from the episodic store's dot-only cosine (both are kept).
 * =========================================================================== */

double ca_cosine_full(const float *a, size_t alen, const float *b, size_t blen);

/* ===========================================================================
 * Tier stores (in-memory)
 * =========================================================================== */

/* --- Tier-2: daily summaries, keyed by day (upsert replaces same-day). --- */
typedef struct ca_daily_store ca_daily_store_t;

ca_daily_store_t *ca_daily_store_create(void);
void              ca_daily_store_destroy(ca_daily_store_t *store);

/* Upsert a deep copy of summary (replaces any existing entry for its day). */
void ca_daily_store_upsert(ca_daily_store_t *store, const ca_daily_summary_t *summary);
/* Fetch the summary for day into *out (deep copy). Returns true if found. */
bool ca_daily_store_get(const ca_daily_store_t *store, ca_civil_date_t day,
                        ca_daily_summary_t *out);
/* All summaries with from<=day<=to, ascending by day (deep copies). */
ca_daily_summary_t *ca_daily_store_get_range(const ca_daily_store_t *store,
                                             ca_civil_date_t from_inclusive,
                                             ca_civil_date_t to_inclusive,
                                             size_t *out_count);
/* Remove summaries strictly before cutoff. Returns count removed. */
int    ca_daily_store_prune_older_than(ca_daily_store_t *store, ca_civil_date_t cutoff);
size_t ca_daily_store_count(const ca_daily_store_t *store);

/* --- Tier-3: semantic clusters. --- */
typedef struct ca_semantic_store ca_semantic_store_t;

ca_semantic_store_t *ca_semantic_store_create(void);
void                 ca_semantic_store_destroy(ca_semantic_store_t *store);

/* Add a deep copy of cluster. */
void ca_semantic_store_add(ca_semantic_store_t *store, const ca_semantic_cluster_t *cluster);
/* Clusters for the given week, topic_weight desc (deep copies). */
ca_semantic_cluster_t *ca_semantic_store_get_week(const ca_semantic_store_t *store,
                                                  ca_civil_date_t week_starting_monday,
                                                  size_t *out_count);
/* Top-top_k by centroid full-cosine to query; recency fallback when query NULL.
 * top_k <= 0 → default 5. Deep copies. */
ca_semantic_cluster_t *ca_semantic_store_search(const ca_semantic_store_t *store,
                                                const float *query, size_t query_len,
                                                int top_k, size_t *out_count);
/* Remove clusters whose week start is strictly before cutoff. Returns removed. */
int    ca_semantic_store_prune_older_than(ca_semantic_store_t *store, ca_civil_date_t cutoff);
size_t ca_semantic_store_count(const ca_semantic_store_t *store);

/* --- Tier-4: persona-delta snapshots (retained forever). --- */
typedef struct ca_persona_delta_store ca_persona_delta_store_t;

ca_persona_delta_store_t *ca_persona_delta_store_create(void);
void                      ca_persona_delta_store_destroy(ca_persona_delta_store_t *store);

/* Add a deep copy of snapshot. */
void ca_persona_delta_store_add(ca_persona_delta_store_t *store, const ca_persona_delta_t *snapshot);
/* All snapshots for user_id, ascending by period_start (deep copies). */
ca_persona_delta_t *ca_persona_delta_store_get_for_user(const ca_persona_delta_store_t *store,
                                                        const char *user_id, size_t *out_count);
size_t ca_persona_delta_store_count(const ca_persona_delta_store_t *store);

/* --- Tier-5: core memories. --- */
typedef struct ca_core_store ca_core_store_t;

ca_core_store_t *ca_core_store_create(void);
void             ca_core_store_destroy(ca_core_store_t *store);

/* Add a deep copy of memory. */
void ca_core_store_add(ca_core_store_t *store, const ca_core_memory_t *memory);
/* Fetch by id into *out (deep copy). Returns true if found. */
bool ca_core_store_get(const ca_core_store_t *store, const char *id, ca_core_memory_t *out);
/* Top-top_k by embedding full-cosine; reinforcement-order fallback when query
 * NULL. top_k <= 0 → default 5. Deep copies. */
ca_core_memory_t *ca_core_store_search(const ca_core_store_t *store,
                                       const float *query, size_t query_len,
                                       int top_k, size_t *out_count);
/* All memories in reinforcement order (most reinforced first). Deep copies. */
ca_core_memory_t *ca_core_store_list_all(const ca_core_store_t *store, size_t *out_count);
/* Increment reinforcement_count and bump last_reinforced. No-op when unknown. */
void   ca_core_store_reinforce(ca_core_store_t *store, const char *id);
/* Remove a memory by id. Returns true if one was removed. */
bool   ca_core_store_remove(ca_core_store_t *store, const char *id);
size_t ca_core_store_count(const ca_core_store_t *store);

/* ===========================================================================
 * Heuristic summarizer
 * ===========================================================================
 *
 * Produces the text + scores for each tier from structural signals only (no
 * LLM). Formulas identical to the C# HeuristicSummarizer.
 */

typedef struct ca_heuristic_summarizer ca_heuristic_summarizer_t;

/* Create a summarizer. highlight_count <= 0 → default 5;
 * min_days_per_topic <= 0 → default 2. clock NULL → real time; clock_user is
 * passed through. */
ca_heuristic_summarizer_t *ca_heuristic_summarizer_create(int highlight_count,
                                                          int min_days_per_topic,
                                                          ca_clock_fn clock,
                                                          void *clock_user);
void ca_heuristic_summarizer_destroy(ca_heuristic_summarizer_t *s);

/* Produce a daily summary from the day's episodic entries (into *out, owned by
 * the caller — free with ca_daily_summary_free). entries may be empty. */
void ca_heuristic_summarizer_summarize_day(const ca_heuristic_summarizer_t *s,
                                           ca_civil_date_t day,
                                           const ca_episodic_entry_t *entries,
                                           size_t entry_count,
                                           ca_daily_summary_t *out);

/* Produce zero or more clusters from a week's daily summaries. Returns a fresh
 * array (caller frees with ca_semantic_cluster_free_array); *out_count set. */
ca_semantic_cluster_t *ca_heuristic_summarizer_consolidate_week(
    const ca_heuristic_summarizer_t *s,
    ca_civil_date_t week_starting_monday,
    const ca_daily_summary_t *days_in_week, size_t day_count,
    size_t *out_count);

/* Compute the persona delta across a period (into *out, owned by the caller —
 * free with ca_persona_delta_free). */
void ca_heuristic_summarizer_derive_persona_delta(
    const ca_heuristic_summarizer_t *s,
    const ca_consolidation_persona_t *before, const ca_consolidation_persona_t *after,
    const ca_daily_summary_t *days_in_period, size_t day_count,
    ca_persona_delta_t *out);

/* ===========================================================================
 * MemoryConsolidator — the tick engine
 * =========================================================================== */

typedef struct ca_memory_consolidator ca_memory_consolidator_t;

/* Create a consolidator over the stores (all borrowed — the caller keeps them
 * alive). opts may be NULL (defaults; any zero field falls back to its
 * default). clock NULL → real time. user_id NULL → "default". */
ca_memory_consolidator_t *ca_memory_consolidator_create(
    ca_episodic_store_t *episodic /* borrowed */,
    ca_daily_store_t *daily /* borrowed */,
    ca_semantic_store_t *semantic /* borrowed */,
    ca_persona_delta_store_t *persona_delta /* borrowed */,
    ca_core_store_t *core /* borrowed */,
    ca_persona_store_t *persona_store /* borrowed */,
    ca_heuristic_summarizer_t *summarizer /* borrowed */,
    const ca_consolidation_options_t *opts,
    ca_clock_fn clock, void *clock_user,
    const char *user_id);
void ca_memory_consolidator_destroy(ca_memory_consolidator_t *c);

/* Run the consolidation pass for kind (OnDemand runs every tier with work
 * pending). Fills *out with the breakdown of what was produced and pruned. */
void ca_memory_consolidator_tick(ca_memory_consolidator_t *c, ca_sleep_kind_t kind,
                                 ca_consolidation_outcome_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CONSOLIDATION_H */
