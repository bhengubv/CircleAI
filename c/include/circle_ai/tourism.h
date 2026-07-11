#ifndef CIRCLE_AI_TOURISM_H
#define CIRCLE_AI_TOURISM_H

/*
 * tourism.h — CircleAI.Tourism (C11 port of TourismPrimitives.cs).
 *
 *   Records : Attraction(AttractionId, Name, City, Country, double Lat, double Lon,
 *                        IReadOnlyList<string> Tags);
 *             ItineraryItem(int DayIndex, TimeSpan StartLocal, TimeSpan EndLocal,
 *                        AttractionId, string? Note);
 *             Itinerary(ItineraryId, Title, IReadOnlyList<ItineraryItem> Items);
 *             TourismBooking(BookingId, ItineraryId, DateTime StartDate,
 *                        int Travelers, decimal TotalPrice, Currency).
 *   Board   : ITourismBoard -> InMemoryTourismBoard
 *               Add (AttractionId keyed), AttractionsInCity(city) (OrdinalIgnore-
 *               Case; ordered by Name; throws on blank), ByTag(tag)
 *               (OrdinalIgnoreCase Tags.Any; ordered by Name; throws on blank),
 *               Plan (ItineraryId keyed), GetItinerary(id), Book (appends),
 *               Bookings (append order).
 *
 * decimal TotalPrice via ca_decimal_t. TimeSpan carried as .NET ticks (100ns);
 * DateTime as Unix ms UTC. Item Note optional via has_note.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_tourism_decimal_t; /* micro-units (1e-6) */
#define CA_TOURISM_DECIMAL_SCALE 1000000LL

/* Attraction(AttractionId, Name, City, Country, double Lat, double Lon, Tags[]). */
typedef struct {
    char   *attraction_id; /* owned, non-null */
    char   *name;          /* owned, non-null */
    char   *city;          /* owned, non-null */
    char   *country;       /* owned, non-null */
    double  lat, lon;
    char  **tags;          /* owned array of owned strings (may be NULL if 0) */
    size_t  tag_count;
} ca_tourism_attraction_t;

void ca_tourism_attraction_free(ca_tourism_attraction_t *a);
void ca_tourism_attraction_free_array(ca_tourism_attraction_t *arr, size_t count);

/* ItineraryItem(int DayIndex, TimeSpan StartLocal, TimeSpan EndLocal,
 * AttractionId, string? Note). */
typedef struct {
    int     day_index;
    int64_t start_local_ticks; /* TimeSpan ticks (100ns) */
    int64_t end_local_ticks;
    char   *attraction_id;     /* owned, non-null */
    bool    has_note;          /* false == C# null Note */
    char   *note;              /* owned, valid only when has_note */
} ca_tourism_itinerary_item_t;

/* Itinerary(ItineraryId, Title, IReadOnlyList<ItineraryItem> Items). */
typedef struct {
    char   *itinerary_id;  /* owned, non-null */
    char   *title;         /* owned, non-null */
    ca_tourism_itinerary_item_t *items; /* owned array (may be NULL if 0) */
    size_t  item_count;
} ca_tourism_itinerary_t;

void ca_tourism_itinerary_free(ca_tourism_itinerary_t *i);

/* TourismBooking(BookingId, ItineraryId, DateTime StartDate, int Travelers,
 * decimal TotalPrice, Currency). */
typedef struct {
    char   *booking_id;    /* owned, non-null */
    char   *itinerary_id;  /* owned, non-null */
    int64_t start_date_ms;
    int     travelers;
    ca_tourism_decimal_t total_price; /* micro-units */
    char   *currency;      /* owned, non-null */
} ca_tourism_booking_t;

void ca_tourism_booking_free(ca_tourism_booking_t *b);
void ca_tourism_booking_free_array(ca_tourism_booking_t *arr, size_t count);

typedef struct ca_tourism_board ca_tourism_board_t;

ca_tourism_board_t *ca_tourism_board_create(void); /* NULL on OOM */
void ca_tourism_board_destroy(ca_tourism_board_t *b);

/* Add(a) — AttractionId keyed set. 0 / -1. */
int ca_tourism_board_add(ca_tourism_board_t *b,
                         const ca_tourism_attraction_t *a);

/* AttractionsInCity(city) -> fresh owned array (Name asc) with City matching
 * (OrdinalIgnoreCase). city must be non-null / non-whitespace (SIZE_MAX on blank).
 * NULL + 0 empty. */
ca_tourism_attraction_t *ca_tourism_board_attractions_in_city(
    const ca_tourism_board_t *b, const char *city, size_t *out_count);

/* ByTag(tag) -> fresh owned array (Name asc) whose Tags contain tag
 * (OrdinalIgnoreCase). tag must be non-null / non-whitespace (SIZE_MAX on blank).
 * NULL + 0 empty. */
ca_tourism_attraction_t *ca_tourism_board_by_tag(const ca_tourism_board_t *b,
                                                 const char *tag,
                                                 size_t *out_count);

/* Plan(i) — ItineraryId keyed set. 0 / -1. */
int ca_tourism_board_plan(ca_tourism_board_t *b,
                          const ca_tourism_itinerary_t *i);

/* GetItinerary(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_tourism_board_get_itinerary(const ca_tourism_board_t *b, const char *id,
                                    ca_tourism_itinerary_t *out);

/* Book(bk) — appends. 0 / -1. */
int ca_tourism_board_book(ca_tourism_board_t *b,
                          const ca_tourism_booking_t *bk);

/* Bookings -> fresh owned array in append order. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_tourism_booking_t *ca_tourism_board_bookings(const ca_tourism_board_t *b,
                                                size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TOURISM_H */
