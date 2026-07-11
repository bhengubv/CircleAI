/*
 * test_construction.c — CircleAI.Construction (C11 port) verification against
 * ConstructionPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define S CA_CONSTRUCTION_DECIMAL_SCALE

static void test_construction(void) {
    ca_construction_board_t *b = ca_construction_board_create();
    assert(b);
    assert(ca_construction_board_create_project(b, NULL) == -1);

    ca_construction_project_t p1; memset(&p1, 0, sizeof(p1));
    p1.project_id = (char *)"p1"; p1.name = (char *)"Bridge"; p1.start_on_ms = 0;
    p1.has_end_on = true; p1.end_on_ms = 1000; p1.budget = 10000 * S; p1.currency = (char *)"USD";
    assert(ca_construction_board_create_project(b, &p1) == 0);

    ca_construction_project_t got;
    assert(ca_construction_board_get_project(b, "p1", &got) &&
           got.budget == 10000 * S && got.has_end_on && got.end_on_ms == 1000);
    ca_construction_project_free(&got);

    /* Tasks. */
    ca_construction_task_t t1; memset(&t1, 0, sizeof(t1));
    t1.task_id = (char *)"t1"; t1.project_id = (char *)"p1"; t1.description = (char *)"pour";
    t1.due_on_ms = 500; t1.completed = false;
    ca_construction_task_t t2; memset(&t2, 0, sizeof(t2));
    t2.task_id = (char *)"t2"; t2.project_id = (char *)"p1"; t2.description = (char *)"survey";
    t2.due_on_ms = 100; t2.completed = false;
    assert(ca_construction_board_add_task(b, &t1) == 0);
    assert(ca_construction_board_add_task(b, &t2) == 0);

    /* OpenTasks ordered by DueOn asc: t2(100), t1(500). */
    size_t n = 0;
    ca_construction_task_t *ts = ca_construction_board_open_tasks_for(b, "p1", &n);
    assert(n == 2 && strcmp(ts[0].task_id, "t2") == 0 && strcmp(ts[1].task_id, "t1") == 0);
    ca_construction_task_free_array(ts, n);

    /* Complete t2. */
    assert(ca_construction_board_complete(b, "nope") == -2);
    assert(ca_construction_board_complete(b, "t2") == 0);
    ts = ca_construction_board_open_tasks_for(b, "p1", &n);
    assert(n == 1 && strcmp(ts[0].task_id, "t1") == 0);
    ca_construction_task_free_array(ts, n);

    /* Costs. */
    ca_construction_cost_t c1; memset(&c1, 0, sizeof(c1));
    c1.entry_id = (char *)"c1"; c1.project_id = (char *)"p1"; c1.category = (char *)"steel";
    c1.amount = 3000 * S; c1.at_utc_ms = 10;
    ca_construction_cost_t c2; memset(&c2, 0, sizeof(c2));
    c2.entry_id = (char *)"c2"; c2.project_id = (char *)"p1"; c2.category = (char *)"labor";
    c2.amount = 2000 * S; c2.at_utc_ms = 20;
    assert(ca_construction_board_record_cost(b, &c1) == 0);
    assert(ca_construction_board_record_cost(b, &c2) == 0);

    /* SpendFor = 5000; RemainingBudget = 10000 - 5000 = 5000. */
    assert(ca_construction_board_spend_for(b, "p1") == 5000 * S);
    ca_construction_decimal_t rem = 0;
    assert(ca_construction_board_remaining_budget(b, "p1", &rem) == 0 && rem == 5000 * S);
    assert(ca_construction_board_remaining_budget(b, "nope", &rem) == -2);

    ca_construction_board_destroy(b);
    printf("  construction: ok\n");
}

int main(void) {
    test_construction();
    printf("test_construction: all assertions passed\n");
    return 0;
}
