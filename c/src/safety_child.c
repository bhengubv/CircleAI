/*
 * safety_child.c — CircleAI.Safety.Child domain primitives (C11 port).
 *
 * Ports ChildSafetyPrimitives.cs: TrustedAdult / Geofence / CheckIn records
 * and InMemoryChildSafetyBoard. Adults + fences are keyed dictionaries
 * (last-write-wins); check-ins are an append list. RingOrdered sorts by
 * RingPriority ascending (stable); RecentCheckIns filters by ChildId and
 * returns the newest `limit` descending; IsInsideAnyFence uses the same
 * Haversine formula (R = 6_371_000 m) as the C#.
 *
 * Pure C11 + libc + libm.
 */

#include "circle_ai/safety_child.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

/* Local PI (matches the C# Math.PI double); avoids relying on the non-standard
 * M_PI from <math.h>, which is unavailable under strict -std=c11. */
#define SC_PI 3.14159265358979323846

static char *sc_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ── records ────────────────────────────────────────────────────────────── */

void ca_trusted_adult_free(ca_trusted_adult_t *a) {
    if (!a) return;
    free(a->adult_id);
    free(a->name);
    free(a->phone);
    free(a->relationship);
    a->adult_id = a->name = a->phone = a->relationship = NULL;
}
void ca_trusted_adult_free_array(ca_trusted_adult_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_trusted_adult_free(&arr[i]);
    free(arr);
}
ca_trusted_adult_t *ca_trusted_adult_copy(ca_trusted_adult_t *dst,
                                          const ca_trusted_adult_t *src) {
    if (!dst || !src) return dst;
    dst->adult_id      = sc_strdup(src->adult_id);
    dst->name          = sc_strdup(src->name);
    dst->phone         = sc_strdup(src->phone);
    dst->relationship  = sc_strdup(src->relationship);
    dst->ring_priority = src->ring_priority;
    return dst;
}

void ca_geofence_free(ca_geofence_t *g) {
    if (!g) return;
    free(g->fence_id);
    free(g->name);
    g->fence_id = g->name = NULL;
}
ca_geofence_t *ca_geofence_copy(ca_geofence_t *dst, const ca_geofence_t *src) {
    if (!dst || !src) return dst;
    dst->fence_id      = sc_strdup(src->fence_id);
    dst->name          = sc_strdup(src->name);
    dst->centre_lat    = src->centre_lat;
    dst->centre_lon    = src->centre_lon;
    dst->radius_meters = src->radius_meters;
    return dst;
}

void ca_check_in_free(ca_check_in_t *c) {
    if (!c) return;
    free(c->child_id);
    free(c->status);
    c->child_id = c->status = NULL;
}
void ca_check_in_free_array(ca_check_in_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_check_in_free(&arr[i]);
    free(arr);
}
ca_check_in_t *ca_check_in_copy(ca_check_in_t *dst, const ca_check_in_t *src) {
    if (!dst || !src) return dst;
    dst->child_id  = sc_strdup(src->child_id);
    dst->status    = sc_strdup(src->status);
    dst->has_lat   = src->has_lat;
    dst->lat       = src->lat;
    dst->has_lon   = src->has_lon;
    dst->lon       = src->lon;
    dst->at_utc_ms = src->at_utc_ms;
    return dst;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_child_safety_board {
    ca_trusted_adult_t *adults;    /* keyed by adult_id */
    size_t              adu_count, adu_cap;
    ca_geofence_t      *fences;    /* keyed by fence_id */
    size_t              fen_count, fen_cap;
    ca_check_in_t      *checkins;  /* append list */
    size_t              chk_count, chk_cap;
};

ca_child_safety_board_t *ca_child_safety_board_create(void) {
    return (ca_child_safety_board_t *)calloc(1, sizeof(ca_child_safety_board_t));
}
void ca_child_safety_board_destroy(ca_child_safety_board_t *board) {
    if (!board) return;
    for (size_t i = 0; i < board->adu_count; ++i) ca_trusted_adult_free(&board->adults[i]);
    free(board->adults);
    for (size_t i = 0; i < board->fen_count; ++i) ca_geofence_free(&board->fences[i]);
    free(board->fences);
    for (size_t i = 0; i < board->chk_count; ++i) ca_check_in_free(&board->checkins[i]);
    free(board->checkins);
    free(board);
}

bool ca_child_safety_board_add_adult(ca_child_safety_board_t *board,
                                     const ca_trusted_adult_t *a) {
    if (!board || !a) return false;
    for (size_t i = 0; i < board->adu_count; ++i) {
        if (board->adults[i].adult_id && a->adult_id &&
            strcmp(board->adults[i].adult_id, a->adult_id) == 0) {
            ca_trusted_adult_t copy; memset(&copy, 0, sizeof(copy));
            ca_trusted_adult_copy(&copy, a);
            ca_trusted_adult_free(&board->adults[i]);
            board->adults[i] = copy;
            return true;
        }
    }
    if (board->adu_count == board->adu_cap) {
        size_t nc = board->adu_cap ? board->adu_cap * 2 : 8;
        void *n = realloc(board->adults, nc * sizeof(*board->adults));
        if (!n) return false;
        board->adults = n; board->adu_cap = nc;
    }
    ca_trusted_adult_copy(&board->adults[board->adu_count], a);
    board->adu_count++;
    return true;
}

ca_trusted_adult_t *ca_child_safety_board_ring_ordered(ca_child_safety_board_t *board,
                                                       size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!board) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = board->adu_count;
    if (n == 0) return NULL;
    /* OrderBy(a => a.RingPriority) — ascending, stable for ties. */
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    for (size_t i = 1; i < n; ++i) {
        size_t cur = idx[i];
        int key = board->adults[cur].ring_priority;
        size_t j = i;
        while (j > 0 && board->adults[idx[j - 1]].ring_priority > key) { idx[j] = idx[j - 1]; --j; }
        idx[j] = cur;
    }
    ca_trusted_adult_t *res = (ca_trusted_adult_t *)calloc(n, sizeof(*res));
    if (!res) { free(idx); if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < n; ++i) ca_trusted_adult_copy(&res[i], &board->adults[idx[i]]);
    free(idx);
    if (out_count) *out_count = n;
    return res;
}

bool ca_child_safety_board_define_geofence(ca_child_safety_board_t *board,
                                           const ca_geofence_t *g) {
    if (!board || !g) return false;
    for (size_t i = 0; i < board->fen_count; ++i) {
        if (board->fences[i].fence_id && g->fence_id &&
            strcmp(board->fences[i].fence_id, g->fence_id) == 0) {
            ca_geofence_t copy; memset(&copy, 0, sizeof(copy));
            ca_geofence_copy(&copy, g);
            ca_geofence_free(&board->fences[i]);
            board->fences[i] = copy;
            return true;
        }
    }
    if (board->fen_count == board->fen_cap) {
        size_t nc = board->fen_cap ? board->fen_cap * 2 : 8;
        void *n = realloc(board->fences, nc * sizeof(*board->fences));
        if (!n) return false;
        board->fences = n; board->fen_cap = nc;
    }
    ca_geofence_copy(&board->fences[board->fen_count], g);
    board->fen_count++;
    return true;
}

bool ca_child_safety_board_get_geofence(ca_child_safety_board_t *board,
                                        const char *id, ca_geofence_t *out) {
    if (!board || !id || !out) return false;
    for (size_t i = 0; i < board->fen_count; ++i)
        if (board->fences[i].fence_id && strcmp(board->fences[i].fence_id, id) == 0) {
            ca_geofence_copy(out, &board->fences[i]);
            return true;
        }
    return false;
}

static double sc_deg_to_rad(double d) { return d * SC_PI / 180.0; }

static double sc_haversine_m(double a_lat, double a_lon, double b_lat, double b_lon) {
    const double R = 6371000.0; /* 6_371_000 */
    double d_lat = sc_deg_to_rad(b_lat - a_lat);
    double d_lon = sc_deg_to_rad(b_lon - a_lon);
    double s1 = sin(d_lat / 2.0);
    double s2 = sin(d_lon / 2.0);
    double a = s1 * s1 + cos(sc_deg_to_rad(a_lat)) * cos(sc_deg_to_rad(b_lat)) * s2 * s2;
    double c = 2.0 * atan2(sqrt(a), sqrt(1.0 - a));
    return R * c;
}

bool ca_child_safety_board_is_inside_any_fence(ca_child_safety_board_t *board,
                                               double lat, double lon) {
    if (!board) return false;
    for (size_t i = 0; i < board->fen_count; ++i) {
        const ca_geofence_t *g = &board->fences[i];
        if (sc_haversine_m(g->centre_lat, g->centre_lon, lat, lon) <= g->radius_meters)
            return true;
    }
    return false;
}

bool ca_child_safety_board_record_check_in(ca_child_safety_board_t *board,
                                           const ca_check_in_t *c) {
    if (!board || !c) return false;
    if (board->chk_count == board->chk_cap) {
        size_t nc = board->chk_cap ? board->chk_cap * 2 : 8;
        void *n = realloc(board->checkins, nc * sizeof(*board->checkins));
        if (!n) return false;
        board->checkins = n; board->chk_cap = nc;
    }
    ca_check_in_copy(&board->checkins[board->chk_count], c);
    board->chk_count++;
    return true;
}

ca_check_in_t *ca_child_safety_board_recent_check_ins(ca_child_safety_board_t *board,
                                                      const char *child_id, int limit,
                                                      size_t *out_count) {
    if (out_count) *out_count = 0;
    /* C# throws ArgumentOutOfRangeException for limit<=0 (checked before the
     * childId filter). */
    if (limit <= 0) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    if (!board || !child_id) { if (out_count) *out_count = SIZE_MAX; return NULL; }

    /* pick matching indices in source order */
    size_t *pick = (size_t *)malloc(board->chk_count ? board->chk_count * sizeof(size_t) : 1);
    if (!pick) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t m = 0;
    for (size_t i = 0; i < board->chk_count; ++i)
        if (board->checkins[i].child_id && strcmp(board->checkins[i].child_id, child_id) == 0)
            pick[m++] = i;
    if (m == 0) { free(pick); return NULL; }

    /* OrderByDescending(c => c.AtUtc) — stable descending. */
    for (size_t i = 1; i < m; ++i) {
        size_t cur = pick[i];
        int64_t key = board->checkins[cur].at_utc_ms;
        size_t j = i;
        while (j > 0 && board->checkins[pick[j - 1]].at_utc_ms < key) { pick[j] = pick[j - 1]; --j; }
        pick[j] = cur;
    }

    size_t take = m < (size_t)limit ? m : (size_t)limit;
    ca_check_in_t *res = (ca_check_in_t *)calloc(take, sizeof(*res));
    if (!res) { free(pick); if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < take; ++i) ca_check_in_copy(&res[i], &board->checkins[pick[i]]);
    free(pick);
    if (out_count) *out_count = take;
    return res;
}
