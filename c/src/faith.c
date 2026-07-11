/*
 * faith.c — CircleAI.Faith (C11 port of FaithPrimitives.cs).
 *
 * InMemoryFaithBoard: services (ServiceId keyed), prayers (append list), scripture
 * (ReferenceId keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/faith.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_faith_service_free(ca_faith_service_t *s) {
    if (!s) return;
    free(s->service_id);
    free(s->community_name);
    free(s->title);
    free(s->location);
    s->service_id = s->community_name = s->title = s->location = NULL;
}
void ca_faith_service_free_array(ca_faith_service_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_faith_service_free(&arr[i]);
    free(arr);
}

static bool service_copy(ca_faith_service_t *dst, const ca_faith_service_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->service_id     = cab_strdup_empty(src->service_id);
    dst->community_name = cab_strdup_empty(src->community_name);
    dst->title          = cab_strdup_empty(src->title);
    dst->start_utc_ms   = src->start_utc_ms;
    dst->location       = cab_strdup_empty(src->location);
    if (!dst->service_id || !dst->community_name || !dst->title || !dst->location) {
        ca_faith_service_free(dst);
        return false;
    }
    return true;
}

void ca_faith_prayer_free(ca_faith_prayer_t *p) {
    if (!p) return;
    free(p->request_id);
    free(p->author);
    free(p->body);
    p->request_id = p->author = p->body = NULL;
}
void ca_faith_prayer_free_array(ca_faith_prayer_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_faith_prayer_free(&arr[i]);
    free(arr);
}

static bool prayer_copy(ca_faith_prayer_t *dst, const ca_faith_prayer_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->request_id       = cab_strdup_empty(src->request_id);
    dst->author           = cab_strdup_empty(src->author);
    dst->body             = cab_strdup_empty(src->body);
    dst->submitted_utc_ms = src->submitted_utc_ms;
    dst->is_anonymous     = src->is_anonymous;
    if (!dst->request_id || !dst->author || !dst->body) {
        ca_faith_prayer_free(dst);
        return false;
    }
    return true;
}

void ca_faith_scripture_free(ca_faith_scripture_t *s) {
    if (!s) return;
    free(s->reference_id);
    free(s->tradition);
    free(s->book);
    free(s->text);
    s->reference_id = s->tradition = s->book = s->text = NULL;
}
void ca_faith_scripture_free_array(ca_faith_scripture_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_faith_scripture_free(&arr[i]);
    free(arr);
}

static bool scripture_copy(ca_faith_scripture_t *dst,
                           const ca_faith_scripture_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->reference_id = cab_strdup_empty(src->reference_id);
    dst->tradition    = cab_strdup_empty(src->tradition);
    dst->book         = cab_strdup_empty(src->book);
    dst->chapter      = src->chapter;
    dst->verse        = src->verse;
    dst->text         = cab_strdup_empty(src->text);
    if (!dst->reference_id || !dst->tradition || !dst->book || !dst->text) {
        ca_faith_scripture_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_faith_board {
    ca_faith_service_t   *services;
    size_t                s_count, s_cap;
    ca_faith_prayer_t    *prayers;
    size_t                p_count, p_cap;
    ca_faith_scripture_t *scripture;
    size_t                sc_count, sc_cap;
};

ca_faith_board_t *ca_faith_board_create(void) {
    return (ca_faith_board_t *)calloc(1, sizeof(ca_faith_board_t));
}
void ca_faith_board_destroy(ca_faith_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->s_count; ++i) ca_faith_service_free(&b->services[i]);
    for (size_t i = 0; i < b->p_count; ++i) ca_faith_prayer_free(&b->prayers[i]);
    for (size_t i = 0; i < b->sc_count; ++i) ca_faith_scripture_free(&b->scripture[i]);
    free(b->services);
    free(b->prayers);
    free(b->scripture);
    free(b);
}

int ca_faith_board_schedule(ca_faith_board_t *b, const ca_faith_service_t *s) {
    if (!b || !s) return -1;
    for (size_t i = 0; i < b->s_count; ++i) {
        if (cab_ord_eq(b->services[i].service_id, s->service_id)) {
            ca_faith_service_t copy;
            if (!service_copy(&copy, s)) return -1;
            ca_faith_service_free(&b->services[i]);
            b->services[i] = copy;
            return 0;
        }
    }
    ca_faith_service_t copy;
    if (!service_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->services, nc * sizeof(*b->services));
        if (!n) { ca_faith_service_free(&copy); return -1; }
        b->services = (ca_faith_service_t *)n;
        b->s_cap = nc;
    }
    b->services[b->s_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by StartUtc. */
static void service_sort_asc(const ca_faith_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->services[key].start_utc_ms;
        size_t j = i;
        while (j > 0 && b->services[idx[j - 1]].start_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_faith_service_t *ca_faith_board_services_between(const ca_faith_board_t *b,
                                                    int64_t start_ms,
                                                    int64_t end_ms,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->s_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->s_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        int64_t t = b->services[i].start_utc_ms;
        if (t >= start_ms && t <= end_ms) idx[n++] = i;
    }
    service_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_faith_service_t *out = (ca_faith_service_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!service_copy(&out[i], &b->services[idx[i]])) {
            ca_faith_service_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_faith_board_submit_prayer(ca_faith_board_t *b,
                                 const ca_faith_prayer_t *p) {
    if (!b || !p) return -1;
    ca_faith_prayer_t copy;
    if (!prayer_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->prayers, nc * sizeof(*b->prayers));
        if (!n) { ca_faith_prayer_free(&copy); return -1; }
        b->prayers = (ca_faith_prayer_t *)n;
        b->p_cap = nc;
    }
    b->prayers[b->p_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by SubmittedUtc. */
static void prayer_sort_desc(const ca_faith_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->prayers[key].submitted_utc_ms;
        size_t j = i;
        while (j > 0 && b->prayers[idx[j - 1]].submitted_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_faith_prayer_t *ca_faith_board_recent_prayers(const ca_faith_board_t *b,
                                                 int limit, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->p_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->p_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    prayer_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    ca_faith_prayer_t *out = (ca_faith_prayer_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!prayer_copy(&out[i], &b->prayers[idx[i]])) {
            ca_faith_prayer_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_faith_board_add_scripture(ca_faith_board_t *b,
                                 const ca_faith_scripture_t *s) {
    if (!b || !s) return -1;
    for (size_t i = 0; i < b->sc_count; ++i) {
        if (cab_ord_eq(b->scripture[i].reference_id, s->reference_id)) {
            ca_faith_scripture_t copy;
            if (!scripture_copy(&copy, s)) return -1;
            ca_faith_scripture_free(&b->scripture[i]);
            b->scripture[i] = copy;
            return 0;
        }
    }
    ca_faith_scripture_t copy;
    if (!scripture_copy(&copy, s)) return -1;
    if (b->sc_count == b->sc_cap) {
        size_t nc = b->sc_cap ? b->sc_cap * 2 : 4;
        void *n = realloc(b->scripture, nc * sizeof(*b->scripture));
        if (!n) { ca_faith_scripture_free(&copy); return -1; }
        b->scripture = (ca_faith_scripture_t *)n;
        b->sc_cap = nc;
    }
    b->scripture[b->sc_count++] = copy;
    return 0;
}

bool ca_faith_board_lookup(const ca_faith_board_t *b, const char *tradition,
                           const char *book, int chapter, int verse,
                           ca_faith_scripture_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !tradition || !book || !out) return false;
    for (size_t i = 0; i < b->sc_count; ++i) {
        const ca_faith_scripture_t *s = &b->scripture[i];
        if (cab_ord_eq(s->tradition, tradition) && cab_ord_eq(s->book, book) &&
            s->chapter == chapter && s->verse == verse)
            return scripture_copy(out, s);
    }
    return false;
}

ca_faith_scripture_t *ca_faith_board_by_tradition(const ca_faith_board_t *b,
                                                  const char *tradition,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !tradition) { *out_count = (size_t)-1; return NULL; }
    if (b->sc_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->sc_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->sc_count; ++i)
        if (cab_ci_eq(b->scripture[i].tradition, tradition)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_faith_scripture_t *out = (ca_faith_scripture_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!scripture_copy(&out[i], &b->scripture[idx[i]])) {
            ca_faith_scripture_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
