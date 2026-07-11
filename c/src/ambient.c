/*
 * ambient.c — CircleAI.Ambient (C11 port of AmbientPrimitives.cs).
 *
 * InMemoryAmbientBoard: readings (append list), preferences (Location keyed).
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/ambient.h"
#include "board_common.h"
#include <math.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_ambient_reading_free(ca_ambient_reading_t *r) {
    if (!r) return;
    free(r->device_id);
    r->device_id = NULL;
}
void ca_ambient_reading_free_array(ca_ambient_reading_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_ambient_reading_free(&arr[i]);
    free(arr);
}

static bool reading_copy(ca_ambient_reading_t *dst,
                         const ca_ambient_reading_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->device_id     = cab_strdup_empty(src->device_id);
    dst->temperature_c = src->temperature_c;
    dst->humidity      = src->humidity;
    dst->lux_light     = src->lux_light;
    dst->db_noise      = src->db_noise;
    dst->at_utc_ms     = src->at_utc_ms;
    if (!dst->device_id) return false;
    return true;
}

void ca_ambient_preference_free(ca_ambient_preference_t *p) {
    if (!p) return;
    free(p->location);
    p->location = NULL;
}

static bool preference_copy(ca_ambient_preference_t *dst,
                            const ca_ambient_preference_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->location        = cab_strdup_empty(src->location);
    dst->target_temp_c   = src->target_temp_c;
    dst->target_humidity = src->target_humidity;
    dst->max_noise_db    = src->max_noise_db;
    if (!dst->location) return false;
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_ambient_board {
    ca_ambient_reading_t    *readings;
    size_t                   r_count, r_cap;
    ca_ambient_preference_t *prefs;
    size_t                   p_count, p_cap;
};

ca_ambient_board_t *ca_ambient_board_create(void) {
    return (ca_ambient_board_t *)calloc(1, sizeof(ca_ambient_board_t));
}
void ca_ambient_board_destroy(ca_ambient_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->r_count; ++i) ca_ambient_reading_free(&b->readings[i]);
    for (size_t i = 0; i < b->p_count; ++i) ca_ambient_preference_free(&b->prefs[i]);
    free(b->readings);
    free(b->prefs);
    free(b);
}

int ca_ambient_board_record(ca_ambient_board_t *b,
                            const ca_ambient_reading_t *r) {
    if (!b || !r) return -1;
    ca_ambient_reading_t copy;
    if (!reading_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->readings, nc * sizeof(*b->readings));
        if (!n) { ca_ambient_reading_free(&copy); return -1; }
        b->readings = (ca_ambient_reading_t *)n;
        b->r_cap = nc;
    }
    b->readings[b->r_count++] = copy;
    return 0;
}

/* Index of the newest reading for a device (ties -> first-seen); SIZE_MAX none. */
static size_t latest_index(const ca_ambient_board_t *b, const char *device_id) {
    size_t best = (size_t)-1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (!cab_ord_eq(b->readings[i].device_id, device_id)) continue;
        if (best == (size_t)-1 ||
            b->readings[i].at_utc_ms > b->readings[best].at_utc_ms)
            best = i;
    }
    return best;
}

bool ca_ambient_board_latest(const ca_ambient_board_t *b, const char *device_id,
                             ca_ambient_reading_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !device_id || !out) return false;
    size_t i = latest_index(b, device_id);
    if (i == (size_t)-1) return false;
    return reading_copy(out, &b->readings[i]);
}

/* Stable descending sort of collected indices by AtUtc. */
static void reading_sort_desc(const ca_ambient_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->readings[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->readings[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_ambient_reading_t *ca_ambient_board_history(const ca_ambient_board_t *b,
                                               const char *device_id, int limit,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !device_id || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ord_eq(b->readings[i].device_id, device_id)) idx[n++] = i;
    reading_sort_desc(b, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_ambient_reading_t *out = (ca_ambient_reading_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!reading_copy(&out[i], &b->readings[idx[i]])) {
            ca_ambient_reading_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_ambient_board_set_preference(ca_ambient_board_t *b,
                                    const ca_ambient_preference_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->prefs[i].location, p->location)) {
            ca_ambient_preference_t copy;
            if (!preference_copy(&copy, p)) return -1;
            ca_ambient_preference_free(&b->prefs[i]);
            b->prefs[i] = copy;
            return 0;
        }
    }
    ca_ambient_preference_t copy;
    if (!preference_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->prefs, nc * sizeof(*b->prefs));
        if (!n) { ca_ambient_preference_free(&copy); return -1; }
        b->prefs = (ca_ambient_preference_t *)n;
        b->p_cap = nc;
    }
    b->prefs[b->p_count++] = copy;
    return 0;
}

bool ca_ambient_board_get_preference(const ca_ambient_board_t *b,
                                     const char *location,
                                     ca_ambient_preference_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !location || !out) return false;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->prefs[i].location, location))
            return preference_copy(out, &b->prefs[i]);
    return false;
}

bool ca_ambient_board_is_comfortable(const ca_ambient_board_t *b,
                                     const char *device_id,
                                     const char *location) {
    if (!b || !device_id || !location) return false;
    /* Locate preference. */
    const ca_ambient_preference_t *pref = NULL;
    for (size_t i = 0; i < b->p_count; ++i)
        if (cab_ord_eq(b->prefs[i].location, location)) { pref = &b->prefs[i]; break; }
    size_t li = latest_index(b, device_id);
    if (!pref || li == (size_t)-1) return false;
    const ca_ambient_reading_t *last = &b->readings[li];
    return fabs(last->temperature_c - pref->target_temp_c) <= 2.0
        && fabs(last->humidity      - pref->target_humidity) <= 10.0
        && last->db_noise <= pref->max_noise_db;
}
