/*
 * test_travel.c — CircleAI.Travel (C11 port) verification against
 * TravelPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL
#define S CA_TRAVEL_DECIMAL_SCALE

static void test_trip_cost(void) {
    ca_travel_board_t *b = ca_travel_board_create();
    assert(b);
    assert(ca_travel_board_add_flight(b, NULL) == -1);

    ca_travel_flight_t f1; memset(&f1, 0, sizeof(f1));
    f1.flight_id = (char *)"f1"; f1.from = (char *)"JFK"; f1.to = (char *)"LHR";
    f1.depart_utc_ms = 0; f1.arrive_utc_ms = DAY; f1.carrier = (char *)"BA";
    f1.cabin = (char *)"Economy"; f1.price = 500 * S; f1.currency = (char *)"USD";
    assert(ca_travel_board_add_flight(b, &f1) == 0);

    ca_travel_stay_t s1; memset(&s1, 0, sizeof(s1));
    s1.stay_id = (char *)"s1"; s1.hotel = (char *)"Ritz"; s1.city = (char *)"London";
    s1.check_in_ms = 2 * DAY; s1.check_out_ms = 5 * DAY;   /* 3 nights */
    s1.nightly_rate = 200 * S; s1.currency = (char *)"USD";
    assert(ca_travel_board_add_stay(b, &s1) == 0);

    ca_travel_flight_t gf;
    assert(ca_travel_board_get_flight(b, "f1", &gf) && gf.price == 500 * S);
    ca_travel_flight_free(&gf);
    ca_travel_stay_t gs;
    assert(ca_travel_board_get_stay(b, "s1", &gs) && gs.nightly_rate == 200 * S);
    ca_travel_stay_free(&gs);

    /* Trip references f1, s1, plus a bogus id (skipped). */
    char *fids[] = { (char *)"f1", (char *)"bogus" };
    char *sids[] = { (char *)"s1" };
    ca_travel_trip_t t; memset(&t, 0, sizeof(t));
    t.trip_id = (char *)"t1"; t.name = (char *)"London Trip";
    t.start_date_ms = 1 * DAY; t.end_date_ms = 6 * DAY;
    t.flight_ids = fids; t.flight_id_count = 2; t.stay_ids = sids; t.stay_id_count = 1;
    assert(ca_travel_board_plan(b, &t) == 0);

    /* TripCost = 500 (flight) + 200*3 (stay) = 1100. */
    ca_travel_decimal_t cost = 0;
    assert(ca_travel_board_trip_cost(b, "t1", &cost) == 0);
    assert(cost == 1100 * S);
    assert(ca_travel_board_trip_cost(b, "nope", &cost) == -2);

    ca_travel_trip_t gt;
    assert(ca_travel_board_get_trip(b, "t1", &gt) && gt.flight_id_count == 2);
    ca_travel_trip_free(&gt);

    ca_travel_board_destroy(b);
    printf("  trip_cost: ok\n");
}

static void test_stay_min_nights(void) {
    ca_travel_board_t *b = ca_travel_board_create();
    /* Same-day stay -> floored at 1 night. */
    ca_travel_stay_t s1; memset(&s1, 0, sizeof(s1));
    s1.stay_id = (char *)"s1"; s1.hotel = (char *)"H"; s1.city = (char *)"C";
    s1.check_in_ms = 3 * DAY; s1.check_out_ms = 3 * DAY;
    s1.nightly_rate = 100 * S; s1.currency = (char *)"USD";
    assert(ca_travel_board_add_stay(b, &s1) == 0);

    char *sids[] = { (char *)"s1" };
    ca_travel_trip_t t; memset(&t, 0, sizeof(t));
    t.trip_id = (char *)"t1"; t.name = (char *)"N"; t.stay_ids = sids; t.stay_id_count = 1;
    assert(ca_travel_board_plan(b, &t) == 0);

    ca_travel_decimal_t cost = 0;
    assert(ca_travel_board_trip_cost(b, "t1", &cost) == 0 && cost == 100 * S);

    ca_travel_board_destroy(b);
    printf("  stay_min_nights: ok\n");
}

static void test_upcoming(void) {
    ca_travel_board_t *b = ca_travel_board_create();
    ca_travel_trip_t t1; memset(&t1, 0, sizeof(t1));
    t1.trip_id = (char *)"t1"; t1.name = (char *)"A"; t1.start_date_ms = 500;
    ca_travel_trip_t t2; memset(&t2, 0, sizeof(t2));
    t2.trip_id = (char *)"t2"; t2.name = (char *)"B"; t2.start_date_ms = 300;
    ca_travel_trip_t t3; memset(&t3, 0, sizeof(t3));
    t3.trip_id = (char *)"t3"; t3.name = (char *)"C"; t3.start_date_ms = 50;
    assert(ca_travel_board_plan(b, &t1) == 0);
    assert(ca_travel_board_plan(b, &t2) == 0);
    assert(ca_travel_board_plan(b, &t3) == 0);

    /* UpcomingTrips(now=100): t2(300), t1(500) [t3 past]; asc. */
    size_t n = 0;
    ca_travel_trip_t *up = ca_travel_board_upcoming_trips(b, 100, &n);
    assert(n == 2 && strcmp(up[0].trip_id, "t2") == 0 && strcmp(up[1].trip_id, "t1") == 0);
    ca_travel_trip_free_array(up, n);

    ca_travel_board_destroy(b);
    printf("  upcoming: ok\n");
}

int main(void) {
    test_trip_cost();
    test_stay_min_nights();
    test_upcoming();
    printf("test_travel: all assertions passed\n");
    return 0;
}
