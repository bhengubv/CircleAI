/*
 * travel.c — CircleAI.Travel (C11 port of TravelPrimitives.cs).
 *
 * InMemoryTravelBoard: flights (FlightId keyed), stays (StayId keyed), trips
 * (TripId keyed). TripCost joins the trip's flight/stay id lists to the stores.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/travel.h"
#include "board_common.h"

/* ── Flight ─────────────────────────────────────────────────────────────── */

void ca_travel_flight_free(ca_travel_flight_t *f) {
    if (!f) return;
    free(f->flight_id);
    free(f->from);
    free(f->to);
    free(f->carrier);
    free(f->cabin);
    free(f->currency);
    f->flight_id = f->from = f->to = f->carrier = f->cabin = f->currency = NULL;
}

static bool flight_copy(ca_travel_flight_t *dst, const ca_travel_flight_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->flight_id     = cab_strdup_empty(src->flight_id);
    dst->from          = cab_strdup_empty(src->from);
    dst->to            = cab_strdup_empty(src->to);
    dst->depart_utc_ms = src->depart_utc_ms;
    dst->arrive_utc_ms = src->arrive_utc_ms;
    dst->carrier       = cab_strdup_empty(src->carrier);
    dst->cabin         = cab_strdup_empty(src->cabin);
    dst->price         = src->price;
    dst->currency      = cab_strdup_empty(src->currency);
    if (!dst->flight_id || !dst->from || !dst->to || !dst->carrier ||
        !dst->cabin || !dst->currency) {
        ca_travel_flight_free(dst);
        return false;
    }
    return true;
}

/* ── HotelStay ──────────────────────────────────────────────────────────── */

void ca_travel_stay_free(ca_travel_stay_t *s) {
    if (!s) return;
    free(s->stay_id);
    free(s->hotel);
    free(s->city);
    free(s->currency);
    s->stay_id = s->hotel = s->city = s->currency = NULL;
}

static bool stay_copy(ca_travel_stay_t *dst, const ca_travel_stay_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->stay_id      = cab_strdup_empty(src->stay_id);
    dst->hotel        = cab_strdup_empty(src->hotel);
    dst->city         = cab_strdup_empty(src->city);
    dst->check_in_ms  = src->check_in_ms;
    dst->check_out_ms = src->check_out_ms;
    dst->nightly_rate = src->nightly_rate;
    dst->currency     = cab_strdup_empty(src->currency);
    if (!dst->stay_id || !dst->hotel || !dst->city || !dst->currency) {
        ca_travel_stay_free(dst);
        return false;
    }
    return true;
}

/* ── TravelTrip ─────────────────────────────────────────────────────────── */

void ca_travel_trip_free(ca_travel_trip_t *t) {
    if (!t) return;
    free(t->trip_id);
    free(t->name);
    cab_strv_free(t->flight_ids, t->flight_id_count);
    cab_strv_free(t->stay_ids, t->stay_id_count);
    t->trip_id = t->name = NULL;
    t->flight_ids = t->stay_ids = NULL;
    t->flight_id_count = t->stay_id_count = 0;
}
void ca_travel_trip_free_array(ca_travel_trip_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_travel_trip_free(&arr[i]);
    free(arr);
}

static bool trip_copy(ca_travel_trip_t *dst, const ca_travel_trip_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->trip_id       = cab_strdup_empty(src->trip_id);
    dst->name          = cab_strdup_empty(src->name);
    dst->start_date_ms = src->start_date_ms;
    dst->end_date_ms   = src->end_date_ms;
    bool ok = dst->trip_id && dst->name;
    if (ok) ok = cab_strv_copy(&dst->flight_ids, src->flight_ids,
                               src->flight_id_count);
    if (ok) dst->flight_id_count = src->flight_id_count;
    if (ok) ok = cab_strv_copy(&dst->stay_ids, src->stay_ids, src->stay_id_count);
    if (ok) dst->stay_id_count = src->stay_id_count;
    if (!ok) { ca_travel_trip_free(dst); return false; }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_travel_board {
    ca_travel_flight_t *flights;
    size_t              f_count, f_cap;
    ca_travel_stay_t   *stays;
    size_t              s_count, s_cap;
    ca_travel_trip_t   *trips;
    size_t              t_count, t_cap;
};

ca_travel_board_t *ca_travel_board_create(void) {
    return (ca_travel_board_t *)calloc(1, sizeof(ca_travel_board_t));
}
void ca_travel_board_destroy(ca_travel_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->f_count; ++i) ca_travel_flight_free(&b->flights[i]);
    for (size_t i = 0; i < b->s_count; ++i) ca_travel_stay_free(&b->stays[i]);
    for (size_t i = 0; i < b->t_count; ++i) ca_travel_trip_free(&b->trips[i]);
    free(b->flights);
    free(b->stays);
    free(b->trips);
    free(b);
}

int ca_travel_board_add_flight(ca_travel_board_t *b, const ca_travel_flight_t *f) {
    if (!b || !f) return -1;
    for (size_t i = 0; i < b->f_count; ++i) {
        if (cab_ord_eq(b->flights[i].flight_id, f->flight_id)) {
            ca_travel_flight_t copy;
            if (!flight_copy(&copy, f)) return -1;
            ca_travel_flight_free(&b->flights[i]);
            b->flights[i] = copy;
            return 0;
        }
    }
    ca_travel_flight_t copy;
    if (!flight_copy(&copy, f)) return -1;
    if (b->f_count == b->f_cap) {
        size_t nc = b->f_cap ? b->f_cap * 2 : 4;
        void *n = realloc(b->flights, nc * sizeof(*b->flights));
        if (!n) { ca_travel_flight_free(&copy); return -1; }
        b->flights = (ca_travel_flight_t *)n;
        b->f_cap = nc;
    }
    b->flights[b->f_count++] = copy;
    return 0;
}

int ca_travel_board_add_stay(ca_travel_board_t *b, const ca_travel_stay_t *s) {
    if (!b || !s) return -1;
    for (size_t i = 0; i < b->s_count; ++i) {
        if (cab_ord_eq(b->stays[i].stay_id, s->stay_id)) {
            ca_travel_stay_t copy;
            if (!stay_copy(&copy, s)) return -1;
            ca_travel_stay_free(&b->stays[i]);
            b->stays[i] = copy;
            return 0;
        }
    }
    ca_travel_stay_t copy;
    if (!stay_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->stays, nc * sizeof(*b->stays));
        if (!n) { ca_travel_stay_free(&copy); return -1; }
        b->stays = (ca_travel_stay_t *)n;
        b->s_cap = nc;
    }
    b->stays[b->s_count++] = copy;
    return 0;
}

int ca_travel_board_plan(ca_travel_board_t *b, const ca_travel_trip_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->trips[i].trip_id, t->trip_id)) {
            ca_travel_trip_t copy;
            if (!trip_copy(&copy, t)) return -1;
            ca_travel_trip_free(&b->trips[i]);
            b->trips[i] = copy;
            return 0;
        }
    }
    ca_travel_trip_t copy;
    if (!trip_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->trips, nc * sizeof(*b->trips));
        if (!n) { ca_travel_trip_free(&copy); return -1; }
        b->trips = (ca_travel_trip_t *)n;
        b->t_cap = nc;
    }
    b->trips[b->t_count++] = copy;
    return 0;
}

bool ca_travel_board_get_trip(const ca_travel_board_t *b, const char *id,
                              ca_travel_trip_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->trips[i].trip_id, id))
            return trip_copy(out, &b->trips[i]);
    return false;
}
bool ca_travel_board_get_flight(const ca_travel_board_t *b, const char *id,
                                ca_travel_flight_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ord_eq(b->flights[i].flight_id, id))
            return flight_copy(out, &b->flights[i]);
    return false;
}
bool ca_travel_board_get_stay(const ca_travel_board_t *b, const char *id,
                              ca_travel_stay_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->stays[i].stay_id, id))
            return stay_copy(out, &b->stays[i]);
    return false;
}

/* Borrowed lookups by id (NULL when absent). */
static const ca_travel_flight_t *find_flight(const ca_travel_board_t *b,
                                             const char *id) {
    for (size_t i = 0; i < b->f_count; ++i)
        if (cab_ord_eq(b->flights[i].flight_id, id)) return &b->flights[i];
    return NULL;
}
static const ca_travel_stay_t *find_stay(const ca_travel_board_t *b,
                                         const char *id) {
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->stays[i].stay_id, id)) return &b->stays[i];
    return NULL;
}

int ca_travel_board_trip_cost(const ca_travel_board_t *b, const char *trip_id,
                              ca_travel_decimal_t *out) {
    if (out) *out = 0;
    if (!b || !trip_id || !out) return -1;
    const ca_travel_trip_t *t = NULL;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->trips[i].trip_id, trip_id)) { t = &b->trips[i]; break; }
    if (!t) return -2; /* Unknown trip -> C# InvalidOperationException */

    ca_travel_decimal_t total = 0;
    for (size_t i = 0; i < t->flight_id_count; ++i) {
        const ca_travel_flight_t *f = find_flight(b, t->flight_ids[i]);
        if (f) total += f->price;
    }
    for (size_t i = 0; i < t->stay_id_count; ++i) {
        const ca_travel_stay_t *s = find_stay(b, t->stay_ids[i]);
        if (s) {
            /* (CheckOut - CheckIn).Days, truncated toward zero, floored at 1. */
            int64_t days = (s->check_out_ms - s->check_in_ms) / CAB_MS_PER_DAY;
            if (days < 1) days = 1;
            total += s->nightly_rate * days;
        }
    }
    *out = total;
    return 0;
}

/* Stable ascending sort of collected indices by StartDate. */
static void trip_sort_asc(const ca_travel_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->trips[key].start_date_ms;
        size_t j = i;
        while (j > 0 && b->trips[idx[j - 1]].start_date_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_travel_trip_t *ca_travel_board_upcoming_trips(const ca_travel_board_t *b,
                                                 int64_t now_ms,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i)
        if (b->trips[i].start_date_ms >= now_ms) idx[n++] = i;
    trip_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_travel_trip_t *out = (ca_travel_trip_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!trip_copy(&out[i], &b->trips[idx[i]])) {
            ca_travel_trip_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
