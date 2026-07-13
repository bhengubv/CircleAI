/*
 * agriculture.c — CircleAI.Agriculture (C11 port of AgriculturePrimitives.cs).
 *
 * InMemoryFarmBoard: fields (FieldId keyed), crops (CropId keyed), yields (append
 * list). AvgYieldOfVariety joins yields to crops by CropId. Pure C11 + libc.
 */

#include "circle_ai/agriculture.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_farm_field_free(ca_farm_field_t *f) {
    if (!f) return;
    free(f->field_id);
    free(f->soil_type);
    free(f->irrigation_kind);
    f->field_id = f->soil_type = f->irrigation_kind = NULL;
}

static bool field_copy(ca_farm_field_t *dst, const ca_farm_field_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->field_id        = cab_strdup_empty(src->field_id);
    dst->area_ha         = src->area_ha;
    dst->soil_type       = cab_strdup_empty(src->soil_type);
    dst->irrigation_kind = cab_strdup_empty(src->irrigation_kind);
    if (!dst->field_id || !dst->soil_type || !dst->irrigation_kind) {
        ca_farm_field_free(dst);
        return false;
    }
    return true;
}

void ca_farm_crop_free(ca_farm_crop_t *c) {
    if (!c) return;
    free(c->crop_id);
    free(c->field_id);
    free(c->variety);
    c->crop_id = c->field_id = c->variety = NULL;
}
void ca_farm_crop_free_array(ca_farm_crop_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_farm_crop_free(&arr[i]);
    free(arr);
}

static bool crop_copy(ca_farm_crop_t *dst, const ca_farm_crop_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->crop_id              = cab_strdup_empty(src->crop_id);
    dst->field_id             = cab_strdup_empty(src->field_id);
    dst->variety              = cab_strdup_empty(src->variety);
    dst->planted_on_ms        = src->planted_on_ms;
    dst->has_expected_harvest = src->has_expected_harvest;
    dst->expected_harvest_ms  = src->has_expected_harvest ? src->expected_harvest_ms : 0;
    if (!dst->crop_id || !dst->field_id || !dst->variety) {
        ca_farm_crop_free(dst);
        return false;
    }
    return true;
}

void ca_farm_yield_free(ca_farm_yield_t *y) {
    if (!y) return;
    free(y->crop_id);
    y->crop_id = NULL;
}

static bool yield_copy(ca_farm_yield_t *dst, const ca_farm_yield_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->crop_id         = cab_strdup_empty(src->crop_id);
    dst->tons_per_ha     = src->tons_per_ha;
    dst->harvested_on_ms = src->harvested_on_ms;
    if (!dst->crop_id) return false;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_farm_board {
    ca_farm_field_t *fields;
    size_t           f_count, f_cap;
    ca_farm_crop_t  *crops;
    size_t           c_count, c_cap;
    ca_farm_yield_t *yields;
    size_t           y_count, y_cap;
};

ca_farm_board_t *ca_farm_board_create(void) {
    return (ca_farm_board_t *)calloc(1, sizeof(ca_farm_board_t));
}
void ca_farm_board_destroy(ca_farm_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->f_count; ++i) ca_farm_field_free(&b->fields[i]);
    for (size_t i = 0; i < b->c_count; ++i) ca_farm_crop_free(&b->crops[i]);
    for (size_t i = 0; i < b->y_count; ++i) ca_farm_yield_free(&b->yields[i]);
    free(b->fields);
    free(b->crops);
    free(b->yields);
    free(b);
}

int ca_farm_board_add_field(ca_farm_board_t *b, const ca_farm_field_t *f) {
    if (!b || !f) return -1;
    for (size_t i = 0; i < b->f_count; ++i) {
        if (cab_ord_eq(b->fields[i].field_id, f->field_id)) {
            ca_farm_field_t copy;
            if (!field_copy(&copy, f)) return -1;
            ca_farm_field_free(&b->fields[i]);
            b->fields[i] = copy;
            return 0;
        }
    }
    ca_farm_field_t copy;
    if (!field_copy(&copy, f)) return -1;
    if (b->f_count == b->f_cap) {
        size_t nc = b->f_cap ? b->f_cap * 2 : 4;
        void *n = realloc(b->fields, nc * sizeof(*b->fields));
        if (!n) { ca_farm_field_free(&copy); return -1; }
        b->fields = (ca_farm_field_t *)n;
        b->f_cap = nc;
    }
    b->fields[b->f_count++] = copy;
    return 0;
}

int ca_farm_board_plant(ca_farm_board_t *b, const ca_farm_crop_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->crops[i].crop_id, c->crop_id)) {
            ca_farm_crop_t copy;
            if (!crop_copy(&copy, c)) return -1;
            ca_farm_crop_free(&b->crops[i]);
            b->crops[i] = copy;
            return 0;
        }
    }
    ca_farm_crop_t copy;
    if (!crop_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->crops, nc * sizeof(*b->crops));
        if (!n) { ca_farm_crop_free(&copy); return -1; }
        b->crops = (ca_farm_crop_t *)n;
        b->c_cap = nc;
    }
    b->crops[b->c_count++] = copy;
    return 0;
}

int ca_farm_board_record_yield(ca_farm_board_t *b, const ca_farm_yield_t *y) {
    if (!b || !y) return -1;
    ca_farm_yield_t copy;
    if (!yield_copy(&copy, y)) return -1;
    if (b->y_count == b->y_cap) {
        size_t nc = b->y_cap ? b->y_cap * 2 : 4;
        void *n = realloc(b->yields, nc * sizeof(*b->yields));
        if (!n) { ca_farm_yield_free(&copy); return -1; }
        b->yields = (ca_farm_yield_t *)n;
        b->y_cap = nc;
    }
    b->yields[b->y_count++] = copy;
    return 0;
}

bool ca_farm_board_get_field(const ca_farm_board_t *b, const char *id,
                             ca_farm_field_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ord_eq(b->fields[i].field_id, id))
            return field_copy(out, &b->fields[i]);
    return false;
}

/* Stable ascending sort of collected indices by PlantedOn. */
static void crop_sort_asc(const ca_farm_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->crops[key].planted_on_ms;
        size_t j = i;
        while (j > 0 && b->crops[idx[j - 1]].planted_on_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_farm_crop_t *ca_farm_board_crops_for_field(const ca_farm_board_t *b,
                                              const char *field_id,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !field_id) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->c_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->crops[i].field_id, field_id)) idx[n++] = i;
    crop_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_farm_crop_t *out = (ca_farm_crop_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!crop_copy(&out[i], &b->crops[idx[i]])) {
            ca_farm_crop_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* Look up a crop's Variety by CropId (borrowed); NULL if absent. */
static const char *crop_variety(const ca_farm_board_t *b, const char *crop_id) {
    for (size_t i = 0; i < b->c_count; ++i)
        if (cab_ord_eq(b->crops[i].crop_id, crop_id)) return b->crops[i].variety;
    return NULL;
}

double ca_farm_board_avg_yield_of_variety(const ca_farm_board_t *b,
                                          const char *variety) {
    if (!b || !variety) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < b->y_count; ++i) {
        const char *v = crop_variety(b, b->yields[i].crop_id);
        if (v && cab_ci_eq(v, variety)) {
            sum += b->yields[i].tons_per_ha;
            n++;
        }
    }
    return n == 0 ? 0.0 : sum / (double)n;
}

size_t ca_farm_board_field_count(const ca_farm_board_t *b) {
    return b ? b->f_count : 0;
}

bool ca_farm_board_remove_field(ca_farm_board_t *b, const char *field_id) {
    /* _fields.TryRemove(fieldId, out _). */
    if (!b || !field_id) return false;
    for (size_t i = 0; i < b->f_count; ++i) {
        if (cab_ord_eq(b->fields[i].field_id, field_id)) {
            ca_farm_field_free(&b->fields[i]);
            for (size_t j = i; j + 1 < b->f_count; ++j)
                b->fields[j] = b->fields[j + 1];
            b->f_count--;
            return true;
        }
    }
    return false;
}

double ca_farm_board_total_area_ha(const ca_farm_board_t *b) {
    if (!b) return 0.0;
    double sum = 0.0;
    for (size_t i = 0; i < b->f_count; ++i) sum += b->fields[i].area_ha;
    return sum;
}

ca_farm_field_t *ca_farm_board_fields_by_soil(const ca_farm_board_t *b,
                                              const char *soil_type,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !soil_type) { *out_count = (size_t)-1; return NULL; }
    if (b->f_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->f_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ci_eq(b->fields[i].soil_type, soil_type)) idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderByDescending(AreaHa), stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        double key = b->fields[cur].area_ha;
        size_t j = i;
        while (j > 0 && b->fields[idx[j - 1]].area_ha < key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_farm_field_t *out = (ca_farm_field_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!field_copy(&out[i], &b->fields[idx[i]])) {
            for (size_t j = 0; j < i; ++j) ca_farm_field_free(&out[j]);
            free(out); free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_farm_crop_t *ca_farm_board_due_for_harvest(const ca_farm_board_t *b,
                                              int64_t as_of_ms,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->c_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->c_count; ++i)
        if (b->crops[i].has_expected_harvest &&
            b->crops[i].expected_harvest_ms <= as_of_ms)
            idx[n++] = i;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }

    /* OrderBy(ExpectedHarvest) ascending, stable insertion sort. */
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int64_t key = b->crops[cur].expected_harvest_ms;
        size_t j = i;
        while (j > 0 && b->crops[idx[j - 1]].expected_harvest_ms > key) {
            idx[j] = idx[j - 1]; --j;
        }
        idx[j] = cur;
    }

    ca_farm_crop_t *out = (ca_farm_crop_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!crop_copy(&out[i], &b->crops[idx[i]])) {
            ca_farm_crop_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

char *ca_farm_board_best_yielding_variety(const ca_farm_board_t *b) {
    if (!b || b->y_count == 0) return NULL;

    /* Group yields (whose crop exists) by the crop's Variety (OrdinalIgnoreCase,
     * first-seen spelling as the key), accumulating sum + count. */
    typedef struct { const char *variety; double sum; size_t count; } grp_t;
    grp_t *g = (grp_t *)malloc(b->y_count * sizeof(*g));
    if (!g) return NULL;
    size_t gc = 0;
    for (size_t i = 0; i < b->y_count; ++i) {
        const char *v = crop_variety(b, b->yields[i].crop_id);
        if (!v) continue;   /* _crops.ContainsKey(y.CropId) filter */
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ci_eq(g[k].variety, v)) break;
        if (k == gc) { g[gc].variety = v; g[gc].sum = 0.0; g[gc].count = 0; gc++; }
        g[k].sum += b->yields[i].tons_per_ha;
        g[k].count++;
    }
    if (gc == 0) { free(g); return NULL; }

    /* OrderByDescending(avg); groups already in first-appearance order and the
     * sort is stable, so ties keep that order — pick the first. */
    size_t best = 0;
    double best_avg = g[0].sum / (double)g[0].count;
    for (size_t k = 1; k < gc; ++k) {
        double avg = g[k].sum / (double)g[k].count;
        if (avg > best_avg) { best_avg = avg; best = k; }
    }
    char *res = cab_strdup_empty(g[best].variety);
    free(g);
    return res;   /* NULL on OOM (cab_strdup_empty) */
}
