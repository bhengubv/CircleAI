/*
 * legal.c — CircleAI.Legal (C11 port of LegalPrimitives.cs).
 *
 * InMemoryLegalBoard over four id-keyed linear stores: matters, contracts,
 * deadlines, clauses. Contract.Counterparties and Clause.Tags are owned string
 * arrays, deep-copied in and out. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/legal.h"
#include "board_common.h"

/* ── Matter ─────────────────────────────────────────────────────────────── */

void ca_legal_matter_free(ca_legal_matter_t *m) {
    if (!m) return;
    free(m->matter_id);
    free(m->title);
    free(m->jurisdiction);
    free(m->client);
    m->matter_id = m->title = m->jurisdiction = m->client = NULL;
}
void ca_legal_matter_free_array(ca_legal_matter_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_legal_matter_free(&arr[i]);
    free(arr);
}

static bool matter_copy(ca_legal_matter_t *dst, const ca_legal_matter_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->matter_id    = cab_strdup_empty(src->matter_id);
    dst->title        = cab_strdup_empty(src->title);
    dst->jurisdiction = cab_strdup_empty(src->jurisdiction);
    dst->client       = cab_strdup_empty(src->client);
    dst->opened_at_utc_ms = src->opened_at_utc_ms;
    dst->open = src->open;
    if (!dst->matter_id || !dst->title || !dst->jurisdiction || !dst->client) {
        ca_legal_matter_free(dst);
        return false;
    }
    return true;
}

/* ── Contract ───────────────────────────────────────────────────────────── */

void ca_legal_contract_free(ca_legal_contract_t *c) {
    if (!c) return;
    free(c->contract_id);
    free(c->matter_id);
    free(c->title);
    cab_strv_free(c->counterparties, c->counterparty_count);
    c->contract_id = c->matter_id = c->title = NULL;
    c->counterparties = NULL;
    c->counterparty_count = 0;
}
void ca_legal_contract_free_array(ca_legal_contract_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_legal_contract_free(&arr[i]);
    free(arr);
}

static bool contract_copy(ca_legal_contract_t *dst,
                          const ca_legal_contract_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->contract_id = cab_strdup_empty(src->contract_id);
    dst->matter_id   = cab_strdup_empty(src->matter_id);
    dst->title       = cab_strdup_empty(src->title);
    dst->effective_date_ms = src->effective_date_ms;
    dst->has_expiry     = src->has_expiry;
    dst->expiry_date_ms = src->expiry_date_ms;
    if (!dst->contract_id || !dst->matter_id || !dst->title) {
        ca_legal_contract_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->counterparties, src->counterparties,
                       src->counterparty_count)) {
        ca_legal_contract_free(dst);
        return false;
    }
    dst->counterparty_count = src->counterparty_count;
    return true;
}

/* ── LegalDeadline ──────────────────────────────────────────────────────── */

void ca_legal_deadline_free(ca_legal_deadline_t *d) {
    if (!d) return;
    free(d->deadline_id);
    free(d->matter_id);
    free(d->description);
    d->deadline_id = d->matter_id = d->description = NULL;
}
void ca_legal_deadline_free_array(ca_legal_deadline_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_legal_deadline_free(&arr[i]);
    free(arr);
}

static bool deadline_copy(ca_legal_deadline_t *dst,
                          const ca_legal_deadline_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->deadline_id = cab_strdup_empty(src->deadline_id);
    dst->matter_id   = cab_strdup_empty(src->matter_id);
    dst->description = cab_strdup_empty(src->description);
    dst->due_on_ms   = src->due_on_ms;
    if (!dst->deadline_id || !dst->matter_id || !dst->description) {
        ca_legal_deadline_free(dst);
        return false;
    }
    return true;
}

/* ── Clause ─────────────────────────────────────────────────────────────── */

void ca_legal_clause_free(ca_legal_clause_t *c) {
    if (!c) return;
    free(c->clause_id);
    free(c->title);
    free(c->body);
    cab_strv_free(c->tags, c->tag_count);
    c->clause_id = c->title = c->body = NULL;
    c->tags = NULL;
    c->tag_count = 0;
}
void ca_legal_clause_free_array(ca_legal_clause_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_legal_clause_free(&arr[i]);
    free(arr);
}

static bool clause_copy(ca_legal_clause_t *dst, const ca_legal_clause_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->clause_id = cab_strdup_empty(src->clause_id);
    dst->title     = cab_strdup_empty(src->title);
    dst->body      = cab_strdup_empty(src->body);
    if (!dst->clause_id || !dst->title || !dst->body) {
        ca_legal_clause_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_legal_clause_free(dst);
        return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_legal_board {
    ca_legal_matter_t   *matters;
    size_t               matter_count, matter_cap;
    ca_legal_contract_t *contracts;
    size_t               contract_count, contract_cap;
    ca_legal_deadline_t *deadlines;
    size_t               deadline_count, deadline_cap;
    ca_legal_clause_t   *clauses;
    size_t               clause_count, clause_cap;
};

ca_legal_board_t *ca_legal_board_create(void) {
    return (ca_legal_board_t *)calloc(1, sizeof(ca_legal_board_t));
}
void ca_legal_board_destroy(ca_legal_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->matter_count; ++i)   ca_legal_matter_free(&b->matters[i]);
    for (size_t i = 0; i < b->contract_count; ++i) ca_legal_contract_free(&b->contracts[i]);
    for (size_t i = 0; i < b->deadline_count; ++i) ca_legal_deadline_free(&b->deadlines[i]);
    for (size_t i = 0; i < b->clause_count; ++i)   ca_legal_clause_free(&b->clauses[i]);
    free(b->matters);
    free(b->contracts);
    free(b->deadlines);
    free(b->clauses);
    free(b);
}

int ca_legal_board_open(ca_legal_board_t *b, const ca_legal_matter_t *m) {
    if (!b || !m) return -1;
    for (size_t i = 0; i < b->matter_count; ++i) {
        if (cab_ord_eq(b->matters[i].matter_id, m->matter_id)) {
            ca_legal_matter_t copy;
            if (!matter_copy(&copy, m)) return -1;
            ca_legal_matter_free(&b->matters[i]);
            b->matters[i] = copy;
            return 0;
        }
    }
    ca_legal_matter_t copy;
    if (!matter_copy(&copy, m)) return -1;
    if (b->matter_count == b->matter_cap) {
        size_t nc = b->matter_cap ? b->matter_cap * 2 : 4;
        void *n = realloc(b->matters, nc * sizeof(*b->matters));
        if (!n) { ca_legal_matter_free(&copy); return -1; }
        b->matters = (ca_legal_matter_t *)n;
        b->matter_cap = nc;
    }
    b->matters[b->matter_count++] = copy;
    return 0;
}

int ca_legal_board_close(ca_legal_board_t *b, const char *matter_id) {
    if (!b || !matter_id) return -1;
    for (size_t i = 0; i < b->matter_count; ++i) {
        if (cab_ord_eq(b->matters[i].matter_id, matter_id)) {
            b->matters[i].open = false;
            return 0;
        }
    }
    return 1;   /* InvalidOperationException: unknown matter */
}

bool ca_legal_board_get_matter(const ca_legal_board_t *b, const char *id,
                               ca_legal_matter_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->matter_count; ++i)
        if (cab_ord_eq(b->matters[i].matter_id, id))
            return matter_copy(out, &b->matters[i]);
    return false;
}

/* Stable descending sort of collected matter indices by opened_at_utc_ms. */
static void matter_sort_desc(const ca_legal_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->matters[key].opened_at_utc_ms;
        size_t j = i;
        while (j > 0 && b->matters[idx[j - 1]].opened_at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_legal_matter_t *ca_legal_board_active_matters(const ca_legal_board_t *b,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->matter_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->matter_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->matter_count; ++i)
        if (b->matters[i].open) idx[n++] = i;
    matter_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_legal_matter_t *out = (ca_legal_matter_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!matter_copy(&out[i], &b->matters[idx[i]])) {
            ca_legal_matter_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_legal_board_add_contract(ca_legal_board_t *b,
                                const ca_legal_contract_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->contract_count; ++i) {
        if (cab_ord_eq(b->contracts[i].contract_id, c->contract_id)) {
            ca_legal_contract_t copy;
            if (!contract_copy(&copy, c)) return -1;
            ca_legal_contract_free(&b->contracts[i]);
            b->contracts[i] = copy;
            return 0;
        }
    }
    ca_legal_contract_t copy;
    if (!contract_copy(&copy, c)) return -1;
    if (b->contract_count == b->contract_cap) {
        size_t nc = b->contract_cap ? b->contract_cap * 2 : 4;
        void *n = realloc(b->contracts, nc * sizeof(*b->contracts));
        if (!n) { ca_legal_contract_free(&copy); return -1; }
        b->contracts = (ca_legal_contract_t *)n;
        b->contract_cap = nc;
    }
    b->contracts[b->contract_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected contract indices by expiry_date_ms. */
static void contract_sort_asc(const ca_legal_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->contracts[key].expiry_date_ms;
        size_t j = i;
        while (j > 0 && b->contracts[idx[j - 1]].expiry_date_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_legal_contract_t *ca_legal_board_contracts_expiring_before(
    const ca_legal_board_t *b, int64_t date_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->contract_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->contract_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->contract_count; ++i)
        if (b->contracts[i].has_expiry && b->contracts[i].expiry_date_ms <= date_ms)
            idx[n++] = i;
    contract_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_legal_contract_t *out = (ca_legal_contract_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!contract_copy(&out[i], &b->contracts[idx[i]])) {
            ca_legal_contract_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_legal_board_add_deadline(ca_legal_board_t *b,
                                const ca_legal_deadline_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->deadline_count; ++i) {
        if (cab_ord_eq(b->deadlines[i].deadline_id, d->deadline_id)) {
            ca_legal_deadline_t copy;
            if (!deadline_copy(&copy, d)) return -1;
            ca_legal_deadline_free(&b->deadlines[i]);
            b->deadlines[i] = copy;
            return 0;
        }
    }
    ca_legal_deadline_t copy;
    if (!deadline_copy(&copy, d)) return -1;
    if (b->deadline_count == b->deadline_cap) {
        size_t nc = b->deadline_cap ? b->deadline_cap * 2 : 4;
        void *n = realloc(b->deadlines, nc * sizeof(*b->deadlines));
        if (!n) { ca_legal_deadline_free(&copy); return -1; }
        b->deadlines = (ca_legal_deadline_t *)n;
        b->deadline_cap = nc;
    }
    b->deadlines[b->deadline_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected deadline indices by due_on_ms. */
static void deadline_sort_asc(const ca_legal_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->deadlines[key].due_on_ms;
        size_t j = i;
        while (j > 0 && b->deadlines[idx[j - 1]].due_on_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_legal_deadline_t *ca_legal_board_upcoming_deadlines(const ca_legal_board_t *b,
                                                       int64_t now_ms,
                                                       size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->deadline_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->deadline_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->deadline_count; ++i)
        if (b->deadlines[i].due_on_ms >= now_ms) idx[n++] = i;
    deadline_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_legal_deadline_t *out = (ca_legal_deadline_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!deadline_copy(&out[i], &b->deadlines[idx[i]])) {
            ca_legal_deadline_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_legal_board_add_clause(ca_legal_board_t *b, const ca_legal_clause_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->clause_count; ++i) {
        if (cab_ord_eq(b->clauses[i].clause_id, c->clause_id)) {
            ca_legal_clause_t copy;
            if (!clause_copy(&copy, c)) return -1;
            ca_legal_clause_free(&b->clauses[i]);
            b->clauses[i] = copy;
            return 0;
        }
    }
    ca_legal_clause_t copy;
    if (!clause_copy(&copy, c)) return -1;
    if (b->clause_count == b->clause_cap) {
        size_t nc = b->clause_cap ? b->clause_cap * 2 : 4;
        void *n = realloc(b->clauses, nc * sizeof(*b->clauses));
        if (!n) { ca_legal_clause_free(&copy); return -1; }
        b->clauses = (ca_legal_clause_t *)n;
        b->clause_cap = nc;
    }
    b->clauses[b->clause_count++] = copy;
    return 0;
}

ca_legal_clause_t *ca_legal_board_clauses_by_tag(const ca_legal_board_t *b,
                                                 const char *tag,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    /* ArgumentException on null/whitespace tag -> SIZE_MAX. */
    if (!b || cab_is_ws(tag)) { *out_count = (size_t)-1; return NULL; }
    if (b->clause_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->clause_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->clause_count; ++i)
        if (cab_strv_ci_contains(b->clauses[i].tags, b->clauses[i].tag_count, tag))
            idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_legal_clause_t *out = (ca_legal_clause_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!clause_copy(&out[i], &b->clauses[idx[i]])) {
            ca_legal_clause_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
