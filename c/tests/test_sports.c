/*
 * test_sports.c — CircleAI.Sports (C11 port) verification against
 * SportsPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL
#define TICKS_PER_SEC 10000000LL

static ca_sports_activity_t mk_act(const char *id, const char *uid,
                                   ca_distance_kind_t k, double km,
                                   int64_t dur_ticks, int64_t at) {
    ca_sports_activity_t a; memset(&a, 0, sizeof(a));
    a.activity_id = (char *)id; a.user_id = (char *)uid; a.kind = k;
    a.distance_km = km; a.duration_ticks = dur_ticks; a.at_utc_ms = at;
    return a;
}

static void test_history_week(void) {
    ca_sports_board_t *b = ca_sports_board_create();
    assert(b);
    assert(ca_sports_board_log(b, NULL) == -1);

    /* Wednesday 2021-01-06 is day 18633; DayOfWeek(Wed)=3. Week-start = Sunday
     * 2021-01-03 = day 18630. */
    int64_t wed = 18633LL * DAY + 12 * 3600000LL;      /* Wed noon */
    int64_t sun = 18630LL * DAY;                        /* start of week */
    int64_t prev_sat = 18629LL * DAY;                  /* previous week */

    ca_sports_activity_t a1 = mk_act("a1", "u1", CA_DISTANCE_KIND_RUN, 5.0,
                                     30 * 60 * TICKS_PER_SEC, wed);
    ca_sports_activity_t a2 = mk_act("a2", "u1", CA_DISTANCE_KIND_RUN, 3.0,
                                     20 * 60 * TICKS_PER_SEC, sun);
    ca_sports_activity_t a3 = mk_act("a3", "u1", CA_DISTANCE_KIND_RUN, 10.0,
                                     70 * 60 * TICKS_PER_SEC, prev_sat);
    ca_sports_activity_t a4 = mk_act("a4", "u1", CA_DISTANCE_KIND_BIKE, 40.0,
                                     60 * 60 * TICKS_PER_SEC, wed);
    assert(ca_sports_board_log(b, &a1) == 0);
    assert(ca_sports_board_log(b, &a2) == 0);
    assert(ca_sports_board_log(b, &a3) == 0);
    assert(ca_sports_board_log(b, &a4) == 0);

    /* This week's RUN km: a1(5)+a2(3)=8; a3 in previous week excluded. */
    double km = ca_sports_board_total_km_this_week(b, "u1", CA_DISTANCE_KIND_RUN, wed);
    assert(km == 8.0);
    /* BIKE this week: 40. */
    assert(ca_sports_board_total_km_this_week(b, "u1", CA_DISTANCE_KIND_BIKE, wed) == 40.0);

    /* History newest-first by AtUtc: a1(wed) > a2(sun) > a4(wed?) — a1 and a4
     * both at wed; ties keep source order (a1 before a4). Filter RUN via limit. */
    size_t n = 0;
    ca_sports_activity_t *h = ca_sports_board_history(b, "u1", 50, &n);
    assert(n == 4);
    /* Two at wed (a1,a4 source order), then a2(sun), then a3(prev_sat). */
    assert(strcmp(h[0].activity_id, "a1") == 0);
    assert(strcmp(h[1].activity_id, "a4") == 0);
    assert(strcmp(h[2].activity_id, "a2") == 0);
    assert(strcmp(h[3].activity_id, "a3") == 0);
    ca_sports_activity_free_array(h, n);

    /* limit clamps. */
    h = ca_sports_board_history(b, "u1", 2, &n);
    assert(n == 2);
    ca_sports_activity_free_array(h, n);
    assert(ca_sports_board_history(b, "u1", 0, &n) == NULL && n == (size_t)-1);

    ca_sports_board_destroy(b);
    printf("  history_week: ok\n");
}

static void test_best(void) {
    ca_sports_board_t *b = ca_sports_board_create();
    /* Two 5km runs; fastest wins. */
    ca_sports_activity_t a1 = mk_act("a1", "u1", CA_DISTANCE_KIND_RUN, 5.0,
                                     30 * 60 * TICKS_PER_SEC, 100);
    ca_sports_activity_t a2 = mk_act("a2", "u1", CA_DISTANCE_KIND_RUN, 6.0,
                                     25 * 60 * TICKS_PER_SEC, 200);
    ca_sports_activity_t a3 = mk_act("a3", "u1", CA_DISTANCE_KIND_RUN, 2.0,
                                     10 * 60 * TICKS_PER_SEC, 300); /* too short */
    assert(ca_sports_board_log(b, &a1) == 0);
    assert(ca_sports_board_log(b, &a2) == 0);
    assert(ca_sports_board_log(b, &a3) == 0);

    ca_sports_personal_best_t pb;
    /* Best 5km: a2 (25 min) beats a1 (30 min); a3 too short. */
    assert(ca_sports_board_best(b, "u1", CA_DISTANCE_KIND_RUN, 5.0, &pb));
    assert(pb.distance_km == 5.0); /* query distance, not hit's 6.0 */
    assert(pb.time_ticks == 25 * 60 * TICKS_PER_SEC);
    assert(pb.achieved_utc_ms == 200);
    ca_sports_personal_best_free(&pb);

    /* No 100km run. */
    assert(!ca_sports_board_best(b, "u1", CA_DISTANCE_KIND_RUN, 100.0, &pb));

    ca_sports_board_destroy(b);
    printf("  best: ok\n");
}

static void test_sessions(void) {
    ca_sports_board_t *b = ca_sports_board_create();

    ca_sports_session_t s1; memset(&s1, 0, sizeof(s1));
    s1.session_id = (char *)"s1"; s1.user_id = (char *)"u1"; s1.plan = (char *)"5x400";
    s1.scheduled_utc_ms = 500; s1.completed = false;
    ca_sports_session_t s2; memset(&s2, 0, sizeof(s2));
    s2.session_id = (char *)"s2"; s2.user_id = (char *)"u1"; s2.plan = (char *)"long run";
    s2.scheduled_utc_ms = 300; s2.completed = false;
    ca_sports_session_t s3; memset(&s3, 0, sizeof(s3));
    s3.session_id = (char *)"s3"; s3.user_id = (char *)"u1"; s3.plan = (char *)"past";
    s3.scheduled_utc_ms = 50; s3.completed = false;
    assert(ca_sports_board_schedule(b, &s1) == 0);
    assert(ca_sports_board_schedule(b, &s2) == 0);
    assert(ca_sports_board_schedule(b, &s3) == 0);

    assert(ca_sports_board_complete(b, "nope") == -2);
    assert(ca_sports_board_complete(b, "s2") == 0);

    /* Upcoming(now=100): s1(500) only [s2 completed, s3 past]. */
    size_t n = 0;
    ca_sports_session_t *up = ca_sports_board_upcoming(b, "u1", 100, &n);
    assert(n == 1 && strcmp(up[0].session_id, "s1") == 0);
    ca_sports_session_free_array(up, n);

    /* Uncomplete s2 by re-scheduling; now two upcoming ordered by time. */
    s2.completed = false;
    assert(ca_sports_board_schedule(b, &s2) == 0);
    up = ca_sports_board_upcoming(b, "u1", 100, &n);
    assert(n == 2 && strcmp(up[0].session_id, "s2") == 0 &&
           strcmp(up[1].session_id, "s1") == 0);
    ca_sports_session_free_array(up, n);

    ca_sports_board_destroy(b);
    printf("  sessions: ok\n");
}

int main(void) {
    test_history_week();
    test_best();
    test_sessions();
    printf("test_sports: all assertions passed\n");
    return 0;
}
