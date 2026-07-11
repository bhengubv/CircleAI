/*
 * test_kids.c — CircleAI.Kids (C11 port) verification against KidsPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL
#define TICKS_PER_MIN (60LL * 10000000LL)

static void test_content(void) {
    ca_kids_board_t *b = ca_kids_board_create();
    assert(b);
    assert(ca_kids_board_add_content(b, NULL) == -1);

    ca_kids_content_t c1; memset(&c1, 0, sizeof(c1));
    c1.content_id = (char *)"c1"; c1.title = (char *)"Zebra"; c1.age_band = CA_AGE_TODDLER;
    c1.kind = (char *)"video";
    ca_kids_content_t c2; memset(&c2, 0, sizeof(c2));
    c2.content_id = (char *)"c2"; c2.title = (char *)"Apple"; c2.age_band = CA_AGE_TODDLER;
    c2.kind = (char *)"book";
    ca_kids_content_t c3; memset(&c3, 0, sizeof(c3));
    c3.content_id = (char *)"c3"; c3.title = (char *)"Teen Drama"; c3.age_band = CA_AGE_TEEN;
    c3.kind = (char *)"video";
    assert(ca_kids_board_add_content(b, &c1) == 0);
    assert(ca_kids_board_add_content(b, &c2) == 0);
    assert(ca_kids_board_add_content(b, &c3) == 0);

    /* ContentFor Toddler ordered by Title: Apple, Zebra. */
    size_t n = 0;
    ca_kids_content_t *cs = ca_kids_board_content_for(b, CA_AGE_TODDLER, &n);
    assert(n == 2 && strcmp(cs[0].title, "Apple") == 0 && strcmp(cs[1].title, "Zebra") == 0);
    ca_kids_content_free_array(cs, n);

    ca_kids_board_destroy(b);
    printf("  content: ok\n");
}

static void test_limits(void) {
    ca_kids_board_t *b = ca_kids_board_create();

    ca_kids_daily_time_t d; memset(&d, 0, sizeof(d));
    d.kid_name = (char *)"Sam"; d.screen_limit_ticks = 60 * TICKS_PER_MIN;
    d.reading_limit_ticks = 30 * TICKS_PER_MIN;
    assert(ca_kids_board_set_limits(b, &d) == 0);

    ca_kids_daily_time_t got;
    assert(ca_kids_board_limits_for(b, "Sam", &got) &&
           got.screen_limit_ticks == 60 * TICKS_PER_MIN);
    ca_kids_daily_time_free(&got);
    assert(!ca_kids_board_limits_for(b, "Nobody", &got));

    /* Logs on day 100: screen 40+30=70 min, reading 10 min. */
    int64_t today = 100 * DAY + 3600000LL;
    int64_t other = 99 * DAY;
    ca_kids_time_log_t l1; memset(&l1, 0, sizeof(l1));
    l1.kid_name = (char *)"Sam"; l1.kind = (char *)"screen"; l1.duration_ticks = 40 * TICKS_PER_MIN;
    l1.at_utc_ms = today;
    ca_kids_time_log_t l2; memset(&l2, 0, sizeof(l2));
    l2.kid_name = (char *)"Sam"; l2.kind = (char *)"screen"; l2.duration_ticks = 30 * TICKS_PER_MIN;
    l2.at_utc_ms = today + 60000;
    ca_kids_time_log_t l3; memset(&l3, 0, sizeof(l3));
    l3.kid_name = (char *)"Sam"; l3.kind = (char *)"reading"; l3.duration_ticks = 10 * TICKS_PER_MIN;
    l3.at_utc_ms = today;
    ca_kids_time_log_t l4; memset(&l4, 0, sizeof(l4)); /* yesterday, excluded */
    l4.kid_name = (char *)"Sam"; l4.kind = (char *)"screen"; l4.duration_ticks = 999 * TICKS_PER_MIN;
    l4.at_utc_ms = other;
    assert(ca_kids_board_record_time(b, &l1) == 0);
    assert(ca_kids_board_record_time(b, &l2) == 0);
    assert(ca_kids_board_record_time(b, &l3) == 0);
    assert(ca_kids_board_record_time(b, &l4) == 0);

    /* UsedToday screen = 70 min. */
    assert(ca_kids_board_used_today(b, "Sam", "screen", today) == 70 * TICKS_PER_MIN);
    assert(ca_kids_board_used_today(b, "Sam", "reading", today) == 10 * TICKS_PER_MIN);

    /* OverLimit screen: 70 > 60 -> true. reading: 10 > 30 -> false. */
    assert(ca_kids_board_over_limit(b, "Sam", "screen", today) == true);
    assert(ca_kids_board_over_limit(b, "Sam", "reading", today) == false);
    /* unknown kind -> cap MaxValue -> false. */
    assert(ca_kids_board_over_limit(b, "Sam", "gaming", today) == false);
    /* no limits set -> false. */
    assert(ca_kids_board_over_limit(b, "Nobody", "screen", today) == false);

    ca_kids_board_destroy(b);
    printf("  limits: ok\n");
}

int main(void) {
    test_content();
    test_limits();
    printf("test_kids: all assertions passed\n");
    return 0;
}
