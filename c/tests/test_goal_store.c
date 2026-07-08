/*
 * test_goal_store.c — CircleAI.Memory Goal + IGoalStore (C11 port).
 *
 * Verifies the rich Goal record (AdvanceProgress clamp) and InMemoryGoalStore
 * (List/Get/Upsert/Delete/GetActive) against Goal.cs + InMemoryGoalStore.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_goal_record_t mk_goal(const char *id, const char *user, const char *title,
                                ca_goal_status_t2 status, ca_goal_priority_t prio) {
    ca_goal_record_t g; memset(&g, 0, sizeof(g));
    g.id = strdup(id);
    g.user_id = strdup(user);
    g.title = strdup(title);
    g.description = strdup("desc");
    g.status = status;
    g.priority = prio;
    g.created_utc_ms = 1000;
    g.progress = 0.0f;
    return g;
}

static void test_advance(void) {
    ca_goal_record_t g = mk_goal("g1", "u1", "T", CA_GOAL_STATUS_ACTIVE, CA_GOAL_PRIORITY_NORMAL);
    g.progress = 0.75f;
    ca_goal_record_t out; memset(&out, 0, sizeof(out));
    ca_goal_record_advance_progress(&g, 0.25f, &out);
    assert(fabsf(out.progress - 1.0f) < 1e-6f);
    ca_goal_record_free(&out);
    /* clamp max */
    ca_goal_record_advance_progress(&g, 0.5f, &out);
    assert(fabsf(out.progress - 1.0f) < 1e-6f);
    ca_goal_record_free(&out);
    /* clamp min (negative) */
    g.progress = 0.1f;
    ca_goal_record_advance_progress(&g, -0.5f, &out);
    assert(fabsf(out.progress - 0.0f) < 1e-6f);
    /* original unchanged (record copy semantics) */
    assert(fabsf(g.progress - 0.1f) < 1e-6f);
    ca_goal_record_free(&out);
    ca_goal_record_free(&g);
    printf("  advance: ok\n");
}

static void test_store(void) {
    ca_goal_store_t *s = ca_goal_store_create();
    assert(s);

    /* blank user id → error sentinel */
    size_t n = 0;
    assert(ca_goal_store_list(s, "  ", &n) == NULL && n == SIZE_MAX);
    assert(ca_goal_store_list(s, "nobody", &n) == NULL && n == 0);

    ca_goal_record_t g1 = mk_goal("g1", "u1", "one", CA_GOAL_STATUS_ACTIVE, CA_GOAL_PRIORITY_HIGH);
    ca_goal_record_t g2 = mk_goal("g2", "u1", "two", CA_GOAL_STATUS_COMPLETED, CA_GOAL_PRIORITY_LOW);
    ca_goal_record_t g3 = mk_goal("g3", "u2", "three", CA_GOAL_STATUS_ACTIVE, CA_GOAL_PRIORITY_NORMAL);
    assert(ca_goal_store_upsert(s, &g1));
    assert(ca_goal_store_upsert(s, &g2));
    assert(ca_goal_store_upsert(s, &g3));

    /* upsert rejects blank id / NULL */
    ca_goal_record_t bad = mk_goal("", "u1", "x", CA_GOAL_STATUS_ACTIVE, CA_GOAL_PRIORITY_LOW);
    assert(ca_goal_store_upsert(s, &bad) == false);
    ca_goal_record_free(&bad);
    assert(ca_goal_store_upsert(s, NULL) == false);

    /* list by user */
    ca_goal_record_t *list = ca_goal_store_list(s, "u1", &n);
    assert(n == 2);
    ca_goal_record_free_array(list, n);
    list = ca_goal_store_list(s, "u2", &n);
    assert(n == 1 && strcmp(list[0].id, "g3") == 0);
    ca_goal_record_free_array(list, n);

    /* get by id */
    ca_goal_record_t got; memset(&got, 0, sizeof(got));
    assert(ca_goal_store_get(s, "g2", &got));
    assert(strcmp(got.title, "two") == 0 && got.status == CA_GOAL_STATUS_COMPLETED);
    assert(got.priority == CA_GOAL_PRIORITY_LOW);
    ca_goal_record_free(&got);
    assert(ca_goal_store_get(s, "missing", &got) == false);
    assert(ca_goal_store_get(s, "  ", &got) == false);

    /* active only */
    ca_goal_record_t *active = ca_goal_store_get_active(s, "u1", &n);
    assert(n == 1 && strcmp(active[0].id, "g1") == 0);   /* g2 is completed */
    ca_goal_record_free_array(active, n);

    /* upsert replaces by id */
    ca_goal_record_t g1b = mk_goal("g1", "u1", "one-updated", CA_GOAL_STATUS_ABANDONED, CA_GOAL_PRIORITY_LOW);
    g1b.has_due_utc = true; g1b.due_utc_ms = 555;
    g1b.notes = strdup("note");
    assert(ca_goal_store_upsert(s, &g1b));
    ca_goal_record_free(&g1b);
    assert(ca_goal_store_get(s, "g1", &got));
    assert(strcmp(got.title, "one-updated") == 0);
    assert(got.status == CA_GOAL_STATUS_ABANDONED);
    assert(got.has_due_utc && got.due_utc_ms == 555);
    assert(got.notes && strcmp(got.notes, "note") == 0);
    ca_goal_record_free(&got);
    /* u1 still has 2 goals (replaced, not added) */
    list = ca_goal_store_list(s, "u1", &n);
    assert(n == 2);
    ca_goal_record_free_array(list, n);
    /* g1 no longer active */
    active = ca_goal_store_get_active(s, "u1", &n);
    assert(n == 0 && active == NULL);

    /* delete */
    assert(ca_goal_store_delete(s, "g2"));
    assert(ca_goal_store_get(s, "g2", &got) == false);
    assert(ca_goal_store_delete(s, "nonexistent"));   /* no-op returns true */
    assert(ca_goal_store_delete(s, "  ") == false);   /* blank → false */
    list = ca_goal_store_list(s, "u1", &n);
    assert(n == 1);   /* only g1 remains for u1 */
    ca_goal_record_free_array(list, n);

    ca_goal_record_free(&g1); ca_goal_record_free(&g2); ca_goal_record_free(&g3);
    ca_goal_store_destroy(s);
    printf("  store: ok\n");
}

int main(void) {
    test_advance();
    test_store();
    printf("test_goal_store: all assertions passed\n");
    return 0;
}
