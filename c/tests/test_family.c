/*
 * test_family.c — CircleAI.Family (C11 port) verification against
 * FamilyPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_fam_member_t mk_member(const char *id, const char *name) {
    ca_fam_member_t m; memset(&m, 0, sizeof(m));
    m.member_id = (char *)id; m.name = (char *)name; m.role = (char *)"parent";
    m.date_of_birth_ms = 0;
    return m;
}
static ca_fam_expense_t mk_exp(const char *id, const char *payer, int64_t amt,
                               const char *cat, int64_t at) {
    ca_fam_expense_t e; memset(&e, 0, sizeof(e));
    e.expense_id = (char *)id; e.paid_by_id = (char *)payer; e.amount = amt;
    e.currency = (char *)"USD"; e.category = (char *)cat; e.at_utc_ms = at;
    return e;
}

static void test_members_events(void) {
    ca_fam_board_t *b = ca_fam_board_create();
    assert(b);
    assert(ca_fam_board_add(b, NULL) == -1);

    ca_fam_member_t m1 = mk_member("m1", "Charlie");
    ca_fam_member_t m2 = mk_member("m2", "Alice");
    assert(ca_fam_board_add(b, &m1) == 0);
    assert(ca_fam_board_add(b, &m2) == 0);

    ca_fam_member_t got;
    assert(ca_fam_board_get_member(b, "m1", &got) && strcmp(got.name, "Charlie") == 0);
    ca_fam_member_free(&got);

    /* Members ordered by Name: Alice, Charlie. */
    size_t n = 0;
    ca_fam_member_t *arr = ca_fam_board_members(b, &n);
    assert(n == 2 && strcmp(arr[0].name, "Alice") == 0);
    ca_fam_member_free_array(arr, n);

    /* Events. */
    char *ids_a[] = { (char *)"m1", (char *)"m2" };
    char *ids_b[] = { (char *)"m2" };
    ca_fam_event_t e1; memset(&e1, 0, sizeof(e1));
    e1.event_id = (char *)"e1"; e1.title = (char *)"Dinner"; e1.at_utc_ms = 300;
    e1.member_ids = ids_a; e1.member_id_count = 2;
    ca_fam_event_t e2; memset(&e2, 0, sizeof(e2));
    e2.event_id = (char *)"e2"; e2.title = (char *)"Movie"; e2.at_utc_ms = 100;
    e2.member_ids = ids_b; e2.member_id_count = 1;
    assert(ca_fam_board_schedule(b, &e1) == 0);
    assert(ca_fam_board_schedule(b, &e2) == 0);

    /* EventsForMember(m2): both, ordered by AtUtc asc => e2(100), e1(300). */
    ca_fam_event_t *evs = ca_fam_board_events_for_member(b, "m2", &n);
    assert(n == 2);
    assert(strcmp(evs[0].event_id, "e2") == 0);
    assert(strcmp(evs[1].event_id, "e1") == 0);
    ca_fam_event_free_array(evs, n);

    /* EventsForMember(m1): only e1. */
    evs = ca_fam_board_events_for_member(b, "m1", &n);
    assert(n == 1 && strcmp(evs[0].event_id, "e1") == 0);
    assert(evs[0].member_id_count == 2);
    ca_fam_event_free_array(evs, n);

    ca_fam_board_destroy(b);
    printf("  members_events: ok\n");
}

static void test_expenses(void) {
    ca_fam_board_t *b = ca_fam_board_create();

    ca_fam_expense_t e1 = mk_exp("x1", "m1", 100 * CA_FAM_DECIMAL_SCALE, "food", 100);
    ca_fam_expense_t e2 = mk_exp("x2", "m1", 50 * CA_FAM_DECIMAL_SCALE, "fuel", 300);
    ca_fam_expense_t e3 = mk_exp("x3", "m2", 25 * CA_FAM_DECIMAL_SCALE, "FOOD", 200);
    ca_fam_expense_t e4 = mk_exp("x4", "m1", 999 * CA_FAM_DECIMAL_SCALE, "food", 50); /* before since */
    assert(ca_fam_board_record(b, &e1) == 0);
    assert(ca_fam_board_record(b, &e2) == 0);
    assert(ca_fam_board_record(b, &e3) == 0);
    assert(ca_fam_board_record(b, &e4) == 0);

    /* TotalPaidBy(m1, since=100): e1(100)+e2(50); e4 excluded (at 50 < 100). */
    assert(ca_fam_board_total_paid_by(b, "m1", 100) == 150 * CA_FAM_DECIMAL_SCALE);

    /* SpendByCategory("food" CI, since=100): e1(100 food)+e3(25 FOOD) = 125. */
    assert(ca_fam_board_spend_by_category(b, "food", 100) == 125 * CA_FAM_DECIMAL_SCALE);

    /* since past everything => 0. */
    assert(ca_fam_board_total_paid_by(b, "m1", 10000) == 0);

    ca_fam_board_destroy(b);
    printf("  expenses: ok\n");
}

int main(void) {
    test_members_events();
    test_expenses();
    printf("test_family: all assertions passed\n");
    return 0;
}
