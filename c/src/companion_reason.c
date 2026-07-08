/*
 * companion_reason.c — CircleAI companion reasoning core (C11 port).
 *
 * FrequencyWorldModel + BayesianWorldModel + HistogramPredictiveEngine +
 * SequencePredictiveEngine + TemplateInnerMonologue + ReasoningLoopInnerMonologue
 * + BeliefTrackerTheoryOfMind. Ported 1:1 from the C# reference (CircleAI.Companion).
 * In-memory: dynamic arrays + linear search where the C# uses ConcurrentDictionary.
 * Pure C11 + libc, links -lm.
 */

#include "circle_ai/companion_reason.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <math.h>

/* ===========================================================================
 * Shared file-local helpers
 * =========================================================================== */

static char *cr_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static char *cr_dup_n(const char *s, size_t n) {
    char *p = (char *)malloc(n + 1);
    if (!p) return NULL;
    if (n) memcpy(p, s, n);
    p[n] = '\0';
    return p;
}

static bool cr_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) if (!isspace((unsigned char)*s)) return false;
    return true;
}

/* Case-insensitive equality (ASCII), used for OrdinalIgnoreCase maps. */
static bool cr_eq_ci(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

/* Case-insensitive substring test (ASCII). */
static bool cr_contains_ci(const char *haystack, const char *needle) {
    if (!haystack || !needle) return false;
    size_t hn = strlen(haystack), nn = strlen(needle);
    if (nn == 0) return true;
    if (nn > hn) return false;
    for (size_t i = 0; i + nn <= hn; ++i) {
        size_t j = 0;
        while (j < nn && tolower((unsigned char)haystack[i + j]) == tolower((unsigned char)needle[j])) ++j;
        if (j == nn) return true;
    }
    return false;
}

void ca_string_array_free_local(char **arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * Record frees
 * =========================================================================== */

void ca_causal_prediction_free(ca_causal_prediction_t *p) {
    if (!p) return;
    free(p->outcome);
    if (p->supporting_factors) {
        for (size_t i = 0; i < p->factor_count; ++i) free(p->supporting_factors[i]);
        free(p->supporting_factors);
    }
    memset(p, 0, sizeof(*p));
}

void ca_anticipated_need_free(ca_anticipated_need_t *n) {
    if (!n) return;
    free(n->description);
    n->description = NULL;
}

void ca_anticipated_need_free_array(ca_anticipated_need_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_anticipated_need_free(&arr[i]);
    free(arr);
}

void ca_self_reflection_free(ca_self_reflection_t *r) {
    if (!r) return;
    free(r->thought);
    r->thought = NULL;
}

void ca_other_mind_estimate_free(ca_other_mind_estimate_t *e) {
    if (!e) return;
    free(e->target_identifier);
    free(e->likely_belief_json);
    e->target_identifier = e->likely_belief_json = NULL;
}

/* ===========================================================================
 * Tolerant JSON scanner (object-property capture), matching the semantics of
 * System.Text.Json JsonDocument.Parse for the ExtractObservations path:
 *
 *   - The whole root must parse as a strict JSON object (any error → empty).
 *   - Each top-level property yields "name=value" where `value` is the
 *     JsonElement.ToString() rendering:
 *        string  → decoded content, no quotes
 *        number  → raw source text
 *        true    → "True"     false → "False"    null → "" (empty)
 *        object/array → the raw source slice (as written)
 * =========================================================================== */

typedef struct {
    const char *p;
    const char *end;
    bool        error;
} cr_scan_t;

static void cr_skip_ws(cr_scan_t *st) {
    while (st->p < st->end && isspace((unsigned char)*st->p)) st->p++;
}
static bool cr_at_end(cr_scan_t *st) { return st->p >= st->end; }

/* Decode a JSON string at '"'; returns malloc'd content, advances past close. */
static char *cr_parse_string(cr_scan_t *st) {
    if (cr_at_end(st) || *st->p != '"') { st->error = true; return NULL; }
    st->p++;
    size_t cap = 16, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) { st->error = true; return NULL; }
    while (!cr_at_end(st)) {
        char ch = *st->p++;
        if (ch == '"') { out[len] = '\0'; return out; }
        if (ch == '\\') {
            if (cr_at_end(st)) break;
            char esc = *st->p++;
            char decoded;
            switch (esc) {
                case '"':  decoded = '"';  break;
                case '\\': decoded = '\\'; break;
                case '/':  decoded = '/';  break;
                case 'b':  decoded = '\b'; break;
                case 'f':  decoded = '\f'; break;
                case 'n':  decoded = '\n'; break;
                case 'r':  decoded = '\r'; break;
                case 't':  decoded = '\t'; break;
                case 'u': {
                    if (st->end - st->p < 4) { free(out); st->error = true; return NULL; }
                    unsigned code = 0;
                    for (int i = 0; i < 4; ++i) {
                        char h = *st->p++;
                        code <<= 4;
                        if      (h >= '0' && h <= '9') code |= (unsigned)(h - '0');
                        else if (h >= 'a' && h <= 'f') code |= (unsigned)(h - 'a' + 10);
                        else if (h >= 'A' && h <= 'F') code |= (unsigned)(h - 'A' + 10);
                        else { free(out); st->error = true; return NULL; }
                    }
                    char utf[4]; int n;
                    if (code < 0x80) { utf[0] = (char)code; n = 1; }
                    else if (code < 0x800) {
                        utf[0] = (char)(0xC0 | (code >> 6));
                        utf[1] = (char)(0x80 | (code & 0x3F));
                        n = 2;
                    } else {
                        utf[0] = (char)(0xE0 | (code >> 12));
                        utf[1] = (char)(0x80 | ((code >> 6) & 0x3F));
                        utf[2] = (char)(0x80 | (code & 0x3F));
                        n = 3;
                    }
                    if (len + (size_t)n + 1 > cap) {
                        while (len + (size_t)n + 1 > cap) cap *= 2;
                        char *nb = (char *)realloc(out, cap);
                        if (!nb) { free(out); st->error = true; return NULL; }
                        out = nb;
                    }
                    memcpy(out + len, utf, (size_t)n);
                    len += (size_t)n;
                    continue;
                }
                default: free(out); st->error = true; return NULL;
            }
            if (len + 2 > cap) {
                cap *= 2;
                char *nb = (char *)realloc(out, cap);
                if (!nb) { free(out); st->error = true; return NULL; }
                out = nb;
            }
            out[len++] = decoded;
        } else {
            if (len + 2 > cap) {
                cap *= 2;
                char *nb = (char *)realloc(out, cap);
                if (!nb) { free(out); st->error = true; return NULL; }
                out = nb;
            }
            out[len++] = ch;
        }
    }
    free(out);
    st->error = true;
    return NULL;
}

/* Validate+consume a JSON number. */
static void cr_parse_number(cr_scan_t *st) {
    if (!cr_at_end(st) && *st->p == '-') st->p++;
    bool any = false;
    while (!cr_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; any = true; }
    if (!cr_at_end(st) && *st->p == '.') {
        st->p++;
        while (!cr_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; any = true; }
    }
    if (!cr_at_end(st) && (*st->p == 'e' || *st->p == 'E')) {
        st->p++;
        if (!cr_at_end(st) && (*st->p == '+' || *st->p == '-')) st->p++;
        bool ed = false;
        while (!cr_at_end(st) && isdigit((unsigned char)*st->p)) { st->p++; ed = true; }
        if (!ed) { st->error = true; return; }
    }
    if (!any) { st->error = true; return; }
}

static void cr_skip_value(cr_scan_t *st);

static void cr_skip_object(cr_scan_t *st) {
    st->p++; /* '{' */
    cr_skip_ws(st);
    if (!cr_at_end(st) && *st->p == '}') { st->p++; return; }
    for (;;) {
        cr_skip_ws(st);
        char *key = cr_parse_string(st);
        if (st->error) { free(key); return; }
        free(key);
        cr_skip_ws(st);
        if (cr_at_end(st) || *st->p != ':') { st->error = true; return; }
        st->p++;
        cr_skip_value(st);
        if (st->error) return;
        cr_skip_ws(st);
        if (cr_at_end(st)) { st->error = true; return; }
        if (*st->p == ',') { st->p++; continue; }
        if (*st->p == '}') { st->p++; return; }
        st->error = true; return;
    }
}

static void cr_skip_array(cr_scan_t *st) {
    st->p++; /* '[' */
    cr_skip_ws(st);
    if (!cr_at_end(st) && *st->p == ']') { st->p++; return; }
    for (;;) {
        cr_skip_value(st);
        if (st->error) return;
        cr_skip_ws(st);
        if (cr_at_end(st)) { st->error = true; return; }
        if (*st->p == ',') { st->p++; continue; }
        if (*st->p == ']') { st->p++; return; }
        st->error = true; return;
    }
}

static void cr_match_literal(cr_scan_t *st, const char *lit) {
    size_t n = strlen(lit);
    if ((size_t)(st->end - st->p) < n || strncmp(st->p, lit, n) != 0) { st->error = true; return; }
    st->p += n;
}

static void cr_skip_value(cr_scan_t *st) {
    cr_skip_ws(st);
    if (cr_at_end(st)) { st->error = true; return; }
    char ch = *st->p;
    if (ch == '"') { char *s = cr_parse_string(st); free(s); return; }
    if (ch == '{') { cr_skip_object(st); return; }
    if (ch == '[') { cr_skip_array(st); return; }
    if (ch == 't') { cr_match_literal(st, "true"); return; }
    if (ch == 'f') { cr_match_literal(st, "false"); return; }
    if (ch == 'n') { cr_match_literal(st, "null"); return; }
    if (ch == '-' || isdigit((unsigned char)ch)) { cr_parse_number(st); return; }
    st->error = true;
}

/* Render the value at the cursor as JsonElement.ToString() would, advancing past
 * it. Returns a fresh malloc'd string, or NULL on error (st->error set). */
static char *cr_render_value(cr_scan_t *st) {
    cr_skip_ws(st);
    if (cr_at_end(st)) { st->error = true; return NULL; }
    char ch = *st->p;
    if (ch == '"') {
        return cr_parse_string(st); /* decoded content, no quotes */
    }
    if (ch == 't') { cr_match_literal(st, "true");  return st->error ? NULL : cr_dup("True"); }
    if (ch == 'f') { cr_match_literal(st, "false"); return st->error ? NULL : cr_dup("False"); }
    if (ch == 'n') { cr_match_literal(st, "null");  return st->error ? NULL : cr_dup(""); }
    /* number / object / array → raw source slice */
    const char *start = st->p;
    if (ch == '{') cr_skip_object(st);
    else if (ch == '[') cr_skip_array(st);
    else if (ch == '-' || isdigit((unsigned char)ch)) cr_parse_number(st);
    else { st->error = true; return NULL; }
    if (st->error) return NULL;
    return cr_dup_n(start, (size_t)(st->p - start));
}

/* Parse the scenario JSON object, capturing "name=value" observation strings.
 * On any structural error (matching JsonDocument.Parse throwing) returns an empty
 * list. A non-object root also yields empty. Returns malloc'd array of owned
 * strings (or NULL when empty); *out_count set. */
static char **cr_extract_observations(const char *scenario_json, size_t *out_count) {
    *out_count = 0;
    if (cr_is_blank(scenario_json)) return NULL;

    cr_scan_t st;
    st.p = scenario_json;
    st.end = scenario_json + strlen(scenario_json);
    st.error = false;

    cr_skip_ws(&st);
    if (cr_at_end(&st) || *st.p != '{') return NULL; /* non-object → empty */

    char **items = NULL;
    size_t count = 0, cap = 0;

    st.p++; /* '{' */
    cr_skip_ws(&st);
    if (!cr_at_end(&st) && *st.p == '}') { st.p++; return NULL; /* {} → empty */ }

    for (;;) {
        cr_skip_ws(&st);
        char *name = cr_parse_string(&st);
        if (st.error) { free(name); break; }
        cr_skip_ws(&st);
        if (cr_at_end(&st) || *st.p != ':') { free(name); st.error = true; break; }
        st.p++;
        char *val = cr_render_value(&st);
        if (st.error) { free(name); free(val); break; }

        /* Build "name=value". */
        size_t nn = strlen(name), vn = val ? strlen(val) : 0;
        char *obs = (char *)malloc(nn + 1 + vn + 1);
        if (!obs) { free(name); free(val); st.error = true; break; }
        memcpy(obs, name, nn);
        obs[nn] = '=';
        if (vn) memcpy(obs + nn + 1, val, vn);
        obs[nn + 1 + vn] = '\0';
        free(name); free(val);

        if (count == cap) {
            size_t nc = cap ? cap * 2 : 8;
            char **na = (char **)realloc(items, nc * sizeof(*na));
            if (!na) { free(obs); st.error = true; break; }
            items = na; cap = nc;
        }
        items[count++] = obs;

        cr_skip_ws(&st);
        if (cr_at_end(&st)) { st.error = true; break; }
        if (*st.p == ',') { st.p++; continue; }
        if (*st.p == '}') { st.p++; break; }
        st.error = true; break;
    }

    if (st.error) {
        for (size_t i = 0; i < count; ++i) free(items[i]);
        free(items);
        return NULL; /* malformed → empty (mirrors the C# catch) */
    }
    *out_count = count;
    return items;
}

/* ===========================================================================
 * 5a. FrequencyWorldModel
 * ===========================================================================
 *
 * _counts: observation (CI key) -> { outcome (CI key) -> count }. Linear arrays.
 */

typedef struct {
    char   *outcome; /* owned */
    int64_t count;
} cr_outcome_count_t;

typedef struct {
    char               *observation; /* owned */
    cr_outcome_count_t *outcomes;
    size_t              count, cap;
} cr_obs_bucket_t;

struct ca_frequency_world_model {
    cr_obs_bucket_t *buckets;
    size_t           count, cap;
};

ca_frequency_world_model_t *ca_frequency_world_model_create(void) {
    return (ca_frequency_world_model_t *)calloc(1, sizeof(ca_frequency_world_model_t));
}

void ca_frequency_world_model_destroy(ca_frequency_world_model_t *m) {
    if (!m) return;
    for (size_t i = 0; i < m->count; ++i) {
        cr_obs_bucket_t *b = &m->buckets[i];
        free(b->observation);
        for (size_t j = 0; j < b->count; ++j) free(b->outcomes[j].outcome);
        free(b->outcomes);
    }
    free(m->buckets);
    free(m);
}

static cr_obs_bucket_t *fwm_get_or_add_bucket(ca_frequency_world_model_t *m, const char *obs) {
    for (size_t i = 0; i < m->count; ++i)
        if (cr_eq_ci(m->buckets[i].observation, obs)) return &m->buckets[i];
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 8;
        cr_obs_bucket_t *nb = (cr_obs_bucket_t *)realloc(m->buckets, nc * sizeof(*nb));
        if (!nb) return NULL;
        m->buckets = nb; m->cap = nc;
    }
    cr_obs_bucket_t *b = &m->buckets[m->count++];
    b->observation = cr_dup(obs);
    b->outcomes = NULL; b->count = 0; b->cap = 0;
    return b;
}

static void fwm_bucket_bump(cr_obs_bucket_t *b, const char *outcome) {
    for (size_t i = 0; i < b->count; ++i)
        if (cr_eq_ci(b->outcomes[i].outcome, outcome)) { b->outcomes[i].count++; return; }
    if (b->count == b->cap) {
        size_t nc = b->cap ? b->cap * 2 : 4;
        cr_outcome_count_t *no = (cr_outcome_count_t *)realloc(b->outcomes, nc * sizeof(*no));
        if (!no) return;
        b->outcomes = no; b->cap = nc;
    }
    b->outcomes[b->count].outcome = cr_dup(outcome);
    b->outcomes[b->count].count = 1;
    b->count++;
}

void ca_frequency_world_model_observe(ca_frequency_world_model_t *m,
                                      const char *const *observations, size_t count,
                                      const char *outcome) {
    if (!m || !observations || cr_is_blank(outcome)) return;
    for (size_t i = 0; i < count; ++i) {
        const char *obs = observations[i];
        if (!obs) continue;
        cr_obs_bucket_t *b = fwm_get_or_add_bucket(m, obs);
        if (b) fwm_bucket_bump(b, outcome);
    }
}

/* Aggregated tally entry (case-insensitive outcome key), preserving first-seen
 * order to match the C# Dictionary enumeration for a deterministic argmax tie. */
typedef struct { char *outcome; int64_t n; } cr_tally_t;

static bool fwm_predict_impl(const ca_frequency_world_model_t *m,
                             const char *scenario_json, ca_causal_prediction_t *out) {
    memset(out, 0, sizeof(*out));

    size_t obs_n = 0;
    char **obs = cr_extract_observations(scenario_json, &obs_n);

    cr_tally_t *tally = NULL; size_t tn = 0, tcap = 0;
    char **supporters = NULL; size_t sup_n = 0, sup_cap = 0;

    for (size_t i = 0; i < obs_n; ++i) {
        /* find matching bucket */
        const cr_obs_bucket_t *bucket = NULL;
        for (size_t k = 0; k < m->count; ++k)
            if (cr_eq_ci(m->buckets[k].observation, obs[i])) { bucket = &m->buckets[k]; break; }
        if (!bucket) continue;
        /* supporters.Add(obs) */
        if (sup_n == sup_cap) {
            size_t nc = sup_cap ? sup_cap * 2 : 8;
            char **ns = (char **)realloc(supporters, nc * sizeof(*ns));
            if (ns) { supporters = ns; sup_cap = nc; }
        }
        if (sup_n < sup_cap) supporters[sup_n++] = cr_dup(obs[i]);
        /* fold this bucket's outcome counts into the tally */
        for (size_t j = 0; j < bucket->count; ++j) {
            const char *oc = bucket->outcomes[j].outcome;
            int64_t add = bucket->outcomes[j].count;
            size_t f = (size_t)-1;
            for (size_t t = 0; t < tn; ++t) if (cr_eq_ci(tally[t].outcome, oc)) { f = t; break; }
            if (f == (size_t)-1) {
                if (tn == tcap) {
                    size_t nc = tcap ? tcap * 2 : 8;
                    cr_tally_t *ntl = (cr_tally_t *)realloc(tally, nc * sizeof(*ntl));
                    if (ntl) { tally = ntl; tcap = nc; }
                }
                if (tn < tcap) { tally[tn].outcome = cr_dup(oc); tally[tn].n = add; tn++; }
            } else {
                tally[f].n += add;
            }
        }
    }
    ca_string_array_free_local(obs, obs_n);

    if (tn == 0) {
        /* ("unknown", 0.5, supporters) */
        out->outcome = cr_dup("unknown");
        out->probability = 0.5;
        out->supporting_factors = supporters;
        out->factor_count = sup_n;
        for (size_t t = 0; t < tn; ++t) free(tally[t].outcome);
        free(tally);
        return true;
    }

    int64_t total = 0;
    for (size_t t = 0; t < tn; ++t) total += tally[t].n;
    /* argmax by count; first-seen wins ties (OrderByDescending is stable). */
    size_t best = 0;
    for (size_t t = 1; t < tn; ++t) if (tally[t].n > tally[best].n) best = t;

    out->outcome = cr_dup(tally[best].outcome);
    out->probability = total > 0 ? (double)tally[best].n / (double)total : 0.0;
    out->supporting_factors = supporters;
    out->factor_count = sup_n;

    for (size_t t = 0; t < tn; ++t) free(tally[t].outcome);
    free(tally);
    return true;
}

bool ca_frequency_world_model_predict(const ca_frequency_world_model_t *m,
                                      const char *scenario_json,
                                      ca_causal_prediction_t *out) {
    if (!m || !out) return false;
    return fwm_predict_impl(m, scenario_json, out);
}

/* ===========================================================================
 * 5b. BayesianWorldModel
 * ===========================================================================
 *
 * _outcomeCounts: outcome (CI) -> count
 * _condCounts:    outcome (CI) -> { observation (CI) -> count }
 * _vocab:         distinct observations (CI)
 */

typedef struct {
    char               *outcome;   /* owned */
    int64_t             count;
    cr_outcome_count_t *cond;      /* observation -> count (reuse outcome_count shape) */
    size_t              cond_n, cond_cap;
} cr_bayes_outcome_t;

struct ca_bayesian_world_model {
    cr_bayes_outcome_t *outcomes;
    size_t              count, cap;
    char              **vocab;      /* distinct observations */
    size_t              vocab_n, vocab_cap;
    int64_t             total_observations;
    double              alpha;
};

ca_bayesian_world_model_t *ca_bayesian_world_model_create(double laplace_alpha) {
    if (laplace_alpha <= 0) return NULL;
    ca_bayesian_world_model_t *m = (ca_bayesian_world_model_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->alpha = laplace_alpha;
    return m;
}

void ca_bayesian_world_model_destroy(ca_bayesian_world_model_t *m) {
    if (!m) return;
    for (size_t i = 0; i < m->count; ++i) {
        cr_bayes_outcome_t *o = &m->outcomes[i];
        free(o->outcome);
        for (size_t j = 0; j < o->cond_n; ++j) free(o->cond[j].outcome);
        free(o->cond);
    }
    free(m->outcomes);
    ca_string_array_free_local(m->vocab, m->vocab_n);
    free(m);
}

static cr_bayes_outcome_t *bwm_get_or_add_outcome(ca_bayesian_world_model_t *m, const char *outcome) {
    for (size_t i = 0; i < m->count; ++i)
        if (cr_eq_ci(m->outcomes[i].outcome, outcome)) return &m->outcomes[i];
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 8;
        cr_bayes_outcome_t *no = (cr_bayes_outcome_t *)realloc(m->outcomes, nc * sizeof(*no));
        if (!no) return NULL;
        m->outcomes = no; m->cap = nc;
    }
    cr_bayes_outcome_t *o = &m->outcomes[m->count++];
    o->outcome = cr_dup(outcome);
    o->count = 0; o->cond = NULL; o->cond_n = 0; o->cond_cap = 0;
    return o;
}

static void bwm_cond_bump(cr_bayes_outcome_t *o, const char *obs) {
    for (size_t i = 0; i < o->cond_n; ++i)
        if (cr_eq_ci(o->cond[i].outcome, obs)) { o->cond[i].count++; return; }
    if (o->cond_n == o->cond_cap) {
        size_t nc = o->cond_cap ? o->cond_cap * 2 : 4;
        cr_outcome_count_t *nn = (cr_outcome_count_t *)realloc(o->cond, nc * sizeof(*nn));
        if (!nn) return;
        o->cond = nn; o->cond_cap = nc;
    }
    o->cond[o->cond_n].outcome = cr_dup(obs);
    o->cond[o->cond_n].count = 1;
    o->cond_n++;
}

static void bwm_vocab_add(ca_bayesian_world_model_t *m, const char *obs) {
    for (size_t i = 0; i < m->vocab_n; ++i) if (cr_eq_ci(m->vocab[i], obs)) return;
    if (m->vocab_n == m->vocab_cap) {
        size_t nc = m->vocab_cap ? m->vocab_cap * 2 : 8;
        char **nv = (char **)realloc(m->vocab, nc * sizeof(*nv));
        if (!nv) return;
        m->vocab = nv; m->vocab_cap = nc;
    }
    m->vocab[m->vocab_n++] = cr_dup(obs);
}

void ca_bayesian_world_model_observe(ca_bayesian_world_model_t *m,
                                     const char *const *observations, size_t count,
                                     const char *outcome) {
    if (!m || !observations || cr_is_blank(outcome)) return;
    cr_bayes_outcome_t *o = bwm_get_or_add_outcome(m, outcome);
    if (!o) return;
    o->count++;
    m->total_observations++;
    for (size_t i = 0; i < count; ++i) {
        const char *obs = observations[i];
        if (cr_is_blank(obs)) continue;
        bwm_cond_bump(o, obs);
        bwm_vocab_add(m, obs);
    }
}

static bool bwm_predict_impl(const ca_bayesian_world_model_t *m,
                             const char *scenario_json, ca_causal_prediction_t *out) {
    memset(out, 0, sizeof(*out));

    size_t obs_n = 0;
    char **obs = cr_extract_observations(scenario_json, &obs_n);

    if (obs_n == 0 || m->count == 0) {
        ca_string_array_free_local(obs, obs_n);
        out->outcome = cr_dup("unknown");
        out->probability = 0.5;
        out->supporting_factors = NULL;
        out->factor_count = 0;
        return true;
    }

    double vocab_size = (double)(m->vocab_n > 1 ? m->vocab_n : 1);
    double total_ex   = (double)(m->total_observations > 1 ? m->total_observations : 1);

    /* Score every outcome by log-posterior. */
    double *logpost = (double *)malloc(m->count * sizeof(double));
    if (!logpost) { ca_string_array_free_local(obs, obs_n); return false; }

    for (size_t i = 0; i < m->count; ++i) {
        const cr_bayes_outcome_t *o = &m->outcomes[i];
        double log_prior = log(((double)o->count + m->alpha) /
                               (total_ex + m->alpha * (double)m->count));
        int64_t total_for_outcome = 0;
        for (size_t j = 0; j < o->cond_n; ++j) total_for_outcome += o->cond[j].count;
        double log_likelihood = 0.0;
        for (size_t k = 0; k < obs_n; ++k) {
            int64_t n = 0;
            for (size_t j = 0; j < o->cond_n; ++j)
                if (cr_eq_ci(o->cond[j].outcome, obs[k])) { n = o->cond[j].count; break; }
            double p = ((double)n + m->alpha) /
                       ((double)total_for_outcome + m->alpha * vocab_size);
            log_likelihood += log(p);
        }
        logpost[i] = log_prior + log_likelihood;
    }

    /* Softmax over log-posteriors; argmax (first-seen wins ties). */
    double max_lp = logpost[0];
    size_t best = 0;
    for (size_t i = 1; i < m->count; ++i)
        if (logpost[i] > max_lp) { max_lp = logpost[i]; best = i; }
    /* (argmax and max coincide since OrderByDescending.First == Max) */
    double exp_sum = 0.0;
    for (size_t i = 0; i < m->count; ++i) exp_sum += exp(logpost[i] - max_lp);
    double prob = exp(logpost[best] - max_lp) / exp_sum;

    out->outcome = cr_dup(m->outcomes[best].outcome);
    out->probability = prob;
    /* SupportingFactors == the extracted observations (transfer ownership). */
    out->supporting_factors = obs;
    out->factor_count = obs_n;

    free(logpost);
    return true;
}

bool ca_bayesian_world_model_predict(const ca_bayesian_world_model_t *m,
                                     const char *scenario_json,
                                     ca_causal_prediction_t *out) {
    if (!m || !out) return false;
    return bwm_predict_impl(m, scenario_json, out);
}

/* ===========================================================================
 * 14a. HistogramPredictiveEngine
 * ===========================================================================
 *
 * _hist: description (CI) -> long[24*7]. Slot = dayOfWeek*24 + hourUtc.
 */

#define CR_HIST_SLOTS (24 * 7)

typedef struct {
    char   *description;      /* owned */
    int64_t slots[CR_HIST_SLOTS];
} cr_hist_entry_t;

struct ca_histogram_predictive_engine {
    cr_hist_entry_t *entries;
    size_t           count, cap;
};

ca_histogram_predictive_engine_t *ca_histogram_predictive_engine_create(void) {
    return (ca_histogram_predictive_engine_t *)calloc(1, sizeof(ca_histogram_predictive_engine_t));
}

void ca_histogram_predictive_engine_destroy(ca_histogram_predictive_engine_t *e) {
    if (!e) return;
    for (size_t i = 0; i < e->count; ++i) free(e->entries[i].description);
    free(e->entries);
    free(e);
}

/* Day-of-week/hour from Unix ms UTC. .NET DayOfWeek: Sunday=0..Saturday=6.
 * The Unix epoch (1970-01-01) is a Thursday (=4). */
static void cr_utc_dow_hour(int64_t ms, int *dow, int *hour) {
    int64_t secs = ms / 1000;
    if (ms < 0 && ms % 1000 != 0) secs -= 1;       /* floor division for negatives */
    int64_t days = secs / 86400;
    int64_t rem  = secs % 86400;
    if (rem < 0) { rem += 86400; days -= 1; }
    *hour = (int)(rem / 3600);
    int64_t d = (days % 7 + 7) % 7;                /* 0 = Thursday (epoch) */
    *dow = (int)((d + 4) % 7);                     /* shift so Sunday=0 */
}

static cr_hist_entry_t *hist_get_or_add(ca_histogram_predictive_engine_t *e, const char *desc) {
    for (size_t i = 0; i < e->count; ++i)
        if (cr_eq_ci(e->entries[i].description, desc)) return &e->entries[i];
    if (e->count == e->cap) {
        size_t nc = e->cap ? e->cap * 2 : 8;
        cr_hist_entry_t *ne = (cr_hist_entry_t *)realloc(e->entries, nc * sizeof(*ne));
        if (!ne) return NULL;
        e->entries = ne; e->cap = nc;
    }
    cr_hist_entry_t *h = &e->entries[e->count++];
    h->description = cr_dup(desc);
    memset(h->slots, 0, sizeof(h->slots));
    return h;
}

void ca_histogram_predictive_engine_observe(ca_histogram_predictive_engine_t *e,
                                            const char *description, int64_t at_ms) {
    if (!e || cr_is_blank(description)) return;
    cr_hist_entry_t *h = hist_get_or_add(e, description);
    if (!h) return;
    int dow, hour;
    cr_utc_dow_hour(at_ms, &dow, &hour);
    h->slots[dow * 24 + hour]++;
}

/* insertion sort by probability descending (stable). */
static void need_sort_desc(ca_anticipated_need_t *a, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_anticipated_need_t key = a[i];
        size_t j = i;
        while (j > 0 && a[j - 1].probability < key.probability) { a[j] = a[j - 1]; --j; }
        a[j] = key;
    }
}

ca_anticipated_need_t *ca_histogram_predictive_engine_anticipate(
    const ca_histogram_predictive_engine_t *e,
    int horizon_minutes, int64_t now_ms, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!e || horizon_minutes <= 0) { if (out_count) *out_count = (size_t)-1; return NULL; }

    ca_anticipated_need_t *res = NULL; size_t rn = 0, rcap = 0;
    for (size_t i = 0; i < e->count; ++i) {
        const cr_hist_entry_t *h = &e->entries[i];
        int64_t total = 0;
        for (int s = 0; s < CR_HIST_SLOTS; ++s) total += h->slots[s];
        int64_t upcoming = 0;
        for (int mmin = 0; mmin <= horizon_minutes; mmin += 30) {
            int64_t when = now_ms + (int64_t)mmin * 60000;
            int dow, hour;
            cr_utc_dow_hour(when, &dow, &hour);
            upcoming += h->slots[dow * 24 + hour];
        }
        if (total == 0 || upcoming == 0) continue;
        if (rn == rcap) {
            size_t nc = rcap ? rcap * 2 : 8;
            ca_anticipated_need_t *nr = (ca_anticipated_need_t *)realloc(res, nc * sizeof(*nr));
            if (!nr) { ca_anticipated_need_free_array(res, rn); return NULL; }
            res = nr; rcap = nc;
        }
        res[rn].description = cr_dup(h->description);
        res[rn].expected_by_ms = now_ms + (int64_t)(horizon_minutes / 2) * 60000;
        res[rn].probability = (double)upcoming / (double)total;
        rn++;
    }
    need_sort_desc(res, rn);
    if (out_count) *out_count = rn;
    return res; /* NULL + 0 when empty */
}

/* ===========================================================================
 * 14b. SequencePredictiveEngine
 * ===========================================================================
 *
 * _transitions: context-key -> { next event -> count }   (Ordinal keys)
 * _interArrivals: event -> (count, sumSeconds)
 * _history: (event, at_ms) timeline
 */

typedef struct { char *event; int64_t at_ms; } cr_seq_hist_t;

typedef struct {
    char               *key;       /* context key "a|b|c" (owned) */
    cr_outcome_count_t *nexts;     /* next event -> count */
    size_t              n, cap;
} cr_seq_trans_t;

typedef struct {
    char   *event;    /* owned */
    int64_t count;
    double  sum_secs;
} cr_seq_inter_t;

struct ca_sequence_predictive_engine {
    cr_seq_hist_t  *history; size_t hist_n, hist_cap;
    cr_seq_trans_t *trans;   size_t trans_n, trans_cap;
    cr_seq_inter_t *inter;   size_t inter_n, inter_cap;
    int             order;
};

ca_sequence_predictive_engine_t *ca_sequence_predictive_engine_create(int order) {
    if (order < 1 || order > 6) return NULL;
    ca_sequence_predictive_engine_t *e =
        (ca_sequence_predictive_engine_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->order = order;
    return e;
}

void ca_sequence_predictive_engine_destroy(ca_sequence_predictive_engine_t *e) {
    if (!e) return;
    for (size_t i = 0; i < e->hist_n; ++i) free(e->history[i].event);
    free(e->history);
    for (size_t i = 0; i < e->trans_n; ++i) {
        free(e->trans[i].key);
        for (size_t j = 0; j < e->trans[i].n; ++j) free(e->trans[i].nexts[j].outcome);
        free(e->trans[i].nexts);
    }
    free(e->trans);
    for (size_t i = 0; i < e->inter_n; ++i) free(e->inter[i].event);
    free(e->inter);
    free(e);
}

/* Ordinal (case-sensitive) key equality. */
static bool cr_eq_ord(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    return strcmp(a, b) == 0;
}

static cr_seq_trans_t *seq_get_or_add_trans(ca_sequence_predictive_engine_t *e, char *key /*owned*/) {
    for (size_t i = 0; i < e->trans_n; ++i)
        if (cr_eq_ord(e->trans[i].key, key)) { free(key); return &e->trans[i]; }
    if (e->trans_n == e->trans_cap) {
        size_t nc = e->trans_cap ? e->trans_cap * 2 : 8;
        cr_seq_trans_t *nt = (cr_seq_trans_t *)realloc(e->trans, nc * sizeof(*nt));
        if (!nt) { free(key); return NULL; }
        e->trans = nt; e->trans_cap = nc;
    }
    cr_seq_trans_t *t = &e->trans[e->trans_n++];
    t->key = key; t->nexts = NULL; t->n = 0; t->cap = 0;
    return t;
}

static void seq_trans_bump(cr_seq_trans_t *t, const char *next) {
    for (size_t i = 0; i < t->n; ++i)
        if (cr_eq_ord(t->nexts[i].outcome, next)) { t->nexts[i].count++; return; }
    if (t->n == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 4;
        cr_outcome_count_t *nn = (cr_outcome_count_t *)realloc(t->nexts, nc * sizeof(*nn));
        if (!nn) return;
        t->nexts = nn; t->cap = nc;
    }
    t->nexts[t->n].outcome = cr_dup(next);
    t->nexts[t->n].count = 1;
    t->n++;
}

/* Join history[start..start+k) events with '|'. Returns owned string. */
static char *seq_join_key(const cr_seq_hist_t *hist, size_t start, size_t k) {
    size_t total = 0;
    for (size_t i = 0; i < k; ++i) total += strlen(hist[start + i].event) + 1;
    char *key = (char *)malloc(total + 1);
    if (!key) return NULL;
    size_t pos = 0;
    for (size_t i = 0; i < k; ++i) {
        if (i) key[pos++] = '|';
        size_t l = strlen(hist[start + i].event);
        memcpy(key + pos, hist[start + i].event, l);
        pos += l;
    }
    key[pos] = '\0';
    return key;
}

/* Join from a plain array of event strings. */
static char *seq_join_events(const char *const *events, size_t start, size_t k) {
    size_t total = 0;
    for (size_t i = 0; i < k; ++i) total += strlen(events[start + i]) + 1;
    char *key = (char *)malloc(total + 1);
    if (!key) return NULL;
    size_t pos = 0;
    for (size_t i = 0; i < k; ++i) {
        if (i) key[pos++] = '|';
        size_t l = strlen(events[start + i]);
        memcpy(key + pos, events[start + i], l);
        pos += l;
    }
    key[pos] = '\0';
    return key;
}

void ca_sequence_predictive_engine_observe(ca_sequence_predictive_engine_t *e,
                                           const char *event, int64_t at_ms) {
    if (!e || cr_is_blank(event)) return;
    /* append to history */
    if (e->hist_n == e->hist_cap) {
        size_t nc = e->hist_cap ? e->hist_cap * 2 : 16;
        cr_seq_hist_t *nh = (cr_seq_hist_t *)realloc(e->history, nc * sizeof(*nh));
        if (!nh) return;
        e->history = nh; e->hist_cap = nc;
    }
    e->history[e->hist_n].event = cr_dup(event);
    e->history[e->hist_n].at_ms = at_ms;
    e->hist_n++;

    /* n-gram contexts up to order: for k in 1.._order while history.Count > k */
    for (int k = 1; k <= e->order && (size_t)e->hist_n > (size_t)k; ++k) {
        /* contextStart = history.Count - 1 - k */
        long context_start = (long)e->hist_n - 1 - k;
        if (context_start < 0) break;
        char *key = seq_join_key(e->history, (size_t)context_start, (size_t)k);
        if (!key) continue;
        cr_seq_trans_t *t = seq_get_or_add_trans(e, key); /* takes ownership of key */
        if (t) seq_trans_bump(t, event);
    }

    /* inter-arrival: only when the immediately-preceding event equals this one */
    if (e->hist_n >= 2) {
        cr_seq_hist_t *last = &e->history[e->hist_n - 2];
        if (cr_eq_ord(last->event, event)) {
            double gap = (double)(at_ms - last->at_ms) / 1000.0;
            /* find or add inter-arrival entry */
            cr_seq_inter_t *it = NULL;
            for (size_t i = 0; i < e->inter_n; ++i)
                if (cr_eq_ord(e->inter[i].event, event)) { it = &e->inter[i]; break; }
            if (!it) {
                if (e->inter_n == e->inter_cap) {
                    size_t nc = e->inter_cap ? e->inter_cap * 2 : 8;
                    cr_seq_inter_t *ni = (cr_seq_inter_t *)realloc(e->inter, nc * sizeof(*ni));
                    if (!ni) return;
                    e->inter = ni; e->inter_cap = nc;
                }
                it = &e->inter[e->inter_n++];
                it->event = cr_dup(event);
                it->count = 1;
                it->sum_secs = gap;
            } else {
                it->count += 1;
                it->sum_secs += gap;
            }
        }
    }
}

/* score accumulator: next-event (Ordinal) -> weighted probability sum. */
typedef struct { char *event; double score; } cr_seq_score_t;

ca_anticipated_need_t *ca_sequence_predictive_engine_anticipate(
    const ca_sequence_predictive_engine_t *e,
    int horizon_minutes, int64_t now_ms, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!e || horizon_minutes <= 0) { if (out_count) *out_count = (size_t)-1; return NULL; }

    if (e->hist_n == 0) return NULL; /* empty */

    /* context = most-recent min(order, count) events. */
    size_t context_len = (size_t)e->order < e->hist_n ? (size_t)e->order : e->hist_n;
    /* materialise context event pointers */
    const char **context = (const char **)malloc(context_len * sizeof(char *));
    if (!context) return NULL;
    for (size_t i = 0; i < context_len; ++i)
        context[i] = e->history[e->hist_n - context_len + i].event;

    cr_seq_score_t *scores = NULL; size_t sc_n = 0, sc_cap = 0;

    /* back off from longest (k=context_len) to shortest (k=1). */
    for (size_t k = context_len; k >= 1; --k) {
        char *key = seq_join_events(context, context_len - k, k);
        if (!key) break;
        const cr_seq_trans_t *bucket = NULL;
        for (size_t i = 0; i < e->trans_n; ++i)
            if (cr_eq_ord(e->trans[i].key, key)) { bucket = &e->trans[i]; break; }
        free(key);
        if (!bucket) { if (k == 1) break; else continue; }
        int64_t total_for_ctx = 0;
        for (size_t j = 0; j < bucket->n; ++j) total_for_ctx += bucket->nexts[j].count;
        if (total_for_ctx == 0) { if (k == 1) break; else continue; }
        double weight = pow(2.0, (double)k);
        for (size_t j = 0; j < bucket->n; ++j) {
            const char *next = bucket->nexts[j].outcome;
            double prob = (double)bucket->nexts[j].count / (double)total_for_ctx;
            size_t f = (size_t)-1;
            for (size_t s = 0; s < sc_n; ++s) if (cr_eq_ord(scores[s].event, next)) { f = s; break; }
            if (f == (size_t)-1) {
                if (sc_n == sc_cap) {
                    size_t nc = sc_cap ? sc_cap * 2 : 8;
                    cr_seq_score_t *ns = (cr_seq_score_t *)realloc(scores, nc * sizeof(*ns));
                    if (!ns) break;
                    scores = ns; sc_cap = nc;
                }
                if (sc_n < sc_cap) { scores[sc_n].event = cr_dup(next); scores[sc_n].score = weight * prob; sc_n++; }
            } else {
                scores[f].score += weight * prob;
            }
        }
        if (k == 1) break; /* avoid size_t underflow */
    }
    free(context);

    if (sc_n == 0) {
        free(scores);
        return NULL; /* empty */
    }

    double total_weight = 0.0;
    for (size_t s = 0; s < sc_n; ++s) total_weight += scores[s].score;
    double horizon_sec = (double)horizon_minutes * 60.0;

    /* Sort scores by value descending (stable) to match OrderByDescending. */
    for (size_t i = 1; i < sc_n; ++i) {
        cr_seq_score_t key = scores[i];
        size_t j = i;
        while (j > 0 && scores[j - 1].score < key.score) { scores[j] = scores[j - 1]; --j; }
        scores[j] = key;
    }

    ca_anticipated_need_t *res = NULL; size_t rn = 0, rcap = 0;
    for (size_t s = 0; s < sc_n; ++s) {
        double prob = total_weight != 0.0 ? scores[s].score / total_weight : 0.0;
        if (prob <= 0) continue;
        /* mean inter-arrival, or horizonSec*0.5 default */
        double mean_interval = horizon_sec * 0.5;
        for (size_t i = 0; i < e->inter_n; ++i)
            if (cr_eq_ord(e->inter[i].event, scores[s].event)) {
                if (e->inter[i].count > 0) mean_interval = e->inter[i].sum_secs / (double)e->inter[i].count;
                break;
            }
        if (mean_interval > horizon_sec) continue;
        if (rn == rcap) {
            size_t nc = rcap ? rcap * 2 : 8;
            ca_anticipated_need_t *nr = (ca_anticipated_need_t *)realloc(res, nc * sizeof(*nr));
            if (!nr) break;
            res = nr; rcap = nc;
        }
        res[rn].description = cr_dup(scores[s].event);
        res[rn].expected_by_ms = now_ms + (int64_t)llround(mean_interval * 1000.0);
        res[rn].probability = prob;
        rn++;
    }

    for (size_t s = 0; s < sc_n; ++s) free(scores[s].event);
    free(scores);

    if (out_count) *out_count = rn;
    return res; /* NULL + 0 when nothing survived the horizon filter */
}

/* ===========================================================================
 * 13a. TemplateInnerMonologue
 * ===========================================================================
 */

static const char *const CR_FRAMES[] = {
    "Observation: {summary}. Implication: this likely means {direction}.",
    "Looking at {summary}, the salient pattern is {direction}.",
    "Given {summary}, my next step is to {direction}.",
};

/* Deterministic FNV-1a (the C# uses randomised String.GetHashCode; we pick a
 * fixed hash so the C port is deterministic — see the header note). */
static uint32_t cr_fnv1a(const char *s) {
    uint32_t h = 2166136261u;
    for (; s && *s; ++s) { h ^= (unsigned char)*s; h *= 16777619u; }
    return h;
}

/* Summarise: replace {}[]" with spaces, keep first 12 whitespace-split tokens,
 * join with single spaces. */
static char *cr_summarise(const char *json) {
    size_t n = strlen(json);
    char *clean = (char *)malloc(n + 1);
    if (!clean) return cr_dup("");
    for (size_t i = 0; i < n; ++i) {
        char c = json[i];
        clean[i] = (c == '{' || c == '}' || c == '[' || c == ']' || c == '"') ? ' ' : c;
    }
    clean[n] = '\0';

    /* accumulate up to 12 tokens */
    size_t out_cap = n + 1, out_len = 0;
    char *out = (char *)malloc(out_cap);
    if (!out) { free(clean); return cr_dup(""); }
    out[0] = '\0';
    int taken = 0;
    size_t i = 0;
    while (i < n && taken < 12) {
        while (i < n && isspace((unsigned char)clean[i])) ++i; /* skip ws */
        if (i >= n) break;
        size_t start = i;
        while (i < n && !isspace((unsigned char)clean[i])) ++i;
        size_t tok_len = i - start;
        if (tok_len == 0) break;
        if (taken > 0) out[out_len++] = ' ';
        memcpy(out + out_len, clean + start, tok_len);
        out_len += tok_len;
        out[out_len] = '\0';
        ++taken;
    }
    free(clean);
    return out;
}

static const char *cr_infer_direction(const char *json) {
    if (cr_contains_ci(json, "error")) return "diagnose the failure first";
    if (cr_contains_ci(json, "goal"))  return "advance toward the stated goal";
    if (cr_contains_ci(json, "user"))  return "respond to the user";
    return "gather more context";
}

/* Replace every occurrence of `token` in `src` with `rep`; returns owned string. */
static char *cr_replace_all(const char *src, const char *token, const char *rep) {
    size_t tl = strlen(token), rl = strlen(rep);
    size_t cap = strlen(src) + 1, len = 0;
    char *out = (char *)malloc(cap);
    if (!out) return NULL;
    const char *p = src;
    while (*p) {
        if (tl && strncmp(p, token, tl) == 0) {
            if (len + rl + 1 > cap) { while (len + rl + 1 > cap) cap *= 2; char *nb = realloc(out, cap); if (!nb) { free(out); return NULL; } out = nb; }
            memcpy(out + len, rep, rl); len += rl; p += tl;
        } else {
            if (len + 2 > cap) { cap *= 2; char *nb = realloc(out, cap); if (!nb) { free(out); return NULL; } out = nb; }
            out[len++] = *p++;
        }
    }
    out[len] = '\0';
    return out;
}

bool ca_template_inner_monologue_reflect(const char *context_json, int64_t at_ms,
                                         ca_self_reflection_t *out) {
    if (!context_json || !out) return false;
    memset(out, 0, sizeof(*out));

    char *summary = cr_summarise(context_json);
    const char *direction = cr_infer_direction(context_json);
    uint32_t seed = cr_fnv1a(context_json) & 0x7fffffffu;
    size_t frames_n = sizeof(CR_FRAMES) / sizeof(CR_FRAMES[0]);
    const char *frame = CR_FRAMES[seed % frames_n];

    char *tmp = cr_replace_all(frame, "{summary}", summary ? summary : "");
    free(summary);
    if (!tmp) return false;
    char *thought = cr_replace_all(tmp, "{direction}", direction);
    free(tmp);
    if (!thought) return false;

    out->thought = thought;
    out->at_ms = at_ms;
    return true;
}

/* ===========================================================================
 * 13b. ReasoningLoopInnerMonologue
 * ===========================================================================
 */

static const char CR_REASONING_SYSTEM_PROMPT[] =
    "You are this user's inner monologue. Reason carefully before responding. "
    "Use <think>...</think> blocks for chain-of-thought. The visible answer "
    "afterwards should be short and reflective — not a solution, an observation.";

const char *ca_reasoning_inner_monologue_system_prompt(void) {
    return CR_REASONING_SYSTEM_PROMPT;
}

/* Accumulator threaded through the fragment callback. */
typedef struct {
    char  *reasoning; size_t r_len, r_cap;
    char  *content;   size_t c_len, c_cap;
} cr_reason_sink_t;

static void cr_sink_append(char **buf, size_t *len, size_t *cap, const char *text) {
    if (!text) return;
    size_t tl = strlen(text);
    if (*len + tl + 1 > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (*len + tl + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(*buf, nc);
        if (!nb) return;
        *buf = nb; *cap = nc;
    }
    memcpy(*buf + *len, text, tl);
    *len += tl;
    (*buf)[*len] = '\0';
}

static void cr_reason_fragment_cb(const ca_chat_fragment_t *fragment, void *userdata) {
    cr_reason_sink_t *sink = (cr_reason_sink_t *)userdata;
    if (!fragment || !fragment->text) return;
    if (fragment->kind == CA_CHAT_FRAGMENT_REASONING)
        cr_sink_append(&sink->reasoning, &sink->r_len, &sink->r_cap, fragment->text);
    else
        cr_sink_append(&sink->content, &sink->c_len, &sink->c_cap, fragment->text);
}

/* Trim ASCII whitespace in place, returning an owned trimmed copy. NULL-safe. */
static char *cr_trim_dup(const char *s) {
    if (!s) return cr_dup("");
    const char *a = s;
    while (*a && isspace((unsigned char)*a)) ++a;
    const char *b = s + strlen(s);
    while (b > a && isspace((unsigned char)*(b - 1))) --b;
    return cr_dup_n(a, (size_t)(b - a));
}

bool ca_reasoning_inner_monologue_reflect(ca_reasoning_stream_fn driver, void *driver_user,
                                          const char *context_json, int64_t at_ms,
                                          ca_self_reflection_t *out) {
    if (!driver || !context_json || !out) return false;
    memset(out, 0, sizeof(*out));

    /* Build the user turn: "Context (raw JSON):\n{ctx}\n\nReflect on this in 2-3 sentences." */
    static const char *pre  = "Context (raw JSON):\n";
    static const char *post = "\n\nReflect on this in 2-3 sentences.";
    size_t ul = strlen(pre) + strlen(context_json) + strlen(post) + 1;
    char *user_turn = (char *)malloc(ul);
    if (!user_turn) return false;
    snprintf(user_turn, ul, "%s%s%s", pre, context_json, post);

    ca_chat_message_t messages[2];
    messages[0].role = CA_ROLE_SYSTEM;
    messages[0].content = CR_REASONING_SYSTEM_PROMPT;
    messages[0].created_at = 0;
    messages[1].role = CA_ROLE_USER;
    messages[1].content = user_turn;
    messages[1].created_at = 0;

    ca_generation_options_t options;
    ca_generation_options_init(&options);
    options.max_tokens = 256;
    options.temperature = 0.5f;
    options.include_reasoning = 1;

    cr_reason_sink_t sink = {0};
    driver(driver_user, messages, 2, &options, cr_reason_fragment_cb, &sink);

    free(user_turn);

    /* Prefer reasoning trace, else content, else "(no inner state)". */
    char *thought;
    if (sink.r_len > 0) thought = cr_trim_dup(sink.reasoning);
    else                thought = cr_trim_dup(sink.content);
    free(sink.reasoning);
    free(sink.content);
    if (!thought) return false;
    if (thought[0] == '\0') { free(thought); thought = cr_dup("(no inner state)"); }

    out->thought = thought;
    out->at_ms = at_ms;
    return true;
}

/* ===========================================================================
 * 10. BeliefTrackerTheoryOfMind
 * ===========================================================================
 *
 * Regex: \b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)  (IgnoreCase)
 * We scan for these verb stems at word boundaries, then capture the claim up to
 * the next . ; ! or ? and trim it (regex . excludes \n as well? — .NET without
 * Singleline: '.' does NOT match \n, and the negated class [^.;!?] DOES match \n.
 * The class is negated, so newlines are included in the claim.)  Then trim per
 * Match.Value.Trim() semantics (ASCII whitespace).
 */

/* The five verbs with their allowed optional trailing 's'. Order matters for the
 * left-to-right scan (matches .NET Regex leftmost-longest-alternative at each pos). */
typedef struct { const char *stem; bool optional_s; } cr_verb_t;

/* thinks? believes? wants? fears? hopes?  → stem + optional 's'.
 * "believes?" means "believe" + optional 's'. */
static const cr_verb_t CR_VERBS[] = {
    { "think",   true },
    { "believe", true },
    { "want",    true },
    { "fear",    true },
    { "hope",    true },
};
static const size_t CR_VERBS_N = sizeof(CR_VERBS) / sizeof(CR_VERBS[0]);

static bool cr_is_word_char(char c) {
    return isalnum((unsigned char)c) || c == '_';
}

/* Try to match a verb token starting at text[i] (case-insensitive), requiring a
 * word boundary before i. On success returns the verb index and sets *tok_end to
 * one past the matched verb (including the optional trailing 's'); else -1. */
static int cr_match_verb(const char *text, size_t len, size_t i, size_t *tok_end) {
    /* word boundary before: previous char is non-word (or start). */
    if (i > 0 && cr_is_word_char(text[i - 1])) return -1;
    for (size_t v = 0; v < CR_VERBS_N; ++v) {
        size_t sl = strlen(CR_VERBS[v].stem);
        if (i + sl > len) continue;
        bool ok = true;
        for (size_t k = 0; k < sl; ++k)
            if (tolower((unsigned char)text[i + k]) != CR_VERBS[v].stem[k]) { ok = false; break; }
        if (!ok) continue;
        size_t end = i + sl;
        /* optional 's' */
        if (end < len && (text[end] == 's' || text[end] == 'S')) end++;
        /* \s+ must follow: at least one whitespace char. Also the matched verb must
         * end on a word boundary (the next char is whitespace here, which is fine). */
        if (end < len && isspace((unsigned char)text[end])) {
            *tok_end = end;
            return (int)v;
        }
        /* If no whitespace follows, the \s+ fails → not a match at this alternative.
         * .NET would try the shorter alternative (without 's'); emulate by retrying
         * with the stem-only end when we consumed an 's'. */
        if (end > i + sl) {
            size_t end2 = i + sl;
            if (end2 < len && isspace((unsigned char)text[end2])) { *tok_end = end2; return (int)v; }
        }
        /* else: this verb alternative fails; keep trying others (rare overlap). */
    }
    return -1;
}

/* Belief accumulator: "verb:claim" (CI key) -> weight sum, first-seen order. */
typedef struct { char *key; double weight; } cr_belief_t;

/* Emit "\uXXXX" (uppercase hex) for a UTF-16 code unit. */
static void cr_emit_u(char **buf, size_t *len, size_t *cap, unsigned unit) {
    char tmp[8];
    snprintf(tmp, sizeof(tmp), "\\u%04X", unit & 0xFFFFu);
    for (size_t k = 0; k < 6; ++k) {
        if (*len + 2 > *cap) {
            size_t nc = *cap ? *cap * 2 : 64;
            char *nb = (char *)realloc(*buf, nc);
            if (!nb) return;
            *buf = nb; *cap = nc;
        }
        (*buf)[(*len)++] = tmp[k];
        (*buf)[*len] = '\0';
    }
}

static void cr_emit_raw(char **buf, size_t *len, size_t *cap, const char *s, size_t n) {
    if (*len + n + 1 > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (*len + n + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(*buf, nc);
        if (!nb) return;
        *buf = nb; *cap = nc;
    }
    memcpy(*buf + *len, s, n);
    *len += n;
    (*buf)[*len] = '\0';
}

/* Append `s` (UTF-8) escaped exactly as System.Text.Json's default encoder
 * (JavaScriptEncoder.Default) writes it:
 *   \\ \b \t \n \f \r as short escapes; " → "; other C0 controls → \uXXXX;
 *   the HTML/JS-sensitive ASCII set < > & ' ` + → \uXXXX (uppercase); everything
 *   else printable ASCII verbatim; and every NON-ASCII code point → \uXXXX per
 *   UTF-16 unit (surrogate pairs for astral). Hex digits are UPPERCASE. */
static void cr_json_escape_append(char **buf, size_t *len, size_t *cap, const char *s) {
    const unsigned char *p = (const unsigned char *)s;
    while (*p) {
        unsigned char c = *p;
        if (c < 0x80) {
            /* ASCII */
            switch (c) {
                case '\\': cr_emit_raw(buf, len, cap, "\\\\", 2); ++p; continue;
                case '\b': cr_emit_raw(buf, len, cap, "\\b", 2);  ++p; continue;
                case '\t': cr_emit_raw(buf, len, cap, "\\t", 2);  ++p; continue;
                case '\n': cr_emit_raw(buf, len, cap, "\\n", 2);  ++p; continue;
                case '\f': cr_emit_raw(buf, len, cap, "\\f", 2);  ++p; continue;
                case '\r': cr_emit_raw(buf, len, cap, "\\r", 2);  ++p; continue;
                default: break;
            }
            if (c < 0x20 || c == '"' || c == '<' || c == '>' || c == '&' ||
                c == '\'' || c == '`' || c == '+') {
                cr_emit_u(buf, len, cap, c);
            } else {
                char ch = (char)c;
                cr_emit_raw(buf, len, cap, &ch, 1);
            }
            ++p;
            continue;
        }
        /* Decode one UTF-8 sequence → code point, then emit \uXXXX per UTF-16 unit. */
        unsigned cp; int adv;
        if ((c & 0xE0) == 0xC0 && p[1]) { cp = ((c & 0x1Fu) << 6) | (p[1] & 0x3Fu); adv = 2; }
        else if ((c & 0xF0) == 0xE0 && p[1] && p[2]) {
            cp = ((c & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu); adv = 3;
        } else if ((c & 0xF8) == 0xF0 && p[1] && p[2] && p[3]) {
            cp = ((c & 0x07u) << 18) | ((p[1] & 0x3Fu) << 12) |
                 ((p[2] & 0x3Fu) << 6) | (p[3] & 0x3Fu); adv = 4;
        } else { cp = c; adv = 1; } /* malformed lead byte: treat as-is */

        if (cp <= 0xFFFF) {
            cr_emit_u(buf, len, cap, cp);
        } else {
            unsigned v = cp - 0x10000u;
            cr_emit_u(buf, len, cap, 0xD800u | (v >> 10));
            cr_emit_u(buf, len, cap, 0xDC00u | (v & 0x3FFu));
        }
        p += adv;
    }
}

/* Format a double the way System.Text.Json writes it (shortest round-trip, "R").
 * For the integral / simple decimal weights produced here (sums of 1.0 * decay
 * and 0.7 * decay) the default %g-based shortest form matches; we use the
 * round-trip formatter to be safe. */
static void cr_json_number_append(char **buf, size_t *len, size_t *cap, double v) {
    char tmp[64];
    /* Shortest representation that round-trips: try increasing precision. */
    for (int prec = 1; prec <= 17; ++prec) {
        snprintf(tmp, sizeof(tmp), "%.*g", prec, v);
        double back = strtod(tmp, NULL);
        if (back == v) break;
    }
    size_t tl = strlen(tmp);
    if (*len + tl + 1 > *cap) {
        size_t nc = *cap ? *cap : 64;
        while (*len + tl + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(*buf, nc);
        if (!nb) return;
        *buf = nb; *cap = nc;
    }
    memcpy(*buf + *len, tmp, tl);
    *len += tl;
    (*buf)[*len] = '\0';
}

bool ca_belief_tracker_theory_of_mind_estimate(const char *target,
                                               const char *interaction_history_json,
                                               ca_other_mind_estimate_t *out) {
    if (cr_is_blank(target) || !interaction_history_json || !out) return false;
    memset(out, 0, sizeof(*out));

    const char *text = interaction_history_json;
    size_t len = strlen(text);

    cr_belief_t *beliefs = NULL; size_t bn = 0, bcap = 0;
    int idx = 0;

    size_t i = 0;
    while (i < len) {
        size_t tok_end;
        int v = cr_match_verb(text, len, i, &tok_end);
        if (v < 0) { ++i; continue; }
        /* verb matched over [i, tok_end); skip the \s+ */
        size_t claim_start = tok_end;
        while (claim_start < len && isspace((unsigned char)text[claim_start])) ++claim_start;
        /* claim runs until the next . ; ! or ?  (negated class includes newlines). */
        size_t claim_end = claim_start;
        while (claim_end < len) {
            char c = text[claim_end];
            if (c == '.' || c == ';' || c == '!' || c == '?') break;
            ++claim_end;
        }
        /* The regex requires [^.;!?]+ i.e. at least one char in the claim. If the
         * claim is empty (immediate terminator), this alternative fails to match at
         * this position → advance by one and continue scanning. */
        if (claim_end == claim_start) { ++i; continue; }

        /* verb lowercased, up to and including any 's' that was part of the match. */
        size_t verb_len = tok_end - i;
        char *verb = cr_dup_n(text + i, verb_len);
        for (char *q = verb; *q; ++q) *q = (char)tolower((unsigned char)*q);

        /* claim = Match.Groups[2].Value.Trim(). Group 2 is [claim_start, claim_end). */
        size_t cs = claim_start, ce = claim_end;
        while (cs < ce && isspace((unsigned char)text[cs])) ++cs;
        while (ce > cs && isspace((unsigned char)text[ce - 1])) --ce;
        char *claim = cr_dup_n(text + cs, ce - cs);

        double decay  = 1.0 / (1.0 + idx * 0.1);
        double weight = (strncmp(verb, "believ", 6) == 0) ? 1.0 : 0.7;

        /* key = verb + ":" + claim */
        size_t kl = strlen(verb) + 1 + strlen(claim) + 1;
        char *key = (char *)malloc(kl);
        snprintf(key, kl, "%s:%s", verb, claim);
        free(verb); free(claim);

        /* beliefs[key] += weight*decay (CI key match, first-seen order). */
        size_t f = (size_t)-1;
        for (size_t b = 0; b < bn; ++b) if (cr_eq_ci(beliefs[b].key, key)) { f = b; break; }
        if (f == (size_t)-1) {
            if (bn == bcap) {
                size_t nc = bcap ? bcap * 2 : 8;
                cr_belief_t *nb = (cr_belief_t *)realloc(beliefs, nc * sizeof(*nb));
                if (!nb) { free(key); break; }
                beliefs = nb; bcap = nc;
            }
            beliefs[bn].key = key;
            beliefs[bn].weight = weight * decay;
            bn++;
        } else {
            beliefs[f].weight += weight * decay;
            free(key);
        }
        idx++;

        /* Continue scanning AFTER the matched region (non-overlapping, like
         * Regex.Matches): resume at claim_end (the terminator or end). */
        i = claim_end;
    }

    /* Serialise beliefs to JSON: {"key":weight,...} with default encoder escaping. */
    char *json = NULL; size_t jl = 0, jcap = 0;
    cr_sink_append(&json, &jl, &jcap, "{");
    for (size_t b = 0; b < bn; ++b) {
        if (b > 0) cr_sink_append(&json, &jl, &jcap, ",");
        cr_sink_append(&json, &jl, &jcap, "\"");
        cr_json_escape_append(&json, &jl, &jcap, beliefs[b].key);
        cr_sink_append(&json, &jl, &jcap, "\":");
        cr_json_number_append(&json, &jl, &jcap, beliefs[b].weight);
    }
    cr_sink_append(&json, &jl, &jcap, "}");

    /* confidence = beliefs.Count == 0 ? 0 : min(1, sum/5). */
    double conf;
    if (bn == 0) conf = 0.0;
    else {
        double sum = 0.0;
        for (size_t b = 0; b < bn; ++b) sum += beliefs[b].weight;
        conf = sum / 5.0;
        if (conf > 1.0) conf = 1.0;
    }

    for (size_t b = 0; b < bn; ++b) free(beliefs[b].key);
    free(beliefs);

    out->target_identifier = cr_dup(target);
    out->likely_belief_json = json ? json : cr_dup("{}");
    out->confidence = conf;
    return true;
}
