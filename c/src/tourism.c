/*
 * tourism.c — CircleAI.Tourism (C11 port of TourismPrimitives.cs).
 *
 * InMemoryTourismBoard: attractions (AttractionId keyed), itineraries (Itinerary
 * Id keyed, each holding a nested ItineraryItem list), bookings (append list).
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/tourism.h"
#include "board_common.h"

/* ── ItineraryItem ──────────────────────────────────────────────────────── */

static void item_free(ca_tourism_itinerary_item_t *it) {
    if (!it) return;
    free(it->attraction_id);
    free(it->note);
    it->attraction_id = it->note = NULL;
    it->has_note = false;
}

static bool item_copy(ca_tourism_itinerary_item_t *dst,
                      const ca_tourism_itinerary_item_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->day_index         = src->day_index;
    dst->start_local_ticks = src->start_local_ticks;
    dst->end_local_ticks   = src->end_local_ticks;
    dst->attraction_id     = cab_strdup_empty(src->attraction_id);
    bool ok = dst->attraction_id != NULL;
    if (ok && src->has_note) {
        dst->note = cab_strdup_empty(src->note);
        ok = dst->note != NULL;
        dst->has_note = ok;
    }
    if (!ok) { item_free(dst); return false; }
    return true;
}

/* ── Attraction ─────────────────────────────────────────────────────────── */

void ca_tourism_attraction_free(ca_tourism_attraction_t *a) {
    if (!a) return;
    free(a->attraction_id);
    free(a->name);
    free(a->city);
    free(a->country);
    cab_strv_free(a->tags, a->tag_count);
    a->attraction_id = a->name = a->city = a->country = NULL;
    a->tags = NULL;
    a->tag_count = 0;
}
void ca_tourism_attraction_free_array(ca_tourism_attraction_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_tourism_attraction_free(&arr[i]);
    free(arr);
}

static bool attraction_copy(ca_tourism_attraction_t *dst,
                            const ca_tourism_attraction_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->attraction_id = cab_strdup_empty(src->attraction_id);
    dst->name          = cab_strdup_empty(src->name);
    dst->city          = cab_strdup_empty(src->city);
    dst->country       = cab_strdup_empty(src->country);
    dst->lat = src->lat; dst->lon = src->lon;
    bool ok = dst->attraction_id && dst->name && dst->city && dst->country;
    if (ok) ok = cab_strv_copy(&dst->tags, src->tags, src->tag_count);
    if (ok) dst->tag_count = src->tag_count;
    if (!ok) { ca_tourism_attraction_free(dst); return false; }
    return true;
}

/* ── Itinerary ──────────────────────────────────────────────────────────── */

void ca_tourism_itinerary_free(ca_tourism_itinerary_t *i) {
    if (!i) return;
    free(i->itinerary_id);
    free(i->title);
    if (i->items)
        for (size_t k = 0; k < i->item_count; ++k) item_free(&i->items[k]);
    free(i->items);
    i->itinerary_id = i->title = NULL;
    i->items = NULL;
    i->item_count = 0;
}

static bool itinerary_copy(ca_tourism_itinerary_t *dst,
                           const ca_tourism_itinerary_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->itinerary_id = cab_strdup_empty(src->itinerary_id);
    dst->title        = cab_strdup_empty(src->title);
    if (!dst->itinerary_id || !dst->title) { ca_tourism_itinerary_free(dst); return false; }
    if (src->item_count > 0) {
        dst->items = (ca_tourism_itinerary_item_t *)calloc(src->item_count,
                                                           sizeof(*dst->items));
        if (!dst->items) { ca_tourism_itinerary_free(dst); return false; }
        for (size_t k = 0; k < src->item_count; ++k) {
            if (!item_copy(&dst->items[k], &src->items[k])) {
                for (size_t j = 0; j < k; ++j) item_free(&dst->items[j]);
                free(dst->items);
                dst->items = NULL;
                ca_tourism_itinerary_free(dst);
                return false;
            }
        }
        dst->item_count = src->item_count;
    }
    return true;
}

/* ── TourismBooking ─────────────────────────────────────────────────────── */

void ca_tourism_booking_free(ca_tourism_booking_t *b) {
    if (!b) return;
    free(b->booking_id);
    free(b->itinerary_id);
    free(b->currency);
    b->booking_id = b->itinerary_id = b->currency = NULL;
}
void ca_tourism_booking_free_array(ca_tourism_booking_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_tourism_booking_free(&arr[i]);
    free(arr);
}

static bool booking_copy(ca_tourism_booking_t *dst,
                         const ca_tourism_booking_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->booking_id   = cab_strdup_empty(src->booking_id);
    dst->itinerary_id = cab_strdup_empty(src->itinerary_id);
    dst->start_date_ms = src->start_date_ms;
    dst->travelers    = src->travelers;
    dst->total_price  = src->total_price;
    dst->currency     = cab_strdup_empty(src->currency);
    if (!dst->booking_id || !dst->itinerary_id || !dst->currency) {
        ca_tourism_booking_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_tourism_board {
    ca_tourism_attraction_t *attractions;
    size_t                   a_count, a_cap;
    ca_tourism_itinerary_t  *itineraries;
    size_t                   i_count, i_cap;
    ca_tourism_booking_t    *bookings;
    size_t                   b_count, b_cap;
};

ca_tourism_board_t *ca_tourism_board_create(void) {
    return (ca_tourism_board_t *)calloc(1, sizeof(ca_tourism_board_t));
}
void ca_tourism_board_destroy(ca_tourism_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->a_count; ++i) ca_tourism_attraction_free(&b->attractions[i]);
    for (size_t i = 0; i < b->i_count; ++i) ca_tourism_itinerary_free(&b->itineraries[i]);
    for (size_t i = 0; i < b->b_count; ++i) ca_tourism_booking_free(&b->bookings[i]);
    free(b->attractions);
    free(b->itineraries);
    free(b->bookings);
    free(b);
}

int ca_tourism_board_add(ca_tourism_board_t *b,
                         const ca_tourism_attraction_t *a) {
    if (!b || !a) return -1;
    for (size_t i = 0; i < b->a_count; ++i) {
        if (cab_ord_eq(b->attractions[i].attraction_id, a->attraction_id)) {
            ca_tourism_attraction_t copy;
            if (!attraction_copy(&copy, a)) return -1;
            ca_tourism_attraction_free(&b->attractions[i]);
            b->attractions[i] = copy;
            return 0;
        }
    }
    ca_tourism_attraction_t copy;
    if (!attraction_copy(&copy, a)) return -1;
    if (b->a_count == b->a_cap) {
        size_t nc = b->a_cap ? b->a_cap * 2 : 4;
        void *n = realloc(b->attractions, nc * sizeof(*b->attractions));
        if (!n) { ca_tourism_attraction_free(&copy); return -1; }
        b->attractions = (ca_tourism_attraction_t *)n;
        b->a_cap = nc;
    }
    b->attractions[b->a_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by Name (ordinal). */
static void attraction_sort_name(const ca_tourism_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(b->attractions[idx[j - 1]].name,
                              b->attractions[key].name) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Shared collector: gather + Name-sort + deep-copy attractions matched by `pred`. */
static ca_tourism_attraction_t *collect_attractions(
    const ca_tourism_board_t *b,
    bool (*pred)(const ca_tourism_attraction_t *, const char *),
    const char *arg, size_t *out_count) {
    if (b->a_count == 0) { *out_count = 0; return NULL; }
    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i)
        if (pred(&b->attractions[i], arg)) idx[n++] = i;
    attraction_sort_name(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_tourism_attraction_t *out =
        (ca_tourism_attraction_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!attraction_copy(&out[i], &b->attractions[idx[i]])) {
            ca_tourism_attraction_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

static bool pred_city(const ca_tourism_attraction_t *a, const char *city) {
    return cab_ci_eq(a->city, city);
}
static bool pred_tag(const ca_tourism_attraction_t *a, const char *tag) {
    return cab_strv_ci_contains(a->tags, a->tag_count, tag);
}

ca_tourism_attraction_t *ca_tourism_board_attractions_in_city(
    const ca_tourism_board_t *b, const char *city, size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || cab_is_ws(city)) { *out_count = (size_t)-1; return NULL; }
    return collect_attractions(b, pred_city, city, out_count);
}

ca_tourism_attraction_t *ca_tourism_board_by_tag(const ca_tourism_board_t *b,
                                                 const char *tag,
                                                 size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || cab_is_ws(tag)) { *out_count = (size_t)-1; return NULL; }
    return collect_attractions(b, pred_tag, tag, out_count);
}

int ca_tourism_board_plan(ca_tourism_board_t *b,
                          const ca_tourism_itinerary_t *i) {
    if (!b || !i) return -1;
    for (size_t k = 0; k < b->i_count; ++k) {
        if (cab_ord_eq(b->itineraries[k].itinerary_id, i->itinerary_id)) {
            ca_tourism_itinerary_t copy;
            if (!itinerary_copy(&copy, i)) return -1;
            ca_tourism_itinerary_free(&b->itineraries[k]);
            b->itineraries[k] = copy;
            return 0;
        }
    }
    ca_tourism_itinerary_t copy;
    if (!itinerary_copy(&copy, i)) return -1;
    if (b->i_count == b->i_cap) {
        size_t nc = b->i_cap ? b->i_cap * 2 : 4;
        void *n = realloc(b->itineraries, nc * sizeof(*b->itineraries));
        if (!n) { ca_tourism_itinerary_free(&copy); return -1; }
        b->itineraries = (ca_tourism_itinerary_t *)n;
        b->i_cap = nc;
    }
    b->itineraries[b->i_count++] = copy;
    return 0;
}

bool ca_tourism_board_get_itinerary(const ca_tourism_board_t *b, const char *id,
                                    ca_tourism_itinerary_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->i_count; ++i)
        if (cab_ord_eq(b->itineraries[i].itinerary_id, id))
            return itinerary_copy(out, &b->itineraries[i]);
    return false;
}

int ca_tourism_board_book(ca_tourism_board_t *b,
                          const ca_tourism_booking_t *bk) {
    if (!b || !bk) return -1;
    ca_tourism_booking_t copy;
    if (!booking_copy(&copy, bk)) return -1;
    if (b->b_count == b->b_cap) {
        size_t nc = b->b_cap ? b->b_cap * 2 : 4;
        void *n = realloc(b->bookings, nc * sizeof(*b->bookings));
        if (!n) { ca_tourism_booking_free(&copy); return -1; }
        b->bookings = (ca_tourism_booking_t *)n;
        b->b_cap = nc;
    }
    b->bookings[b->b_count++] = copy;
    return 0;
}

ca_tourism_booking_t *ca_tourism_board_bookings(const ca_tourism_board_t *b,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->b_count == 0) { *out_count = 0; return NULL; }
    ca_tourism_booking_t *out =
        (ca_tourism_booking_t *)calloc(b->b_count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->b_count; ++i) {
        if (!booking_copy(&out[i], &b->bookings[i])) {
            ca_tourism_booking_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = b->b_count;
    return out;
}
