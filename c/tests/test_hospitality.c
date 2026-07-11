/*
 * test_hospitality.c — CircleAI.Hospitality (C11 port) verification against
 * HospitalityPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL

static ca_hospitality_room_t mk_room(const char *id, bool clean) {
    ca_hospitality_room_t r; memset(&r, 0, sizeof(r));
    r.room_id = (char *)id; r.type = (char *)"Suite";
    r.nightly_rate = 100 * CA_HOSPITALITY_DECIMAL_SCALE; r.currency = (char *)"USD";
    r.is_clean = clean;
    return r;
}

static void test_rooms_avail(void) {
    ca_hospitality_board_t *b = ca_hospitality_board_create();
    assert(b);
    assert(ca_hospitality_board_add_room(b, NULL) == -1);

    ca_hospitality_room_t r1 = mk_room("r1", true);
    ca_hospitality_room_t r2 = mk_room("r2", true);
    ca_hospitality_room_t r3 = mk_room("r3", false); /* dirty */
    assert(ca_hospitality_board_add_room(b, &r1) == 0);
    assert(ca_hospitality_board_add_room(b, &r2) == 0);
    assert(ca_hospitality_board_add_room(b, &r3) == 0);

    ca_hospitality_room_t got;
    assert(ca_hospitality_board_get_room(b, "r1", &got) &&
           got.nightly_rate == 100 * CA_HOSPITALITY_DECIMAL_SCALE);
    ca_hospitality_room_free(&got);

    /* r1 booked days 10..12 (checkout exclusive). */
    ca_hospitality_reservation_t res; memset(&res, 0, sizeof(res));
    res.reservation_id = (char *)"res1"; res.guest_name = (char *)"Ann";
    res.room_id = (char *)"r1"; res.check_in_ms = 10 * DAY; res.check_out_ms = 12 * DAY;
    assert(ca_hospitality_board_reserve(b, &res) == 0);

    /* AvailableOn day 11: r1 booked, r3 dirty -> only r2. */
    size_t n = 0;
    ca_hospitality_room_t *av = ca_hospitality_board_available_on(b, 11 * DAY, &n);
    assert(n == 1 && strcmp(av[0].room_id, "r2") == 0);
    ca_hospitality_room_free_array(av, n);

    /* AvailableOn day 12 (checkout day, exclusive): r1 free again, r3 dirty. */
    av = ca_hospitality_board_available_on(b, 12 * DAY, &n);
    assert(n == 2 && strcmp(av[0].room_id, "r1") == 0 && strcmp(av[1].room_id, "r2") == 0);
    ca_hospitality_room_free_array(av, n);

    /* CheckOut with cleaning flips r1 dirty. */
    assert(ca_hospitality_board_check_out(b, "nope", true) == -2);
    assert(ca_hospitality_board_check_out(b, "res1", true) == 0);
    assert(ca_hospitality_board_get_room(b, "r1", &got) && !got.is_clean);
    ca_hospitality_room_free(&got);

    ca_hospitality_board_destroy(b);
    printf("  rooms_avail: ok\n");
}

static void test_notes(void) {
    ca_hospitality_board_t *b = ca_hospitality_board_create();

    ca_hospitality_note_t n1; memset(&n1, 0, sizeof(n1));
    n1.note_id = (char *)"n1"; n1.reservation_id = (char *)"res1";
    n1.body = (char *)"late checkin"; n1.at_utc_ms = 100;
    ca_hospitality_note_t n2; memset(&n2, 0, sizeof(n2));
    n2.note_id = (char *)"n2"; n2.reservation_id = (char *)"res1";
    n2.body = (char *)"vip"; n2.at_utc_ms = 300;
    assert(ca_hospitality_board_add_note(b, &n1) == 0);
    assert(ca_hospitality_board_add_note(b, &n2) == 0);

    /* Newest-first: n2(300), n1(100). */
    size_t n = 0;
    ca_hospitality_note_t *ns = ca_hospitality_board_notes_for(b, "res1", &n);
    assert(n == 2 && strcmp(ns[0].note_id, "n2") == 0 && strcmp(ns[1].note_id, "n1") == 0);
    ca_hospitality_note_free_array(ns, n);

    ca_hospitality_board_destroy(b);
    printf("  notes: ok\n");
}

int main(void) {
    test_rooms_avail();
    test_notes();
    printf("test_hospitality: all assertions passed\n");
    return 0;
}
