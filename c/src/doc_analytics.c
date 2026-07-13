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

void ca_doc_top_free_array(ca_doc_top_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i].document_id);
    free(arr);
}

/* Is `doc_id` the first-appearance of that DocumentId in the view list (i.e. no
 * earlier view shares it)? Used to enumerate distinct documents deterministically
 * in first-appearance order. */
static bool doc_first_seen_at(const ca_doc_tracker_t *t, size_t at) {
    const char *id = t->items[at].document_id;
    for (size_t i = 0; i < at; ++i)
        if (cab_ord_eq(t->items[i].document_id, id)) return false;
    return true;
}

size_t ca_doc_tracker_document_count(const ca_doc_tracker_t *t) {
    if (!t) return 0;
    /* _byDoc.Count — number of distinct DocumentIds. */
    size_t n = 0;
    for (size_t i = 0; i < t->count; ++i)
        if (doc_first_seen_at(t, i)) n++;
    return n;
}

size_t ca_doc_tracker_total_views(const ca_doc_tracker_t *t) {
    /* _byDoc.Values.Sum(v => v.Count) — the flat list holds every view. */
    return t ? t->count : 0;
}

bool ca_doc_tracker_clear(ca_doc_tracker_t *t, const char *document_id) {
    if (!t || cab_is_ws(document_id)) return false;
    /* _byDoc.TryRemove(documentId, out _): drop every view of that document. */
    size_t w = 0;
    bool removed = false;
    for (size_t i = 0; i < t->count; ++i) {
        if (cab_ord_eq(t->items[i].document_id, document_id)) {
            ca_doc_view_free(&t->items[i]);
            removed = true;
        } else {
            if (w != i) t->items[w] = t->items[i];
            w++;
        }
    }
    t->count = w;
    return removed;
}

ca_doc_top_t *ca_doc_tracker_top_documents(const ca_doc_tracker_t *t, int top_k,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!t || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (t->count == 0) { *out_count = 0; return NULL; }

    /* Group by DocumentId in first-appearance order, counting views. */
    ca_doc_top_t *g = (ca_doc_top_t *)calloc(t->count, sizeof(*g));
    if (!g) { *out_count = (size_t)-1; return NULL; }
    size_t gc = 0;
    for (size_t i = 0; i < t->count; ++i) {
        const char *id = t->items[i].document_id;
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ord_eq(g[k].document_id, id)) break;
        if (k == gc) {
            g[gc].document_id = cab_strdup_empty(id);
            if (!g[gc].document_id) {
                ca_doc_top_free_array(g, gc);
                *out_count = (size_t)-1;
                return NULL;
            }
            g[gc].views = 0;
            gc++;
        }
        g[k].views++;
    }

    /* OrderByDescending(Views), stable insertion sort (first-appearance ties). */
    for (size_t i = 1; i < gc; ++i) {
        ca_doc_top_t cur = g[i];
        size_t j = i;
        while (j > 0 && g[j - 1].views < cur.views) { g[j] = g[j - 1]; --j; }
        g[j] = cur;
    }

    /* Take(topK): trim the tail, freeing dropped ids. */
    size_t take = (size_t)top_k < gc ? (size_t)top_k : gc;
    for (size_t i = take; i < gc; ++i) free(g[i].document_id);
    *out_count = take;
    return g;   /* caller frees the first `take` entries + block */
}

ca_doc_view_t *ca_doc_tracker_recent_views(const ca_doc_tracker_t *t,
                                           const char *document_id, int limit,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!t || cab_is_ws(document_id) || limit <= 0) {
        *out_count = (size_t)-1;
        return NULL;
    }
    if (t->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(t->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < t->count; ++i)
        if (cab_ord_eq(t->items[i].document_id, document_id)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; } /* unknown doc */

    /* OrderByDescending(AtUtc), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = t->items[cur].at_utc_ms;
        size_t j = i;
        while (j > 0 && t->items[idx[j - 1]].at_utc_ms < key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }
    if ((size_t)limit < n) n = (size_t)limit;

    ca_doc_view_t *out = (ca_doc_view_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!view_copy(&out[i], &t->items[idx[i]])) {
            ca_doc_view_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_doc_tracker_total_pages_viewed(const ca_doc_tracker_t *t,
                                      const char *document_id) {
    if (!t || cab_is_ws(document_id)) return 0;
    int sum = 0;
    for (size_t i = 0; i < t->count; ++i)
        if (cab_ord_eq(t->items[i].document_id, document_id))
            sum += t->items[i].pages_viewed;
    return sum;
}

char *ca_doc_tracker_most_engaged_viewer(const ca_doc_tracker_t *t,
                                         const char *document_id) {
    if (!t || cab_is_ws(document_id)) return NULL;

    /* Group the document's views by ViewerId (Ordinal, first-appearance order),
     * summing Duration. TotalSeconds ordering == total-ms ordering (monotonic),
     * so the max total_ms picks the same viewer the C# sum-of-seconds does. */
    typedef struct { const char *viewer; int64_t total_ms; } grp_t;
    grp_t *g = NULL;
    size_t gc = 0, cap = 0;
    for (size_t i = 0; i < t->count; ++i) {
        if (!cab_ord_eq(t->items[i].document_id, document_id)) continue;
        const char *vid = t->items[i].viewer_id;
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ord_eq(g[k].viewer, vid)) break;
        if (k == gc) {
            if (gc == cap) {
                size_t nc = cap ? cap * 2 : 4;
                grp_t *ng = (grp_t *)realloc(g, nc * sizeof(*ng));
                if (!ng) { free(g); return NULL; }
                g = ng; cap = nc;
            }
            g[gc].viewer = vid;
            g[gc].total_ms = 0;
            gc++;
        }
        g[k].total_ms += t->items[i].duration_ms;
    }
    if (gc == 0) { free(g); return NULL; }   /* no views -> null */

    /* OrderByDescending(sum).Select(Key).First(): max total, first-appearance
     * order breaks ties (strict >). */
    size_t best = 0;
    for (size_t k = 1; k < gc; ++k)
        if (g[k].total_ms > g[best].total_ms) best = k;
    char *res = cab_strdup_empty(g[best].viewer);
    free(g);
    return res;   /* NULL on OOM */
}

const char *ca_doc_null_tracker_backend_id(void) { return "null"; }
const char *ca_doc_null_insights_backend_id(void) { return "null"; }
