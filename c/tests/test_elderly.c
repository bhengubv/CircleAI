/*
 * test_elderly.c — CircleAI.Elderly (C11 port) verification against
 * ElderlyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_plans(void) {
    ca_eld_board_t *b = ca_eld_board_create();
    assert(b);
    assert(ca_eld_board_set_plan(b, NULL) == -1);

    char *conds[] = { (char *)"diabetes", (char *)"hypertension" };
    char *allergies[] = { (char *)"penicillin" };
    ca_eld_care_plan_t p; memset(&p, 0, sizeof(p));
    p.plan_id = (char *)"plan1"; p.resident_name = (char *)"Gran";
    p.medical_conditions = conds; p.medical_condition_count = 2;
    p.allergies = allergies; p.allergy_count = 1;
    p.carer_notes = (char *)"gentle";
    assert(ca_eld_board_set_plan(b, &p) == 0);

    ca_eld_care_plan_t got;
    assert(ca_eld_board_get_plan(b, "Gran", &got));
    assert(got.medical_condition_count == 2 && strcmp(got.medical_conditions[1], "hypertension") == 0);
    assert(got.allergy_count == 1 && strcmp(got.allergies[0], "penicillin") == 0);
    assert(strcmp(got.carer_notes, "gentle") == 0);
    ca_eld_care_plan_free(&got);
    assert(!ca_eld_board_get_plan(b, "nope", &got));

    ca_eld_board_destroy(b);
    printf("  plans: ok\n");
}

static ca_eld_reminder_t mk_rem(const char *id, const char *res, bool active) {
    ca_eld_reminder_t r; memset(&r, 0, sizeof(r));
    r.reminder_id = (char *)id; r.resident_name = (char *)res;
    r.medication = (char *)"pill"; r.daily_at_ms = 8 * 3600000LL; r.active = active;
    return r;
}

static void test_reminders(void) {
    ca_eld_board_t *b = ca_eld_board_create();

    assert(ca_eld_board_deactivate_reminder(b, "nope") == 1);

    ca_eld_reminder_t r1 = mk_rem("r1", "Gran", true);
    ca_eld_reminder_t r2 = mk_rem("r2", "Gran", false);
    ca_eld_reminder_t r3 = mk_rem("r3", "Gramps", true);
    assert(ca_eld_board_add_reminder(b, &r1) == 0);
    assert(ca_eld_board_add_reminder(b, &r2) == 0);
    assert(ca_eld_board_add_reminder(b, &r3) == 0);

    /* ActiveRemindersFor(Gran): only r1. */
    size_t n = 0;
    ca_eld_reminder_t *arr = ca_eld_board_active_reminders_for(b, "Gran", &n);
    assert(n == 1 && strcmp(arr[0].reminder_id, "r1") == 0);
    assert(arr[0].daily_at_ms == 8 * 3600000LL);
    ca_eld_reminder_free_array(arr, n);

    /* Deactivate r1 => none active for Gran. */
    assert(ca_eld_board_deactivate_reminder(b, "r1") == 0);
    arr = ca_eld_board_active_reminders_for(b, "Gran", &n);
    assert(n == 0 && arr == NULL);

    ca_eld_board_destroy(b);
    printf("  reminders: ok\n");
}

static ca_eld_check_in_t mk_ci(const char *id, const char *res, int64_t at,
                               const char *note) {
    ca_eld_check_in_t c; memset(&c, 0, sizeof(c));
    c.check_in_id = (char *)id; c.resident_name = (char *)res; c.at_utc_ms = at;
    c.status = (char *)"ok";
    if (note) { c.has_note = true; c.note = (char *)note; }
    return c;
}

static void test_check_ins(void) {
    ca_eld_board_t *b = ca_eld_board_create();

    /* No check-in => LatestCheckIn false, MissedCheckIn true. */
    ca_eld_check_in_t got;
    assert(!ca_eld_board_latest_check_in(b, "Gran", &got));
    assert(ca_eld_board_missed_check_in(b, "Gran", 100));

    ca_eld_check_in_t c1 = mk_ci("c1", "Gran", 100, NULL);
    ca_eld_check_in_t c2 = mk_ci("c2", "Gran", 300, "fine"); /* newest */
    ca_eld_check_in_t c3 = mk_ci("c3", "Gramps", 200, NULL);
    assert(ca_eld_board_record_check_in(b, &c1) == 0);
    assert(ca_eld_board_record_check_in(b, &c2) == 0);
    assert(ca_eld_board_record_check_in(b, &c3) == 0);

    /* LatestCheckIn(Gran) => c2 (newest), with note. */
    assert(ca_eld_board_latest_check_in(b, "Gran", &got));
    assert(strcmp(got.check_in_id, "c2") == 0 && got.has_note &&
           strcmp(got.note, "fine") == 0);
    ca_eld_check_in_free(&got);

    /* MissedCheckIn: latest.AtUtc(300) < since? */
    assert(!ca_eld_board_missed_check_in(b, "Gran", 300)); /* 300 < 300 false */
    assert(ca_eld_board_missed_check_in(b, "Gran", 301));  /* 300 < 301 true */

    ca_eld_board_destroy(b);
    printf("  check_ins: ok\n");
}

int main(void) {
    test_plans();
    test_reminders();
    test_check_ins();
    printf("test_elderly: all assertions passed\n");
    return 0;
}
