/*
 * test_realestate.c — CircleAI.RealEstate (C11 port) verification against
 * RealEstatePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_re_property_t mk_prop(const char *id, const char *suburb,
                                ca_re_property_kind_t kind) {
    ca_re_property_t p; memset(&p, 0, sizeof(p));
    p.property_id = (char *)id; p.suburb = (char *)suburb; p.kind = kind;
    p.beds = 3; p.baths = 2; p.floor_area_m2 = 120.0;
    return p;
}
static ca_re_listing_t mk_listing(const char *lid, const char *pid,
                                  int64_t price, int64_t listed, bool active) {
    ca_re_listing_t l; memset(&l, 0, sizeof(l));
    l.listing_id = (char *)lid; l.property_id = (char *)pid;
    l.asking_price = price; l.currency = (char *)"ZAR";
    l.listed_utc_ms = listed; l.is_active = active;
    return l;
}

static void test_listings(void) {
    ca_re_board_t *b = ca_re_board_create();
    assert(b);
    assert(ca_re_board_register_property(b, NULL) == -1);

    ca_re_property_t p1 = mk_prop("p1", "Sea Point", CA_RE_KIND_APARTMENT);
    ca_re_property_t p2 = mk_prop("p2", "sea point", CA_RE_KIND_HOUSE); /* CI */
    ca_re_property_t p3 = mk_prop("p3", "Camps Bay", CA_RE_KIND_HOUSE);
    assert(ca_re_board_register_property(b, &p1) == 0);
    assert(ca_re_board_register_property(b, &p2) == 0);
    assert(ca_re_board_register_property(b, &p3) == 0);

    ca_re_listing_t l1 = mk_listing("l1", "p1", 2000000LL * CA_RE_DECIMAL_SCALE, 100, true);
    ca_re_listing_t l2 = mk_listing("l2", "p2", 4000000LL * CA_RE_DECIMAL_SCALE, 300, true);
    ca_re_listing_t l3 = mk_listing("l3", "p3", 9000000LL * CA_RE_DECIMAL_SCALE, 200, true);
    ca_re_listing_t l4 = mk_listing("l4", "p1", 1000000LL * CA_RE_DECIMAL_SCALE, 400, false);
    assert(ca_re_board_list(b, &l1) == 0);
    assert(ca_re_board_list(b, &l2) == 0);
    assert(ca_re_board_list(b, &l3) == 0);
    assert(ca_re_board_list(b, &l4) == 0);

    /* ActiveInSuburb("SEA POINT") CI: active l1(100),l2(300) [l4 inactive],
     * ordered by ListedUtc desc => l2, l1. */
    size_t n = 0;
    ca_re_listing_t *arr = ca_re_board_active_in_suburb(b, "SEA POINT", &n);
    assert(n == 2);
    assert(strcmp(arr[0].listing_id, "l2") == 0);
    assert(strcmp(arr[1].listing_id, "l1") == 0);
    ca_re_listing_free_array(arr, n);

    /* SuburbAverage(Sea Point) = (2,000,000 + 4,000,000)/2 = 3,000,000. */
    ca_re_decimal_t avg;
    assert(ca_re_board_suburb_average(b, "Sea Point", &avg));
    assert(avg == 3000000LL * CA_RE_DECIMAL_SCALE);

    /* Close(l2) => IsActive false; ActiveInSuburb now just l1; avg = 2,000,000. */
    assert(ca_re_board_close(b, "l2") == 0);
    arr = ca_re_board_active_in_suburb(b, "Sea Point", &n);
    assert(n == 1 && strcmp(arr[0].listing_id, "l1") == 0);
    ca_re_listing_free_array(arr, n);
    assert(ca_re_board_suburb_average(b, "Sea Point", &avg));
    assert(avg == 2000000LL * CA_RE_DECIMAL_SCALE);

    /* Close unknown => 1. */
    assert(ca_re_board_close(b, "nope") == 1);

    /* Empty suburb => null (false). */
    assert(!ca_re_board_suburb_average(b, "Newlands", &avg));
    arr = ca_re_board_active_in_suburb(b, "Newlands", &n);
    assert(n == 0 && arr == NULL);

    ca_re_board_destroy(b);
    printf("  listings: ok\n");
}

static void test_valuations_viewings(void) {
    ca_re_board_t *b = ca_re_board_create();

    ca_re_valuation_t v; memset(&v, 0, sizeof(v));
    v.property_id = (char *)"p1"; v.estimated_value = 5 * CA_RE_DECIMAL_SCALE;
    v.source = (char *)"agent"; v.at_utc_ms = 10;
    assert(ca_re_board_value(b, &v) == 0);
    assert(ca_re_board_value(b, NULL) == -1);

    ca_re_viewing_t w; memset(&w, 0, sizeof(w));
    w.viewing_id = (char *)"v1"; w.listing_id = (char *)"l1";
    w.attendee_name = (char *)"Zed"; w.at_utc_ms = 20;
    assert(ca_re_board_schedule_viewing(b, &w) == 0);
    assert(ca_re_board_schedule_viewing(b, NULL) == -1);

    ca_re_board_destroy(b);
    printf("  valuations_viewings: ok\n");
}

int main(void) {
    test_listings();
    test_valuations_viewings();
    printf("test_realestate: all assertions passed\n");
    return 0;
}
