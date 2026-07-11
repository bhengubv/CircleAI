/*
 * energy.c — CircleAI.Energy (C11 port of EnergyPrimitives.cs).
 *
 * InMemoryEnergyBoard: readings (append list), tariffs (TariffId keyed), outages
 * (OutageId keyed). Pure C11 + libc + libm. No pthreads.
 */

#include "circle_ai/energy.h"
#include "board_common.h"
#include <math.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_energy_reading_free(ca_energy_reading_t *r) {
    if (!r) return;
    free(r->meter_id);
    r->meter_id = NULL;
}
void ca_energy_reading_free_array(ca_energy_reading_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_energy_reading_free(&arr[i]);
    free(arr);
}

static bool reading_copy(ca_energy_reading_t *dst,
                         const ca_energy_reading_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->meter_id  = cab_strdup_empty(src->meter_id);
    dst->kwh       = src->kwh;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->meter_id) return false;
    return true;
}

void ca_energy_tariff_free(ca_energy_tariff_t *t) {
    if (!t) return;
    free(t->tariff_id);
    free(t->name);
    free(t->currency);
    t->tariff_id = t->name = t->currency = NULL;
}

static bool tariff_copy(ca_energy_tariff_t *dst, const ca_energy_tariff_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->tariff_id         = cab_strdup_empty(src->tariff_id);
    dst->name              = cab_strdup_empty(src->name);
    dst->peak_kwh_rate     = src->peak_kwh_rate;
    dst->off_peak_kwh_rate = src->off_peak_kwh_rate;
    dst->currency          = cab_strdup_empty(src->currency);
    if (!dst->tariff_id || !dst->name || !dst->currency) {
        ca_energy_tariff_free(dst);
        return false;
    }
    return true;
}

void ca_energy_outage_free(ca_energy_outage_t *o) {
    if (!o) return;
    free(o->outage_id);
    free(o->area);
    free(o->reason);
    o->outage_id = o->area = o->reason = NULL;
    o->has_end_utc = o->has_reason = false;
}
void ca_energy_outage_free_array(ca_energy_outage_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_energy_outage_free(&arr[i]);
    free(arr);
}

static bool outage_copy(ca_energy_outage_t *dst, const ca_energy_outage_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->outage_id    = cab_strdup_empty(src->outage_id);
    dst->area         = cab_strdup_empty(src->area);
    dst->start_utc_ms = src->start_utc_ms;
    dst->has_end_utc  = src->has_end_utc;
    dst->end_utc_ms   = src->has_end_utc ? src->end_utc_ms : 0;
    bool ok = dst->outage_id && dst->area;
    if (ok && src->has_reason) {
        dst->reason = cab_strdup_empty(src->reason);
        ok = dst->reason != NULL;
        dst->has_reason = ok;
    }
    if (!ok) { ca_energy_outage_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_energy_board {
    ca_energy_reading_t *readings;
    size_t               r_count, r_cap;
    ca_energy_tariff_t  *tariffs;
    size_t               t_count, t_cap;
    ca_energy_outage_t  *outages;
    size_t               o_count, o_cap;
};

ca_energy_board_t *ca_energy_board_create(void) {
    return (ca_energy_board_t *)calloc(1, sizeof(ca_energy_board_t));
}
void ca_energy_board_destroy(ca_energy_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->r_count; ++i) ca_energy_reading_free(&b->readings[i]);
    for (size_t i = 0; i < b->t_count; ++i) ca_energy_tariff_free(&b->tariffs[i]);
    for (size_t i = 0; i < b->o_count; ++i) ca_energy_outage_free(&b->outages[i]);
    free(b->readings);
    free(b->tariffs);
    free(b->outages);
    free(b);
}

int ca_energy_board_record(ca_energy_board_t *b, const ca_energy_reading_t *r) {
    if (!b || !r) return -1;
    ca_energy_reading_t copy;
    if (!reading_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->readings, nc * sizeof(*b->readings));
        if (!n) { ca_energy_reading_free(&copy); return -1; }
        b->readings = (ca_energy_reading_t *)n;
        b->r_cap = nc;
    }
    b->readings[b->r_count++] = copy;
    return 0;
}

/* Collect this meter's readings (>= since) into idx, ascending by AtUtc. */
static size_t collect_readings_sorted(const ca_energy_board_t *b,
                                      const char *meter_id, int64_t since_ms,
                                      size_t *idx) {
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i) {
        const ca_energy_reading_t *r = &b->readings[i];
        if (cab_ord_eq(r->meter_id, meter_id) && r->at_utc_ms >= since_ms)
            idx[n++] = i;
    }
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->readings[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->readings[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
    return n;
}

ca_energy_reading_t *ca_energy_board_readings_for(const ca_energy_board_t *b,
                                                  const char *meter_id,
                                                  int64_t since_ms,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !meter_id) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = collect_readings_sorted(b, meter_id, since_ms, idx);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_energy_reading_t *out = (ca_energy_reading_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!reading_copy(&out[i], &b->readings[idx[i]])) {
            ca_energy_reading_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

double ca_energy_board_total_kwh_since(const ca_energy_board_t *b,
                                       const char *meter_id, int64_t since_ms) {
    if (!b || !meter_id || b->r_count == 0) return 0.0;
    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) return 0.0;
    size_t n = collect_readings_sorted(b, meter_id, since_ms, idx);
    double result = 0.0;
    if (n >= 2)
        result = b->readings[idx[n - 1]].kwh - b->readings[idx[0]].kwh;
    free(idx);
    return result;
}

int ca_energy_board_set_tariff(ca_energy_board_t *b, const ca_energy_tariff_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->tariffs[i].tariff_id, t->tariff_id)) {
            ca_energy_tariff_t copy;
            if (!tariff_copy(&copy, t)) return -1;
            ca_energy_tariff_free(&b->tariffs[i]);
            b->tariffs[i] = copy;
            return 0;
        }
    }
    ca_energy_tariff_t copy;
    if (!tariff_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->tariffs, nc * sizeof(*b->tariffs));
        if (!n) { ca_energy_tariff_free(&copy); return -1; }
        b->tariffs = (ca_energy_tariff_t *)n;
        b->t_cap = nc;
    }
    b->tariffs[b->t_count++] = copy;
    return 0;
}

bool ca_energy_board_get_tariff(const ca_energy_board_t *b, const char *id,
                                ca_energy_tariff_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->tariffs[i].tariff_id, id))
            return tariff_copy(out, &b->tariffs[i]);
    return false;
}

int ca_energy_board_estimate_cost(const ca_energy_board_t *b,
                                  const char *meter_id, const char *tariff_id,
                                  int64_t since_ms, ca_energy_decimal_t *out) {
    if (out) *out = 0;
    if (!b || !meter_id || !tariff_id || !out) return -1;
    const ca_energy_tariff_t *t = NULL;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->tariffs[i].tariff_id, tariff_id)) {
            t = &b->tariffs[i];
            break;
        }
    if (!t) return -2; /* Unknown tariff -> C# InvalidOperationException */
    double kwh = ca_energy_board_total_kwh_since(b, meter_id, since_ms);
    double cost = kwh * t->peak_kwh_rate;
    *out = (ca_energy_decimal_t)llround(cost * (double)CA_ENERGY_DECIMAL_SCALE);
    return 0;
}

int ca_energy_board_log_outage(ca_energy_board_t *b,
                               const ca_energy_outage_t *o) {
    if (!b || !o) return -1;
    for (size_t i = 0; i < b->o_count; ++i) {
        if (cab_ord_eq(b->outages[i].outage_id, o->outage_id)) {
            ca_energy_outage_t copy;
            if (!outage_copy(&copy, o)) return -1;
            ca_energy_outage_free(&b->outages[i]);
            b->outages[i] = copy;
            return 0;
        }
    }
    ca_energy_outage_t copy;
    if (!outage_copy(&copy, o)) return -1;
    if (b->o_count == b->o_cap) {
        size_t nc = b->o_cap ? b->o_cap * 2 : 4;
        void *n = realloc(b->outages, nc * sizeof(*b->outages));
        if (!n) { ca_energy_outage_free(&copy); return -1; }
        b->outages = (ca_energy_outage_t *)n;
        b->o_cap = nc;
    }
    b->outages[b->o_count++] = copy;
    return 0;
}

ca_energy_outage_t *ca_energy_board_active_outages(const ca_energy_board_t *b,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->o_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->o_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->o_count; ++i)
        if (!b->outages[i].has_end_utc) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_energy_outage_t *out = (ca_energy_outage_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!outage_copy(&out[i], &b->outages[idx[i]])) {
            ca_energy_outage_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
