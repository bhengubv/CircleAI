/*
 * test_creative.c — CircleAI.Creative (C11 port) verification against
 * CreativePrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_works(void) {
    ca_creative_board_t *b = ca_creative_board_create();
    assert(b);
    assert(ca_creative_board_add_work(b, NULL) == -1);

    char *t1[] = { (char *)"abstract", (char *)"oil" };
    ca_creative_work_t w1; memset(&w1, 0, sizeof(w1));
    w1.work_id = (char *)"w1"; w1.title = (char *)"Sunrise"; w1.medium = (char *)"paint";
    w1.author = (char *)"Ann"; w1.created_utc_ms = 100; w1.tags = t1; w1.tag_count = 2;
    char *t2[] = { (char *)"digital" };
    ca_creative_work_t w2; memset(&w2, 0, sizeof(w2));
    w2.work_id = (char *)"w2"; w2.title = (char *)"City"; w2.medium = (char *)"vector";
    w2.author = (char *)"Bob"; w2.created_utc_ms = 200; w2.tags = t2; w2.tag_count = 1;
    assert(ca_creative_board_add_work(b, &w1) == 0);
    assert(ca_creative_board_add_work(b, &w2) == 0);

    ca_creative_work_t got;
    assert(ca_creative_board_get_work(b, "w1", &got) && got.tag_count == 2);
    ca_creative_work_free(&got);

    /* WorksByTag "OIL" (CI): w1. */
    size_t n = 0;
    ca_creative_work_t *ws = ca_creative_board_works_by_tag(b, "OIL", &n);
    assert(n == 1 && strcmp(ws[0].work_id, "w1") == 0);
    ca_creative_work_free_array(ws, n);

    ca_creative_board_destroy(b);
    printf("  works: ok\n");
}

static void test_inspiration_critique(void) {
    ca_creative_board_t *b = ca_creative_board_create();

    ca_creative_inspiration_t i1; memset(&i1, 0, sizeof(i1));
    i1.inspiration_id = (char *)"i1"; i1.prompt_text = (char *)"dawn"; i1.source_url = (char *)"u1";
    i1.seen_utc_ms = 100;
    ca_creative_inspiration_t i2; memset(&i2, 0, sizeof(i2));
    i2.inspiration_id = (char *)"i2"; i2.prompt_text = (char *)"dusk"; i2.source_url = (char *)"u2";
    i2.seen_utc_ms = 300;
    assert(ca_creative_board_record_inspiration(b, &i1) == 0);
    assert(ca_creative_board_record_inspiration(b, &i2) == 0);

    /* RecentInspiration newest-first: i2(300), i1(100). */
    size_t n = 0;
    ca_creative_inspiration_t *ins = ca_creative_board_recent_inspiration(b, 20, &n);
    assert(n == 2 && strcmp(ins[0].inspiration_id, "i2") == 0 &&
           strcmp(ins[1].inspiration_id, "i1") == 0);
    ca_creative_inspiration_free_array(ins, n);

    /* Critiques: w1 scores 8, 6 -> avg 7; w2 none -> 0. */
    ca_creative_critique_t c1; memset(&c1, 0, sizeof(c1));
    c1.critique_id = (char *)"c1"; c1.work_id = (char *)"w1"; c1.reviewer = (char *)"R1";
    c1.body = (char *)"good"; c1.score = 8;
    ca_creative_critique_t c2; memset(&c2, 0, sizeof(c2));
    c2.critique_id = (char *)"c2"; c2.work_id = (char *)"w1"; c2.reviewer = (char *)"R2";
    c2.body = (char *)"ok"; c2.score = 6;
    assert(ca_creative_board_add_critique(b, &c1) == 0);
    assert(ca_creative_board_add_critique(b, &c2) == 0);

    assert(fabs(ca_creative_board_avg_score(b, "w1") - 7.0) < 1e-9);
    assert(ca_creative_board_avg_score(b, "w2") == 0.0);

    ca_creative_board_destroy(b);
    printf("  inspiration_critique: ok\n");
}

int main(void) {
    test_works();
    test_inspiration_critique();
    printf("test_creative: all assertions passed\n");
    return 0;
}
