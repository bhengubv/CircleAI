/*
 * logistics.c — CircleAI.Logistics (C11 port of LogisticsPrimitives.cs).
 *
 * InMemoryLogisticsBoard: shipments (ShipmentId keyed), vehicles (VehicleId
 * keyed). PlanRoute sums leg distances and derives a decimal cost from the
 * vehicle's per-km rate. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/logistics.h"
#include "board_common.h"
#include <math.h>
#include <stdio.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_log_shipment_free(ca_log_shipment_t *s) {
    if (!s) return;
    free(s->shipment_id);
    free(s->origin);
    free(s->destination);
    free(s->incoterm);
    s->shipment_id = s->origin = s->destination = s->incoterm = NULL;
}

static bool shipment_copy(ca_log_shipment_t *dst, const ca_log_shipment_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->shipment_id      = cab_strdup_empty(src->shipment_id);
    dst->origin           = cab_strdup_empty(src->origin);
    dst->destination      = cab_strdup_empty(src->destination);
    dst->incoterm         = cab_strdup_empty(src->incoterm);
    dst->weight_kg        = src->weight_kg;
    dst->volume_m3        = src->volume_m3;
    dst->pickup_at_utc_ms = src->pickup_at_utc_ms;
    if (!dst->shipment_id || !dst->origin || !dst->destination ||
        !dst->incoterm) { ca_log_shipment_free(dst); return false; }
    return true;
}

void ca_log_vehicle_free(ca_log_vehicle_t *v) {
    if (!v) return;
    free(v->vehicle_id);
    v->vehicle_id = NULL;
}
void ca_log_vehicle_free_array(ca_log_vehicle_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_log_vehicle_free(&arr[i]);
    free(arr);
}

static bool vehicle_copy(ca_log_vehicle_t *dst, const ca_log_vehicle_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->vehicle_id  = cab_strdup_empty(src->vehicle_id);
    dst->capacity_kg = src->capacity_kg;
    dst->capacity_m3 = src->capacity_m3;
    dst->cost_per_km = src->cost_per_km;
    if (!dst->vehicle_id) return false;
    return true;
}

void ca_log_route_leg_free(ca_log_route_leg_t *l) {
    if (!l) return;
    free(l->from_code);
    free(l->to_code);
    l->from_code = l->to_code = NULL;
}
void ca_log_route_leg_free_array(ca_log_route_leg_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_log_route_leg_free(&arr[i]);
    free(arr);
}

static bool route_leg_copy(ca_log_route_leg_t *dst,
                           const ca_log_route_leg_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->from_code   = cab_strdup_empty(src->from_code);
    dst->to_code     = cab_strdup_empty(src->to_code);
    dst->distance_km = src->distance_km;
    if (!dst->from_code || !dst->to_code) {
        ca_log_route_leg_free(dst);
        return false;
    }
    return true;
}

void ca_log_route_plan_free(ca_log_route_plan_t *p) {
    if (!p) return;
    free(p->plan_id);
    free(p->vehicle_id);
    ca_log_route_leg_free_array(p->legs, p->leg_count);
    p->plan_id = p->vehicle_id = NULL;
    p->legs = NULL;
    p->leg_count = 0;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_log_board {
    ca_log_shipment_t *shipments;
    size_t             sh_count, sh_cap;
    ca_log_vehicle_t  *vehicles;
    size_t             v_count, v_cap;
    long long          seq;
};

ca_log_board_t *ca_log_board_create(void) {
    return (ca_log_board_t *)calloc(1, sizeof(ca_log_board_t));
}
void ca_log_board_destroy(ca_log_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->sh_count; ++i) ca_log_shipment_free(&b->shipments[i]);
    for (size_t i = 0; i < b->v_count; ++i)  ca_log_vehicle_free(&b->vehicles[i]);
    free(b->shipments);
    free(b->vehicles);
    free(b);
}

int ca_log_board_register_shipment(ca_log_board_t *b,
                                   const ca_log_shipment_t *s) {
    if (!b || !s) return -1;
    if (cab_is_ws(s->shipment_id)) return 2;
    for (size_t i = 0; i < b->sh_count; ++i) {
        if (cab_ord_eq(b->shipments[i].shipment_id, s->shipment_id)) {
            ca_log_shipment_t copy;
            if (!shipment_copy(&copy, s)) return -1;
            ca_log_shipment_free(&b->shipments[i]);
            b->shipments[i] = copy;
            return 0;
        }
    }
    ca_log_shipment_t copy;
    if (!shipment_copy(&copy, s)) return -1;
    if (b->sh_count == b->sh_cap) {
        size_t nc = b->sh_cap ? b->sh_cap * 2 : 4;
        void *n = realloc(b->shipments, nc * sizeof(*b->shipments));
        if (!n) { ca_log_shipment_free(&copy); return -1; }
        b->shipments = (ca_log_shipment_t *)n;
        b->sh_cap = nc;
    }
    b->shipments[b->sh_count++] = copy;
    return 0;
}

int ca_log_board_register_vehicle(ca_log_board_t *b, const ca_log_vehicle_t *v) {
    if (!b || !v) return -1;
    if (cab_is_ws(v->vehicle_id)) return 2;
    for (size_t i = 0; i < b->v_count; ++i) {
        if (cab_ord_eq(b->vehicles[i].vehicle_id, v->vehicle_id)) {
            ca_log_vehicle_t copy;
            if (!vehicle_copy(&copy, v)) return -1;
            ca_log_vehicle_free(&b->vehicles[i]);
            b->vehicles[i] = copy;
            return 0;
        }
    }
    ca_log_vehicle_t copy;
    if (!vehicle_copy(&copy, v)) return -1;
    if (b->v_count == b->v_cap) {
        size_t nc = b->v_cap ? b->v_cap * 2 : 4;
        void *n = realloc(b->vehicles, nc * sizeof(*b->vehicles));
        if (!n) { ca_log_vehicle_free(&copy); return -1; }
        b->vehicles = (ca_log_vehicle_t *)n;
        b->v_cap = nc;
    }
    b->vehicles[b->v_count++] = copy;
    return 0;
}

bool ca_log_board_get_shipment(const ca_log_board_t *b, const char *id,
                               ca_log_shipment_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->sh_count; ++i)
        if (cab_ord_eq(b->shipments[i].shipment_id, id))
            return shipment_copy(out, &b->shipments[i]);
    return false;
}

/* Stable ascending sort of collected indices by VehicleId (ordinal). */
static void vehicle_sort_id(const ca_log_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(b->vehicles[idx[j - 1]].vehicle_id,
                      b->vehicles[key].vehicle_id) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_log_vehicle_t *ca_log_board_vehicles(const ca_log_board_t *b,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->v_count == 0) { *out_count = 0; return NULL; }

    size_t n = b->v_count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    vehicle_sort_id(b, idx, n);

    ca_log_vehicle_t *out = (ca_log_vehicle_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!vehicle_copy(&out[i], &b->vehicles[idx[i]])) {
            ca_log_vehicle_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_log_board_plan_route(ca_log_board_t *b, const char *vehicle_id,
                            const ca_log_route_leg_t *legs, size_t leg_count,
                            ca_log_route_plan_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !out || (leg_count > 0 && !legs)) return -1;
    if (cab_is_ws(vehicle_id)) return 2;

    const ca_log_vehicle_t *vehicle = NULL;
    for (size_t i = 0; i < b->v_count; ++i)
        if (cab_ord_eq(b->vehicles[i].vehicle_id, vehicle_id)) {
            vehicle = &b->vehicles[i];
            break;
        }
    if (!vehicle) return 1; /* InvalidOperationException: unknown vehicle */

    double total_km = 0.0;
    for (size_t i = 0; i < leg_count; ++i) total_km += legs[i].distance_km;

    /* (decimal)(totalKm * CostPerKm) — carried at 1e6 scale (round to nearest). */
    double cost = total_km * vehicle->cost_per_km;
    ca_log_decimal_t est = (ca_log_decimal_t)llround(cost * (double)CA_LOG_DECIMAL_SCALE);

    char plan_id[64];
    long long id = ++b->seq;
    int w = snprintf(plan_id, sizeof(plan_id), "plan-%lld", id);
    if (w <= 0 || (size_t)w >= sizeof(plan_id)) return -1;

    out->plan_id           = cab_strdup_empty(plan_id);
    out->vehicle_id        = cab_strdup_empty(vehicle_id);
    out->total_distance_km = total_km;
    out->estimated_cost    = est;
    if (!out->plan_id || !out->vehicle_id) { ca_log_route_plan_free(out); return -1; }

    if (leg_count > 0) {
        out->legs = (ca_log_route_leg_t *)calloc(leg_count, sizeof(*out->legs));
        if (!out->legs) { ca_log_route_plan_free(out); return -1; }
        for (size_t i = 0; i < leg_count; ++i) {
            if (!route_leg_copy(&out->legs[i], &legs[i])) {
                out->leg_count = i;
                ca_log_route_plan_free(out);
                return -1;
            }
        }
        out->leg_count = leg_count;
    }
    return 0;
}
