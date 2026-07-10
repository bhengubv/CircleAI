/*
 * test_safety_child.c — CircleAI.Safety.Child domain primitives (C11 port).
 *
 * Verifies InMemoryChildSafetyBoard: trusted-adult ring (priority-ordered,
 * last-write-wins), geofences (last-write-wins, GetGeofence, Haversine
 * IsInsideAnyFence), check-ins (RecentCheckIns filter + descending + limit),
 * against ChildSafetyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_trusted_adult_t mk_adult(const char *id, const char *name, int prio) {
    ca_trusted_adult_t a; memset(&a, 0, sizeof(a));
    a.adult_id = strdup(id);
    a.name = strdup(name);
    a.phone = strdup("000");
    a.relationship = strdup("parent");
    a.ring_priority = prio;
    return a;
}
static ca_geofence_t mk_fence(const char *id, const char *name,
                              double lat, double lon, double radius) {
    ca_geofence_t g; memset(&g, 0, sizeof(g));
    g.fence_id = strdup(id);
    g.name = strdup(name);
    g.centre_lat = lat;
    g.centre_lon = lon;
    g.radius_meters = radius;
    return g;
}
static ca_check_in_t mk_checkin(const char *child, const char *status, int64_t at) {
    ca_check_in_t c; memset(&c, 0, sizeof(c));
    c.child_id = strdup(child);
    c.status = strdup(status);
    c.at_utc_ms = at;
    return c;
}

static void test_ring(void) {
    ca_child_safety_board_t *b = ca_child_safety_board_create();
    assert(b);
    size_t n = 0;
    assert(ca_child_safety_board_ring_ordered(b, &n) == NULL && n == 0);

    ca_trusted_adult_t a1 = mk_adult("a1", "Mum",    2);
    ca_trusted_adult_t a2 = mk_adult("a2", "Dad",    1);
    ca_trusted_adult_t a3 = mk_adult("a3", "Gran",   3);
    assert(ca_child_safety_board_add_adult(b, &a1));
    assert(ca_child_safety_board_add_adult(b, &a2));
    assert(ca_child_safety_board_add_adult(b, &a3));
    assert(ca_child_safety_board_add_adult(b, NULL) == false);

    /* ordered by ring_priority ascending → Dad(1), Mum(2), Gran(3) */
    ca_trusted_adult_t *ring = ca_child_safety_board_ring_ordered(b, &n);
    assert(n == 3);
    assert(strcmp(ring[0].name, "Dad") == 0);
    assert(strcmp(ring[1].name, "Mum") == 0);
    assert(strcmp(ring[2].name, "Gran") == 0);
    ca_trusted_adult_free_array(ring, n);

    /* last-write-wins by adult_id: re-add a2 with new priority + name */
    ca_trusted_adult_t a2b = mk_adult("a2", "Father", 5);
    assert(ca_child_safety_board_add_adult(b, &a2b));
    ca_trusted_adult_free(&a2b);
    ring = ca_child_safety_board_ring_ordered(b, &n);
    assert(n == 3);   /* replaced */
    /* now priorities: Mum(2), Gran(3), Father(5) */
    assert(strcmp(ring[0].name, "Mum") == 0);
    assert(strcmp(ring[1].name, "Gran") == 0);
    assert(strcmp(ring[2].name, "Father") == 0 && ring[2].ring_priority == 5);
    ca_trusted_adult_free_array(ring, n);

    assert(ca_child_safety_board_ring_ordered(NULL, &n) == NULL && n == SIZE_MAX);

    ca_trusted_adult_free(&a1); ca_trusted_adult_free(&a2); ca_trusted_adult_free(&a3);
    ca_child_safety_board_destroy(b);
    printf("  ring: ok\n");
}

static void test_geofence(void) {
    ca_child_safety_board_t *b = ca_child_safety_board_create();

    /* Johannesburg CBD approx (-26.2041, 28.0473), 500 m radius */
    ca_geofence_t home = mk_fence("home", "Home", -26.2041, 28.0473, 500.0);
    assert(ca_child_safety_board_define_geofence(b, &home));
    assert(ca_child_safety_board_define_geofence(b, NULL) == false);

    /* GetGeofence returns a deep copy */
    ca_geofence_t got; memset(&got, 0, sizeof(got));
    assert(ca_child_safety_board_get_geofence(b, "home", &got));
    assert(strcmp(got.name, "Home") == 0 && got.radius_meters == 500.0);
    ca_geofence_free(&got);
    assert(ca_child_safety_board_get_geofence(b, "missing", &got) == false);

    /* Inside: the exact centre is inside */
    assert(ca_child_safety_board_is_inside_any_fence(b, -26.2041, 28.0473) == true);
    /* A point ~100 m away is inside a 500 m fence (0.001 deg lat ~= 111 m) */
    assert(ca_child_safety_board_is_inside_any_fence(b, -26.2050, 28.0473) == true);
    /* A point far away (Cape Town ~ -33.9, 18.4) is outside */
    assert(ca_child_safety_board_is_inside_any_fence(b, -33.9249, 18.4241) == false);

    /* Define a second, distant fence → the Cape Town point is now inside it */
    ca_geofence_t ct = mk_fence("ct", "CapeTown", -33.9249, 18.4241, 1000.0);
    assert(ca_child_safety_board_define_geofence(b, &ct));
    assert(ca_child_safety_board_is_inside_any_fence(b, -33.9249, 18.4241) == true);
    ca_check_in_t dummy; (void)dummy;

    /* last-write-wins: shrink home to 1 m; the 100 m-away point is now outside */
    ca_geofence_t home_small = mk_fence("home", "HomeSmall", -26.2041, 28.0473, 1.0);
    assert(ca_child_safety_board_define_geofence(b, &home_small));
    ca_geofence_free(&home_small);
    assert(ca_child_safety_board_is_inside_any_fence(b, -26.2050, 28.0473) == false);
    assert(ca_child_safety_board_is_inside_any_fence(b, -26.2041, 28.0473) == true); /* centre still in */

    /* NULL board → false, never crashes */
    assert(ca_child_safety_board_is_inside_any_fence(NULL, 0, 0) == false);

    ca_geofence_free(&home); ca_geofence_free(&ct);
    ca_child_safety_board_destroy(b);
    printf("  geofence: ok\n");
}

static void test_checkins(void) {
    ca_child_safety_board_t *b = ca_child_safety_board_create();
    size_t n = 0;

    ca_check_in_t c1 = mk_checkin("kid1", "arrived", 100);
    ca_check_in_t c2 = mk_checkin("kid2", "left",    150);
    ca_check_in_t c3 = mk_checkin("kid1", "left",    300);
    ca_check_in_t c4 = mk_checkin("kid1", "arrived", 200);
    c3.has_lat = true; c3.lat = -26.2; c3.has_lon = true; c3.lon = 28.0;
    assert(ca_child_safety_board_record_check_in(b, &c1));
    assert(ca_child_safety_board_record_check_in(b, &c2));
    assert(ca_child_safety_board_record_check_in(b, &c3));
    assert(ca_child_safety_board_record_check_in(b, &c4));
    assert(ca_child_safety_board_record_check_in(b, NULL) == false);

    /* kid1 recent, descending by at → c3(300), c4(200), c1(100) */
    ca_check_in_t *recent = ca_child_safety_board_recent_check_ins(b, "kid1", 20, &n);
    assert(n == 3);
    assert(recent[0].at_utc_ms == 300 && recent[0].has_lat && recent[0].lat == -26.2);
    assert(recent[1].at_utc_ms == 200);
    assert(recent[2].at_utc_ms == 100);
    ca_check_in_free_array(recent, n);

    /* limit caps the count (newest first) */
    recent = ca_child_safety_board_recent_check_ins(b, "kid1", 2, &n);
    assert(n == 2 && recent[0].at_utc_ms == 300 && recent[1].at_utc_ms == 200);
    ca_check_in_free_array(recent, n);

    /* kid2 has one */
    recent = ca_child_safety_board_recent_check_ins(b, "kid2", 20, &n);
    assert(n == 1 && strcmp(recent[0].status, "left") == 0);
    ca_check_in_free_array(recent, n);

    /* unknown child → empty */
    assert(ca_child_safety_board_recent_check_ins(b, "ghost", 20, &n) == NULL && n == 0);

    /* limit <= 0 → SIZE_MAX (ArgumentOutOfRangeException analogue) */
    assert(ca_child_safety_board_recent_check_ins(b, "kid1", 0, &n) == NULL && n == SIZE_MAX);
    assert(ca_child_safety_board_recent_check_ins(b, "kid1", -1, &n) == NULL && n == SIZE_MAX);
    /* NULL board / NULL child → SIZE_MAX */
    assert(ca_child_safety_board_recent_check_ins(NULL, "kid1", 20, &n) == NULL && n == SIZE_MAX);
    assert(ca_child_safety_board_recent_check_ins(b, NULL, 20, &n) == NULL && n == SIZE_MAX);

    ca_check_in_free(&c1); ca_check_in_free(&c2); ca_check_in_free(&c3); ca_check_in_free(&c4);
    ca_child_safety_board_destroy(b);
    printf("  checkins: ok\n");
}

int main(void) {
    test_ring();
    test_geofence();
    test_checkins();
    printf("test_safety_child: all assertions passed\n");
    return 0;
}
