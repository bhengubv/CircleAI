/*
 * realestate.c — CircleAI.RealEstate (C11 port of RealEstatePrimitives.cs).
 *
 * InMemoryRealEstateBoard: properties (PropertyId keyed), listings (ListingId
 * keyed), valuations + viewings (flat append lists). ActiveInSuburb joins a
 * listing to its property's suburb; SuburbAverage means the asking prices.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/realestate.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_re_property_free(ca_re_property_t *p) {
    if (!p) return;
    free(p->property_id);
    free(p->suburb);
    p->property_id = p->suburb = NULL;
}

static bool property_copy(ca_re_property_t *dst, const ca_re_property_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->property_id   = cab_strdup_empty(src->property_id);
    dst->suburb        = cab_strdup_empty(src->suburb);
    dst->kind          = src->kind;
    dst->beds          = src->beds;
    dst->baths         = src->baths;
    dst->floor_area_m2 = src->floor_area_m2;
    if (!dst->property_id || !dst->suburb) { ca_re_property_free(dst); return false; }
    return true;
}

void ca_re_listing_free(ca_re_listing_t *l) {
    if (!l) return;
    free(l->listing_id);
    free(l->property_id);
    free(l->currency);
    l->listing_id = l->property_id = l->currency = NULL;
}
void ca_re_listing_free_array(ca_re_listing_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_re_listing_free(&arr[i]);
    free(arr);
}

static bool listing_copy(ca_re_listing_t *dst, const ca_re_listing_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->listing_id    = cab_strdup_empty(src->listing_id);
    dst->property_id   = cab_strdup_empty(src->property_id);
    dst->currency      = cab_strdup_empty(src->currency);
    dst->asking_price  = src->asking_price;
    dst->listed_utc_ms = src->listed_utc_ms;
    dst->is_active     = src->is_active;
    if (!dst->listing_id || !dst->property_id || !dst->currency) {
        ca_re_listing_free(dst);
        return false;
    }
    return true;
}

void ca_re_valuation_free(ca_re_valuation_t *v) {
    if (!v) return;
    free(v->property_id);
    free(v->source);
    v->property_id = v->source = NULL;
}

static bool valuation_copy(ca_re_valuation_t *dst,
                           const ca_re_valuation_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->property_id     = cab_strdup_empty(src->property_id);
    dst->source          = cab_strdup_empty(src->source);
    dst->estimated_value = src->estimated_value;
    dst->at_utc_ms       = src->at_utc_ms;
    if (!dst->property_id || !dst->source) { ca_re_valuation_free(dst); return false; }
    return true;
}

void ca_re_viewing_free(ca_re_viewing_t *v) {
    if (!v) return;
    free(v->viewing_id);
    free(v->listing_id);
    free(v->attendee_name);
    v->viewing_id = v->listing_id = v->attendee_name = NULL;
}

static bool viewing_copy(ca_re_viewing_t *dst, const ca_re_viewing_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->viewing_id    = cab_strdup_empty(src->viewing_id);
    dst->listing_id    = cab_strdup_empty(src->listing_id);
    dst->attendee_name = cab_strdup_empty(src->attendee_name);
    dst->at_utc_ms     = src->at_utc_ms;
    if (!dst->viewing_id || !dst->listing_id || !dst->attendee_name) {
        ca_re_viewing_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_re_board {
    ca_re_property_t  *props;
    size_t             p_count, p_cap;
    ca_re_listing_t   *listings;
    size_t             l_count, l_cap;
    ca_re_valuation_t *vals;
    size_t             val_count, val_cap;
    ca_re_viewing_t   *viewings;
    size_t             view_count, view_cap;
};

ca_re_board_t *ca_re_board_create(void) {
    return (ca_re_board_t *)calloc(1, sizeof(ca_re_board_t));
}
void ca_re_board_destroy(ca_re_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->p_count; ++i)    ca_re_property_free(&b->props[i]);
    for (size_t i = 0; i < b->l_count; ++i)    ca_re_listing_free(&b->listings[i]);
    for (size_t i = 0; i < b->val_count; ++i)  ca_re_valuation_free(&b->vals[i]);
    for (size_t i = 0; i < b->view_count; ++i) ca_re_viewing_free(&b->viewings[i]);
    free(b->props);
    free(b->listings);
    free(b->vals);
    free(b->viewings);
    free(b);
}

int ca_re_board_register_property(ca_re_board_t *b, const ca_re_property_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->props[i].property_id, p->property_id)) {
            ca_re_property_t copy;
            if (!property_copy(&copy, p)) return -1;
            ca_re_property_free(&b->props[i]);
            b->props[i] = copy;
            return 0;
        }
    }
    ca_re_property_t copy;
    if (!property_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->props, nc * sizeof(*b->props));
        if (!n) { ca_re_property_free(&copy); return -1; }
        b->props = (ca_re_property_t *)n;
        b->p_cap = nc;
    }
    b->props[b->p_count++] = copy;
    return 0;
}

int ca_re_board_list(ca_re_board_t *b, const ca_re_listing_t *l) {
    if (!b || !l) return -1;
    for (size_t i = 0; i < b->l_count; ++i) {
        if (cab_ord_eq(b->listings[i].listing_id, l->listing_id)) {
            ca_re_listing_t copy;
            if (!listing_copy(&copy, l)) return -1;
            ca_re_listing_free(&b->listings[i]);
            b->listings[i] = copy;
            return 0;
        }
    }
    ca_re_listing_t copy;
    if (!listing_copy(&copy, l)) return -1;
    if (b->l_count == b->l_cap) {
        size_t nc = b->l_cap ? b->l_cap * 2 : 4;
        void *n = realloc(b->listings, nc * sizeof(*b->listings));
        if (!n) { ca_re_listing_free(&copy); return -1; }
        b->listings = (ca_re_listing_t *)n;
        b->l_cap = nc;
    }
    b->listings[b->l_count++] = copy;
    return 0;
}

int ca_re_board_close(ca_re_board_t *b, const char *listing_id) {
    if (!b || !listing_id) return -1;
    for (size_t i = 0; i < b->l_count; ++i) {
        if (cab_ord_eq(b->listings[i].listing_id, listing_id)) {
            b->listings[i].is_active = false;
            return 0;
        }
    }
    return 1; /* InvalidOperationException: unknown listing */
}

int ca_re_board_value(ca_re_board_t *b, const ca_re_valuation_t *v) {
    if (!b || !v) return -1;
    ca_re_valuation_t copy;
    if (!valuation_copy(&copy, v)) return -1;
    if (b->val_count == b->val_cap) {
        size_t nc = b->val_cap ? b->val_cap * 2 : 4;
        void *n = realloc(b->vals, nc * sizeof(*b->vals));
        if (!n) { ca_re_valuation_free(&copy); return -1; }
        b->vals = (ca_re_valuation_t *)n;
        b->val_cap = nc;
    }
    b->vals[b->val_count++] = copy;
    return 0;
}

int ca_re_board_schedule_viewing(ca_re_board_t *b, const ca_re_viewing_t *v) {
    if (!b || !v) return -1;
    ca_re_viewing_t copy;
    if (!viewing_copy(&copy, v)) return -1;
    if (b->view_count == b->view_cap) {
        size_t nc = b->view_cap ? b->view_cap * 2 : 4;
        void *n = realloc(b->viewings, nc * sizeof(*b->viewings));
        if (!n) { ca_re_viewing_free(&copy); return -1; }
        b->viewings = (ca_re_viewing_t *)n;
        b->view_cap = nc;
    }
    b->viewings[b->view_count++] = copy;
    return 0;
}

/* Does a listing's property match `suburb` (OrdinalIgnoreCase)? */
static bool listing_in_suburb(const ca_re_board_t *b, const ca_re_listing_t *l,
                              const char *suburb) {
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->props[i].property_id, l->property_id))
            return cab_ci_eq(b->props[i].suburb, suburb);
    return false; /* property not found => no match (C# TryGetValue false) */
}

/* Stable descending sort of collected indices by ListedUtc. */
static void listing_sort_desc(const ca_re_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->listings[key].listed_utc_ms;
        size_t j = i;
        while (j > 0 && b->listings[idx[j - 1]].listed_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Collect the sorted active-in-suburb listing indices into *out_idx (owned).
 * Returns count, or SIZE_MAX on error/bad args. Sets *out_idx = NULL when 0. */
static size_t collect_active_in_suburb(const ca_re_board_t *b,
                                       const char *suburb, size_t **out_idx) {
    *out_idx = NULL;
    if (!b || cab_is_ws(suburb)) return (size_t)-1;
    if (b->l_count == 0) return 0;
    size_t *idx = (size_t *)malloc(b->l_count * sizeof(size_t));
    if (!idx) return (size_t)-1;
    size_t n = 0;
    for (size_t i = 0; i < b->l_count; ++i) {
        if (b->listings[i].is_active &&
            listing_in_suburb(b, &b->listings[i], suburb))
            idx[n++] = i;
    }
    if (n == 0) { free(idx); return 0; }
    listing_sort_desc(b, idx, n);
    *out_idx = idx;
    return n;
}

ca_re_listing_t *ca_re_board_active_in_suburb(const ca_re_board_t *b,
                                              const char *suburb,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    size_t *idx = NULL;
    size_t n = collect_active_in_suburb(b, suburb, &idx);
    if (n == (size_t)-1) { *out_count = (size_t)-1; return NULL; }
    if (n == 0) { *out_count = 0; return NULL; }

    ca_re_listing_t *out = (ca_re_listing_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!listing_copy(&out[i], &b->listings[idx[i]])) {
            ca_re_listing_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_re_board_suburb_average(const ca_re_board_t *b, const char *suburb,
                                ca_re_decimal_t *out) {
    if (out) *out = 0;
    if (!out) return false;
    size_t *idx = NULL;
    size_t n = collect_active_in_suburb(b, suburb, &idx);
    if (n == (size_t)-1 || n == 0) { free(idx); return false; }

    /* decimal Average => rounded mean of the micro-unit asking prices. */
    int64_t sum = 0;
    for (size_t i = 0; i < n; ++i) sum += b->listings[idx[i]].asking_price;
    free(idx);
    int64_t cnt = (int64_t)n;
    int64_t avg;
    if (sum >= 0) avg = (sum + cnt / 2) / cnt;
    else          avg = -((-sum + cnt / 2) / cnt);
    *out = (ca_re_decimal_t)avg;
    return true;
}
