/*
 * doc_analytics.c — CircleAI.DocAnalytics (C11 port).
 *
 * Views appended per document (flat list, filtered on read). Insight computes
 * total views, distinct viewers, and the mean duration in seconds. Deterministic
 * linear arrays. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/doc_analytics.h"
#include "board_common.h"

/* ── DocumentView ───────────────────────────────────────────────────────── */

void ca_doc_view_free(ca_doc_view_t *v) {
    if (!v) return;
    free(v->document_id);
    free(v->viewer_id);
    v->document_id = v->viewer_id = NULL;
}
void ca_doc_view_free_array(ca_doc_view_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_doc_view_free(&arr[i]);
    free(arr);
}
static bool view_copy(ca_doc_view_t *dst, const ca_doc_view_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_utc_ms    = src->at_utc_ms;
    dst->duration_ms  = src->duration_ms;
    dst->pages_viewed = src->pages_viewed;
    dst->document_id  = cab_strdup_empty(src->document_id);
    dst->viewer_id    = cab_strdup_empty(src->viewer_id);
    if (!dst->document_id || !dst->viewer_id) {
        ca_doc_view_free(dst);
        return false;
    }
    return true;
}

/* ── DocumentInsight ────────────────────────────────────────────────────── */

void ca_doc_insight_free(ca_doc_insight_t *i) {
    if (!i) return;
    free(i->document_id);
    i->document_id = NULL;
}

/* ── InMemoryDocumentTracker ────────────────────────────────────────────── */

struct ca_doc_tracker {
    ca_doc_view_t *items;
    size_t         count, cap;
};

ca_doc_tracker_t *ca_doc_tracker_create(void) {
    return (ca_doc_tracker_t *)calloc(1, sizeof(ca_doc_tracker_t));
}
void ca_doc_tracker_destroy(ca_doc_tracker_t *t) {
    if (!t) return;
    for (size_t i = 0; i < t->count; ++i) ca_doc_view_free(&t->items[i]);
    free(t->items);
    free(t);
}
const char *ca_doc_tracker_backend_id(const ca_doc_tracker_t *t) {
    (void)t; return "in-memory";
}

int ca_doc_tracker_record_view(ca_doc_tracker_t *t, const ca_doc_view_t *view) {
    if (!t || !view || cab_is_ws(view->document_id)) return -1;
    ca_doc_view_t copy;
    if (!view_copy(&copy, view)) return -1;
    if (t->count == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 4;
        void *n = realloc(t->items, nc * sizeof(*t->items));
        if (!n) { ca_doc_view_free(&copy); return -1; }
        t->items = (ca_doc_view_t *)n;
        t->cap = nc;
    }
    t->items[t->count++] = copy;
    return 0;
}

ca_doc_view_t *ca_doc_tracker_list_views(const ca_doc_tracker_t *t,
                                         const char *document_id,
                                         size_t *out_count) {
    if (!out_count) return NULL;
    if (!t || cab_is_ws(document_id)) { *out_count = (size_t)-1; return NULL; }

    size_t n = 0;
    for (size_t i = 0; i < t->count; ++i)
        if (cab_ord_eq(t->items[i].document_id, document_id)) n++;
    if (n == 0) { *out_count = 0; return NULL; }

    ca_doc_view_t *out = (ca_doc_view_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < t->count; ++i) {
        if (!cab_ord_eq(t->items[i].document_id, document_id)) continue;
        if (!view_copy(&out[k], &t->items[i])) {
            ca_doc_view_free_array(out, k);
            *out_count = (size_t)-1;
            return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

bool ca_doc_tracker_compute(const ca_doc_tracker_t *t, const char *document_id,
                            ca_doc_insight_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!t || cab_is_ws(document_id) || !out) return false;

    int total = 0;
    double sum_seconds = 0.0;
    /* distinct viewer ids among this doc's views */
    const char **seen = NULL;
    size_t seen_n = 0, seen_cap = 0;
    int unique = 0;
    bool ok = true;

    for (size_t i = 0; i < t->count && ok; ++i) {
        if (!cab_ord_eq(t->items[i].document_id, document_id)) continue;
        total++;
        sum_seconds += (double)t->items[i].duration_ms / 1000.0;
        const char *vid = t->items[i].viewer_id;
        bool found = false;
        for (size_t j = 0; j < seen_n; ++j)
            if (cab_ord_eq(seen[j], vid)) { found = true; break; }
        if (!found) {
            if (seen_n == seen_cap) {
                size_t nc = seen_cap ? seen_cap * 2 : 4;
                const char **ns = (const char **)realloc(seen, nc * sizeof(*seen));
                if (!ns) { ok = false; break; }
                seen = ns; seen_cap = nc;
            }
            seen[seen_n++] = vid;
            unique++;
        }
    }
    free(seen);
    if (!ok || total == 0) return false;

    out->document_id = cab_strdup_empty(document_id);
    if (!out->document_id) return false;
    out->total_views          = total;
    out->unique_viewers       = unique;
    out->avg_duration_seconds = sum_seconds / (double)total;
    return true;
}

const char *ca_doc_null_tracker_backend_id(void) { return "null"; }
const char *ca_doc_null_insights_backend_id(void) { return "null"; }
