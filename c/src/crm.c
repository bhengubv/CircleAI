/*
 * crm.c — CircleAI.CRM (C11 port of Contracts.cs + InMemoryCrm.cs).
 *
 * Three independent in-memory backends over linear arrays:
 *   InMemoryContactStore (ContactId keyed, name/email substring search),
 *   InMemoryDealPipeline (DealId keyed, list-by-stage sorted by Value desc),
 *   InMemoryActivityLog  (per-ContactId append list, read newest-first).
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/crm.h"
#include "board_common.h"

/* ── nullable-string helper ─────────────────────────────────────────────── */

/* Copy a C# nullable string into (has,*dst). Returns false only on OOM. */
static bool opt_copy(bool *has_dst, char **dst, bool has_src, const char *src) {
    if (has_src) {
        *dst = cab_strdup_empty(src);
        if (!*dst) { *has_dst = false; return false; }
        *has_dst = true;
    } else {
        *has_dst = false;
        *dst = NULL;
    }
    return true;
}

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_crm_contact_free(ca_crm_contact_t *c) {
    if (!c) return;
    free(c->contact_id);
    free(c->full_name);
    free(c->email);
    free(c->phone);
    free(c->company_id);
    c->contact_id = c->full_name = c->email = c->phone = c->company_id = NULL;
    c->has_email = c->has_phone = c->has_company = false;
}
void ca_crm_contact_free_array(ca_crm_contact_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_crm_contact_free(&arr[i]);
    free(arr);
}

static bool contact_copy(ca_crm_contact_t *dst, const ca_crm_contact_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->contact_id = cab_strdup_empty(src->contact_id);
    dst->full_name  = cab_strdup_empty(src->full_name);
    bool ok = dst->contact_id && dst->full_name;
    ok = ok && opt_copy(&dst->has_email,   &dst->email,      src->has_email,   src->email);
    ok = ok && opt_copy(&dst->has_phone,   &dst->phone,      src->has_phone,   src->phone);
    ok = ok && opt_copy(&dst->has_company, &dst->company_id, src->has_company, src->company_id);
    if (!ok) { ca_crm_contact_free(dst); return false; }
    return true;
}

void ca_crm_company_free(ca_crm_company_t *c) {
    if (!c) return;
    free(c->company_id);
    free(c->name);
    free(c->industry);
    c->company_id = c->name = c->industry = NULL;
    c->has_industry = false;
}

void ca_crm_deal_free(ca_crm_deal_t *d) {
    if (!d) return;
    free(d->deal_id);
    free(d->company_id);
    free(d->name);
    free(d->currency);
    free(d->stage);
    d->deal_id = d->company_id = d->name = d->currency = d->stage = NULL;
}
void ca_crm_deal_free_array(ca_crm_deal_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_crm_deal_free(&arr[i]);
    free(arr);
}

static bool deal_copy(ca_crm_deal_t *dst, const ca_crm_deal_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->deal_id    = cab_strdup_empty(src->deal_id);
    dst->company_id = cab_strdup_empty(src->company_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->currency   = cab_strdup_empty(src->currency);
    dst->stage      = cab_strdup_empty(src->stage);
    dst->value      = src->value;
    if (!dst->deal_id || !dst->company_id || !dst->name || !dst->currency ||
        !dst->stage) { ca_crm_deal_free(dst); return false; }
    return true;
}

void ca_crm_activity_free(ca_crm_activity_t *a) {
    if (!a) return;
    free(a->activity_id);
    free(a->contact_id);
    free(a->kind);
    free(a->body);
    a->activity_id = a->contact_id = a->kind = a->body = NULL;
}
void ca_crm_activity_free_array(ca_crm_activity_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_crm_activity_free(&arr[i]);
    free(arr);
}

static bool activity_copy(ca_crm_activity_t *dst, const ca_crm_activity_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->activity_id = cab_strdup_empty(src->activity_id);
    dst->contact_id  = cab_strdup_empty(src->contact_id);
    dst->kind        = cab_strdup_empty(src->kind);
    dst->body        = cab_strdup_empty(src->body);
    dst->at_utc_ms   = src->at_utc_ms;
    if (!dst->activity_id || !dst->contact_id || !dst->kind || !dst->body) {
        ca_crm_activity_free(dst);
        return false;
    }
    return true;
}

/* ── InMemoryContactStore ───────────────────────────────────────────────── */

struct ca_crm_contact_store {
    ca_crm_contact_t *items;
    size_t            count, cap;
};

ca_crm_contact_store_t *ca_crm_contact_store_create(void) {
    return (ca_crm_contact_store_t *)calloc(1, sizeof(ca_crm_contact_store_t));
}
void ca_crm_contact_store_destroy(ca_crm_contact_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_crm_contact_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_crm_contact_store_backend_id(const ca_crm_contact_store_t *s) {
    (void)s;
    return "in-memory";
}

int ca_crm_contact_store_upsert(ca_crm_contact_store_t *s,
                                const ca_crm_contact_t *c) {
    if (!s || !c) return -1;
    if (cab_is_ws(c->contact_id)) return 2; /* ArgumentException */
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].contact_id, c->contact_id)) {
            ca_crm_contact_t copy;
            if (!contact_copy(&copy, c)) return -1;
            ca_crm_contact_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_crm_contact_t copy;
    if (!contact_copy(&copy, c)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_crm_contact_free(&copy); return -1; }
        s->items = (ca_crm_contact_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_crm_contact_store_get(const ca_crm_contact_store_t *s, const char *id,
                              ca_crm_contact_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].contact_id, id))
            return contact_copy(out, &s->items[i]);
    return false;
}

/* Stable ascending sort of collected indices by FullName (OrdinalIgnoreCase). */
static void contact_sort_ci(const ca_crm_contact_store_t *s, size_t *idx,
                            size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               cab_ci_cmp(s->items[idx[j - 1]].full_name,
                          s->items[key].full_name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_crm_contact_t *ca_crm_contact_store_search(const ca_crm_contact_store_t *s,
                                              const char *query, int top_k,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(s->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i) {
        const ca_crm_contact_t *c = &s->items[i];
        bool match = cab_ci_contains(c->full_name, query) ||
                     (c->has_email && cab_ci_contains(c->email, query));
        if (match) idx[n++] = i;
    }
    contact_sort_ci(s, idx, n);
    if ((size_t)top_k < n) n = (size_t)top_k;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_crm_contact_t *out = (ca_crm_contact_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!contact_copy(&out[i], &s->items[idx[i]])) {
            ca_crm_contact_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* ── InMemoryDealPipeline ───────────────────────────────────────────────── */

struct ca_crm_deal_pipeline {
    ca_crm_deal_t *items;
    size_t         count, cap;
};

ca_crm_deal_pipeline_t *ca_crm_deal_pipeline_create(void) {
    return (ca_crm_deal_pipeline_t *)calloc(1, sizeof(ca_crm_deal_pipeline_t));
}
void ca_crm_deal_pipeline_destroy(ca_crm_deal_pipeline_t *p) {
    if (!p) return;
    for (size_t i = 0; i < p->count; ++i) ca_crm_deal_free(&p->items[i]);
    free(p->items);
    free(p);
}
const char *ca_crm_deal_pipeline_backend_id(const ca_crm_deal_pipeline_t *p) {
    (void)p;
    return "in-memory";
}

int ca_crm_deal_pipeline_upsert(ca_crm_deal_pipeline_t *p,
                                const ca_crm_deal_t *d) {
    if (!p || !d) return -1;
    if (cab_is_ws(d->deal_id)) return 2;
    for (size_t i = 0; i < p->count; ++i) {
        if (cab_ord_eq(p->items[i].deal_id, d->deal_id)) {
            ca_crm_deal_t copy;
            if (!deal_copy(&copy, d)) return -1;
            ca_crm_deal_free(&p->items[i]);
            p->items[i] = copy;
            return 0;
        }
    }
    ca_crm_deal_t copy;
    if (!deal_copy(&copy, d)) return -1;
    if (p->count == p->cap) {
        size_t nc = p->cap ? p->cap * 2 : 4;
        void *n = realloc(p->items, nc * sizeof(*p->items));
        if (!n) { ca_crm_deal_free(&copy); return -1; }
        p->items = (ca_crm_deal_t *)n;
        p->cap = nc;
    }
    p->items[p->count++] = copy;
    return 0;
}

bool ca_crm_deal_pipeline_get(const ca_crm_deal_pipeline_t *p, const char *id,
                              ca_crm_deal_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || !id || !out) return false;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ord_eq(p->items[i].deal_id, id))
            return deal_copy(out, &p->items[i]);
    return false;
}

/* Stable descending sort of collected indices by Value. */
static void deal_sort_value_desc(const ca_crm_deal_pipeline_t *p, size_t *idx,
                                 size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        ca_crm_decimal_t kv = p->items[key].value;
        size_t j = i;
        while (j > 0 && p->items[idx[j - 1]].value < kv) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_crm_deal_t *ca_crm_deal_pipeline_list_by_stage(const ca_crm_deal_pipeline_t *p,
                                                  const char *stage,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!p || cab_is_ws(stage)) { *out_count = (size_t)-1; return NULL; }
    if (p->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(p->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ci_eq(p->items[i].stage, stage)) idx[n++] = i;
    deal_sort_value_desc(p, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_crm_deal_t *out = (ca_crm_deal_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!deal_copy(&out[i], &p->items[idx[i]])) {
            ca_crm_deal_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* ── InMemoryActivityLog ────────────────────────────────────────────────── */

struct ca_crm_activity_log {
    ca_crm_activity_t *items;  /* flat append list across all contacts */
    size_t             count, cap;
};

ca_crm_activity_log_t *ca_crm_activity_log_create(void) {
    return (ca_crm_activity_log_t *)calloc(1, sizeof(ca_crm_activity_log_t));
}
void ca_crm_activity_log_destroy(ca_crm_activity_log_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) ca_crm_activity_free(&l->items[i]);
    free(l->items);
    free(l);
}
const char *ca_crm_activity_log_backend_id(const ca_crm_activity_log_t *l) {
    (void)l;
    return "in-memory";
}

int ca_crm_activity_log_append(ca_crm_activity_log_t *l,
                               const ca_crm_activity_t *a) {
    if (!l || !a) return -1;
    if (cab_is_ws(a->contact_id)) return 2;
    ca_crm_activity_t copy;
    if (!activity_copy(&copy, a)) return -1;
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *n = realloc(l->items, nc * sizeof(*l->items));
        if (!n) { ca_crm_activity_free(&copy); return -1; }
        l->items = (ca_crm_activity_t *)n;
        l->cap = nc;
    }
    l->items[l->count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AtUtc. */
static void activity_sort_desc(const ca_crm_activity_log_t *l, size_t *idx,
                               size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = l->items[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && l->items[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_crm_activity_t *ca_crm_activity_log_read_for_contact(
    const ca_crm_activity_log_t *l, const char *contact_id, int limit,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!l || cab_is_ws(contact_id)) { *out_count = (size_t)-1; return NULL; }
    if (l->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(l->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < l->count; ++i)
        if (cab_ord_eq(l->items[i].contact_id, contact_id)) idx[n++] = i;
    activity_sort_desc(l, idx, n);
    if (limit >= 0 && (size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_crm_activity_t *out = (ca_crm_activity_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!activity_copy(&out[i], &l->items[idx[i]])) {
            ca_crm_activity_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
