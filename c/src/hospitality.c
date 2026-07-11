/*
 * hospitality.c — CircleAI.Hospitality (C11 port of HospitalityPrimitives.cs).
 *
 * InMemoryHospitalityBoard: rooms (RoomId keyed), reservations (ReservationId
 * keyed), notes (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/hospitality.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_hospitality_room_free(ca_hospitality_room_t *r) {
    if (!r) return;
    free(r->room_id);
    free(r->type);
    free(r->currency);
    r->room_id = r->type = r->currency = NULL;
}
void ca_hospitality_room_free_array(ca_hospitality_room_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hospitality_room_free(&arr[i]);
    free(arr);
}

static bool room_copy(ca_hospitality_room_t *dst,
                      const ca_hospitality_room_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->room_id      = cab_strdup_empty(src->room_id);
    dst->type         = cab_strdup_empty(src->type);
    dst->nightly_rate = src->nightly_rate;
    dst->currency     = cab_strdup_empty(src->currency);
    dst->is_clean     = src->is_clean;
    if (!dst->room_id || !dst->type || !dst->currency) {
        ca_hospitality_room_free(dst);
        return false;
    }
    return true;
}

void ca_hospitality_reservation_free(ca_hospitality_reservation_t *r) {
    if (!r) return;
    free(r->reservation_id);
    free(r->guest_name);
    free(r->room_id);
    r->reservation_id = r->guest_name = r->room_id = NULL;
}

static bool reservation_copy(ca_hospitality_reservation_t *dst,
                             const ca_hospitality_reservation_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->reservation_id = cab_strdup_empty(src->reservation_id);
    dst->guest_name     = cab_strdup_empty(src->guest_name);
    dst->room_id        = cab_strdup_empty(src->room_id);
    dst->check_in_ms    = src->check_in_ms;
    dst->check_out_ms   = src->check_out_ms;
    if (!dst->reservation_id || !dst->guest_name || !dst->room_id) {
        ca_hospitality_reservation_free(dst);
        return false;
    }
    return true;
}

void ca_hospitality_note_free(ca_hospitality_note_t *n) {
    if (!n) return;
    free(n->note_id);
    free(n->reservation_id);
    free(n->body);
    n->note_id = n->reservation_id = n->body = NULL;
}
void ca_hospitality_note_free_array(ca_hospitality_note_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_hospitality_note_free(&arr[i]);
    free(arr);
}

static bool note_copy(ca_hospitality_note_t *dst,
                      const ca_hospitality_note_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->note_id        = cab_strdup_empty(src->note_id);
    dst->reservation_id = cab_strdup_empty(src->reservation_id);
    dst->body           = cab_strdup_empty(src->body);
    dst->at_utc_ms      = src->at_utc_ms;
    if (!dst->note_id || !dst->reservation_id || !dst->body) {
        ca_hospitality_note_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_hospitality_board {
    ca_hospitality_room_t        *rooms;
    size_t                        r_count, r_cap;
    ca_hospitality_reservation_t *res;
    size_t                        s_count, s_cap;
    ca_hospitality_note_t        *notes;
    size_t                        n_count, n_cap;
};

ca_hospitality_board_t *ca_hospitality_board_create(void) {
    return (ca_hospitality_board_t *)calloc(1, sizeof(ca_hospitality_board_t));
}
void ca_hospitality_board_destroy(ca_hospitality_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->r_count; ++i) ca_hospitality_room_free(&b->rooms[i]);
    for (size_t i = 0; i < b->s_count; ++i) ca_hospitality_reservation_free(&b->res[i]);
    for (size_t i = 0; i < b->n_count; ++i) ca_hospitality_note_free(&b->notes[i]);
    free(b->rooms);
    free(b->res);
    free(b->notes);
    free(b);
}

int ca_hospitality_board_add_room(ca_hospitality_board_t *b,
                                  const ca_hospitality_room_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->rooms[i].room_id, r->room_id)) {
            ca_hospitality_room_t copy;
            if (!room_copy(&copy, r)) return -1;
            ca_hospitality_room_free(&b->rooms[i]);
            b->rooms[i] = copy;
            return 0;
        }
    }
    ca_hospitality_room_t copy;
    if (!room_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->rooms, nc * sizeof(*b->rooms));
        if (!n) { ca_hospitality_room_free(&copy); return -1; }
        b->rooms = (ca_hospitality_room_t *)n;
        b->r_cap = nc;
    }
    b->rooms[b->r_count++] = copy;
    return 0;
}

bool ca_hospitality_board_get_room(const ca_hospitality_board_t *b,
                                   const char *id, ca_hospitality_room_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ord_eq(b->rooms[i].room_id, id))
            return room_copy(out, &b->rooms[i]);
    return false;
}

/* Is a room booked on date_ms (CheckIn <= date < CheckOut for any reservation)? */
static bool room_booked_on(const ca_hospitality_board_t *b, const char *room_id,
                           int64_t date_ms) {
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_hospitality_reservation_t *r = &b->res[i];
        if (r->check_in_ms <= date_ms && r->check_out_ms > date_ms &&
            cab_ord_eq(r->room_id, room_id))
            return true;
    }
    return false;
}

ca_hospitality_room_t *ca_hospitality_board_available_on(
    const ca_hospitality_board_t *b, int64_t date_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i) {
        const ca_hospitality_room_t *room = &b->rooms[i];
        if (room->is_clean && !room_booked_on(b, room->room_id, date_ms))
            idx[n++] = i;
    }

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_hospitality_room_t *out = (ca_hospitality_room_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!room_copy(&out[i], &b->rooms[idx[i]])) {
            ca_hospitality_room_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_hospitality_board_reserve(ca_hospitality_board_t *b,
                                 const ca_hospitality_reservation_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->s_count; ++i) {
        if (cab_ord_eq(b->res[i].reservation_id, r->reservation_id)) {
            ca_hospitality_reservation_t copy;
            if (!reservation_copy(&copy, r)) return -1;
            ca_hospitality_reservation_free(&b->res[i]);
            b->res[i] = copy;
            return 0;
        }
    }
    ca_hospitality_reservation_t copy;
    if (!reservation_copy(&copy, r)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->res, nc * sizeof(*b->res));
        if (!n) { ca_hospitality_reservation_free(&copy); return -1; }
        b->res = (ca_hospitality_reservation_t *)n;
        b->s_cap = nc;
    }
    b->res[b->s_count++] = copy;
    return 0;
}

int ca_hospitality_board_check_out(ca_hospitality_board_t *b,
                                   const char *reservation_id,
                                   bool room_needs_cleaning) {
    if (!b || !reservation_id) return -1;
    const ca_hospitality_reservation_t *res = NULL;
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->res[i].reservation_id, reservation_id)) {
            res = &b->res[i];
            break;
        }
    if (!res) return -2; /* Unknown reservation -> C# InvalidOperationException */
    if (room_needs_cleaning) {
        for (size_t i = 0; i < b->r_count; ++i)
            if (cab_ord_eq(b->rooms[i].room_id, res->room_id)) {
                b->rooms[i].is_clean = false;
                break;
            }
    }
    return 0;
}

bool ca_hospitality_board_get_reservation(const ca_hospitality_board_t *b,
                                          const char *id,
                                          ca_hospitality_reservation_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->res[i].reservation_id, id))
            return reservation_copy(out, &b->res[i]);
    return false;
}

int ca_hospitality_board_add_note(ca_hospitality_board_t *b,
                                  const ca_hospitality_note_t *note) {
    if (!b || !note) return -1;
    ca_hospitality_note_t copy;
    if (!note_copy(&copy, note)) return -1;
    if (b->n_count == b->n_cap) {
        size_t nc = b->n_cap ? b->n_cap * 2 : 4;
        void *n = realloc(b->notes, nc * sizeof(*b->notes));
        if (!n) { ca_hospitality_note_free(&copy); return -1; }
        b->notes = (ca_hospitality_note_t *)n;
        b->n_cap = nc;
    }
    b->notes[b->n_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AtUtc. */
static void note_sort_desc(const ca_hospitality_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->notes[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->notes[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_hospitality_note_t *ca_hospitality_board_notes_for(
    const ca_hospitality_board_t *b, const char *reservation_id,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !reservation_id) { *out_count = (size_t)-1; return NULL; }
    if (b->n_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->n_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->n_count; ++i)
        if (cab_ord_eq(b->notes[i].reservation_id, reservation_id)) idx[n++] = i;
    note_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_hospitality_note_t *out = (ca_hospitality_note_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!note_copy(&out[i], &b->notes[idx[i]])) {
            ca_hospitality_note_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
