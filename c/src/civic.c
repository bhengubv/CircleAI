/*
 * civic.c — CircleAI.Civic (C11 port of CivicPrimitives.cs).
 *
 * InMemoryCivicBoard: issues (IssueId keyed), reps (RepId keyed), events (EventId
 * keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/civic.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_civic_issue_free(ca_civic_issue_t *i) {
    if (!i) return;
    free(i->issue_id);
    free(i->category);
    free(i->description);
    free(i->status);
    i->issue_id = i->category = i->description = i->status = NULL;
}
void ca_civic_issue_free_array(ca_civic_issue_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_civic_issue_free(&arr[i]);
    free(arr);
}

static bool issue_copy(ca_civic_issue_t *dst, const ca_civic_issue_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->issue_id        = cab_strdup_empty(src->issue_id);
    dst->category        = cab_strdup_empty(src->category);
    dst->description     = cab_strdup_empty(src->description);
    dst->lat = src->lat; dst->lon = src->lon;
    dst->reported_utc_ms = src->reported_utc_ms;
    dst->status          = cab_strdup_empty(src->status);
    if (!dst->issue_id || !dst->category || !dst->description || !dst->status) {
        ca_civic_issue_free(dst);
        return false;
    }
    return true;
}

void ca_civic_rep_free(ca_civic_rep_t *r) {
    if (!r) return;
    free(r->rep_id);
    free(r->name);
    free(r->office);
    free(r->contact_email);
    free(r->district);
    r->rep_id = r->name = r->office = r->contact_email = r->district = NULL;
    r->has_district = false;
}
void ca_civic_rep_free_array(ca_civic_rep_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_civic_rep_free(&arr[i]);
    free(arr);
}

static bool rep_copy(ca_civic_rep_t *dst, const ca_civic_rep_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->rep_id        = cab_strdup_empty(src->rep_id);
    dst->name          = cab_strdup_empty(src->name);
    dst->office        = cab_strdup_empty(src->office);
    dst->contact_email = cab_strdup_empty(src->contact_email);
    bool ok = dst->rep_id && dst->name && dst->office && dst->contact_email;
    if (ok && src->has_district) {
        dst->district = cab_strdup_empty(src->district);
        ok = dst->district != NULL;
        dst->has_district = ok;
    }
    if (!ok) { ca_civic_rep_free(dst); return false; }
    return true;
}

void ca_civic_event_free(ca_civic_event_t *e) {
    if (!e) return;
    free(e->event_id);
    free(e->title);
    free(e->location);
    free(e->audience);
    e->event_id = e->title = e->location = e->audience = NULL;
}
void ca_civic_event_free_array(ca_civic_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_civic_event_free(&arr[i]);
    free(arr);
}

static bool event_copy(ca_civic_event_t *dst, const ca_civic_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->event_id  = cab_strdup_empty(src->event_id);
    dst->title     = cab_strdup_empty(src->title);
    dst->at_utc_ms = src->at_utc_ms;
    dst->location  = cab_strdup_empty(src->location);
    dst->audience  = cab_strdup_empty(src->audience);
    if (!dst->event_id || !dst->title || !dst->location || !dst->audience) {
        ca_civic_event_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_civic_board {
    ca_civic_issue_t *issues;
    size_t            i_count, i_cap;
    ca_civic_rep_t   *reps;
    size_t            r_count, r_cap;
    ca_civic_event_t *events;
    size_t            e_count, e_cap;
};

ca_civic_board_t *ca_civic_board_create(void) {
    return (ca_civic_board_t *)calloc(1, sizeof(ca_civic_board_t));
}
void ca_civic_board_destroy(ca_civic_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->i_count; ++i) ca_civic_issue_free(&b->issues[i]);
    for (size_t i = 0; i < b->r_count; ++i) ca_civic_rep_free(&b->reps[i]);
    for (size_t i = 0; i < b->e_count; ++i) ca_civic_event_free(&b->events[i]);
    free(b->issues);
    free(b->reps);
    free(b->events);
    free(b);
}

int ca_civic_board_report(ca_civic_board_t *b, const ca_civic_issue_t *i) {
    if (!b || !i) return -1;
    for (size_t k = 0; k < b->i_count; ++k) {
        if (cab_ord_eq(b->issues[k].issue_id, i->issue_id)) {
            ca_civic_issue_t copy;
            if (!issue_copy(&copy, i)) return -1;
            ca_civic_issue_free(&b->issues[k]);
            b->issues[k] = copy;
            return 0;
        }
    }
    ca_civic_issue_t copy;
    if (!issue_copy(&copy, i)) return -1;
    if (b->i_count == b->i_cap) {
        size_t nc = b->i_cap ? b->i_cap * 2 : 4;
        void *n = realloc(b->issues, nc * sizeof(*b->issues));
        if (!n) { ca_civic_issue_free(&copy); return -1; }
        b->issues = (ca_civic_issue_t *)n;
        b->i_cap = nc;
    }
    b->issues[b->i_count++] = copy;
    return 0;
}

int ca_civic_board_resolve(ca_civic_board_t *b, const char *issue_id,
                           const char *status) {
    if (!b || !issue_id || !status) return -1;
    for (size_t i = 0; i < b->i_count; ++i) {
        if (cab_ord_eq(b->issues[i].issue_id, issue_id)) {
            char *dup = cab_strdup_empty(status);
            if (!dup) return -1;
            free(b->issues[i].status);
            b->issues[i].status = dup;
            return 0;
        }
    }
    return -2; /* Unknown issue -> C# InvalidOperationException */
}

ca_civic_issue_t *ca_civic_board_open_issues(const ca_civic_board_t *b,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->i_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->i_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->i_count; ++i)
        if (!cab_ci_eq(b->issues[i].status, "Resolved")) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_civic_issue_t *out = (ca_civic_issue_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!issue_copy(&out[i], &b->issues[idx[i]])) {
            ca_civic_issue_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_civic_board_add_rep(ca_civic_board_t *b, const ca_civic_rep_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->reps[i].rep_id, r->rep_id)) {
            ca_civic_rep_t copy;
            if (!rep_copy(&copy, r)) return -1;
            ca_civic_rep_free(&b->reps[i]);
            b->reps[i] = copy;
            return 0;
        }
    }
    ca_civic_rep_t copy;
    if (!rep_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->reps, nc * sizeof(*b->reps));
        if (!n) { ca_civic_rep_free(&copy); return -1; }
        b->reps = (ca_civic_rep_t *)n;
        b->r_cap = nc;
    }
    b->reps[b->r_count++] = copy;
    return 0;
}

ca_civic_rep_t *ca_civic_board_reps_for_district(const ca_civic_board_t *b,
                                                 const char *district,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !district) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i) {
        const ca_civic_rep_t *r = &b->reps[i];
        /* string.Equals(r.District, district, CI): a null District never matches
         * a non-null district (C# Equals(null, x) is false for x != null). */
        if (r->has_district && cab_ci_eq(r->district, district)) idx[n++] = i;
    }

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_civic_rep_t *out = (ca_civic_rep_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!rep_copy(&out[i], &b->reps[idx[i]])) {
            ca_civic_rep_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_civic_board_schedule(ca_civic_board_t *b, const ca_civic_event_t *e) {
    if (!b || !e) return -1;
    for (size_t i = 0; i < b->e_count; ++i) {
        if (cab_ord_eq(b->events[i].event_id, e->event_id)) {
            ca_civic_event_t copy;
            if (!event_copy(&copy, e)) return -1;
            ca_civic_event_free(&b->events[i]);
            b->events[i] = copy;
            return 0;
        }
    }
    ca_civic_event_t copy;
    if (!event_copy(&copy, e)) return -1;
    if (b->e_count == b->e_cap) {
        size_t nc = b->e_cap ? b->e_cap * 2 : 4;
        void *n = realloc(b->events, nc * sizeof(*b->events));
        if (!n) { ca_civic_event_free(&copy); return -1; }
        b->events = (ca_civic_event_t *)n;
        b->e_cap = nc;
    }
    b->events[b->e_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void event_sort_asc(const ca_civic_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->events[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->events[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_civic_event_t *ca_civic_board_upcoming_events(const ca_civic_board_t *b,
                                                 int64_t now_ms,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->e_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->e_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->e_count; ++i)
        if (b->events[i].at_utc_ms >= now_ms) idx[n++] = i;
    event_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_civic_event_t *out = (ca_civic_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!event_copy(&out[i], &b->events[idx[i]])) {
            ca_civic_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

void ca_civic_category_count_free_array(ca_civic_category_count_t *arr,
                                        size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i].category);
    free(arr);
}

/* Is an issue open (Status != "Resolved", OrdinalIgnoreCase)? */
static bool issue_is_open(const ca_civic_issue_t *i) {
    return !cab_ci_eq(i->status, "Resolved");
}

size_t ca_civic_board_open_issue_count(const ca_civic_board_t *b) {
    if (!b) return 0;
    size_t n = 0;
    for (size_t i = 0; i < b->i_count; ++i)
        if (issue_is_open(&b->issues[i])) n++;
    return n;
}

ca_civic_issue_t *ca_civic_board_issues_by_category(const ca_civic_board_t *b,
                                                    const char *category,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !category) { *out_count = (size_t)-1; return NULL; }
    if (b->i_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->i_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->i_count; ++i)
        if (cab_ci_eq(b->issues[i].category, category)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderByDescending(ReportedUtc), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = b->issues[cur].reported_utc_ms;
        size_t j = i;
        while (j > 0 && b->issues[idx[j - 1]].reported_utc_ms < key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_civic_issue_t *out = (ca_civic_issue_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!issue_copy(&out[i], &b->issues[idx[i]])) {
            ca_civic_issue_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_civic_board_remove_rep(ca_civic_board_t *b, const char *rep_id) {
    /* _reps.TryRemove(repId, out _). */
    if (!b || !rep_id) return false;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->reps[i].rep_id, rep_id)) {
            ca_civic_rep_free(&b->reps[i]);
            for (size_t j = i; j + 1 < b->r_count; ++j)
                b->reps[j] = b->reps[j + 1];
            b->r_count--;
            return true;
        }
    }
    return false;
}

ca_civic_rep_t *ca_civic_board_reps_for_office(const ca_civic_board_t *b,
                                               const char *office,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !office) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ci_eq(b->reps[i].office, office)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderBy(Name, OrdinalIgnoreCase), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        const char *kn = b->reps[cur].name;
        size_t j = i;
        while (j > 0 && cab_ci_cmp(b->reps[idx[j - 1]].name, kn) > 0) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_civic_rep_t *out = (ca_civic_rep_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!rep_copy(&out[i], &b->reps[idx[i]])) {
            ca_civic_rep_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_civic_event_t *ca_civic_board_events_for_audience(const ca_civic_board_t *b,
                                                     const char *audience,
                                                     size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !audience) { *out_count = (size_t)-1; return NULL; }
    if (b->e_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->e_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->e_count; ++i)
        if (cab_ci_eq(b->events[i].audience, audience)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    event_sort_asc(b, idx, n);

    ca_civic_event_t *out = (ca_civic_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!event_copy(&out[i], &b->events[idx[i]])) {
            ca_civic_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_civic_category_count_t *ca_civic_board_open_issue_breakdown(
    const ca_civic_board_t *b, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }

    /* Group open issues by Category (OrdinalIgnoreCase, first-seen spelling as
     * the key), in first-appearance order. */
    size_t cap = b->i_count ? b->i_count : 1;
    ca_civic_category_count_t *g =
        (ca_civic_category_count_t *)calloc(cap, sizeof(*g));
    if (!g) { *out_count = (size_t)-1; return NULL; }
    size_t gc = 0;
    for (size_t i = 0; i < b->i_count; ++i) {
        if (!issue_is_open(&b->issues[i])) continue;
        const char *cat = b->issues[i].category;
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ci_eq(g[k].category, cat)) break;
        if (k == gc) {
            g[gc].category = cab_strdup_empty(cat);
            if (!g[gc].category) {
                ca_civic_category_count_free_array(g, gc);
                *out_count = (size_t)-1;
                return NULL;
            }
            g[gc].count = 0;
            gc++;
        }
        g[k].count++;
    }
    if (gc == 0) { free(g); *out_count = 0; return NULL; }

    /* OrderByDescending(Count), stable insertion sort (first-appearance ties). */
    for (size_t i = 1; i < gc; ++i) {
        ca_civic_category_count_t cur = g[i];
        size_t j = i;
        while (j > 0 && g[j - 1].count < cur.count) { g[j] = g[j - 1]; --j; }
        g[j] = cur;
    }
    *out_count = gc;
    return g;
}
