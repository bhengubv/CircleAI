/*
 * test_tourism.c — CircleAI.Tourism (C11 port) verification against
 * TourismPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_attractions(void) {
    ca_tourism_board_t *b = ca_tourism_board_create();
    assert(b);
    assert(ca_tourism_board_add(b, NULL) == -1);

    char *t1[] = { (char *)"museum", (char *)"art" };
    ca_tourism_attraction_t a1; memset(&a1, 0, sizeof(a1));
    a1.attraction_id = (char *)"a1"; a1.name = (char *)"Zoo"; a1.city = (char *)"Paris";
    a1.country = (char *)"FR"; a1.tags = t1; a1.tag_count = 2;
    char *t2[] = { (char *)"art" };
    ca_tourism_attraction_t a2; memset(&a2, 0, sizeof(a2));
    a2.attraction_id = (char *)"a2"; a2.name = (char *)"Louvre"; a2.city = (char *)"paris";
    a2.country = (char *)"FR"; a2.tags = t2; a2.tag_count = 1;
    ca_tourism_attraction_t a3; memset(&a3, 0, sizeof(a3));
    a3.attraction_id = (char *)"a3"; a3.name = (char *)"Colosseum"; a3.city = (char *)"Rome";
    a3.country = (char *)"IT";
    assert(ca_tourism_board_add(b, &a1) == 0);
    assert(ca_tourism_board_add(b, &a2) == 0);
    assert(ca_tourism_board_add(b, &a3) == 0);

    /* AttractionsInCity "paris" (CI) ordered by Name: Louvre, Zoo. */
    size_t n = 0;
    ca_tourism_attraction_t *cs = ca_tourism_board_attractions_in_city(b, "paris", &n);
    assert(n == 2 && strcmp(cs[0].name, "Louvre") == 0 && strcmp(cs[1].name, "Zoo") == 0);
    ca_tourism_attraction_free_array(cs, n);
    /* blank -> SIZE_MAX. */
    assert(ca_tourism_board_attractions_in_city(b, "  ", &n) == NULL && n == (size_t)-1);

    /* ByTag "art" ordered by Name: Louvre, Zoo. */
    ca_tourism_attraction_t *ts = ca_tourism_board_by_tag(b, "art", &n);
    assert(n == 2 && strcmp(ts[0].name, "Louvre") == 0 && strcmp(ts[1].name, "Zoo") == 0);
    ca_tourism_attraction_free_array(ts, n);
    assert(ca_tourism_board_by_tag(b, "", &n) == NULL && n == (size_t)-1);

    ca_tourism_board_destroy(b);
    printf("  attractions: ok\n");
}

static void test_itinerary_bookings(void) {
    ca_tourism_board_t *b = ca_tourism_board_create();

    ca_tourism_itinerary_item_t items[2];
    memset(items, 0, sizeof(items));
    items[0].day_index = 1; items[0].start_local_ticks = 100; items[0].end_local_ticks = 200;
    items[0].attraction_id = (char *)"a1"; items[0].has_note = true; items[0].note = (char *)"morning";
    items[1].day_index = 1; items[1].attraction_id = (char *)"a2"; items[1].has_note = false;

    ca_tourism_itinerary_t it; memset(&it, 0, sizeof(it));
    it.itinerary_id = (char *)"it1"; it.title = (char *)"Day 1"; it.items = items; it.item_count = 2;
    assert(ca_tourism_board_plan(b, &it) == 0);

    ca_tourism_itinerary_t got;
    assert(ca_tourism_board_get_itinerary(b, "it1", &got));
    assert(got.item_count == 2 && strcmp(got.items[0].attraction_id, "a1") == 0);
    assert(got.items[0].has_note && strcmp(got.items[0].note, "morning") == 0);
    assert(!got.items[1].has_note);
    ca_tourism_itinerary_free(&got);
    assert(!ca_tourism_board_get_itinerary(b, "nope", &got));

    /* Bookings in append order. */
    ca_tourism_booking_t bk1; memset(&bk1, 0, sizeof(bk1));
    bk1.booking_id = (char *)"bk1"; bk1.itinerary_id = (char *)"it1";
    bk1.start_date_ms = 1000; bk1.travelers = 2;
    bk1.total_price = 500 * CA_TOURISM_DECIMAL_SCALE; bk1.currency = (char *)"EUR";
    ca_tourism_booking_t bk2; memset(&bk2, 0, sizeof(bk2));
    bk2.booking_id = (char *)"bk2"; bk2.itinerary_id = (char *)"it1";
    bk2.start_date_ms = 2000; bk2.travelers = 1;
    bk2.total_price = 250 * CA_TOURISM_DECIMAL_SCALE; bk2.currency = (char *)"EUR";
    assert(ca_tourism_board_book(b, &bk1) == 0);
    assert(ca_tourism_board_book(b, &bk2) == 0);

    size_t n = 0;
    ca_tourism_booking_t *bs = ca_tourism_board_bookings(b, &n);
    assert(n == 2 && strcmp(bs[0].booking_id, "bk1") == 0 && strcmp(bs[1].booking_id, "bk2") == 0);
    assert(bs[0].total_price == 500 * CA_TOURISM_DECIMAL_SCALE);
    ca_tourism_booking_free_array(bs, n);

    ca_tourism_board_destroy(b);
    printf("  itinerary_bookings: ok\n");
}

int main(void) {
    test_attractions();
    test_itinerary_bookings();
    printf("test_tourism: all assertions passed\n");
    return 0;
}
