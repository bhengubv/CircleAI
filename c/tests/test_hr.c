/*
 * test_hr.c — CircleAI.HR (C11 port) verification against HRPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_hr_employee_t mk_emp(const char *id, const char *name) {
    ca_hr_employee_t e; memset(&e, 0, sizeof(e));
    e.employee_id = (char *)id; e.name = (char *)name; e.role = (char *)"eng";
    e.hired_on_ms = 0; e.salary = 100 * CA_HR_DECIMAL_SCALE; e.currency = (char *)"USD";
    return e;
}
static ca_hr_leave_t mk_leave(const char *id, const char *eid, const char *st) {
    ca_hr_leave_t r; memset(&r, 0, sizeof(r));
    r.request_id = (char *)id; r.employee_id = (char *)eid; r.kind = (char *)"annual";
    r.from_ms = 0; r.to_ms = 0; r.status = (char *)st;
    return r;
}
static ca_hr_review_t mk_review(const char *id, const char *eid, int rating) {
    ca_hr_review_t r; memset(&r, 0, sizeof(r));
    r.review_id = (char *)id; r.employee_id = (char *)eid; r.reviewed_on_ms = 0;
    r.rating_out_of_5 = rating; r.notes = (char *)"ok";
    return r;
}

static void test_employees(void) {
    ca_hr_board_t *b = ca_hr_board_create();
    assert(b);
    assert(ca_hr_board_hire(b, NULL) == -1);

    ca_hr_employee_t e1 = mk_emp("e1", "Charlie");
    ca_hr_employee_t e2 = mk_emp("e2", "Alice");
    ca_hr_employee_t e3 = mk_emp("e3", "Bob");
    assert(ca_hr_board_hire(b, &e1) == 0);
    assert(ca_hr_board_hire(b, &e2) == 0);
    assert(ca_hr_board_hire(b, &e3) == 0);

    ca_hr_employee_t got;
    assert(ca_hr_board_get_employee(b, "e2", &got) && strcmp(got.name, "Alice") == 0);
    assert(got.salary == 100 * CA_HR_DECIMAL_SCALE);
    ca_hr_employee_free(&got);
    assert(!ca_hr_board_get_employee(b, "nope", &got));

    /* Employees ordered by Name: Alice, Bob, Charlie. */
    size_t n = 0;
    ca_hr_employee_t *arr = ca_hr_board_employees(b, &n);
    assert(n == 3);
    assert(strcmp(arr[0].name, "Alice") == 0);
    assert(strcmp(arr[1].name, "Bob") == 0);
    assert(strcmp(arr[2].name, "Charlie") == 0);
    ca_hr_employee_free_array(arr, n);

    ca_hr_board_destroy(b);
    printf("  employees: ok\n");
}

static void test_leaves(void) {
    ca_hr_board_t *b = ca_hr_board_create();

    assert(ca_hr_board_decide_leave(b, "nope", "Approved") == 1);

    ca_hr_leave_t l1 = mk_leave("l1", "e1", "Pending");
    ca_hr_leave_t l2 = mk_leave("l2", "e1", "Approved");
    ca_hr_leave_t l3 = mk_leave("l3", "e2", "pending"); /* CI matches Pending */
    assert(ca_hr_board_request(b, &l1) == 0);
    assert(ca_hr_board_request(b, &l2) == 0);
    assert(ca_hr_board_request(b, &l3) == 0);

    size_t n = 0;
    ca_hr_leave_t *arr = ca_hr_board_pending_leaves(b, &n);
    assert(n == 2); /* l1 and l3 in insertion order */
    assert(strcmp(arr[0].request_id, "l1") == 0);
    assert(strcmp(arr[1].request_id, "l3") == 0);
    ca_hr_leave_free_array(arr, n);

    /* DecideLeave flips status; l1 no longer pending. */
    assert(ca_hr_board_decide_leave(b, "l1", "Approved") == 0);
    arr = ca_hr_board_pending_leaves(b, &n);
    assert(n == 1 && strcmp(arr[0].request_id, "l3") == 0);
    ca_hr_leave_free_array(arr, n);

    ca_hr_board_destroy(b);
    printf("  leaves: ok\n");
}

static void test_reviews(void) {
    ca_hr_board_t *b = ca_hr_board_create();

    /* AvgRatingFor with no reviews => 0.0 (DefaultIfEmpty(0).Average()). */
    assert(ca_hr_board_avg_rating_for(b, "e1") == 0.0);

    ca_hr_review_t r1 = mk_review("rv1", "e1", 4);
    ca_hr_review_t r2 = mk_review("rv2", "e1", 2);
    ca_hr_review_t r3 = mk_review("rv3", "e2", 5);
    assert(ca_hr_board_review(b, &r1) == 0);
    assert(ca_hr_board_review(b, &r2) == 0);
    assert(ca_hr_board_review(b, &r3) == 0);

    assert(ca_hr_board_avg_rating_for(b, "e1") == 3.0); /* (4+2)/2 */
    assert(ca_hr_board_avg_rating_for(b, "e2") == 5.0);
    assert(ca_hr_board_avg_rating_for(b, "zzz") == 0.0);

    ca_hr_board_destroy(b);
    printf("  reviews: ok\n");
}

int main(void) {
    test_employees();
    test_leaves();
    test_reviews();
    printf("test_hr: all assertions passed\n");
    return 0;
}
