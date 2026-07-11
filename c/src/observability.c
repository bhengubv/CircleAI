/*
 * observability.c — CircleAI.Observability (C11 port).
 *
 * Metric sink stores samples flat (filtered by Name on read); trace sink stores
 * spans flat (filtered by TraceId, sorted by StartUtc on read); dashboard
 * publisher keeps a keyed set (last-write wins). Deterministic linear arrays.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/observability.h"
#include "board_common.h"

/* ── shared tag (key/value) helpers ─────────────────────────────────────── */

static void tags_free(ca_obs_tag_t *tags, size_t n) {
    if (!tags) return;
    for (size_t i = 0; i < n; ++i) { free(tags[i].key); free(tags[i].value); }
    free(tags);
}
/* Deep-copy a tag block. *out set to a fresh block (NULL when n==0). false OOM. */
static bool tags_copy(ca_obs_tag_t **out, const ca_obs_tag_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_obs_tag_t *v = (ca_obs_tag_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { tags_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

/* ── MetricSample ───────────────────────────────────────────────────────── */

void ca_metric_sample_free(ca_metric_sample_t *m) {
    if (!m) return;
    free(m->name);
    tags_free(m->tags, m->tag_count);
    m->name = NULL; m->tags = NULL; m->tag_count = 0;
}
void ca_metric_sample_free_array(ca_metric_sample_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_metric_sample_free(&arr[i]);
    free(arr);
}
static bool metric_copy(ca_metric_sample_t *dst, const ca_metric_sample_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->value = src->value;
    dst->name = cab_strdup_empty(src->name);
    if (!dst->name) return false;
    if (!tags_copy(&dst->tags, src->tags, src->tag_count)) {
        free(dst->name); dst->name = NULL; return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── TraceSpan ──────────────────────────────────────────────────────────── */

void ca_trace_span_free(ca_trace_span_t *s) {
    if (!s) return;
    free(s->trace_id);
    free(s->span_id);
    free(s->parent_span_id);
    free(s->name);
    tags_free(s->attributes, s->attribute_count);
    memset(s, 0, sizeof(*s));
}
void ca_trace_span_free_array(ca_trace_span_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_trace_span_free(&arr[i]);
    free(arr);
}
static bool span_copy(ca_trace_span_t *dst, const ca_trace_span_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->start_utc_ms = src->start_utc_ms;
    dst->duration_ms  = src->duration_ms;
    dst->trace_id = cab_strdup_empty(src->trace_id);
    dst->span_id  = cab_strdup_empty(src->span_id);
    dst->name     = cab_strdup_empty(src->name);
    dst->parent_span_id = src->parent_span_id ? cab_strdup(src->parent_span_id) : NULL;
    if (!dst->trace_id || !dst->span_id || !dst->name ||
        (src->parent_span_id && !dst->parent_span_id)) {
        ca_trace_span_free(dst); return false;
    }
    if (!tags_copy(&dst->attributes, src->attributes, src->attribute_count)) {
        ca_trace_span_free(dst); return false;
    }
    dst->attribute_count = src->attribute_count;
    return true;
}

/* ── DashboardSpec ──────────────────────────────────────────────────────── */

void ca_dashboard_spec_free(ca_dashboard_spec_t *d) {
    if (!d) return;
    free(d->dashboard_id);
    free(d->title);
    free(d->json_blob);
    d->dashboard_id = d->title = d->json_blob = NULL;
}
void ca_dashboard_spec_free_array(ca_dashboard_spec_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dashboard_spec_free(&arr[i]);
    free(arr);
}
static bool dashboard_copy(ca_dashboard_spec_t *dst, const ca_dashboard_spec_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->dashboard_id = cab_strdup_empty(src->dashboard_id);
    dst->title        = cab_strdup_empty(src->title);
    dst->json_blob    = cab_strdup_empty(src->json_blob);
    if (!dst->dashboard_id || !dst->title || !dst->json_blob) {
        ca_dashboard_spec_free(dst); return false;
    }
    return true;
}

/* ── InMemoryMetricSink ─────────────────────────────────────────────────── */

struct ca_metric_sink {
    ca_metric_sample_t *items;
    size_t              count, cap;
};

ca_metric_sink_t *ca_metric_sink_create(void) {
    return (ca_metric_sink_t *)calloc(1, sizeof(ca_metric_sink_t));
}
void ca_metric_sink_destroy(ca_metric_sink_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_metric_sample_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_metric_sink_backend_id(const ca_metric_sink_t *s) {
    (void)s; return "in-memory";
}

int ca_metric_sink_emit(ca_metric_sink_t *s, const ca_metric_sample_t *sample) {
    if (!s || !sample || cab_is_ws(sample->name)) return -1;
    ca_metric_sample_t copy;
    if (!metric_copy(&copy, sample)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_metric_sample_free(&copy); return -1; }
        s->items = (ca_metric_sample_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

ca_metric_sample_t *ca_metric_sink_read(const ca_metric_sink_t *s,
                                        const char *name, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !name) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].name, name)) n++;
    if (n == 0) { *out_count = 0; return NULL; }
    ca_metric_sample_t *out = (ca_metric_sample_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < s->count; ++i) {
        if (!cab_ord_eq(s->items[i].name, name)) continue;
        if (!metric_copy(&out[k], &s->items[i])) {
            ca_metric_sample_free_array(out, k);
            *out_count = (size_t)-1;
            return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

char **ca_metric_sink_names(const ca_metric_sink_t *s, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    char **names = (char **)calloc(s->count, sizeof(char *));
    if (!names) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i) {
        bool dup = false;
        for (size_t j = 0; j < n; ++j)
            if (cab_ord_eq(names[j], s->items[i].name)) { dup = true; break; }
        if (dup) continue;
        names[n] = cab_strdup_empty(s->items[i].name);
        if (!names[n]) { cab_strv_free(names, n); *out_count = (size_t)-1; return NULL; }
        n++;
    }
    /* sort ascending (ordinal) — insertion sort, stable enough for names */
    for (size_t i = 1; i < n; ++i) {
        char *key = names[i];
        size_t j = i;
        while (j > 0 && strcmp(names[j - 1], key) > 0) { names[j] = names[j - 1]; j--; }
        names[j] = key;
    }
    *out_count = n;
    return names;
}

const char *ca_obs_null_metric_sink_backend_id(void) { return "null"; }

/* ── InMemoryTraceSink ──────────────────────────────────────────────────── */

struct ca_trace_sink {
    ca_trace_span_t *items;
    size_t           count, cap;
};

ca_trace_sink_t *ca_trace_sink_create(void) {
    return (ca_trace_sink_t *)calloc(1, sizeof(ca_trace_sink_t));
}
void ca_trace_sink_destroy(ca_trace_sink_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_trace_span_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_trace_sink_backend_id(const ca_trace_sink_t *s) {
    (void)s; return "in-memory";
}

int ca_trace_sink_emit(ca_trace_sink_t *s, const ca_trace_span_t *span) {
    if (!s || !span || cab_is_ws(span->trace_id)) return -1;
    ca_trace_span_t copy;
    if (!span_copy(&copy, span)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_trace_span_free(&copy); return -1; }
        s->items = (ca_trace_span_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

/* Stable ascending sort of indices by StartUtc. */
static void span_sort_asc(const ca_trace_sink_t *s, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = s->items[key].start_utc_ms;
        size_t j = i;
        while (j > 0 && s->items[idx[j - 1]].start_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_trace_span_t *ca_trace_sink_read(const ca_trace_sink_t *s,
                                    const char *trace_id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !trace_id) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].trace_id, trace_id)) idx[n++] = i;
    span_sort_asc(s, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_trace_span_t *out = (ca_trace_span_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!span_copy(&out[i], &s->items[idx[i]])) {
            ca_trace_span_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_obs_null_trace_sink_backend_id(void) { return "null"; }

/* ── InMemoryDashboardPublisher ─────────────────────────────────────────── */

struct ca_dashboard_publisher {
    ca_dashboard_spec_t *items;
    size_t               count, cap;
};

ca_dashboard_publisher_t *ca_dashboard_publisher_create(void) {
    return (ca_dashboard_publisher_t *)calloc(1, sizeof(ca_dashboard_publisher_t));
}
void ca_dashboard_publisher_destroy(ca_dashboard_publisher_t *p) {
    if (!p) return;
    for (size_t i = 0; i < p->count; ++i) ca_dashboard_spec_free(&p->items[i]);
    free(p->items);
    free(p);
}
const char *ca_dashboard_publisher_backend_id(const ca_dashboard_publisher_t *p) {
    (void)p; return "in-memory";
}

int ca_dashboard_publisher_publish(ca_dashboard_publisher_t *p,
                                   const ca_dashboard_spec_t *spec) {
    if (!p || !spec || cab_is_ws(spec->dashboard_id)) return -1;
    for (size_t i = 0; i < p->count; ++i) {
        if (cab_ord_eq(p->items[i].dashboard_id, spec->dashboard_id)) {
            ca_dashboard_spec_t copy;
            if (!dashboard_copy(&copy, spec)) return -1;
            ca_dashboard_spec_free(&p->items[i]);
            p->items[i] = copy;
            return 0;
        }
    }
    ca_dashboard_spec_t copy;
    if (!dashboard_copy(&copy, spec)) return -1;
    if (p->count == p->cap) {
        size_t nc = p->cap ? p->cap * 2 : 4;
        void *n = realloc(p->items, nc * sizeof(*p->items));
        if (!n) { ca_dashboard_spec_free(&copy); return -1; }
        p->items = (ca_dashboard_spec_t *)n;
        p->cap = nc;
    }
    p->items[p->count++] = copy;
    return 0;
}

bool ca_dashboard_publisher_get(const ca_dashboard_publisher_t *p,
                                const char *id, ca_dashboard_spec_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ord_eq(p->items[i].dashboard_id, id))
            return dashboard_copy(out, &p->items[i]);
    return false;
}

ca_dashboard_spec_t *ca_dashboard_publisher_all(const ca_dashboard_publisher_t *p,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!p) { *out_count = (size_t)-1; return NULL; }
    if (p->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(p->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < p->count; ++i) idx[i] = i;
    /* sort by DashboardId asc (ordinal) */
    for (size_t i = 1; i < p->count; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(p->items[idx[j - 1]].dashboard_id,
                      p->items[key].dashboard_id) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }

    ca_dashboard_spec_t *out = (ca_dashboard_spec_t *)calloc(p->count, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < p->count; ++i) {
        if (!dashboard_copy(&out[i], &p->items[idx[i]])) {
            ca_dashboard_spec_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = p->count;
    return out;
}

const char *ca_obs_null_dashboard_publisher_backend_id(void) { return "null"; }
