/*
 * test_civic.c — CircleAI.Civic (C11 port) verification against CivicPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_issues(void) {
    ca_civic_board_t *b = ca_civic_board_create();
    assert(b);
    assert(ca_civic_board_report(b, NULL) == -1);

    ca_civic_issue_t i1; memset(&i1, 0, sizeof(i1));
    i1.issue_id = (char *)"i1"; i1.category = (char *)"pothole";
    i1.description = (char *)"big hole"; i1.reported_utc_ms = 100; i1.status = (char *)"Open";
    ca_civic_issue_t i2; memset(&i2, 0, sizeof(i2));
    i2.issue_id = (char *)"i2"; i2.category = (char *)"light";
    i2.description = (char *)"broken"; i2.reported_utc_ms = 200; i2.status = (char *)"Open";
    assert(ca_civic_board_report(b, &i1) == 0);
    assert(ca_civic_board_report(b, &i2) == 0);

    assert(ca_civic_board_resolve(b, "nope", "Resolved") == -2);
    assert(ca_civic_board_resolve(b, "i1", "Resolved") == 0);

    /* OpenIssues: i2 only (i1 resolved, CI). */
    size_t n = 0;
    ca_civic_issue_t *op = ca_civic_board_open_issues(b, &n);
    assert(n == 1 && strcmp(op[0].issue_id, "i2") == 0);
    ca_civic_issue_free_array(op, n);

    ca_civic_board_destroy(b);
    printf("  issues: ok\n");
}

static void test_reps_events(void) {
    ca_civic_board_t *b = ca_civic_board_create();

    ca_civic_rep_t r1; memset(&r1, 0, sizeof(r1));
    r1.rep_id = (char *)"r1"; r1.name = (char *)"Alice"; r1.office = (char *)"Mayor";
    r1.contact_email = (char *)"a@x.gov"; r1.has_district = true; r1.district = (char *)"North";
    ca_civic_rep_t r2; memset(&r2, 0, sizeof(r2));
    r2.rep_id = (char *)"r2"; r2.name = (char *)"Bob"; r2.office = (char *)"Council";
    r2.contact_email = (char *)"b@x.gov"; r2.has_district = false;
    assert(ca_civic_board_add_rep(b, &r1) == 0);
    assert(ca_civic_board_add_rep(b, &r2) == 0);

    /* RepsForDistrict "north" (CI): r1 only. */
    size_t n = 0;
    ca_civic_rep_t *rs = ca_civic_board_reps_for_district(b, "north", &n);
    assert(n == 1 && strcmp(rs[0].rep_id, "r1") == 0);
    ca_civic_rep_free_array(rs, n);

    /* Events. */
    ca_civic_event_t e1; memset(&e1, 0, sizeof(e1));
    e1.event_id = (char *)"e1"; e1.title = (char *)"Townhall"; e1.at_utc_ms = 500;
    e1.location = (char *)"Hall"; e1.audience = (char *)"All";
    ca_civic_event_t e2; memset(&e2, 0, sizeof(e2));
    e2.event_id = (char *)"e2"; e2.title = (char *)"Cleanup"; e2.at_utc_ms = 300;
    e2.location = (char *)"Park"; e2.audience = (char *)"Volunteers";
    ca_civic_event_t e3; memset(&e3, 0, sizeof(e3));
    e3.event_id = (char *)"e3"; e3.title = (char *)"Past"; e3.at_utc_ms = 50;
    e3.location = (char *)"X"; e3.audience = (char *)"Y";
    assert(ca_civic_board_schedule(b, &e1) == 0);
    assert(ca_civic_board_schedule(b, &e2) == 0);
    assert(ca_civic_board_schedule(b, &e3) == 0);

    /* UpcomingEvents(now=100): e2(300), e1(500); e3 past. */
    ca_civic_event_t *ev = ca_civic_board_upcoming_events(b, 100, &n);
    assert(n == 2 && strcmp(ev[0].event_id, "e2") == 0 && strcmp(ev[1].event_id, "e1") == 0);
    ca_civic_event_free_array(ev, n);

    ca_civic_board_destroy(b);
    printf("  reps_events: ok\n");
}

int main(void) {
    test_issues();
    test_reps_events();
    printf("test_civic: all assertions passed\n");
    return 0;
}
