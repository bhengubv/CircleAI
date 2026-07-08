/*
 * consolidation.c — Hierarchical memory consolidation (C11 port).
 *
 * Ported from CircleAI.Memory.Consolidation (C#) and mirroring the verified
 * TypeScript reference (memory/consolidation.ts) 1:1. In-memory only: dynamic
 * arrays + linear search. Every owning struct has a matching free/destroy;
 * returned arrays are deep copies. Pure C11 + libc, links -lm.
 *
 * Formulas/constants match the C# HeuristicSummarizer + MemoryConsolidator
 * exactly (daily salience, dispersion, topic concentration, highlight proxy,
 * cluster salience, persona delta, retention windows, promotion thresholds).
 */

#include "circle_ai/consolidation.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <time.h>
#include <math.h>
#include <float.h>
#include <limits.h>

/* ===========================================================================
 * Small shared helpers
 * =========================================================================== */

static char *cx_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* A generated id: a monotonically increasing counter formatted as a string.
 * Distinct within a process run, which is all the reference relies on (ids are
 * opaque handles; the tests never assert their format). */
static char *cx_new_id(void) {
    static uint64_t counter = 0;
    counter++;
    char buf[32];
    snprintf(buf, sizeof(buf), "cm-%llu", (unsigned long long)counter);
    return cx_dup(buf);
}

static double cx_clamp(double x, double lo, double hi) {
    return x < lo ? lo : (x > hi ? hi : x);
}

/* ASCII lower of a freshly-duplicated, trimmed copy of s. Returns NULL on a
 * blank/empty result. */
static char *cx_trim_lower_dup(const char *s) {
    if (!s) return NULL;
    const char *start = s;
    while (*start && isspace((unsigned char)*start)) ++start;
    const char *end = s + strlen(s);
    while (end > start && isspace((unsigned char)*(end - 1))) --end;
    if (end == start) return NULL;
    size_t len = (size_t)(end - start);
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    for (size_t i = 0; i < len; ++i) out[i] = (char)tolower((unsigned char)start[i]);
    out[len] = '\0';
    return out;
}

static bool cx_eq_ci(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

static float *cx_dup_floats(const float *src, size_t n) {
    if (!src || n == 0) return NULL;
    float *p = (float *)malloc(n * sizeof(float));
    if (p) memcpy(p, src, n * sizeof(float));
    return p;
}

static char **cx_dup_str_array(char *const *src, size_t n) {
    if (n == 0) return NULL;
    char **out = (char **)calloc(n, sizeof(char *));
    if (!out) return NULL;
    for (size_t i = 0; i < n; ++i) out[i] = cx_dup(src[i]);
    return out;
}

static void cx_free_str_array(char **arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * Clock
 * =========================================================================== */

int64_t ca_clock_real(void *user) {
    (void)user;
    return (int64_t)time(NULL) * 1000;
}

static int64_t cx_now(ca_clock_fn clock, void *user) {
    return clock ? clock(user) : ca_clock_real(user);
}

/* ===========================================================================
 * Civil date — UTC proleptic Gregorian (Howard Hinnant's algorithms)
 * =========================================================================== */

/* Days from 1970-01-01 to y-m-d (proleptic Gregorian). */
static int64_t cx_days_from_civil(int y, int m, int d) {
    int64_t yy = y;
    yy -= (m <= 2);
    int64_t era = (yy >= 0 ? yy : yy - 399) / 400;
    int64_t yoe = yy - era * 400;                                  /* [0,399] */
    int64_t doy = (153 * (int64_t)(m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1; /* [0,365] */
    int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;           /* [0,146096] */
    return era * 146097 + doe - 719468;
}

/* Inverse: days since 1970-01-01 → civil y-m-d. */
static ca_civil_date_t cx_civil_from_days(int64_t z) {
    z += 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    int64_t doe = z - era * 146097;                               /* [0,146096] */
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; /* [0,399] */
    int64_t y = yoe + era * 400;
    int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);        /* [0,365] */
    int64_t mp = (5 * doy + 2) / 153;                             /* [0,11] */
    int64_t d = doy - (153 * mp + 2) / 5 + 1;                     /* [1,31] */
    int64_t m = mp + (mp < 10 ? 3 : -9);                          /* [1,12] */
    ca_civil_date_t out;
    out.year = (int)(y + (m <= 2));
    out.month = (int)m;
    out.day = (int)d;
    return out;
}

/* Day-of-week: Sun=0..Sat=6. */
static int cx_weekday(ca_civil_date_t d) {
    int64_t z = cx_days_from_civil(d.year, d.month, d.day);
    int64_t w = (z + 4) % 7;               /* 1970-01-01 was a Thursday (=4) */
    return (int)(w < 0 ? w + 7 : w);
}

ca_civil_date_t ca_civil_date_from_ms(int64_t epoch_ms) {
    /* Floor division to days (handles negatives correctly). */
    int64_t days = epoch_ms / 86400000;
    if (epoch_ms < 0 && epoch_ms % 86400000 != 0) days -= 1;
    return cx_civil_from_days(days);
}

int ca_civil_date_compare(ca_civil_date_t a, ca_civil_date_t b) {
    if (a.year != b.year) return a.year < b.year ? -1 : 1;
    if (a.month != b.month) return a.month < b.month ? -1 : 1;
    if (a.day != b.day) return a.day < b.day ? -1 : 1;
    return 0;
}

ca_civil_date_t ca_civil_date_add_days(ca_civil_date_t a, int days) {
    int64_t z = cx_days_from_civil(a.year, a.month, a.day) + days;
    return cx_civil_from_days(z);
}

ca_civil_date_t ca_civil_date_monday_of(ca_civil_date_t d) {
    int dow = cx_weekday(d);           /* Sun=0..Sat=6 */
    int delta = (dow + 6) % 7;         /* Sun=0..Sat=6 → Mon=0..Sun=6 */
    return ca_civil_date_add_days(d, -delta);
}

ca_civil_date_t ca_civil_date_month_first(ca_civil_date_t d) {
    ca_civil_date_t out = d;
    out.day = 1;
    return out;
}

char *ca_civil_date_to_string(ca_civil_date_t d, char *buf, size_t buf_len) {
    snprintf(buf, buf_len, "%04d-%02d-%02d", d.year, d.month, d.day);
    return buf;
}

/* ===========================================================================
 * Cosine (full) — dot/(‖a‖·‖b‖)
 * =========================================================================== */

double ca_cosine_full(const float *a, size_t alen, const float *b, size_t blen) {
    if (alen != blen) return 0.0;
    double dot = 0, magA = 0, magB = 0;
    for (size_t i = 0; i < alen; ++i) {
        dot += (double)a[i] * (double)b[i];
        magA += (double)a[i] * (double)a[i];
        magB += (double)b[i] * (double)b[i];
    }
    double denom = sqrt(magA) * sqrt(magB);
    return denom < DBL_EPSILON ? 0.0 : dot / denom;
}

/* ===========================================================================
 * Topic-weight map
 * =========================================================================== */

/* Accumulate weight onto label (case-insensitive); appends when new. */
static void cx_tw_accumulate(ca_topic_weights_t *tw, const char *label, double weight) {
    char *key = cx_trim_lower_dup(label);
    if (!key) return;
    for (size_t i = 0; i < tw->count; ++i) {
        if (strcmp(tw->labels[i], key) == 0) {  /* labels are already lowered */
            tw->weights[i] += weight;
            free(key);
            return;
        }
    }
    if (tw->count == tw->cap) {
        size_t nc = tw->cap ? tw->cap * 2 : 8;
        char **nl = (char **)realloc(tw->labels, nc * sizeof(char *));
        double *nw = (double *)realloc(tw->weights, nc * sizeof(double));
        if (nl) tw->labels = nl;
        if (nw) tw->weights = nw;
        if (!nl || !nw) { free(key); return; }
        tw->cap = nc;
    }
    tw->labels[tw->count] = key;
    tw->weights[tw->count] = weight;
    tw->count++;
}

/* Set (upsert) a label to an exact weight. Used by PersonaState. */
static void cx_tw_set(ca_topic_weights_t *tw, const char *label, double weight) {
    char *key = cx_trim_lower_dup(label);
    if (!key) return;
    for (size_t i = 0; i < tw->count; ++i) {
        if (strcmp(tw->labels[i], key) == 0) { tw->weights[i] = weight; free(key); return; }
    }
    if (tw->count == tw->cap) {
        size_t nc = tw->cap ? tw->cap * 2 : 8;
        char **nl = (char **)realloc(tw->labels, nc * sizeof(char *));
        double *nw = (double *)realloc(tw->weights, nc * sizeof(double));
        if (nl) tw->labels = nl;
        if (nw) tw->weights = nw;
        if (!nl || !nw) { free(key); return; }
        tw->cap = nc;
    }
    tw->labels[tw->count] = key;
    tw->weights[tw->count] = weight;
    tw->count++;
}

static void cx_tw_free(ca_topic_weights_t *tw) {
    if (!tw) return;
    for (size_t i = 0; i < tw->count; ++i) free(tw->labels[i]);
    free(tw->labels);
    free(tw->weights);
    tw->labels = NULL; tw->weights = NULL; tw->count = tw->cap = 0;
}

static void cx_tw_copy(ca_topic_weights_t *dst, const ca_topic_weights_t *src) {
    memset(dst, 0, sizeof(*dst));
    if (src->count == 0) return;
    dst->labels = (char **)calloc(src->count, sizeof(char *));
    dst->weights = (double *)malloc(src->count * sizeof(double));
    if (!dst->labels || !dst->weights) { free(dst->labels); free(dst->weights); memset(dst, 0, sizeof(*dst)); return; }
    for (size_t i = 0; i < src->count; ++i) {
        dst->labels[i] = cx_dup(src->labels[i]);
        dst->weights[i] = src->weights[i];
    }
    dst->count = dst->cap = src->count;
}

bool ca_topic_weights_get(const ca_topic_weights_t *tw, const char *label, double *out) {
    if (!tw || !label) return false;
    for (size_t i = 0; i < tw->count; ++i) {
        if (cx_eq_ci(tw->labels[i], label)) { if (out) *out = tw->weights[i]; return true; }
    }
    return false;
}

size_t ca_topic_weights_count(const ca_topic_weights_t *tw) {
    return tw ? tw->count : 0;
}

/* ===========================================================================
 * Episodic entry helpers (deep copy / embedding predicate)
 * =========================================================================== */

/* memory_brain.c owns ca_episodic_entry_free/_free_array; we deep-copy here. */
static void cx_episodic_copy(ca_episodic_entry_t *dst, const ca_episodic_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cx_dup(src->id);
    dst->recorded_at_ms = src->recorded_at_ms;
    dst->user_text = cx_dup(src->user_text);
    dst->assistant_text = cx_dup(src->assistant_text);
    dst->app_context = cx_dup(src->app_context);
    dst->embedding = cx_dup_floats(src->embedding, src->embedding_len);
    if (dst->embedding) dst->embedding_len = src->embedding_len;
}

static bool cx_has_embedding(const ca_episodic_entry_t *e) {
    return e->embedding != NULL && e->embedding_len > 0;
}

/* ===========================================================================
 * Tier record free helpers
 * =========================================================================== */

void ca_core_memory_free(ca_core_memory_t *m) {
    if (!m) return;
    free(m->id);
    free(m->statement);
    free(m->topic);
    free(m->embedding);
    free(m->source_memory_id);
    memset(m, 0, sizeof(*m));
}

void ca_daily_summary_free(ca_daily_summary_t *d) {
    if (!d) return;
    free(d->id);
    free(d->summary);
    if (d->highlights) {
        for (size_t i = 0; i < d->highlight_count; ++i) ca_episodic_entry_free(&d->highlights[i]);
        free(d->highlights);
    }
    cx_tw_free(&d->topic_weights);
    memset(d, 0, sizeof(*d));
}

void ca_semantic_cluster_free(ca_semantic_cluster_t *c) {
    if (!c) return;
    free(c->id);
    free(c->topic);
    free(c->summary);
    free(c->centroid_embedding);
    cx_free_str_array(c->source_daily_ids, c->source_daily_count);
    memset(c, 0, sizeof(*c));
}

void ca_persona_delta_free(ca_persona_delta_t *p) {
    if (!p) return;
    free(p->id);
    free(p->user_id);
    free(p->verbosity_before);
    free(p->verbosity_after);
    free(p->formality_before);
    free(p->formality_after);
    cx_tw_free(&p->new_topics);
    cx_tw_free(&p->strengthened_topics);
    cx_free_str_array(p->newly_disfavoured, p->newly_disfavoured_count);
    free(p->narrative);
    memset(p, 0, sizeof(*p));
}

void ca_core_memory_free_array(ca_core_memory_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_core_memory_free(&arr[i]);
    free(arr);
}
void ca_daily_summary_free_array(ca_daily_summary_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_daily_summary_free(&arr[i]);
    free(arr);
}
void ca_semantic_cluster_free_array(ca_semantic_cluster_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_semantic_cluster_free(&arr[i]);
    free(arr);
}
void ca_persona_delta_free_array(ca_persona_delta_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_persona_delta_free(&arr[i]);
    free(arr);
}

/* ── deep copies of tier records ── */

static void cx_core_copy(ca_core_memory_t *dst, const ca_core_memory_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cx_dup(src->id);
    dst->created_at_ms = src->created_at_ms;
    dst->last_reinforced_ms = src->last_reinforced_ms;
    dst->statement = cx_dup(src->statement);
    dst->kind = src->kind;
    dst->topic = cx_dup(src->topic);
    dst->embedding = cx_dup_floats(src->embedding, src->embedding_len);
    if (dst->embedding) dst->embedding_len = src->embedding_len;
    dst->reinforcement_count = src->reinforcement_count;
    dst->source_memory_id = cx_dup(src->source_memory_id);
}

static void cx_daily_copy(ca_daily_summary_t *dst, const ca_daily_summary_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cx_dup(src->id);
    dst->day = src->day;
    dst->generated_at_ms = src->generated_at_ms;
    dst->summary = cx_dup(src->summary);
    if (src->highlight_count > 0) {
        dst->highlights = (ca_episodic_entry_t *)calloc(src->highlight_count, sizeof(ca_episodic_entry_t));
        if (dst->highlights) {
            for (size_t i = 0; i < src->highlight_count; ++i)
                cx_episodic_copy(&dst->highlights[i], &src->highlights[i]);
            dst->highlight_count = src->highlight_count;
        }
    }
    dst->episode_count = src->episode_count;
    cx_tw_copy(&dst->topic_weights, &src->topic_weights);
    dst->topic_dispersion = src->topic_dispersion;
    dst->salience = src->salience;
}

static void cx_semantic_copy(ca_semantic_cluster_t *dst, const ca_semantic_cluster_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cx_dup(src->id);
    dst->generated_at_ms = src->generated_at_ms;
    dst->week_starting_monday = src->week_starting_monday;
    dst->topic = cx_dup(src->topic);
    dst->summary = cx_dup(src->summary);
    dst->centroid_embedding = cx_dup_floats(src->centroid_embedding, src->centroid_len);
    if (dst->centroid_embedding) dst->centroid_len = src->centroid_len;
    dst->source_daily_ids = cx_dup_str_array(src->source_daily_ids, src->source_daily_count);
    if (dst->source_daily_ids) dst->source_daily_count = src->source_daily_count;
    dst->topic_weight = src->topic_weight;
    dst->salience = src->salience;
}

static void cx_persona_delta_copy(ca_persona_delta_t *dst, const ca_persona_delta_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = cx_dup(src->id);
    dst->generated_at_ms = src->generated_at_ms;
    dst->period_start = src->period_start;
    dst->period_end = src->period_end;
    dst->user_id = cx_dup(src->user_id);
    dst->verbosity_before = cx_dup(src->verbosity_before);
    dst->verbosity_after = cx_dup(src->verbosity_after);
    dst->formality_before = cx_dup(src->formality_before);
    dst->formality_after = cx_dup(src->formality_after);
    cx_tw_copy(&dst->new_topics, &src->new_topics);
    cx_tw_copy(&dst->strengthened_topics, &src->strengthened_topics);
    dst->newly_disfavoured = cx_dup_str_array(src->newly_disfavoured, src->newly_disfavoured_count);
    if (dst->newly_disfavoured) dst->newly_disfavoured_count = src->newly_disfavoured_count;
    dst->net_signal_delta = src->net_signal_delta;
    dst->interactions_in_period = src->interactions_in_period;
    dst->narrative = cx_dup(src->narrative);
}

/* ===========================================================================
 * PersonaState + persona store
 * =========================================================================== */

ca_consolidation_persona_t *ca_consolidation_persona_create(const char *user_id) {
    ca_consolidation_persona_t *p = (ca_consolidation_persona_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->user_id = cx_dup(user_id ? user_id : "default");
    p->verbosity = cx_dup("balanced");
    p->formality = cx_dup("neutral");
    p->preferred_locale = NULL;
    return p;
}

void ca_consolidation_persona_destroy(ca_consolidation_persona_t *p) {
    if (!p) return;
    free(p->user_id);
    free(p->verbosity);
    free(p->formality);
    free(p->preferred_locale);
    cx_tw_free(&p->topic_weights);
    cx_free_str_array(p->disfavoured_topics, p->disfavoured_count);
    free(p);
}

void ca_consolidation_persona_set_topic(ca_consolidation_persona_t *p, const char *label, double weight) {
    if (!p) return;
    cx_tw_set(&p->topic_weights, label, weight);
}

void ca_consolidation_persona_add_disfavoured(ca_consolidation_persona_t *p, const char *label) {
    if (!p || !label) return;
    char **n = (char **)realloc(p->disfavoured_topics, (p->disfavoured_count + 1) * sizeof(char *));
    if (!n) return;
    p->disfavoured_topics = n;
    p->disfavoured_topics[p->disfavoured_count++] = cx_dup(label);
}

static ca_consolidation_persona_t *cx_persona_copy(const ca_consolidation_persona_t *src) {
    ca_consolidation_persona_t *p = (ca_consolidation_persona_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->user_id = cx_dup(src->user_id);
    p->last_updated_ms = src->last_updated_ms;
    p->verbosity = cx_dup(src->verbosity);
    p->formality = cx_dup(src->formality);
    p->preferred_locale = cx_dup(src->preferred_locale);
    cx_tw_copy(&p->topic_weights, &src->topic_weights);
    p->disfavoured_topics = cx_dup_str_array(src->disfavoured_topics, src->disfavoured_count);
    if (p->disfavoured_topics) p->disfavoured_count = src->disfavoured_count;
    p->total_interactions = src->total_interactions;
    p->positive_signals = src->positive_signals;
    p->negative_signals = src->negative_signals;
    return p;
}

struct ca_persona_store {
    ca_consolidation_persona_t **items;   /* owned deep copies */
    size_t               count, cap;
};

ca_persona_store_t *ca_persona_store_create(void) {
    return (ca_persona_store_t *)calloc(1, sizeof(struct ca_persona_store));
}

void ca_persona_store_destroy(ca_persona_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_consolidation_persona_destroy(store->items[i]);
    free(store->items);
    free(store);
}

void ca_persona_store_save(ca_persona_store_t *store, const ca_consolidation_persona_t *persona) {
    if (!store || !persona) return;
    for (size_t i = 0; i < store->count; ++i) {
        if (cx_eq_ci(store->items[i]->user_id, persona->user_id)) {
            ca_consolidation_persona_destroy(store->items[i]);
            store->items[i] = cx_persona_copy(persona);
            return;
        }
    }
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 4;
        ca_consolidation_persona_t **n = (ca_consolidation_persona_t **)realloc(store->items, nc * sizeof(*n));
        if (!n) return;
        store->items = n; store->cap = nc;
    }
    store->items[store->count++] = cx_persona_copy(persona);
}

ca_consolidation_persona_t *ca_persona_store_load(const ca_persona_store_t *store, const char *user_id) {
    if (store) {
        for (size_t i = 0; i < store->count; ++i) {
            if (cx_eq_ci(store->items[i]->user_id, user_id)) return cx_persona_copy(store->items[i]);
        }
    }
    return ca_consolidation_persona_create(user_id);  /* fresh default */
}

/* ===========================================================================
 * Tier-2 store: daily summaries
 * =========================================================================== */

struct ca_daily_store {
    ca_daily_summary_t *items;   /* owned */
    size_t              count, cap;
};

ca_daily_store_t *ca_daily_store_create(void) {
    return (ca_daily_store_t *)calloc(1, sizeof(struct ca_daily_store));
}

void ca_daily_store_destroy(ca_daily_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_daily_summary_free(&store->items[i]);
    free(store->items);
    free(store);
}

void ca_daily_store_upsert(ca_daily_store_t *store, const ca_daily_summary_t *summary) {
    if (!store || !summary) return;
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_civil_date_compare(store->items[i].day, summary->day) == 0) {
            ca_daily_summary_free(&store->items[i]);
            cx_daily_copy(&store->items[i], summary);
            return;
        }
    }
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 8;
        ca_daily_summary_t *n = (ca_daily_summary_t *)realloc(store->items, nc * sizeof(*n));
        if (!n) return;
        store->items = n; store->cap = nc;
    }
    cx_daily_copy(&store->items[store->count], summary);
    store->count++;
}

bool ca_daily_store_get(const ca_daily_store_t *store, ca_civil_date_t day,
                        ca_daily_summary_t *out) {
    if (!store || !out) return false;
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_civil_date_compare(store->items[i].day, day) == 0) {
            cx_daily_copy(out, &store->items[i]);
            return true;
        }
    }
    return false;
}

ca_daily_summary_t *ca_daily_store_get_range(const ca_daily_store_t *store,
                                             ca_civil_date_t from_inclusive,
                                             ca_civil_date_t to_inclusive,
                                             size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    /* Collect matching indices, then insertion-sort ascending by day. */
    size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
    if (!idx) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i) {
        ca_civil_date_t d = store->items[i].day;
        if (ca_civil_date_compare(d, from_inclusive) >= 0 &&
            ca_civil_date_compare(d, to_inclusive) <= 0) {
            idx[n++] = i;
        }
    }
    if (n == 0) { free(idx); return NULL; }
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && ca_civil_date_compare(store->items[idx[j - 1]].day,
                                              store->items[key].day) > 0) {
            idx[j] = idx[j - 1]; j--;
        }
        idx[j] = key;
    }
    ca_daily_summary_t *out = (ca_daily_summary_t *)calloc(n, sizeof(*out));
    if (out) {
        for (size_t i = 0; i < n; ++i) cx_daily_copy(&out[i], &store->items[idx[i]]);
        if (out_count) *out_count = n;
    }
    free(idx);
    return out;
}

int ca_daily_store_prune_older_than(ca_daily_store_t *store, ca_civil_date_t cutoff) {
    if (!store) return 0;
    size_t w = 0; int removed = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_civil_date_compare(store->items[i].day, cutoff) < 0) {
            ca_daily_summary_free(&store->items[i]);
            removed++;
        } else {
            if (w != i) store->items[w] = store->items[i];
            w++;
        }
    }
    store->count = w;
    return removed;
}

size_t ca_daily_store_count(const ca_daily_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * Tier-3 store: semantic clusters
 * =========================================================================== */

struct ca_semantic_store {
    ca_semantic_cluster_t *items;  /* owned */
    size_t                 count, cap;
};

ca_semantic_store_t *ca_semantic_store_create(void) {
    return (ca_semantic_store_t *)calloc(1, sizeof(struct ca_semantic_store));
}

void ca_semantic_store_destroy(ca_semantic_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_semantic_cluster_free(&store->items[i]);
    free(store->items);
    free(store);
}

void ca_semantic_store_add(ca_semantic_store_t *store, const ca_semantic_cluster_t *cluster) {
    if (!store || !cluster) return;
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 8;
        ca_semantic_cluster_t *n = (ca_semantic_cluster_t *)realloc(store->items, nc * sizeof(*n));
        if (!n) return;
        store->items = n; store->cap = nc;
    }
    cx_semantic_copy(&store->items[store->count], cluster);
    store->count++;
}

ca_semantic_cluster_t *ca_semantic_store_get_week(const ca_semantic_store_t *store,
                                                  ca_civil_date_t week_starting_monday,
                                                  size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
    if (!idx) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_civil_date_compare(store->items[i].week_starting_monday, week_starting_monday) == 0)
            idx[n++] = i;
    }
    if (n == 0) { free(idx); return NULL; }
    /* Stable sort by topic_weight desc (insertion order preserved on ties). */
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        double kw = store->items[key].topic_weight;
        size_t j = i;
        while (j > 0 && store->items[idx[j - 1]].topic_weight < kw) { idx[j] = idx[j - 1]; j--; }
        idx[j] = key;
    }
    ca_semantic_cluster_t *out = (ca_semantic_cluster_t *)calloc(n, sizeof(*out));
    if (out) {
        for (size_t i = 0; i < n; ++i) cx_semantic_copy(&out[i], &store->items[idx[i]]);
        if (out_count) *out_count = n;
    }
    free(idx);
    return out;
}

ca_semantic_cluster_t *ca_semantic_store_search(const ca_semantic_store_t *store,
                                                const float *query, size_t query_len,
                                                int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    if (top_k <= 0) top_k = 5;

    typedef struct { size_t idx; double score; size_t order; } scored_t;
    scored_t *sc = (scored_t *)malloc(store->count * sizeof(scored_t));
    if (!sc) return NULL;
    size_t sn = 0;

    if (!query) {
        /* Recency fallback: generated_at desc, insertion order on ties. */
        for (size_t i = 0; i < store->count; ++i) {
            sc[sn].idx = i;
            sc[sn].score = (double)store->items[i].generated_at_ms;
            sc[sn].order = i;
            sn++;
        }
    } else {
        for (size_t i = 0; i < store->count; ++i) {
            if (store->items[i].centroid_embedding == NULL) continue;
            sc[sn].idx = i;
            sc[sn].score = ca_cosine_full(query, query_len,
                                          store->items[i].centroid_embedding,
                                          store->items[i].centroid_len);
            sc[sn].order = sn;
            sn++;
        }
    }
    /* Stable sort by score desc. */
    for (size_t i = 1; i < sn; ++i) {
        scored_t key = sc[i];
        size_t j = i;
        while (j > 0 && sc[j - 1].score < key.score) { sc[j] = sc[j - 1]; j--; }
        sc[j] = key;
    }
    size_t limit = (size_t)top_k < sn ? (size_t)top_k : sn;
    ca_semantic_cluster_t *out = NULL;
    if (limit > 0) {
        out = (ca_semantic_cluster_t *)calloc(limit, sizeof(*out));
        if (out) {
            for (size_t i = 0; i < limit; ++i) cx_semantic_copy(&out[i], &store->items[sc[i].idx]);
            if (out_count) *out_count = limit;
        }
    }
    free(sc);
    return out;
}

int ca_semantic_store_prune_older_than(ca_semantic_store_t *store, ca_civil_date_t cutoff) {
    if (!store) return 0;
    size_t w = 0; int removed = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (ca_civil_date_compare(store->items[i].week_starting_monday, cutoff) < 0) {
            ca_semantic_cluster_free(&store->items[i]);
            removed++;
        } else {
            if (w != i) store->items[w] = store->items[i];
            w++;
        }
    }
    store->count = w;
    return removed;
}

size_t ca_semantic_store_count(const ca_semantic_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * Tier-4 store: persona-delta snapshots
 * =========================================================================== */

struct ca_persona_delta_store {
    ca_persona_delta_t *items;  /* owned */
    size_t              count, cap;
};

ca_persona_delta_store_t *ca_persona_delta_store_create(void) {
    return (ca_persona_delta_store_t *)calloc(1, sizeof(struct ca_persona_delta_store));
}

void ca_persona_delta_store_destroy(ca_persona_delta_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_persona_delta_free(&store->items[i]);
    free(store->items);
    free(store);
}

void ca_persona_delta_store_add(ca_persona_delta_store_t *store, const ca_persona_delta_t *snapshot) {
    if (!store || !snapshot) return;
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 8;
        ca_persona_delta_t *n = (ca_persona_delta_t *)realloc(store->items, nc * sizeof(*n));
        if (!n) return;
        store->items = n; store->cap = nc;
    }
    cx_persona_delta_copy(&store->items[store->count], snapshot);
    store->count++;
}

ca_persona_delta_t *ca_persona_delta_store_get_for_user(const ca_persona_delta_store_t *store,
                                                        const char *user_id, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
    if (!idx) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (cx_eq_ci(store->items[i].user_id, user_id)) idx[n++] = i;
    }
    if (n == 0) { free(idx); return NULL; }
    /* Stable ascending by period_start. */
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && ca_civil_date_compare(store->items[idx[j - 1]].period_start,
                                              store->items[key].period_start) > 0) {
            idx[j] = idx[j - 1]; j--;
        }
        idx[j] = key;
    }
    ca_persona_delta_t *out = (ca_persona_delta_t *)calloc(n, sizeof(*out));
    if (out) {
        for (size_t i = 0; i < n; ++i) cx_persona_delta_copy(&out[i], &store->items[idx[i]]);
        if (out_count) *out_count = n;
    }
    free(idx);
    return out;
}

size_t ca_persona_delta_store_count(const ca_persona_delta_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * Tier-5 store: core memories
 * =========================================================================== */

struct ca_core_store {
    ca_core_memory_t *items;  /* owned */
    size_t            count, cap;
};

ca_core_store_t *ca_core_store_create(void) {
    return (ca_core_store_t *)calloc(1, sizeof(struct ca_core_store));
}

void ca_core_store_destroy(ca_core_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_core_memory_free(&store->items[i]);
    free(store->items);
    free(store);
}

void ca_core_store_add(ca_core_store_t *store, const ca_core_memory_t *memory) {
    if (!store || !memory) return;
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 8;
        ca_core_memory_t *n = (ca_core_memory_t *)realloc(store->items, nc * sizeof(*n));
        if (!n) return;
        store->items = n; store->cap = nc;
    }
    cx_core_copy(&store->items[store->count], memory);
    store->count++;
}

bool ca_core_store_get(const ca_core_store_t *store, const char *id, ca_core_memory_t *out) {
    if (!store || !id || !out) return false;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->items[i].id && strcmp(store->items[i].id, id) == 0) {
            cx_core_copy(out, &store->items[i]);
            return true;
        }
    }
    return false;
}

/* Reinforcement order: reinforcement_count desc, then last_reinforced desc. */
static bool cx_core_before(const ca_core_memory_t *a, const ca_core_memory_t *b) {
    if (a->reinforcement_count != b->reinforcement_count)
        return a->reinforcement_count > b->reinforcement_count;
    return a->last_reinforced_ms > b->last_reinforced_ms;
}

ca_core_memory_t *ca_core_store_search(const ca_core_store_t *store,
                                       const float *query, size_t query_len,
                                       int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    if (top_k <= 0) top_k = 5;

    if (!query) {
        /* Reinforcement order fallback. */
        size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
        if (!idx) return NULL;
        for (size_t i = 0; i < store->count; ++i) idx[i] = i;
        for (size_t i = 1; i < store->count; ++i) {
            size_t key = idx[i];
            size_t j = i;
            while (j > 0 && cx_core_before(&store->items[key], &store->items[idx[j - 1]])) {
                idx[j] = idx[j - 1]; j--;
            }
            idx[j] = key;
        }
        size_t limit = (size_t)top_k < store->count ? (size_t)top_k : store->count;
        ca_core_memory_t *out = (ca_core_memory_t *)calloc(limit, sizeof(*out));
        if (out) {
            for (size_t i = 0; i < limit; ++i) cx_core_copy(&out[i], &store->items[idx[i]]);
            if (out_count) *out_count = limit;
        }
        free(idx);
        return out;
    }

    typedef struct { size_t idx; double score; } scored_t;
    scored_t *sc = (scored_t *)malloc(store->count * sizeof(scored_t));
    if (!sc) return NULL;
    size_t sn = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->items[i].embedding == NULL) continue;
        sc[sn].idx = i;
        sc[sn].score = ca_cosine_full(query, query_len,
                                      store->items[i].embedding, store->items[i].embedding_len);
        sn++;
    }
    for (size_t i = 1; i < sn; ++i) {
        scored_t key = sc[i];
        size_t j = i;
        while (j > 0 && sc[j - 1].score < key.score) { sc[j] = sc[j - 1]; j--; }
        sc[j] = key;
    }
    size_t limit = (size_t)top_k < sn ? (size_t)top_k : sn;
    ca_core_memory_t *out = NULL;
    if (limit > 0) {
        out = (ca_core_memory_t *)calloc(limit, sizeof(*out));
        if (out) {
            for (size_t i = 0; i < limit; ++i) cx_core_copy(&out[i], &store->items[sc[i].idx]);
            if (out_count) *out_count = limit;
        }
    }
    free(sc);
    return out;
}

ca_core_memory_t *ca_core_store_list_all(const ca_core_store_t *store, size_t *out_count) {
    return ca_core_store_search(store, NULL, 0, (int)(store ? (store->count ? store->count : 1) : 1), out_count);
}

void ca_core_store_reinforce(ca_core_store_t *store, const char *id) {
    if (!store || !id) return;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->items[i].id && strcmp(store->items[i].id, id) == 0) {
            store->items[i].reinforcement_count++;
            store->items[i].last_reinforced_ms = ca_clock_real(NULL);
            return;
        }
    }
}

bool ca_core_store_remove(ca_core_store_t *store, const char *id) {
    if (!store || !id) return false;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->items[i].id && strcmp(store->items[i].id, id) == 0) {
            ca_core_memory_free(&store->items[i]);
            memmove(&store->items[i], &store->items[i + 1],
                    (store->count - i - 1) * sizeof(ca_core_memory_t));
            store->count--;
            return true;
        }
    }
    return false;
}

size_t ca_core_store_count(const ca_core_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * Heuristic summarizer
 * =========================================================================== */

struct ca_heuristic_summarizer {
    int         highlight_count;
    int         min_days_per_topic;
    ca_clock_fn clock;
    void       *clock_user;
};

ca_heuristic_summarizer_t *ca_heuristic_summarizer_create(int highlight_count,
                                                          int min_days_per_topic,
                                                          ca_clock_fn clock,
                                                          void *clock_user) {
    ca_heuristic_summarizer_t *s = (ca_heuristic_summarizer_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->highlight_count = highlight_count > 0 ? highlight_count : 5;
    s->min_days_per_topic = min_days_per_topic > 0 ? min_days_per_topic : 2;
    s->clock = clock;
    s->clock_user = clock_user;
    return s;
}

void ca_heuristic_summarizer_destroy(ca_heuristic_summarizer_t *s) { free(s); }

/* ── topic aggregation ── */

static void cx_aggregate_topic_weights(const ca_episodic_entry_t *entries, size_t n,
                                       ca_topic_weights_t *out) {
    memset(out, 0, sizeof(*out));
    for (size_t i = 0; i < n; ++i) {
        /* Recognised tag keys: "topic" and "topics" (pipe-delimited), matching
         * the C#/TS reference's e.Tags reads. */
        const char *topic = ca_episodic_entry_get_tag(&entries[i], "topic");
        const char *topics = ca_episodic_entry_get_tag(&entries[i], "topics");
        if (topic && topic[0]) {
            cx_tw_accumulate(out, topic, 1.0);
        }
        if (topics && topics[0]) {
            /* split on '|', RemoveEmptyEntries */
            const char *p = topics;
            while (*p) {
                const char *start = p;
                while (*p && *p != '|') ++p;
                size_t len = (size_t)(p - start);
                if (len > 0) {
                    char *piece = (char *)malloc(len + 1);
                    if (piece) {
                        memcpy(piece, start, len);
                        piece[len] = '\0';
                        cx_tw_accumulate(out, piece, 1.0);
                        free(piece);
                    }
                }
                if (*p == '|') ++p;
            }
        }
    }
}

/* ── dispersion ── */

static double cx_mean_pairwise_cosine_distance(const ca_episodic_entry_t *entries, size_t n) {
    /* Indices of embedded entries. */
    size_t *emb = (size_t *)malloc((n ? n : 1) * sizeof(size_t));
    if (!emb) return 0.0;
    size_t m = 0;
    for (size_t i = 0; i < n; ++i) if (cx_has_embedding(&entries[i])) emb[m++] = i;
    if (m < 2) { free(emb); return 0.0; }
    double total = 0; int pairs = 0;
    for (size_t i = 0; i < m; ++i) {
        for (size_t j = i + 1; j < m; ++j) {
            double sim = ca_cosine_full(entries[emb[i]].embedding, entries[emb[i]].embedding_len,
                                        entries[emb[j]].embedding, entries[emb[j]].embedding_len);
            total += 1.0 - cx_clamp(sim, -1.0, 1.0);
            pairs++;
        }
    }
    free(emb);
    return pairs == 0 ? 0.0 : cx_clamp(total / pairs, 0.0, 1.0);
}

/* ── highlights ── */

static double cx_entry_salience_proxy(const ca_episodic_entry_t *e,
                                      const ca_episodic_entry_t *all, size_t n) {
    size_t ulen = e->user_text ? strlen(e->user_text) : 0;
    size_t alen = e->assistant_text ? strlen(e->assistant_text) : 0;
    /* C#: Math.Min(1.0, (UserText.Length + AssistantText.Length) / 800.0). */
    double length_score = (double)(ulen + alen) / 800.0;
    if (length_score > 1.0) length_score = 1.0;

    double uniqueness = 0.5;
    if (cx_has_embedding(e)) {
        double sum = 0; size_t cnt = 0;
        for (size_t i = 0; i < n; ++i) {
            const ca_episodic_entry_t *o = &all[i];
            if (o->id && e->id && strcmp(o->id, e->id) == 0) continue;
            if (!cx_has_embedding(o)) continue;
            sum += ca_cosine_full(e->embedding, e->embedding_len, o->embedding, o->embedding_len);
            cnt++;
        }
        if (cnt > 0) {
            double mean_sim = sum / (double)cnt;
            uniqueness = 1.0 - cx_clamp(mean_sim, -1.0, 1.0);
        }
    }
    return length_score * 0.6 + uniqueness * 0.4;
}

/* Select up to `count` highlights (deep copies) sorted ascending by time. */
static ca_episodic_entry_t *cx_select_highlights(const ca_episodic_entry_t *entries, size_t n,
                                                 int count, size_t *out_count) {
    *out_count = 0;
    if (n == 0) return NULL;

    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) return NULL;
    for (size_t i = 0; i < n; ++i) idx[i] = i;

    size_t take;
    if (n <= (size_t)count) {
        take = n;
        /* All entries; sorted ascending by time below. */
    } else {
        /* Score, then OrderByDescending(score).ThenByDescending(recordedAt),
         * take `count`. Stable sort over (score, recordedAt). */
        double *score = (double *)malloc(n * sizeof(double));
        if (!score) { free(idx); return NULL; }
        for (size_t i = 0; i < n; ++i) score[i] = cx_entry_salience_proxy(&entries[i], entries, n);
        /* Stable insertion sort by score desc, tie recordedAt desc. */
        for (size_t i = 1; i < n; ++i) {
            size_t key = idx[i];
            size_t j = i;
            while (j > 0) {
                size_t a = idx[j - 1];
                bool less; /* is idx[j-1] "after" key in desc order → should shift */
                if (score[a] != score[key]) less = score[a] < score[key];
                else less = entries[a].recorded_at_ms < entries[key].recorded_at_ms;
                if (!less) break;
                idx[j] = idx[j - 1]; j--;
            }
            idx[j] = key;
        }
        free(score);
        take = (size_t)count;
    }

    /* Now sort the first `take` indices ascending by recordedAt (stable). */
    for (size_t i = 1; i < take; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && entries[idx[j - 1]].recorded_at_ms > entries[key].recorded_at_ms) {
            idx[j] = idx[j - 1]; j--;
        }
        idx[j] = key;
    }

    ca_episodic_entry_t *out = (ca_episodic_entry_t *)calloc(take, sizeof(*out));
    if (out) {
        for (size_t i = 0; i < take; ++i) cx_episodic_copy(&out[i], &entries[idx[i]]);
        *out_count = take;
    }
    free(idx);
    return out;
}

/* ── daily salience ── */

static double cx_compute_daily_salience(int episode_count, const ca_topic_weights_t *tw,
                                        double dispersion) {
    double volume = (double)episode_count / 30.0;
    if (volume > 1.0) volume = 1.0;
    double topic_concentration;
    if (tw->count == 0) {
        topic_concentration = 0.5;
    } else {
        double maxW = -INFINITY, sumW = 0;
        for (size_t i = 0; i < tw->count; ++i) {
            if (tw->weights[i] > maxW) maxW = tw->weights[i];
            sumW += tw->weights[i];
        }
        double denom = sumW > 1.0 ? sumW : 1.0;
        topic_concentration = maxW / denom;
        if (topic_concentration > 1.0) topic_concentration = 1.0;
    }
    return volume * 0.4 + dispersion * 0.3 + topic_concentration * 0.3;
}

/* ── centroid ── */

static float *cx_centroid_of_highlights(const ca_daily_summary_t *days, size_t nd,
                                        size_t *out_len) {
    *out_len = 0;
    /* Gather pointers to embedded highlight vectors. */
    size_t dim = 0, total = 0;
    for (size_t d = 0; d < nd; ++d) {
        for (size_t h = 0; h < days[d].highlight_count; ++h) {
            if (cx_has_embedding(&days[d].highlights[h])) {
                if (total == 0) dim = days[d].highlights[h].embedding_len;
                total++;
            }
        }
    }
    if (total == 0) return NULL;
    double *acc = (double *)calloc(dim, sizeof(double));
    if (!acc) return NULL;
    for (size_t d = 0; d < nd; ++d) {
        for (size_t h = 0; h < days[d].highlight_count; ++h) {
            const ca_episodic_entry_t *e = &days[d].highlights[h];
            if (!cx_has_embedding(e)) continue;
            for (size_t i = 0; i < dim && i < e->embedding_len; ++i) acc[i] += (double)e->embedding[i];
        }
    }
    float *centroid = (float *)malloc(dim * sizeof(float));
    if (!centroid) { free(acc); return NULL; }
    for (size_t i = 0; i < dim; ++i) centroid[i] = (float)(acc[i] / (double)total);
    free(acc);
    *out_len = dim;
    return centroid;
}

/* ── text builders ── */

static char *cx_truncate(const char *s, size_t max) {
    if (!s || s[0] == '\0') return cx_dup("");
    size_t len = strlen(s);
    if (len <= max) return cx_dup(s);
    /* s[..max].TrimEnd() + "…" */
    size_t end = max;
    while (end > 0 && isspace((unsigned char)s[end - 1])) --end;
    /* "…" is U+2026 → 3 UTF-8 bytes E2 80 A6 */
    char *out = (char *)malloc(end + 3 + 1);
    if (!out) return NULL;
    memcpy(out, s, end);
    out[end] = (char)0xE2; out[end + 1] = (char)0x80; out[end + 2] = (char)0xA6;
    out[end + 3] = '\0';
    return out;
}

/* Append a formatted string to a growable buffer. */
typedef struct { char *buf; size_t len, cap; } cx_sb_t;
static void cx_sb_init(cx_sb_t *sb) { sb->buf = NULL; sb->len = 0; sb->cap = 0; }
static void cx_sb_append(cx_sb_t *sb, const char *s) {
    if (!s) return;
    size_t sl = strlen(s);
    if (sb->len + sl + 1 > sb->cap) {
        size_t nc = sb->cap ? sb->cap : 32;
        while (sb->len + sl + 1 > nc) nc *= 2;
        char *n = (char *)realloc(sb->buf, nc);
        if (!n) return;
        sb->buf = n; sb->cap = nc;
    }
    memcpy(sb->buf + sb->len, s, sl);
    sb->len += sl;
    sb->buf[sb->len] = '\0';
}
static char *cx_sb_take(cx_sb_t *sb) {
    if (!sb->buf) return cx_dup("");
    return sb->buf; /* caller owns */
}

/* Top-n labels of a topic-weights map by weight desc (stable), returns owned
 * array of borrowed label pointers (into tw). */
static size_t cx_top_n_labels(const ca_topic_weights_t *tw, size_t nmax, size_t *idx_out) {
    size_t *idx = (size_t *)malloc((tw->count ? tw->count : 1) * sizeof(size_t));
    if (!idx) return 0;
    for (size_t i = 0; i < tw->count; ++i) idx[i] = i;
    /* Stable insertion sort by weight desc. */
    for (size_t i = 1; i < tw->count; ++i) {
        size_t key = idx[i];
        double kw = tw->weights[key];
        size_t j = i;
        while (j > 0 && tw->weights[idx[j - 1]] < kw) { idx[j] = idx[j - 1]; j--; }
        idx[j] = key;
    }
    size_t take = tw->count < nmax ? tw->count : nmax;
    for (size_t i = 0; i < take; ++i) idx_out[i] = idx[i];
    free(idx);
    return take;
}

static char *cx_build_daily_summary_text(ca_civil_date_t day, int count,
                                         const ca_topic_weights_t *topics,
                                         const ca_episodic_entry_t *highlights,
                                         size_t highlight_count) {
    char daybuf[16];
    ca_civil_date_to_string(day, daybuf, sizeof(daybuf));

    cx_sb_t sb; cx_sb_init(&sb);
    char head[64];
    snprintf(head, sizeof(head), "On %s you had %d ", daybuf, count);
    cx_sb_append(&sb, head);
    cx_sb_append(&sb, count == 1 ? "exchange." : "exchanges.");

    if (topics->count > 0) {
        size_t idx[3];
        size_t take = cx_top_n_labels(topics, 3, idx);
        cx_sb_append(&sb, " Top topics: ");
        for (size_t i = 0; i < take; ++i) {
            if (i > 0) cx_sb_append(&sb, ", ");
            cx_sb_append(&sb, topics->labels[idx[i]]);
        }
        cx_sb_append(&sb, ".");
    }

    if (highlight_count > 0) {
        char *trunc = cx_truncate(highlights[0].user_text, 120);
        cx_sb_append(&sb, " Standout moment: \"");
        cx_sb_append(&sb, trunc ? trunc : "");
        cx_sb_append(&sb, "\".");
        free(trunc);
    }
    return cx_sb_take(&sb);
}

void ca_heuristic_summarizer_summarize_day(const ca_heuristic_summarizer_t *s,
                                           ca_civil_date_t day,
                                           const ca_episodic_entry_t *entries,
                                           size_t entry_count,
                                           ca_daily_summary_t *out) {
    memset(out, 0, sizeof(*out));
    out->id = cx_new_id();
    out->day = day;
    out->generated_at_ms = cx_now(s->clock, s->clock_user);

    if (entry_count == 0) {
        char daybuf[16];
        ca_civil_date_to_string(day, daybuf, sizeof(daybuf));
        char msg[64];
        snprintf(msg, sizeof(msg), "No exchanges recorded on %s.", daybuf);
        out->summary = cx_dup(msg);
        out->episode_count = 0;
        return;
    }

    cx_aggregate_topic_weights(entries, entry_count, &out->topic_weights);
    out->topic_dispersion = cx_mean_pairwise_cosine_distance(entries, entry_count);
    out->highlights = cx_select_highlights(entries, entry_count, s->highlight_count, &out->highlight_count);
    out->salience = cx_compute_daily_salience((int)entry_count, &out->topic_weights, out->topic_dispersion);
    out->summary = cx_build_daily_summary_text(day, (int)entry_count, &out->topic_weights,
                                               out->highlights, out->highlight_count);
    out->episode_count = (int)entry_count;
}

static char *cx_build_weekly_cluster_text(const char *topic,
                                          const ca_daily_summary_t *days, size_t nd) {
    int total_episodes = 0;
    for (size_t i = 0; i < nd; ++i) total_episodes += days[i].episode_count;
    cx_sb_t sb; cx_sb_init(&sb);
    char head[64];
    snprintf(head, sizeof(head), "Across %zu days this week you returned to ", nd);
    cx_sb_append(&sb, head);
    cx_sb_append(&sb, "\"");
    cx_sb_append(&sb, topic ? topic : "");
    cx_sb_append(&sb, "\" — ");
    char tail[48];
    snprintf(tail, sizeof(tail), "%d exchanges in total.", total_episodes);
    cx_sb_append(&sb, tail);
    return cx_sb_take(&sb);
}

ca_semantic_cluster_t *ca_heuristic_summarizer_consolidate_week(
    const ca_heuristic_summarizer_t *s,
    ca_civil_date_t week_starting_monday,
    const ca_daily_summary_t *days_in_week, size_t day_count,
    size_t *out_count) {
    if (out_count) *out_count = 0;
    if (day_count == 0) return NULL;

    /* topic → contributing day indices + cumulative weight (case-insensitive,
     * insertion-ordered). Labels arrive already lowercased from AggregateTopicWeights. */
    typedef struct { char *topic; size_t *day_idx; size_t day_n, day_cap; double weight; } tgroup_t;
    tgroup_t *groups = NULL; size_t gn = 0, gcap = 0;

    for (size_t d = 0; d < day_count; ++d) {
        const ca_topic_weights_t *tw = &days_in_week[d].topic_weights;
        for (size_t t = 0; t < tw->count; ++t) {
            const char *topic = tw->labels[t];
            double w = tw->weights[t];
            /* find group (case-insensitive) */
            size_t gi = gn;
            for (size_t g = 0; g < gn; ++g) {
                if (cx_eq_ci(groups[g].topic, topic)) { gi = g; break; }
            }
            if (gi == gn) {
                if (gn == gcap) {
                    size_t nc = gcap ? gcap * 2 : 8;
                    tgroup_t *n = (tgroup_t *)realloc(groups, nc * sizeof(*n));
                    if (!n) continue;
                    groups = n; gcap = nc;
                }
                memset(&groups[gn], 0, sizeof(groups[gn]));
                groups[gn].topic = cx_dup(topic);
                gn++;
            }
            tgroup_t *g = &groups[gi];
            if (g->day_n == g->day_cap) {
                size_t nc = g->day_cap ? g->day_cap * 2 : 4;
                size_t *n = (size_t *)realloc(g->day_idx, nc * sizeof(size_t));
                if (n) { g->day_idx = n; g->day_cap = nc; }
            }
            if (g->day_n < g->day_cap) g->day_idx[g->day_n++] = d;
            g->weight += w;
        }
    }

    double total_weight = 0;
    for (size_t g = 0; g < gn; ++g) total_weight += groups[g].weight;
    if (total_weight <= 0.0) total_weight = 1.0;

    /* Order topics by weight desc (stable → insertion order on ties). */
    size_t *order = (size_t *)malloc((gn ? gn : 1) * sizeof(size_t));
    for (size_t i = 0; i < gn; ++i) order[i] = i;
    for (size_t i = 1; i < gn; ++i) {
        size_t key = order[i];
        double kw = groups[key].weight;
        size_t j = i;
        while (j > 0 && groups[order[j - 1]].weight < kw) { order[j] = order[j - 1]; j--; }
        order[j] = key;
    }

    ca_semantic_cluster_t *clusters = NULL; size_t cn = 0, ccap = 0;
    for (size_t oi = 0; oi < gn; ++oi) {
        tgroup_t *g = &groups[order[oi]];
        if ((int)g->day_n < s->min_days_per_topic) continue;

        /* Assemble a compact contiguous array of the contributing day summaries
         * as shallow, read-only views (the centroid/text/id helpers only read). */
        ca_daily_summary_t *cdays = (ca_daily_summary_t *)malloc(g->day_n * sizeof(ca_daily_summary_t));
        for (size_t i = 0; i < g->day_n; ++i) cdays[i] = days_in_week[g->day_idx[i]]; /* shallow, read-only */

        size_t centroid_len = 0;
        float *centroid = cx_centroid_of_highlights(cdays, g->day_n, &centroid_len);
        double weight = g->weight;
        double cluster_salience = (weight / total_weight) + ((double)g->day_n / 7.0) * 0.25;
        if (cluster_salience > 1.0) cluster_salience = 1.0;

        if (cn == ccap) {
            size_t nc = ccap ? ccap * 2 : 8;
            ca_semantic_cluster_t *n = (ca_semantic_cluster_t *)realloc(clusters, nc * sizeof(*n));
            if (n) { clusters = n; ccap = nc; }
        }
        ca_semantic_cluster_t *c = &clusters[cn];
        memset(c, 0, sizeof(*c));
        c->id = cx_new_id();
        c->generated_at_ms = cx_now(s->clock, s->clock_user);
        c->week_starting_monday = week_starting_monday;
        c->topic = cx_dup(g->topic);
        c->summary = cx_build_weekly_cluster_text(g->topic, cdays, g->day_n);
        c->centroid_embedding = centroid;
        c->centroid_len = centroid_len;
        c->source_daily_count = g->day_n;
        c->source_daily_ids = (char **)calloc(g->day_n, sizeof(char *));
        for (size_t i = 0; i < g->day_n; ++i) c->source_daily_ids[i] = cx_dup(days_in_week[g->day_idx[i]].id);
        c->topic_weight = weight;
        c->salience = cluster_salience;
        cn++;

        free(cdays);
    }
    free(order);
    for (size_t g = 0; g < gn; ++g) { free(groups[g].topic); free(groups[g].day_idx); }
    free(groups);

    if (cn == 0) { free(clusters); return NULL; }
    if (out_count) *out_count = cn;
    return clusters;
}

static char *cx_build_persona_narrative(const ca_consolidation_persona_t *before,
                                        const ca_consolidation_persona_t *after,
                                        const ca_topic_weights_t *new_topics,
                                        const ca_topic_weights_t *strengthened,
                                        char *const *disfavoured, size_t disfavoured_n,
                                        int net_signals, int interactions,
                                        ca_civil_date_t period_start,
                                        ca_civil_date_t period_end) {
    char sbuf[16], ebuf[16];
    ca_civil_date_to_string(period_start, sbuf, sizeof(sbuf));
    ca_civil_date_to_string(period_end, ebuf, sizeof(ebuf));

    cx_sb_t sb; cx_sb_init(&sb);
    char head[96];
    snprintf(head, sizeof(head), "Between %s and %s, %d interactions were recorded.",
             sbuf, ebuf, interactions);
    cx_sb_append(&sb, head);

    if (new_topics->count > 0) {
        size_t idx[3];
        size_t take = cx_top_n_labels(new_topics, 3, idx);
        cx_sb_append(&sb, " New interests appeared: ");
        for (size_t i = 0; i < take; ++i) {
            if (i > 0) cx_sb_append(&sb, ", ");
            cx_sb_append(&sb, new_topics->labels[idx[i]]);
        }
        cx_sb_append(&sb, ".");
    }
    if (strengthened->count > 0) {
        size_t idx[3];
        size_t take = cx_top_n_labels(strengthened, 3, idx);
        cx_sb_append(&sb, " Existing interests deepened around ");
        for (size_t i = 0; i < take; ++i) {
            if (i > 0) cx_sb_append(&sb, ", ");
            cx_sb_append(&sb, strengthened->labels[idx[i]]);
        }
        cx_sb_append(&sb, ".");
    }
    if (disfavoured_n > 0) {
        cx_sb_append(&sb, " Topics now avoided: ");
        for (size_t i = 0; i < disfavoured_n; ++i) {
            if (i > 0) cx_sb_append(&sb, ", ");
            cx_sb_append(&sb, disfavoured[i]);
        }
        cx_sb_append(&sb, ".");
    }
    if (before->verbosity && after->verbosity && strcmp(before->verbosity, after->verbosity) != 0) {
        cx_sb_append(&sb, " Preferred verbosity shifted from ");
        cx_sb_append(&sb, before->verbosity);
        cx_sb_append(&sb, " to ");
        cx_sb_append(&sb, after->verbosity);
        cx_sb_append(&sb, ".");
    }
    if (before->formality && after->formality && strcmp(before->formality, after->formality) != 0) {
        cx_sb_append(&sb, " Preferred tone shifted from ");
        cx_sb_append(&sb, before->formality);
        cx_sb_append(&sb, " to ");
        cx_sb_append(&sb, after->formality);
        cx_sb_append(&sb, ".");
    }
    if (net_signals != 0) {
        char tail[64];
        if (net_signals > 0) snprintf(tail, sizeof(tail), " Net feedback was positive (+%d).", net_signals);
        else                 snprintf(tail, sizeof(tail), " Net feedback was negative (%d).", net_signals);
        cx_sb_append(&sb, tail);
    }
    return cx_sb_take(&sb);
}

void ca_heuristic_summarizer_derive_persona_delta(
    const ca_heuristic_summarizer_t *s,
    const ca_consolidation_persona_t *before, const ca_consolidation_persona_t *after,
    const ca_daily_summary_t *days_in_period, size_t day_count,
    ca_persona_delta_t *out) {
    memset(out, 0, sizeof(*out));

    /* new vs strengthened topics (iterate after.topicWeights in order). */
    for (size_t i = 0; i < after->topic_weights.count; ++i) {
        const char *topic = after->topic_weights.labels[i];
        double afterW = after->topic_weights.weights[i];
        double beforeW = 0;
        ca_topic_weights_get(&before->topic_weights, topic, &beforeW);
        double delta = afterW - beforeW;
        if (beforeW <= 0.0 && afterW > 0.0) {
            cx_tw_set(&out->new_topics, topic, afterW);
        } else if (delta > 0.0) {
            cx_tw_set(&out->strengthened_topics, topic, delta);
        }
    }

    /* disfavoured topics new in after (not in before). */
    char **disf = NULL; size_t disf_n = 0;
    for (size_t i = 0; i < after->disfavoured_count; ++i) {
        const char *t = after->disfavoured_topics[i];
        bool in_before = false;
        for (size_t j = 0; j < before->disfavoured_count; ++j) {
            if (cx_eq_ci(before->disfavoured_topics[j], t)) { in_before = true; break; }
        }
        if (!in_before) {
            char **n = (char **)realloc(disf, (disf_n + 1) * sizeof(char *));
            if (n) { disf = n; disf[disf_n++] = cx_dup(t); }
        }
    }

    int net_signals = (after->positive_signals - before->positive_signals)
                    - (after->negative_signals - before->negative_signals);
    int interactions = after->total_interactions - before->total_interactions;

    ca_civil_date_t period_start, period_end;
    if (day_count > 0) {
        period_start = days_in_period[0].day;
        period_end = days_in_period[0].day;
        for (size_t i = 1; i < day_count; ++i) {
            if (ca_civil_date_compare(days_in_period[i].day, period_start) < 0) period_start = days_in_period[i].day;
            if (ca_civil_date_compare(days_in_period[i].day, period_end) > 0) period_end = days_in_period[i].day;
        }
    } else {
        period_start = ca_civil_date_from_ms(after->last_updated_ms);
        period_end = period_start;
    }

    out->id = cx_new_id();
    out->generated_at_ms = cx_now(s->clock, s->clock_user);
    out->period_start = period_start;
    out->period_end = period_end;
    out->user_id = cx_dup(after->user_id);
    out->verbosity_before = cx_dup(before->verbosity);
    out->verbosity_after = cx_dup(after->verbosity);
    out->formality_before = cx_dup(before->formality);
    out->formality_after = cx_dup(after->formality);
    out->newly_disfavoured = disf;
    out->newly_disfavoured_count = disf_n;
    out->net_signal_delta = net_signals;
    out->interactions_in_period = interactions;
    out->narrative = cx_build_persona_narrative(before, after, &out->new_topics,
                                                &out->strengthened_topics, disf, disf_n,
                                                net_signals, interactions,
                                                period_start, period_end);
}

/* ===========================================================================
 * MemoryConsolidator
 * =========================================================================== */

struct ca_memory_consolidator {
    ca_episodic_store_t       *episodic;
    ca_daily_store_t          *daily;
    ca_semantic_store_t       *semantic;
    ca_persona_delta_store_t  *persona_delta;
    ca_core_store_t           *core;
    ca_persona_store_t        *persona_store;
    ca_heuristic_summarizer_t *summarizer;
    int    episodic_retention_days;
    int    daily_retention_days;
    int    semantic_retention_days;
    double daily_core_promotion_threshold;
    double weekly_core_promotion_threshold;
    ca_clock_fn clock;
    void       *clock_user;
    char       *user_id;
};

ca_memory_consolidator_t *ca_memory_consolidator_create(
    ca_episodic_store_t *episodic, ca_daily_store_t *daily,
    ca_semantic_store_t *semantic, ca_persona_delta_store_t *persona_delta,
    ca_core_store_t *core, ca_persona_store_t *persona_store,
    ca_heuristic_summarizer_t *summarizer,
    const ca_consolidation_options_t *opts,
    ca_clock_fn clock, void *clock_user, const char *user_id) {
    if (!episodic || !daily || !semantic || !persona_delta || !core || !persona_store || !summarizer)
        return NULL;
    ca_memory_consolidator_t *c = (ca_memory_consolidator_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->episodic = episodic;
    c->daily = daily;
    c->semantic = semantic;
    c->persona_delta = persona_delta;
    c->core = core;
    c->persona_store = persona_store;
    c->summarizer = summarizer;
    c->episodic_retention_days = 7;
    c->daily_retention_days = 30;
    c->semantic_retention_days = 365;
    c->daily_core_promotion_threshold = 0.80;
    c->weekly_core_promotion_threshold = 0.75;
    if (opts) {
        if (opts->episodic_retention_days != 0) c->episodic_retention_days = opts->episodic_retention_days;
        if (opts->daily_retention_days != 0) c->daily_retention_days = opts->daily_retention_days;
        if (opts->semantic_retention_days != 0) c->semantic_retention_days = opts->semantic_retention_days;
        if (opts->daily_core_promotion_threshold != 0.0)
            c->daily_core_promotion_threshold = opts->daily_core_promotion_threshold;
        if (opts->weekly_core_promotion_threshold != 0.0)
            c->weekly_core_promotion_threshold = opts->weekly_core_promotion_threshold;
    }
    c->clock = clock;
    c->clock_user = clock_user;
    c->user_id = cx_dup(user_id ? user_id : "default");
    return c;
}

void ca_memory_consolidator_destroy(ca_memory_consolidator_t *c) {
    if (!c) return;
    free(c->user_id);
    free(c);
}

/* ── core promotions ── */

static int cx_promote_daily_to_core(ca_memory_consolidator_t *c, const ca_daily_summary_t *summary) {
    /* top topic by weight (FirstOrDefault of OrderByDescending → NULL when empty). */
    const char *top_topic = NULL;
    double top_weight = -INFINITY;
    for (size_t i = 0; i < summary->topic_weights.count; ++i) {
        if (summary->topic_weights.weights[i] > top_weight) {
            top_weight = summary->topic_weights.weights[i];
            top_topic = summary->topic_weights.labels[i];
        }
    }
    char daybuf[16];
    ca_civil_date_to_string(summary->day, daybuf, sizeof(daybuf));

    char *statement;
    if (top_topic == NULL) {
        char buf[96];
        snprintf(buf, sizeof(buf), "On %s an unusually meaningful day was recorded.", daybuf);
        statement = cx_dup(buf);
    } else {
        cx_sb_t sb; cx_sb_init(&sb);
        cx_sb_append(&sb, "\"");
        cx_sb_append(&sb, top_topic);
        cx_sb_append(&sb, "\" mattered enough on ");
        cx_sb_append(&sb, daybuf);
        cx_sb_append(&sb, " to be remembered.");
        statement = cx_sb_take(&sb);
    }

    /* First highlight embedding, if any. */
    const float *emb = NULL; size_t emb_len = 0;
    for (size_t h = 0; h < summary->highlight_count; ++h) {
        if (cx_has_embedding(&summary->highlights[h])) {
            emb = summary->highlights[h].embedding;
            emb_len = summary->highlights[h].embedding_len;
            break;
        }
    }

    ca_core_memory_t m;
    memset(&m, 0, sizeof(m));
    m.id = cx_new_id();
    m.created_at_ms = cx_now(c->clock, c->clock_user);
    m.last_reinforced_ms = m.created_at_ms;
    m.statement = statement;
    m.kind = CA_CORE_HIGH_SALIENCE;
    m.topic = cx_dup(top_topic);
    m.embedding = cx_dup_floats(emb, emb_len);
    if (m.embedding) m.embedding_len = emb_len;
    m.reinforcement_count = 0;
    m.source_memory_id = cx_dup(summary->id);
    ca_core_store_add(c->core, &m);
    ca_core_memory_free(&m);
    return 1;
}

static int cx_promote_cluster_to_core(ca_memory_consolidator_t *c, const ca_semantic_cluster_t *cluster) {
    char weekbuf[16];
    ca_civil_date_to_string(cluster->week_starting_monday, weekbuf, sizeof(weekbuf));
    cx_sb_t sb; cx_sb_init(&sb);
    cx_sb_append(&sb, "\"");
    cx_sb_append(&sb, cluster->topic ? cluster->topic : "");
    cx_sb_append(&sb, "\" has been a recurring theme (week of ");
    cx_sb_append(&sb, weekbuf);
    cx_sb_append(&sb, ").");

    ca_core_memory_t m;
    memset(&m, 0, sizeof(m));
    m.id = cx_new_id();
    m.created_at_ms = cx_now(c->clock, c->clock_user);
    m.last_reinforced_ms = m.created_at_ms;
    m.statement = cx_sb_take(&sb);
    m.kind = CA_CORE_PATTERN_INFERRED;
    m.topic = cx_dup(cluster->topic);
    m.embedding = cx_dup_floats(cluster->centroid_embedding, cluster->centroid_len);
    if (m.embedding) m.embedding_len = cluster->centroid_len;
    m.reinforcement_count = 0;
    m.source_memory_id = cx_dup(cluster->id);
    ca_core_store_add(c->core, &m);
    ca_core_memory_free(&m);
    return 1;
}

/* ── daily pass ── */

static void cx_run_daily(ca_memory_consolidator_t *c, int64_t now_ms,
                         int *produced_out, int *promoted_out) {
    *produced_out = 0; *promoted_out = 0;

    size_t rn = 0;
    ca_episodic_entry_t *recent = ca_episodic_store_get_recent(c->episodic, INT_MAX, &rn);
    if (rn == 0) { ca_episodic_entry_free_array(recent, rn); return; }

    ca_civil_date_t today = ca_civil_date_from_ms(now_ms);

    /* Group by UTC day (insertion order of first-seen day). */
    typedef struct { ca_civil_date_t day; size_t *idx; size_t n, cap; } grp_t;
    grp_t *groups = NULL; size_t gn = 0, gcap = 0;
    for (size_t i = 0; i < rn; ++i) {
        ca_civil_date_t d = ca_civil_date_from_ms(recent[i].recorded_at_ms);
        size_t gi = gn;
        for (size_t g = 0; g < gn; ++g) {
            if (ca_civil_date_compare(groups[g].day, d) == 0) { gi = g; break; }
        }
        if (gi == gn) {
            if (gn == gcap) {
                size_t nc = gcap ? gcap * 2 : 8;
                grp_t *n = (grp_t *)realloc(groups, nc * sizeof(*n));
                if (!n) continue;
                groups = n; gcap = nc;
            }
            memset(&groups[gn], 0, sizeof(groups[gn]));
            groups[gn].day = d;
            gi = gn; gn++;
        }
        grp_t *g = &groups[gi];
        if (g->n == g->cap) {
            size_t nc = g->cap ? g->cap * 2 : 4;
            size_t *n = (size_t *)realloc(g->idx, nc * sizeof(size_t));
            if (n) { g->idx = n; g->cap = nc; }
        }
        if (g->n < g->cap) g->idx[g->n++] = i;
    }

    int produced = 0, promoted = 0;
    for (size_t g = 0; g < gn; ++g) {
        if (ca_civil_date_compare(groups[g].day, today) >= 0) continue; /* completed days only */

        /* idempotency: existing daily with matching episodeCount → skip. */
        ca_daily_summary_t existing;
        bool have = ca_daily_store_get(c->daily, groups[g].day, &existing);
        if (have) {
            int ec = existing.episode_count;
            ca_daily_summary_free(&existing);
            if (ec == (int)groups[g].n) continue;
        }

        /* ordered entries by recordedAt ascending (stable). */
        size_t m = groups[g].n;
        ca_episodic_entry_t *ordered = (ca_episodic_entry_t *)malloc(m * sizeof(ca_episodic_entry_t));
        size_t *oi = (size_t *)malloc(m * sizeof(size_t));
        for (size_t i = 0; i < m; ++i) oi[i] = groups[g].idx[i];
        for (size_t i = 1; i < m; ++i) {
            size_t key = oi[i]; size_t j = i;
            while (j > 0 && recent[oi[j - 1]].recorded_at_ms > recent[key].recorded_at_ms) { oi[j] = oi[j - 1]; j--; }
            oi[j] = key;
        }
        for (size_t i = 0; i < m; ++i) ordered[i] = recent[oi[i]]; /* shallow view (read-only) */
        free(oi);

        ca_daily_summary_t summary;
        ca_heuristic_summarizer_summarize_day(c->summarizer, groups[g].day, ordered, m, &summary);
        free(ordered);

        ca_daily_store_upsert(c->daily, &summary);
        produced++;
        if (summary.salience >= c->daily_core_promotion_threshold) {
            promoted += cx_promote_daily_to_core(c, &summary);
        }
        ca_daily_summary_free(&summary);
    }

    for (size_t g = 0; g < gn; ++g) free(groups[g].idx);
    free(groups);
    ca_episodic_entry_free_array(recent, rn);
    *produced_out = produced;
    *promoted_out = promoted;
}

/* ── weekly pass ── */

static void cx_run_weekly(ca_memory_consolidator_t *c, int64_t now_ms,
                          int *produced_out, int *promoted_out) {
    *produced_out = 0; *promoted_out = 0;
    ca_civil_date_t today = ca_civil_date_from_ms(now_ms);
    ca_civil_date_t this_monday = ca_civil_date_monday_of(today);
    ca_civil_date_t last_monday = ca_civil_date_add_days(this_monday, -7);
    ca_civil_date_t last_sunday = ca_civil_date_add_days(last_monday, 6);

    size_t wn = 0;
    ca_daily_summary_t *last_week = ca_daily_store_get_range(c->daily, last_monday, last_sunday, &wn);
    if (wn == 0) { ca_daily_summary_free_array(last_week, wn); return; }

    size_t en = 0;
    ca_semantic_cluster_t *existing = ca_semantic_store_get_week(c->semantic, last_monday, &en);
    if (en > 0) {
        ca_semantic_cluster_free_array(existing, en);
        ca_daily_summary_free_array(last_week, wn);
        return;
    }
    ca_semantic_cluster_free_array(existing, en);

    size_t cn = 0;
    ca_semantic_cluster_t *clusters = ca_heuristic_summarizer_consolidate_week(
        c->summarizer, last_monday, last_week, wn, &cn);

    int promoted = 0;
    for (size_t i = 0; i < cn; ++i) {
        ca_semantic_store_add(c->semantic, &clusters[i]);
        if (clusters[i].salience >= c->weekly_core_promotion_threshold) {
            promoted += cx_promote_cluster_to_core(c, &clusters[i]);
        }
    }
    *produced_out = (int)cn;
    *promoted_out = promoted;

    ca_semantic_cluster_free_array(clusters, cn);
    ca_daily_summary_free_array(last_week, wn);
}

/* ── reconstruct persona before ── */

static ca_consolidation_persona_t *cx_reconstruct_persona_before(const ca_consolidation_persona_t *after,
                                                         const ca_daily_summary_t *days, size_t nd,
                                                         const ca_persona_delta_t *prior) {
    ca_consolidation_persona_t *before = ca_consolidation_persona_create(after->user_id);
    if (!before) return NULL;
    free(before->verbosity); before->verbosity = cx_dup(prior->verbosity_after);
    free(before->formality); before->formality = cx_dup(prior->formality_after);
    free(before->preferred_locale); before->preferred_locale = cx_dup(after->preferred_locale);

    int episode_sum = 0;
    for (size_t i = 0; i < nd; ++i) episode_sum += days[i].episode_count;
    before->total_interactions = after->total_interactions - episode_sum;

    int net_pos = prior->net_signal_delta < 0 ? 0 : prior->net_signal_delta;
    int pos = after->positive_signals - net_pos;
    before->positive_signals = pos < 0 ? 0 : pos;
    before->negative_signals = after->negative_signals;

    /* topic weights minus strongest in-period gains. */
    for (size_t i = 0; i < after->topic_weights.count; ++i) {
        const char *topic = after->topic_weights.labels[i];
        double w = after->topic_weights.weights[i];
        double delta;
        if (ca_topic_weights_get(&prior->strengthened_topics, topic, &delta)) {
            double v = w - delta;
            cx_tw_set(&before->topic_weights, topic, v < 0 ? 0 : v);
        } else {
            cx_tw_set(&before->topic_weights, topic, w);
        }
    }
    for (size_t i = 0; i < after->disfavoured_count; ++i)
        ca_consolidation_persona_add_disfavoured(before, after->disfavoured_topics[i]);
    return before;
}

/* ── monthly pass ── */

static int cx_run_monthly(ca_memory_consolidator_t *c, int64_t now_ms) {
    ca_civil_date_t today = ca_civil_date_from_ms(now_ms);
    ca_civil_date_t first_of_this_month = ca_civil_date_month_first(today);
    ca_civil_date_t last_month_end = ca_civil_date_add_days(first_of_this_month, -1);
    ca_civil_date_t last_month_start = ca_civil_date_month_first(last_month_end);

    size_t dn = 0;
    ca_persona_delta_t *existing = ca_persona_delta_store_get_for_user(c->persona_delta, c->user_id, &dn);
    /* idempotency: any delta whose period_start month/year == last_month_start */
    for (size_t i = 0; i < dn; ++i) {
        if (existing[i].period_start.year == last_month_start.year &&
            existing[i].period_start.month == last_month_start.month) {
            ca_persona_delta_free_array(existing, dn);
            return 0;
        }
    }

    size_t days_n = 0;
    ca_daily_summary_t *days = ca_daily_store_get_range(c->daily, last_month_start, last_month_end, &days_n);
    if (days_n == 0) {
        ca_daily_summary_free_array(days, days_n);
        ca_persona_delta_free_array(existing, dn);
        return 0;
    }

    ca_consolidation_persona_t *after = ca_persona_store_load(c->persona_store, c->user_id);

    /* prior = most recent delta whose period_end < last_month_start. */
    const ca_persona_delta_t *prior = NULL;
    for (size_t i = 0; i < dn; ++i) {
        if (ca_civil_date_compare(existing[i].period_end, last_month_start) < 0) {
            if (!prior || ca_civil_date_compare(existing[i].period_end, prior->period_end) > 0)
                prior = &existing[i];
        }
    }

    ca_consolidation_persona_t *before = prior
        ? cx_reconstruct_persona_before(after, days, days_n, prior)
        : ca_consolidation_persona_create(c->user_id);

    ca_persona_delta_t delta;
    ca_heuristic_summarizer_derive_persona_delta(c->summarizer, before, after, days, days_n, &delta);
    ca_persona_delta_store_add(c->persona_delta, &delta);
    ca_persona_delta_free(&delta);

    ca_consolidation_persona_destroy(before);
    ca_consolidation_persona_destroy(after);
    ca_daily_summary_free_array(days, days_n);
    ca_persona_delta_free_array(existing, dn);
    return 1;
}

/* ── retention ── */

static int cx_prune_episodic(ca_memory_consolidator_t *c, int64_t now_ms) {
    int64_t cutoff = now_ms - (int64_t)c->episodic_retention_days * 86400000LL;
    return (int)ca_episodic_store_prune_older_than(c->episodic, cutoff);
}

static int cx_prune_dailies(ca_memory_consolidator_t *c, int64_t now_ms) {
    ca_civil_date_t cutoff = ca_civil_date_add_days(ca_civil_date_from_ms(now_ms), -c->daily_retention_days);
    return ca_daily_store_prune_older_than(c->daily, cutoff);
}

static int cx_prune_semantics(ca_memory_consolidator_t *c, int64_t now_ms) {
    ca_civil_date_t cutoff = ca_civil_date_add_days(ca_civil_date_from_ms(now_ms), -c->semantic_retention_days);
    return ca_semantic_store_prune_older_than(c->semantic, cutoff);
}

void ca_memory_consolidator_tick(ca_memory_consolidator_t *c, ca_sleep_kind_t kind,
                                 ca_consolidation_outcome_t *out) {
    int64_t now = cx_now(c->clock, c->clock_user);
    int dailies = 0, clusters = 0, deltas = 0, core_promoted = 0;
    int episodes_pruned = 0, dailies_pruned = 0, semantics_pruned = 0;

    if (kind == CA_SLEEP_DAILY || kind == CA_SLEEP_ONDEMAND) {
        int produced = 0, promoted = 0;
        cx_run_daily(c, now, &produced, &promoted);
        dailies = produced;
        core_promoted += promoted;
        episodes_pruned += cx_prune_episodic(c, now);
    }
    if (kind == CA_SLEEP_WEEKLY || kind == CA_SLEEP_ONDEMAND) {
        int produced = 0, promoted = 0;
        cx_run_weekly(c, now, &produced, &promoted);
        clusters = produced;
        core_promoted += promoted;
        dailies_pruned += cx_prune_dailies(c, now);
    }
    if (kind == CA_SLEEP_MONTHLY || kind == CA_SLEEP_ONDEMAND) {
        deltas = cx_run_monthly(c, now);
        semantics_pruned += cx_prune_semantics(c, now);
    }

    out->kind = kind;
    out->daily_summaries_produced = dailies;
    out->semantic_clusters_produced = clusters;
    out->persona_deltas_produced = deltas;
    out->core_promotions = core_promoted;
    out->episodes_pruned = episodes_pruned;
    out->dailies_pruned = dailies_pruned;
    out->semantics_pruned = semantics_pruned;
    out->ran_at_ms = now;
}
