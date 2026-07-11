/*
 * parenting.c — CircleAI.Parenting (C11 port of ParentingPrimitives.cs).
 *
 * InMemoryParentingBoard: children (ChildId keyed), milestones (flat append
 * list scanned per child), routines (keyed by ChildId + DayOfWeek). Pure C11.
 */

#include "circle_ai/parenting.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_par_child_free(ca_par_child_t *c) {
    if (!c) return;
    free(c->child_id);
    free(c->name);
    free(c->gender);
    c->child_id = c->name = c->gender = NULL;
    c->has_gender = false;
}
void ca_par_child_free_array(ca_par_child_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_par_child_free(&arr[i]);
    free(arr);
}

static bool child_copy(ca_par_child_t *dst, const ca_par_child_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->child_id         = cab_strdup_empty(src->child_id);
    dst->name             = cab_strdup_empty(src->name);
    dst->date_of_birth_ms = src->date_of_birth_ms;
    bool ok = dst->child_id && dst->name;
    if (ok && src->has_gender) {
        dst->gender = cab_strdup_empty(src->gender);
        ok = dst->gender != NULL;
        dst->has_gender = ok;
    }
    if (!ok) { ca_par_child_free(dst); return false; }
    return true;
}

void ca_par_milestone_free(ca_par_milestone_t *m) {
    if (!m) return;
    free(m->milestone_id);
    free(m->child_id);
    free(m->category);
    free(m->description);
    m->milestone_id = m->child_id = m->category = m->description = NULL;
}
void ca_par_milestone_free_array(ca_par_milestone_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_par_milestone_free(&arr[i]);
    free(arr);
}

static bool milestone_copy(ca_par_milestone_t *dst,
                           const ca_par_milestone_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->milestone_id       = cab_strdup_empty(src->milestone_id);
    dst->child_id           = cab_strdup_empty(src->child_id);
    dst->category           = cab_strdup_empty(src->category);
    dst->description        = cab_strdup_empty(src->description);
    dst->achieved_at_utc_ms = src->achieved_at_utc_ms;
    if (!dst->milestone_id || !dst->child_id || !dst->category ||
        !dst->description) { ca_par_milestone_free(dst); return false; }
    return true;
}

static void routine_entries_free(ca_par_routine_entry_t *e, size_t n) {
    if (!e) return;
    for (size_t i = 0; i < n; ++i) {
        free(e[i].time);
        free(e[i].activity);
    }
    free(e);
}

void ca_par_routine_free(ca_par_routine_t *r) {
    if (!r) return;
    free(r->child_id);
    routine_entries_free(r->entries, r->entry_count);
    r->child_id = NULL;
    r->entries = NULL;
    r->entry_count = 0;
}

static bool routine_copy(ca_par_routine_t *dst, const ca_par_routine_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->child_id    = cab_strdup_empty(src->child_id);
    dst->day_of_week = src->day_of_week;
    if (!dst->child_id) return false;
    if (src->entry_count > 0) {
        dst->entries = (ca_par_routine_entry_t *)calloc(src->entry_count,
                                                        sizeof(*dst->entries));
        if (!dst->entries) { ca_par_routine_free(dst); return false; }
        for (size_t i = 0; i < src->entry_count; ++i) {
            dst->entries[i].time     = cab_strdup_empty(src->entries[i].time);
            dst->entries[i].activity = cab_strdup_empty(src->entries[i].activity);
            if (!dst->entries[i].time || !dst->entries[i].activity) {
                dst->entry_count = i + 1;
                ca_par_routine_free(dst);
                return false;
            }
        }
        dst->entry_count = src->entry_count;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_par_board {
    ca_par_child_t     *children;
    size_t              c_count, c_cap;
    ca_par_milestone_t *milestones;
    size_t              m_count, m_cap;
    ca_par_routine_t   *routines;
    size_t              r_count, r_cap;
};

ca_par_board_t *ca_par_board_create(void) {
    return (ca_par_board_t *)calloc(1, sizeof(ca_par_board_t));
}
void ca_par_board_destroy(ca_par_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->c_count; ++i) ca_par_child_free(&b->children[i]);
    for (size_t i = 0; i < b->m_count; ++i) ca_par_milestone_free(&b->milestones[i]);
    for (size_t i = 0; i < b->r_count; ++i) ca_par_routine_free(&b->routines[i]);
    free(b->children);
    free(b->milestones);
    free(b->routines);
    free(b);
}

int ca_par_board_add_child(ca_par_board_t *b, const ca_par_child_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->children[i].child_id, c->child_id)) {
            ca_par_child_t copy;
            if (!child_copy(&copy, c)) return -1;
            ca_par_child_free(&b->children[i]);
            b->children[i] = copy;
            return 0;
        }
    }
    ca_par_child_t copy;
    if (!child_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->children, nc * sizeof(*b->children));
        if (!n) { ca_par_child_free(&copy); return -1; }
        b->children = (ca_par_child_t *)n;
        b->c_cap = nc;
    }
    b->children[b->c_count++] = copy;
    return 0;
}

bool ca_par_board_get_child(const ca_par_board_t *b, const char *id,
                            ca_par_child_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->children[i].child_id, id))
            return child_copy(out, &b->children[i]);
    return false;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void child_sort_name(const ca_par_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->children[idx[j - 1]].name, b->children[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_par_child_t *ca_par_board_children(const ca_par_board_t *b,
                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->c_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    child_sort_name(b, idx, n);

    ca_par_child_t *out = (ca_par_child_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!child_copy(&out[i], &b->children[idx[i]])) {
            ca_par_child_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_par_board_record_milestone(ca_par_board_t *b,
                                  const ca_par_milestone_t *m) {
    if (!b || !m) return -1;
    if (cab_is_ws(m->child_id)) return 2; /* ArgumentException */
    ca_par_milestone_t copy;
    if (!milestone_copy(&copy, m)) return -1;
    if (b->m_count == b->m_cap) {
        size_t nc = b->m_cap ? b->m_cap * 2 : 4;
        void *n = realloc(b->milestones, nc * sizeof(*b->milestones));
        if (!n) { ca_par_milestone_free(&copy); return -1; }
        b->milestones = (ca_par_milestone_t *)n;
        b->m_cap = nc;
    }
    b->milestones[b->m_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AchievedAtUtc. */
static void milestone_sort_desc(const ca_par_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->milestones[key].achieved_at_utc_ms;
        size_t j = i;
        while (j > 0 && b->milestones[idx[j - 1]].achieved_at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_par_milestone_t *ca_par_board_milestones_for(const ca_par_board_t *b,
                                                const char *child_id,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !child_id) { *out_count = (size_t)-1; return NULL; }
    if (b->m_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->m_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->m_count; ++i)
        if (cab_ord_eq(b->milestones[i].child_id, child_id)) idx[n++] = i;
    milestone_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_par_milestone_t *out = (ca_par_milestone_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!milestone_copy(&out[i], &b->milestones[idx[i]])) {
            ca_par_milestone_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_par_board_set_routine(ca_par_board_t *b, const ca_par_routine_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (b->routines[i].day_of_week == r->day_of_week &&
            cab_ord_eq(b->routines[i].child_id, r->child_id)) {
            ca_par_routine_t copy;
            if (!routine_copy(&copy, r)) return -1;
            ca_par_routine_free(&b->routines[i]);
            b->routines[i] = copy;
            return 0;
        }
    }
    ca_par_routine_t copy;
    if (!routine_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->routines, nc * sizeof(*b->routines));
        if (!n) { ca_par_routine_free(&copy); return -1; }
        b->routines = (ca_par_routine_t *)n;
        b->r_cap = nc;
    }
    b->routines[b->r_count++] = copy;
    return 0;
}

bool ca_par_board_get_routine(const ca_par_board_t *b, const char *child_id,
                              ca_day_of_week_t dow, ca_par_routine_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !child_id || !out) return false;
    for (size_t i = 0; i < b->r_count; ++i)
        if (b->routines[i].day_of_week == dow &&
            cab_ord_eq(b->routines[i].child_id, child_id))
            return routine_copy(out, &b->routines[i]);
    return false;
}

int ca_par_board_age_as_of(const ca_par_board_t *b, const char *child_id,
                           int64_t at_ms, int64_t *out_span_ms) {
    if (out_span_ms) *out_span_ms = 0;
    if (!b || !child_id || !out_span_ms) return -1;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->children[i].child_id, child_id)) {
            *out_span_ms = at_ms - b->children[i].date_of_birth_ms;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown child */
}
