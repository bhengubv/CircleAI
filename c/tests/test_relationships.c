/*
 * test_relationships.c — CircleAI.Relationships (C11 port) verification against
 * RelationshipsPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL

static void test_contacts(void) {
    ca_relationships_board_t *b = ca_relationships_board_create();
    assert(b);
    assert(ca_relationships_board_add_contact(b, NULL) == -1);

    ca_relationships_contact_t c1; memset(&c1, 0, sizeof(c1));
    c1.contact_id = (char *)"c1"; c1.name = (char *)"Zoe"; c1.relationship = (char *)"friend";
    c1.has_notes = true; c1.notes = (char *)"met at work";
    ca_relationships_contact_t c2; memset(&c2, 0, sizeof(c2));
    c2.contact_id = (char *)"c2"; c2.name = (char *)"Amy"; c2.relationship = (char *)"family";
    c2.has_notes = false;
    assert(ca_relationships_board_add_contact(b, &c1) == 0);
    assert(ca_relationships_board_add_contact(b, &c2) == 0);

    ca_relationships_contact_t got;
    assert(ca_relationships_board_get_contact(b, "c1", &got) && got.has_notes &&
           strcmp(got.notes, "met at work") == 0);
    ca_relationships_contact_free(&got);

    /* Contacts ordered by Name: Amy, Zoe. */
    size_t n = 0;
    ca_relationships_contact_t *cs = ca_relationships_board_contacts(b, &n);
    assert(n == 2 && strcmp(cs[0].name, "Amy") == 0 && strcmp(cs[1].name, "Zoe") == 0);
    ca_relationships_contact_free_array(cs, n);

    ca_relationships_board_destroy(b);
    printf("  contacts: ok\n");
}

static void test_dates_touchpoints(void) {
    ca_relationships_board_t *b = ca_relationships_board_create();

    /* 2021-03-05 = day 18691 (March); 2021-03-20 = day 18706; 2021-04-10. */
    int64_t mar5  = 18691LL * DAY;
    int64_t mar20 = 18706LL * DAY;
    int64_t apr10 = 18727LL * DAY;

    ca_relationships_important_date_t d1; memset(&d1, 0, sizeof(d1));
    d1.date_id = (char *)"d1"; d1.contact_id = (char *)"c1"; d1.kind = (char *)"birthday";
    d1.date_ms = mar20;
    ca_relationships_important_date_t d2; memset(&d2, 0, sizeof(d2));
    d2.date_id = (char *)"d2"; d2.contact_id = (char *)"c2"; d2.kind = (char *)"anniversary";
    d2.date_ms = mar5;
    ca_relationships_important_date_t d3; memset(&d3, 0, sizeof(d3));
    d3.date_id = (char *)"d3"; d3.contact_id = (char *)"c3"; d3.kind = (char *)"birthday";
    d3.date_ms = apr10;
    assert(ca_relationships_board_add_important_date(b, &d1) == 0);
    assert(ca_relationships_board_add_important_date(b, &d2) == 0);
    assert(ca_relationships_board_add_important_date(b, &d3) == 0);

    /* UpcomingThisMonth(now in March): d2(day5), d1(day20) ordered by day; d3 April. */
    size_t n = 0;
    ca_relationships_important_date_t *up =
        ca_relationships_board_upcoming_this_month(b, mar20, &n);
    assert(n == 2 && strcmp(up[0].date_id, "d2") == 0 && strcmp(up[1].date_id, "d1") == 0);
    ca_relationships_important_date_free_array(up, n);

    /* Touchpoints. */
    ca_relationships_event_t e1; memset(&e1, 0, sizeof(e1));
    e1.contact_id = (char *)"c1"; e1.kind = (char *)"call"; e1.at_utc_ms = 100; e1.has_note = false;
    ca_relationships_event_t e2; memset(&e2, 0, sizeof(e2));
    e2.contact_id = (char *)"c1"; e2.kind = (char *)"text"; e2.at_utc_ms = 500;
    e2.has_note = true; e2.note = (char *)"quick chat";
    assert(ca_relationships_board_record_touchpoint(b, &e1) == 0);
    assert(ca_relationships_board_record_touchpoint(b, &e2) == 0);

    int64_t last = 0;
    assert(ca_relationships_board_last_contact(b, "c1", &last) && last == 500);
    assert(!ca_relationships_board_last_contact(b, "c2", &last));

    /* Contacts + NotContactedSince. */
    ca_relationships_contact_t c1; memset(&c1, 0, sizeof(c1));
    c1.contact_id = (char *)"c1"; c1.name = (char *)"A"; c1.relationship = (char *)"f";
    ca_relationships_contact_t c2; memset(&c2, 0, sizeof(c2));
    c2.contact_id = (char *)"c2"; c2.name = (char *)"B"; c2.relationship = (char *)"f";
    assert(ca_relationships_board_add_contact(b, &c1) == 0);
    assert(ca_relationships_board_add_contact(b, &c2) == 0);

    /* NotContactedSince(400): c1 last=500 >=400 -> excluded; c2 no events -> included. */
    ca_relationships_contact_t *nc = ca_relationships_board_not_contacted_since(b, 400, &n);
    assert(n == 1 && strcmp(nc[0].contact_id, "c2") == 0);
    ca_relationships_contact_free_array(nc, n);
    /* cutoff 600: c1 last=500 < 600 -> included; c2 included. */
    nc = ca_relationships_board_not_contacted_since(b, 600, &n);
    assert(n == 2);
    ca_relationships_contact_free_array(nc, n);

    ca_relationships_board_destroy(b);
    printf("  dates_touchpoints: ok\n");
}

int main(void) {
    test_contacts();
    test_dates_touchpoints();
    printf("test_relationships: all assertions passed\n");
    return 0;
}
