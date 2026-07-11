/*
 * business.c — CircleAI.Business (C11 port of BusinessPrimitives.cs).
 *
 * InMemoryBusinessBoard: units (UnitId keyed), KPI samples (flat append list),
 * quarter targets (composite-key keyed set). Pure C11 + libc.
 */

#include "circle_ai/business.h"
#include "board_common.h"
#include <math.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_biz_unit_free(ca_biz_unit_t *u) {
    if (!u) return;
    free(u->unit_id);
    free(u->name);
    free(u->parent_unit_id);
    cab_strv_free(u->kpi_tags, u->kpi_tag_count);
    u->unit_id = u->name = u->parent_unit_id = NULL;
    u->kpi_tags = NULL;
    u->kpi_tag_count = 0;
}
void ca_biz_unit_free_array(ca_biz_unit_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_biz_unit_free(&arr[i]);
    free(arr);
}

static bool unit_copy(ca_biz_unit_t *dst, const ca_biz_unit_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->unit_id        = cab_strdup_empty(src->unit_id);
    dst->name           = cab_strdup_empty(src->name);
    dst->parent_unit_id = cab_strdup_empty(src->parent_unit_id);
    if (!dst->unit_id || !dst->name || !dst->parent_unit_id) {
        ca_biz_unit_free(dst);
        return false;
    }
    if (!cab_strv_copy(&dst->kpi_tags, src->kpi_tags, src->kpi_tag_count)) {
        ca_biz_unit_free(dst);
        return false;
    }
    dst->kpi_tag_count = src->kpi_tag_count;
    return true;
}

void ca_biz_kpi_free(ca_biz_kpi_t *k) {
    if (!k) return;
    free(k->unit_id);
    free(k->metric);
    k->unit_id = k->metric = NULL;
}

static bool kpi_copy(ca_biz_kpi_t *dst, const ca_biz_kpi_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->unit_id   = cab_strdup_empty(src->unit_id);
    dst->metric    = cab_strdup_empty(src->metric);
    dst->value     = src->value;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->unit_id || !dst->metric) { ca_biz_kpi_free(dst); return false; }
    return true;
}

void ca_biz_target_free(ca_biz_target_t *t) {
    if (!t) return;
    free(t->unit_id);
    free(t->metric);
    t->unit_id = t->metric = NULL;
}

static bool target_copy(ca_biz_target_t *dst, const ca_biz_target_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->unit_id = cab_strdup_empty(src->unit_id);
    dst->metric  = cab_strdup_empty(src->metric);
    dst->year    = src->year;
    dst->quarter = src->quarter;
    dst->target  = src->target;
    if (!dst->unit_id || !dst->metric) { ca_biz_target_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_biz_board {
    ca_biz_unit_t   *units;
    size_t           u_count, u_cap;
    ca_biz_kpi_t    *kpis;
    size_t           k_count, k_cap;
    ca_biz_target_t *targets;
    size_t           t_count, t_cap;
};

ca_biz_board_t *ca_biz_board_create(void) {
    return (ca_biz_board_t *)calloc(1, sizeof(ca_biz_board_t));
}
void ca_biz_board_destroy(ca_biz_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->u_count; ++i) ca_biz_unit_free(&b->units[i]);
    for (size_t i = 0; i < b->k_count; ++i) ca_biz_kpi_free(&b->kpis[i]);
    for (size_t i = 0; i < b->t_count; ++i) ca_biz_target_free(&b->targets[i]);
    free(b->units);
    free(b->kpis);
    free(b->targets);
    free(b);
}

int ca_biz_board_add(ca_biz_board_t *b, const ca_biz_unit_t *u) {
    if (!b || !u) return -1;
    for (size_t i = 0; i < b->u_count; ++i) {
        if (cab_ord_eq(b->units[i].unit_id, u->unit_id)) {
            ca_biz_unit_t copy;
            if (!unit_copy(&copy, u)) return -1;
            ca_biz_unit_free(&b->units[i]);
            b->units[i] = copy;
            return 0;
        }
    }
    ca_biz_unit_t copy;
    if (!unit_copy(&copy, u)) return -1;
    if (b->u_count == b->u_cap) {
        size_t nc = b->u_cap ? b->u_cap * 2 : 4;
        void *n = realloc(b->units, nc * sizeof(*b->units));
        if (!n) { ca_biz_unit_free(&copy); return -1; }
        b->units = (ca_biz_unit_t *)n;
        b->u_cap = nc;
    }
    b->units[b->u_count++] = copy;
    return 0;
}

bool ca_biz_board_get_unit(const ca_biz_board_t *b, const char *id,
                           ca_biz_unit_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->u_count; ++i)
        if (cab_ord_eq(b->units[i].unit_id, id))
            return unit_copy(out, &b->units[i]);
    return false;
}

ca_biz_unit_t *ca_biz_board_children_of(const ca_biz_board_t *b,
                                        const char *parent_unit_id,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !parent_unit_id) { *out_count = (size_t)-1; return NULL; }
    if (b->u_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->u_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->u_count; ++i)
        if (cab_ord_eq(b->units[i].parent_unit_id, parent_unit_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_biz_unit_t *out = (ca_biz_unit_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!unit_copy(&out[i], &b->units[idx[i]])) {
            ca_biz_unit_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_biz_board_record(ca_biz_board_t *b, const ca_biz_kpi_t *s) {
    if (!b || !s) return -1;
    ca_biz_kpi_t copy;
    if (!kpi_copy(&copy, s)) return -1;
    if (b->k_count == b->k_cap) {
        size_t nc = b->k_cap ? b->k_cap * 2 : 4;
        void *n = realloc(b->kpis, nc * sizeof(*b->kpis));
        if (!n) { ca_biz_kpi_free(&copy); return -1; }
        b->kpis = (ca_biz_kpi_t *)n;
        b->k_cap = nc;
    }
    b->kpis[b->k_count++] = copy;
    return 0;
}

double ca_biz_board_latest_kpi(const ca_biz_board_t *b, const char *unit_id,
                               const char *metric) {
    if (!b || !unit_id || !metric) return NAN;
    bool found = false;
    int64_t best_at = 0;
    double best_val = NAN;
    /* OrderByDescending(AtUtc).FirstOrDefault(): the max-AtUtc match; ties keep
     * the first encountered (stable descending sort keeps earliest index). */
    for (size_t i = 0; i < b->k_count; ++i) {
        const ca_biz_kpi_t *k = &b->kpis[i];
        if (cab_ord_eq(k->unit_id, unit_id) && cab_ord_eq(k->metric, metric)) {
            if (!found || k->at_utc_ms > best_at) {
                found = true;
                best_at = k->at_utc_ms;
                best_val = k->value;
            }
        }
    }
    return found ? best_val : NAN;
}

int ca_biz_board_set_target(ca_biz_board_t *b, const ca_biz_target_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        ca_biz_target_t *e = &b->targets[i];
        if (e->year == t->year && e->quarter == t->quarter &&
            cab_ord_eq(e->unit_id, t->unit_id) &&
            cab_ord_eq(e->metric, t->metric)) {
            ca_biz_target_t copy;
            if (!target_copy(&copy, t)) return -1;
            ca_biz_target_free(e);
            *e = copy;
            return 0;
        }
    }
    ca_biz_target_t copy;
    if (!target_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->targets, nc * sizeof(*b->targets));
        if (!n) { ca_biz_target_free(&copy); return -1; }
        b->targets = (ca_biz_target_t *)n;
        b->t_cap = nc;
    }
    b->targets[b->t_count++] = copy;
    return 0;
}

double ca_biz_board_target_achievement(const ca_biz_board_t *b,
                                       const char *unit_id, const char *metric,
                                       int year, int quarter) {
    if (!b || !unit_id || !metric) return NAN;
    for (size_t i = 0; i < b->t_count; ++i) {
        const ca_biz_target_t *e = &b->targets[i];
        if (e->year == year && e->quarter == quarter &&
            cab_ord_eq(e->unit_id, unit_id) && cab_ord_eq(e->metric, metric)) {
            if (e->target == 0.0) return NAN;
            return ca_biz_board_latest_kpi(b, unit_id, metric) / e->target;
        }
    }
    return NAN; /* target missing */
}
