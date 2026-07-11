#ifndef CIRCLE_AI_TRAVEL_H
#define CIRCLE_AI_TRAVEL_H

/*
 * travel.h — CircleAI.Travel (C11 port of TravelPrimitives.cs).
 *
 *   Records : Flight(FlightId, From, To, DateTimeOffset DepartUtc,
 *                    DateTimeOffset ArriveUtc, Carrier, Cabin, decimal Price,
 *                    Currency);
 *             HotelStay(StayId, Hotel, City, DateTime CheckIn, DateTime CheckOut,
 *                    decimal NightlyRate, Currency);
 *             TravelTrip(TripId, Name, DateTime StartDate, DateTime EndDate,
 *                    IReadOnlyList<string> FlightIds, IReadOnlyList<string> StayIds).
 *   Board   : ITravelBoard -> InMemoryTravelBoard
 *               Add(Flight) (FlightId keyed), Add(HotelStay) (StayId keyed),
 *               Plan(TravelTrip) (TripId keyed), GetTrip/GetFlight/GetStay,
 *               TripCost(tripId) — sum of the trip's flight Prices + each stay's
 *               NightlyRate * max(1, (CheckOut-CheckIn) whole days); unknown ids in
 *               the trip are skipped; unknown trip throws,
 *               UpcomingTrips(now) — StartDate >= now, ordered by StartDate asc.
 *
 * decimal Price/NightlyRate via ca_decimal_t. DateTimeOffset/DateTime as Unix ms
 * UTC. Stay nights use whole-day difference floored, min 1.
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

typedef int64_t ca_travel_decimal_t; /* micro-units (1e-6) */
#define CA_TRAVEL_DECIMAL_SCALE 1000000LL

/* Flight(FlightId, From, To, DepartUtc, ArriveUtc, Carrier, Cabin, decimal Price,
 * Currency). */
typedef struct {
    char   *flight_id;   /* owned, non-null */
    char   *from;        /* owned, non-null */
    char   *to;          /* owned, non-null */
    int64_t depart_utc_ms;
    int64_t arrive_utc_ms;
    char   *carrier;     /* owned, non-null */
    char   *cabin;       /* owned, non-null */
    ca_travel_decimal_t price; /* micro-units */
    char   *currency;    /* owned, non-null */
} ca_travel_flight_t;

void ca_travel_flight_free(ca_travel_flight_t *f);

/* HotelStay(StayId, Hotel, City, CheckIn, CheckOut, decimal NightlyRate,
 * Currency). */
typedef struct {
    char   *stay_id;     /* owned, non-null */
    char   *hotel;       /* owned, non-null */
    char   *city;        /* owned, non-null */
    int64_t check_in_ms;
    int64_t check_out_ms;
    ca_travel_decimal_t nightly_rate; /* micro-units */
    char   *currency;    /* owned, non-null */
} ca_travel_stay_t;

void ca_travel_stay_free(ca_travel_stay_t *s);

/* TravelTrip(TripId, Name, StartDate, EndDate, FlightIds[], StayIds[]). */
typedef struct {
    char   *trip_id;     /* owned, non-null */
    char   *name;        /* owned, non-null */
    int64_t start_date_ms;
    int64_t end_date_ms;
    char  **flight_ids;  /* owned array of owned strings (may be NULL if 0) */
    size_t  flight_id_count;
    char  **stay_ids;    /* owned array of owned strings (may be NULL if 0) */
    size_t  stay_id_count;
} ca_travel_trip_t;

void ca_travel_trip_free(ca_travel_trip_t *t);
void ca_travel_trip_free_array(ca_travel_trip_t *arr, size_t count);

typedef struct ca_travel_board ca_travel_board_t;

ca_travel_board_t *ca_travel_board_create(void); /* NULL on OOM */
void ca_travel_board_destroy(ca_travel_board_t *b);

/* Add(Flight) — FlightId keyed set. 0 / -1. */
int ca_travel_board_add_flight(ca_travel_board_t *b, const ca_travel_flight_t *f);
/* Add(HotelStay) — StayId keyed set. 0 / -1. */
int ca_travel_board_add_stay(ca_travel_board_t *b, const ca_travel_stay_t *s);
/* Plan(TravelTrip) — TripId keyed set. 0 / -1. */
int ca_travel_board_plan(ca_travel_board_t *b, const ca_travel_trip_t *t);

/* GetTrip/GetFlight/GetStay(id) -> fresh owned copy into *out, true; false miss. */
bool ca_travel_board_get_trip(const ca_travel_board_t *b, const char *id,
                              ca_travel_trip_t *out);
bool ca_travel_board_get_flight(const ca_travel_board_t *b, const char *id,
                                ca_travel_flight_t *out);
bool ca_travel_board_get_stay(const ca_travel_board_t *b, const char *id,
                              ca_travel_stay_t *out);

/* TripCost(tripId) -> total micro-units into *out; 0 on success, -1 on bad args,
 * -2 when the trip is unknown (C# InvalidOperationException). */
int ca_travel_board_trip_cost(const ca_travel_board_t *b, const char *trip_id,
                              ca_travel_decimal_t *out);

/* UpcomingTrips(now_ms) -> fresh owned array (StartDate >= now) ordered by
 * StartDate asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_travel_trip_t *ca_travel_board_upcoming_trips(const ca_travel_board_t *b,
                                                 int64_t now_ms,
                                                 size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TRAVEL_H */
