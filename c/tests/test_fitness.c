/*
 * test_fitness.c — CircleAI.Fitness (C11 port) verification against
 * FitnessPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

#define DAY 86400000LL

static void test_workouts(void) {
    ca_fitness_board_t *b = ca_fitness_board_create();
    assert(b);
    assert(ca_fitness_board_log(b, NULL) == -1);

    int64_t wed = 18633LL * DAY + 6 * 3600000LL;   /* Wed */
    int64_t sun = 18630LL * DAY;                    /* week start */
    int64_t prev = 18629LL * DAY;                   /* previous week */

    ca_fitness_workout_t w1; memset(&w1, 0, sizeof(w1));
    w1.workout_id = (char *)"w1"; w1.user_id = (char *)"u1"; w1.kind = (char *)"run";
    w1.duration_minutes = 30; w1.calories_burned = 300; w1.at_utc_ms = wed;
    ca_fitness_workout_t w2; memset(&w2, 0, sizeof(w2));
    w2.workout_id = (char *)"w2"; w2.user_id = (char *)"u1"; w2.kind = (char *)"lift";
    w2.duration_minutes = 45; w2.calories_burned = 200; w2.at_utc_ms = sun;
    ca_fitness_workout_t w3; memset(&w3, 0, sizeof(w3));
    w3.workout_id = (char *)"w3"; w3.user_id = (char *)"u1"; w3.kind = (char *)"old";
    w3.duration_minutes = 60; w3.calories_burned = 500; w3.at_utc_ms = prev;
    assert(ca_fitness_board_log(b, &w1) == 0);
    assert(ca_fitness_board_log(b, &w2) == 0);
    assert(ca_fitness_board_log(b, &w3) == 0);

    /* This week ascending by AtUtc: w2(sun) then w1(wed); w3 excluded. */
    size_t n = 0;
    ca_fitness_workout_t *ww = ca_fitness_board_workouts_this_week(b, "u1", wed, &n);
    assert(n == 2 && strcmp(ww[0].workout_id, "w2") == 0 &&
           strcmp(ww[1].workout_id, "w1") == 0);
    ca_fitness_workout_free_array(ww, n);

    /* TotalCaloriesSince(sun): w1(300)+w2(200) = 500; w3 before. */
    assert(ca_fitness_board_total_calories_since(b, "u1", sun) == 500.0);
    /* Since prev: all three. */
    assert(ca_fitness_board_total_calories_since(b, "u1", prev) == 1000.0);

    ca_fitness_board_destroy(b);
    printf("  workouts: ok\n");
}

static void test_goals_sets(void) {
    ca_fitness_board_t *b = ca_fitness_board_create();

    ca_fitness_goal_t g1; memset(&g1, 0, sizeof(g1));
    g1.goal_id = (char *)"g1"; g1.user_id = (char *)"u1"; g1.metric = (char *)"weight";
    g1.target = 75.0; g1.due_on_ms = 1000;
    ca_fitness_goal_t g2; memset(&g2, 0, sizeof(g2));
    g2.goal_id = (char *)"g2"; g2.user_id = (char *)"u2"; g2.metric = (char *)"steps";
    g2.target = 10000.0; g2.due_on_ms = 2000;
    assert(ca_fitness_board_set_goal(b, &g1) == 0);
    assert(ca_fitness_board_set_goal(b, &g2) == 0);

    size_t n = 0;
    ca_fitness_goal_t *g = ca_fitness_board_goals_for(b, "u1", &n);
    assert(n == 1 && strcmp(g[0].goal_id, "g1") == 0 && g[0].target == 75.0);
    ca_fitness_goal_free_array(g, n);

    /* Upsert g1 target. */
    g1.target = 70.0;
    assert(ca_fitness_board_set_goal(b, &g1) == 0);
    g = ca_fitness_board_goals_for(b, "u1", &n);
    assert(n == 1 && g[0].target == 70.0);
    ca_fitness_goal_free_array(g, n);

    /* Sets. */
    ca_fitness_set_t s1; memset(&s1, 0, sizeof(s1));
    s1.set_id = (char *)"s1"; s1.workout_id = (char *)"w1"; s1.exercise = (char *)"squat";
    s1.reps = 5; s1.weight_kg = 100.0;
    ca_fitness_set_t s2; memset(&s2, 0, sizeof(s2));
    s2.set_id = (char *)"s2"; s2.workout_id = (char *)"w1"; s2.exercise = (char *)"bench";
    s2.reps = 8; s2.weight_kg = 80.0;
    ca_fitness_set_t s3; memset(&s3, 0, sizeof(s3));
    s3.set_id = (char *)"s3"; s3.workout_id = (char *)"w2"; s3.exercise = (char *)"row";
    s3.reps = 10; s3.weight_kg = 60.0;
    assert(ca_fitness_board_add_set(b, &s1) == 0);
    assert(ca_fitness_board_add_set(b, &s2) == 0);
    assert(ca_fitness_board_add_set(b, &s3) == 0);

    ca_fitness_set_t *st = ca_fitness_board_sets_for(b, "w1", &n);
    assert(n == 2 && strcmp(st[0].set_id, "s1") == 0 && strcmp(st[1].set_id, "s2") == 0);
    ca_fitness_set_free_array(st, n);

    ca_fitness_board_destroy(b);
    printf("  goals_sets: ok\n");
}

int main(void) {
    test_workouts();
    test_goals_sets();
    printf("test_fitness: all assertions passed\n");
    return 0;
}
