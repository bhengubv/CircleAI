/*
 * test_parenting.c — CircleAI.Parenting (C11 port) verification against
 * ParentingPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL

static ca_par_child_t mk_child(const char *id, const char *name, int64_t dob,
                               const char *gender) {
    ca_par_child_t c; memset(&c, 0, sizeof(c));
    c.child_id = (char *)id; c.name = (char *)name; c.date_of_birth_ms = dob;
    if (gender) { c.has_gender = true; c.gender = (char *)gender; }
    return c;
}
static ca_par_milestone_t mk_ms(const char *id, const char *cid, int64_t at) {
    ca_par_milestone_t m; memset(&m, 0, sizeof(m));
    m.milestone_id = (char *)id; m.child_id = (char *)cid;
    m.category = (char *)"motor"; m.description = (char *)"walk"; m.achieved_at_utc_ms = at;
    return m;
}

static void test_children_milestones(void) {
    ca_par_board_t *b = ca_par_board_create();
    assert(b);

    ca_par_child_t c1 = mk_child("c1", "Zoe", 0, "F");
    ca_par_child_t c2 = mk_child("c2", "Amy", 0, NULL);
    assert(ca_par_board_add_child(b, &c1) == 0);
    assert(ca_par_board_add_child(b, &c2) == 0);

    ca_par_child_t got;
    assert(ca_par_board_get_child(b, "c1", &got) && got.has_gender &&
           strcmp(got.gender, "F") == 0);
    ca_par_child_free(&got);
    assert(ca_par_board_get_child(b, "c2", &got) && !got.has_gender);
    ca_par_child_free(&got);

    /* Children ordered by Name: Amy, Zoe. */
    size_t n = 0;
    ca_par_child_t *arr = ca_par_board_children(b, &n);
    assert(n == 2 && strcmp(arr[0].name, "Amy") == 0);
    ca_par_child_free_array(arr, n);

    /* RecordMilestone whitespace ChildId => 2. */
    ca_par_milestone_t bad = mk_ms("m0", "  ", 10);
    assert(ca_par_board_record_milestone(b, &bad) == 2);

    ca_par_milestone_t m1 = mk_ms("m1", "c1", 100);
    ca_par_milestone_t m2 = mk_ms("m2", "c1", 300); /* newest */
    ca_par_milestone_t m3 = mk_ms("m3", "c2", 200);
    assert(ca_par_board_record_milestone(b, &m1) == 0);
    assert(ca_par_board_record_milestone(b, &m2) == 0);
    assert(ca_par_board_record_milestone(b, &m3) == 0);

    /* MilestonesFor(c1) newest-first: m2(300), m1(100). */
    ca_par_milestone_t *ms = ca_par_board_milestones_for(b, "c1", &n);
    assert(n == 2);
    assert(strcmp(ms[0].milestone_id, "m2") == 0);
    assert(strcmp(ms[1].milestone_id, "m1") == 0);
    ca_par_milestone_free_array(ms, n);

    ms = ca_par_board_milestones_for(b, "zzz", &n);
    assert(n == 0 && ms == NULL);

    ca_par_board_destroy(b);
    printf("  children_milestones: ok\n");
}

static void test_routines_age(void) {
    ca_par_board_t *b = ca_par_board_create();
    ca_par_child_t c1 = mk_child("c1", "Zoe", 10 * DAY, "F");
    assert(ca_par_board_add_child(b, &c1) == 0);

    ca_par_routine_entry_t entries[2];
    memset(entries, 0, sizeof(entries));
    entries[0].time = (char *)"07:00"; entries[0].activity = (char *)"wake";
    entries[1].time = (char *)"08:00"; entries[1].activity = (char *)"school";
    ca_par_routine_t r; memset(&r, 0, sizeof(r));
    r.child_id = (char *)"c1"; r.day_of_week = CA_DOW_MONDAY;
    r.entries = entries; r.entry_count = 2;
    assert(ca_par_board_set_routine(b, &r) == 0);

    ca_par_routine_t got;
    assert(ca_par_board_get_routine(b, "c1", CA_DOW_MONDAY, &got));
    assert(got.entry_count == 2 && strcmp(got.entries[1].activity, "school") == 0);
    ca_par_routine_free(&got);
    /* different day => miss. */
    assert(!ca_par_board_get_routine(b, "c1", CA_DOW_TUESDAY, &got));

    /* SetRoutine same key replaces. */
    ca_par_routine_t r2; memset(&r2, 0, sizeof(r2));
    r2.child_id = (char *)"c1"; r2.day_of_week = CA_DOW_MONDAY;
    r2.entries = NULL; r2.entry_count = 0;
    assert(ca_par_board_set_routine(b, &r2) == 0);
    assert(ca_par_board_get_routine(b, "c1", CA_DOW_MONDAY, &got));
    assert(got.entry_count == 0);
    ca_par_routine_free(&got);

    /* AgeAsOf(c1, at=40 days) = 40-10 = 30 days ms. */
    int64_t span = 0;
    assert(ca_par_board_age_as_of(b, "c1", 40 * DAY, &span) == 0);
    assert(span == 30 * DAY);
    /* unknown child => 1. */
    assert(ca_par_board_age_as_of(b, "zzz", 40 * DAY, &span) == 1);

    ca_par_board_destroy(b);
    printf("  routines_age: ok\n");
}

int main(void) {
    test_children_milestones();
    test_routines_age();
    printf("test_parenting: all assertions passed\n");
    return 0;
}
