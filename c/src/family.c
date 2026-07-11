/*
 * family.c — CircleAI.Family (C11 port of FamilyPrimitives.cs).
 *
 * InMemoryFamilyBoard: members (MemberId keyed), events (EventId keyed),
 * expenses (flat append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/family.h"
#include "board_common.h"

/* List<string>.Contains(memberId) — ordinal equality. */
static bool strv_ord_contains(char *const *v, size_t n, const char *s) {
    if (!v || !s) return false;
    for (size_t i = 0; i < n; ++i)
        if (cab_ord_eq(v[i], s)) return true;
    return false;
}

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_fam_member_free(ca_fam_member_t *m) {
    if (!m) return;
    free(m->member_id);
    free(m->name);
    free(m->role);
    m->member_id = m->name = m->role = NULL;
}
void ca_fam_member_free_array(ca_fam_member_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_fam_member_free(&arr[i]);
    free(arr);
}

static bool member_copy(ca_fam_member_t *dst, const ca_fam_member_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->member_id        = cab_strdup_empty(src->member_id);
    dst->name             = cab_strdup_empty(src->name);
    dst->role             = cab_strdup_empty(src->role);
    dst->date_of_birth_ms = src->date_of_birth_ms;
    if (!dst->member_id || !dst->name || !dst->role) {
        ca_fam_member_free(dst);
        return false;
    }
    return true;
}

void ca_fam_event_free(ca_fam_event_t *e) {
    if (!e) return;
    free(e->event_id);
    free(e->title);
    cab_strv_free(e->member_ids, e->member_id_count);
    e->event_id = e->title = NULL;
    e->member_ids = NULL;
    e->member_id_count = 0;
}
void ca_fam_event_free_array(ca_fam_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_fam_event_free(&arr[i]);
    free(arr);
}

static bool event_copy(ca_fam_event_t *dst, const ca_fam_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->event_id  = cab_strdup_empty(src->event_id);
    dst->title     = cab_strdup_empty(src->title);
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->event_id || !dst->title) { ca_fam_event_free(dst); return false; }
    if (!cab_strv_copy(&dst->member_ids, src->member_ids, src->member_id_count)) {
        ca_fam_event_free(dst);
        return false;
    }
    dst->member_id_count = src->member_id_count;
    return true;
}

void ca_fam_expense_free(ca_fam_expense_t *e) {
    if (!e) return;
    free(e->expense_id);
    free(e->paid_by_id);
    free(e->currency);
    free(e->category);
    e->expense_id = e->paid_by_id = e->currency = e->category = NULL;
}

static bool expense_copy(ca_fam_expense_t *dst, const ca_fam_expense_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->expense_id = cab_strdup_empty(src->expense_id);
    dst->paid_by_id = cab_strdup_empty(src->paid_by_id);
    dst->currency   = cab_strdup_empty(src->currency);
    dst->category   = cab_strdup_empty(src->category);
    dst->amount     = src->amount;
    dst->at_utc_ms  = src->at_utc_ms;
    if (!dst->expense_id || !dst->paid_by_id || !dst->currency ||
        !dst->category) { ca_fam_expense_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_fam_board {
    ca_fam_member_t  *members;
    size_t            m_count, m_cap;
    ca_fam_event_t   *events;
    size_t            e_count, e_cap;
    ca_fam_expense_t *expenses;
    size_t            x_count, x_cap;
};

ca_fam_board_t *ca_fam_board_create(void) {
    return (ca_fam_board_t *)calloc(1, sizeof(ca_fam_board_t));
}
void ca_fam_board_destroy(ca_fam_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->m_count; ++i) ca_fam_member_free(&b->members[i]);
    for (size_t i = 0; i < b->e_count; ++i) ca_fam_event_free(&b->events[i]);
    for (size_t i = 0; i < b->x_count; ++i) ca_fam_expense_free(&b->expenses[i]);
    free(b->members);
    free(b->events);
    free(b->expenses);
    free(b);
}

int ca_fam_board_add(ca_fam_board_t *b, const ca_fam_member_t *m) {
    if (!b || !m) return -1;
    for (size_t i = 0; i < b->m_count; ++i) {
        if (cab_ord_eq(b->members[i].member_id, m->member_id)) {
            ca_fam_member_t copy;
            if (!member_copy(&copy, m)) return -1;
            ca_fam_member_free(&b->members[i]);
            b->members[i] = copy;
            return 0;
        }
    }
    ca_fam_member_t copy;
    if (!member_copy(&copy, m)) return -1;
    if (b->m_count == b->m_cap) {
        size_t nc = b->m_cap ? b->m_cap * 2 : 4;
        void *n = realloc(b->members, nc * sizeof(*b->members));
        if (!n) { ca_fam_member_free(&copy); return -1; }
        b->members = (ca_fam_member_t *)n;
        b->m_cap = nc;
    }
    b->members[b->m_count++] = copy;
    return 0;
}

bool ca_fam_board_get_member(const ca_fam_board_t *b, const char *id,
                             ca_fam_member_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->m_count; ++i)
        if (cab_ord_eq(b->members[i].member_id, id))
            return member_copy(out, &b->members[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void member_sort_name(const ca_fam_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->members[idx[j - 1]].name, b->members[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_fam_member_t *ca_fam_board_members(const ca_fam_board_t *b,
                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->m_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->m_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    member_sort_name(b, idx, n);

    ca_fam_member_t *out = (ca_fam_member_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!member_copy(&out[i], &b->members[idx[i]])) {
            ca_fam_member_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_fam_board_schedule(ca_fam_board_t *b, const ca_fam_event_t *e) {
    if (!b || !e) return -1;
    for (size_t i = 0; i < b->e_count; ++i) {
        if (cab_ord_eq(b->events[i].event_id, e->event_id)) {
            ca_fam_event_t copy;
            if (!event_copy(&copy, e)) return -1;
            ca_fam_event_free(&b->events[i]);
            b->events[i] = copy;
            return 0;
        }
    }
    ca_fam_event_t copy;
    if (!event_copy(&copy, e)) return -1;
    if (b->e_count == b->e_cap) {
        size_t nc = b->e_cap ? b->e_cap * 2 : 4;
        void *n = realloc(b->events, nc * sizeof(*b->events));
        if (!n) { ca_fam_event_free(&copy); return -1; }
        b->events = (ca_fam_event_t *)n;
        b->e_cap = nc;
    }
    b->events[b->e_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void event_sort_asc(const ca_fam_board_t *b, size_t *idx, size_t n) {
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

ca_fam_event_t *ca_fam_board_events_for_member(const ca_fam_board_t *b,
                                               const char *member_id,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !member_id) { *out_count = (size_t)-1; return NULL; }
    if (b->e_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->e_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->e_count; ++i)
        if (strv_ord_contains(b->events[i].member_ids,
                              b->events[i].member_id_count, member_id))
            idx[n++] = i;
    event_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_fam_event_t *out = (ca_fam_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!event_copy(&out[i], &b->events[idx[i]])) {
            ca_fam_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_fam_board_record(ca_fam_board_t *b, const ca_fam_expense_t *e) {
    if (!b || !e) return -1;
    ca_fam_expense_t copy;
    if (!expense_copy(&copy, e)) return -1;
    if (b->x_count == b->x_cap) {
        size_t nc = b->x_cap ? b->x_cap * 2 : 4;
        void *n = realloc(b->expenses, nc * sizeof(*b->expenses));
        if (!n) { ca_fam_expense_free(&copy); return -1; }
        b->expenses = (ca_fam_expense_t *)n;
        b->x_cap = nc;
    }
    b->expenses[b->x_count++] = copy;
    return 0;
}

ca_fam_decimal_t ca_fam_board_total_paid_by(const ca_fam_board_t *b,
                                            const char *member_id,
                                            int64_t since_ms) {
    if (!b || !member_id) return 0;
    ca_fam_decimal_t sum = 0;
    for (size_t i = 0; i < b->x_count; ++i) {
        const ca_fam_expense_t *e = &b->expenses[i];
        if (cab_ord_eq(e->paid_by_id, member_id) && e->at_utc_ms >= since_ms)
            sum += e->amount;
    }
    return sum;
}

ca_fam_decimal_t ca_fam_board_spend_by_category(const ca_fam_board_t *b,
                                                const char *category,
                                                int64_t since_ms) {
    if (!b || !category) return 0;
    ca_fam_decimal_t sum = 0;
    for (size_t i = 0; i < b->x_count; ++i) {
        const ca_fam_expense_t *e = &b->expenses[i];
        if (cab_ci_eq(e->category, category) && e->at_utc_ms >= since_ms)
            sum += e->amount;
    }
    return sum;
}
