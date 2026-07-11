/*
 * relationships.c — CircleAI.Relationships (C11 port of RelationshipsPrimitives.cs).
 *
 * InMemoryRelationshipsBoard: contacts (ContactId keyed), important dates (DateId
 * keyed), events (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/relationships.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_relationships_contact_free(ca_relationships_contact_t *c) {
    if (!c) return;
    free(c->contact_id);
    free(c->name);
    free(c->relationship);
    free(c->notes);
    c->contact_id = c->name = c->relationship = c->notes = NULL;
    c->has_notes = false;
}
void ca_relationships_contact_free_array(ca_relationships_contact_t *arr,
                                         size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_relationships_contact_free(&arr[i]);
    free(arr);
}

static bool contact_copy(ca_relationships_contact_t *dst,
                         const ca_relationships_contact_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->contact_id   = cab_strdup_empty(src->contact_id);
    dst->name         = cab_strdup_empty(src->name);
    dst->relationship = cab_strdup_empty(src->relationship);
    bool ok = dst->contact_id && dst->name && dst->relationship;
    if (ok && src->has_notes) {
        dst->notes = cab_strdup_empty(src->notes);
        ok = dst->notes != NULL;
        dst->has_notes = ok;
    }
    if (!ok) { ca_relationships_contact_free(dst); return false; }
    return true;
}

void ca_relationships_important_date_free(
    ca_relationships_important_date_t *d) {
    if (!d) return;
    free(d->date_id);
    free(d->contact_id);
    free(d->kind);
    d->date_id = d->contact_id = d->kind = NULL;
}
void ca_relationships_important_date_free_array(
    ca_relationships_important_date_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i)
        ca_relationships_important_date_free(&arr[i]);
    free(arr);
}

static bool date_copy(ca_relationships_important_date_t *dst,
                      const ca_relationships_important_date_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->date_id    = cab_strdup_empty(src->date_id);
    dst->contact_id = cab_strdup_empty(src->contact_id);
    dst->kind       = cab_strdup_empty(src->kind);
    dst->date_ms    = src->date_ms;
    if (!dst->date_id || !dst->contact_id || !dst->kind) {
        ca_relationships_important_date_free(dst);
        return false;
    }
    return true;
}

static bool event_copy(ca_relationships_event_t *dst,
                       const ca_relationships_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->contact_id = cab_strdup_empty(src->contact_id);
    dst->kind       = cab_strdup_empty(src->kind);
    dst->at_utc_ms  = src->at_utc_ms;
    bool ok = dst->contact_id && dst->kind;
    if (ok && src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        ok = dst->note != NULL;
        dst->has_note = ok;
    }
    if (!ok) {
        free(dst->contact_id); free(dst->kind); free(dst->note);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void event_free(ca_relationships_event_t *e) {
    if (!e) return;
    free(e->contact_id);
    free(e->kind);
    free(e->note);
    e->contact_id = e->kind = e->note = NULL;
    e->has_note = false;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_relationships_board {
    ca_relationships_contact_t        *contacts;
    size_t                             c_count, c_cap;
    ca_relationships_important_date_t *dates;
    size_t                             d_count, d_cap;
    ca_relationships_event_t          *events;
    size_t                             e_count, e_cap;
};

ca_relationships_board_t *ca_relationships_board_create(void) {
    return (ca_relationships_board_t *)calloc(1, sizeof(ca_relationships_board_t));
}
void ca_relationships_board_destroy(ca_relationships_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->c_count; ++i) ca_relationships_contact_free(&b->contacts[i]);
    for (size_t i = 0; i < b->d_count; ++i) ca_relationships_important_date_free(&b->dates[i]);
    for (size_t i = 0; i < b->e_count; ++i) event_free(&b->events[i]);
    free(b->contacts);
    free(b->dates);
    free(b->events);
    free(b);
}

int ca_relationships_board_add_contact(ca_relationships_board_t *b,
                                       const ca_relationships_contact_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->contacts[i].contact_id, c->contact_id)) {
            ca_relationships_contact_t copy;
            if (!contact_copy(&copy, c)) return -1;
            ca_relationships_contact_free(&b->contacts[i]);
            b->contacts[i] = copy;
            return 0;
        }
    }
    ca_relationships_contact_t copy;
    if (!contact_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->contacts, nc * sizeof(*b->contacts));
        if (!n) { ca_relationships_contact_free(&copy); return -1; }
        b->contacts = (ca_relationships_contact_t *)n;
        b->c_cap = nc;
    }
    b->contacts[b->c_count++] = copy;
    return 0;
}

bool ca_relationships_board_get_contact(const ca_relationships_board_t *b,
                                        const char *id,
                                        ca_relationships_contact_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->contacts[i].contact_id, id))
            return contact_copy(out, &b->contacts[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void contact_sort_name(const ca_relationships_board_t *b, size_t *idx,
                              size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(b->contacts[idx[j - 1]].name,
                              b->contacts[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_relationships_contact_t *ca_relationships_board_contacts(
    const ca_relationships_board_t *b, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->c_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    contact_sort_name(b, idx, n);

    ca_relationships_contact_t *out =
        (ca_relationships_contact_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!contact_copy(&out[i], &b->contacts[idx[i]])) {
            ca_relationships_contact_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_relationships_board_add_important_date(
    ca_relationships_board_t *b, const ca_relationships_important_date_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->d_count; ++i) {
        if (cab_ord_eq(b->dates[i].date_id, d->date_id)) {
            ca_relationships_important_date_t copy;
            if (!date_copy(&copy, d)) return -1;
            ca_relationships_important_date_free(&b->dates[i]);
            b->dates[i] = copy;
            return 0;
        }
    }
    ca_relationships_important_date_t copy;
    if (!date_copy(&copy, d)) return -1;
    if (b->d_count == b->d_cap) {
        size_t nc = b->d_cap ? b->d_cap * 2 : 4;
        void *n = realloc(b->dates, nc * sizeof(*b->dates));
        if (!n) { ca_relationships_important_date_free(&copy); return -1; }
        b->dates = (ca_relationships_important_date_t *)n;
        b->d_cap = nc;
    }
    b->dates[b->d_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected date indices by Date.Day. */
static void date_sort_day(const ca_relationships_board_t *b, size_t *idx,
                          size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int kd = cab_utc_day_of_month(b->dates[key].date_ms);
        size_t j = i;
        while (j > 0 &&
               cab_utc_day_of_month(b->dates[idx[j - 1]].date_ms) > kd) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_relationships_important_date_t *ca_relationships_board_upcoming_this_month(
    const ca_relationships_board_t *b, int64_t now_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->d_count == 0) { *out_count = 0; return NULL; }

    int now_month = cab_utc_month(now_ms);
    size_t *idx = (size_t *)malloc(b->d_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->d_count; ++i)
        if (cab_utc_month(b->dates[i].date_ms) == now_month) idx[n++] = i;
    date_sort_day(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_relationships_important_date_t *out =
        (ca_relationships_important_date_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!date_copy(&out[i], &b->dates[idx[i]])) {
            ca_relationships_important_date_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_relationships_board_record_touchpoint(
    ca_relationships_board_t *b, const ca_relationships_event_t *e) {
    if (!b || !e) return -1;
    ca_relationships_event_t copy;
    if (!event_copy(&copy, e)) return -1;
    if (b->e_count == b->e_cap) {
        size_t nc = b->e_cap ? b->e_cap * 2 : 4;
        void *n = realloc(b->events, nc * sizeof(*b->events));
        if (!n) { event_free(&copy); return -1; }
        b->events = (ca_relationships_event_t *)n;
        b->e_cap = nc;
    }
    b->events[b->e_count++] = copy;
    return 0;
}

/* Newest AtUtc among a contact's events; false when none. */
static bool last_contact_ms(const ca_relationships_board_t *b,
                            const char *contact_id, int64_t *out_ms) {
    bool found = false;
    int64_t best = 0;
    for (size_t i = 0; i < b->e_count; ++i) {
        const ca_relationships_event_t *e = &b->events[i];
        if (cab_ord_eq(e->contact_id, contact_id)) {
            if (!found || e->at_utc_ms > best) { best = e->at_utc_ms; found = true; }
        }
    }
    if (found && out_ms) *out_ms = best;
    return found;
}

bool ca_relationships_board_last_contact(const ca_relationships_board_t *b,
                                         const char *contact_id,
                                         int64_t *out_ms) {
    if (!b || !contact_id) return false;
    return last_contact_ms(b, contact_id, out_ms);
}

ca_relationships_contact_t *ca_relationships_board_not_contacted_since(
    const ca_relationships_board_t *b, int64_t cutoff_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->c_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->c_count; ++i) {
        int64_t last = 0;
        bool has = last_contact_ms(b, b->contacts[i].contact_id, &last);
        if (!has || last < cutoff_ms) idx[n++] = i;
    }

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_relationships_contact_t *out =
        (ca_relationships_contact_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!contact_copy(&out[i], &b->contacts[idx[i]])) {
            ca_relationships_contact_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
