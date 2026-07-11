#ifndef CIRCLE_AI_FITNESS_H
#define CIRCLE_AI_FITNESS_H

/*
 * fitness.h — CircleAI.Fitness (C11 port of FitnessPrimitives.cs).
 *
 *   Records : Workout(WorkoutId, UserId, Kind, int DurationMinutes,
 *                      double CaloriesBurned, DateTimeOffset AtUtc);
 *             FitnessGoal(GoalId, UserId, Metric, double Target, DateTime DueOn);
 *             ExerciseSet(SetId, WorkoutId, Exercise, int Reps, double WeightKg).
 *   Board   : IFitnessBoard -> InMemoryFitnessBoard
 *               Log (appends), WorkoutsThisWeek(userId, now) ascending by AtUtc
 *               since the Sunday week-start, TotalCaloriesSince(userId, since),
 *               SetGoal (GoalId keyed), GoalsFor(userId) [insertion order],
 *               AddSet (appends), SetsFor(workoutId) [insertion order].
 *
 * DateTimeOffset/DateTime as Unix ms UTC; week start per C#
 * now.Date.AddDays(-(int)now.DayOfWeek). GoalsFor / SetsFor iterate the store —
 * the C# .Values / list order is preserved as insertion order (deterministic).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Workout(WorkoutId, UserId, Kind, int DurationMinutes, double CaloriesBurned,
 * DateTimeOffset AtUtc). */
typedef struct {
    char   *workout_id;    /* owned, non-null */
    char   *user_id;       /* owned, non-null */
    char   *kind;          /* owned, non-null */
    int     duration_minutes;
    double  calories_burned;
    int64_t at_utc_ms;
} ca_fitness_workout_t;

void ca_fitness_workout_free(ca_fitness_workout_t *w);
void ca_fitness_workout_free_array(ca_fitness_workout_t *arr, size_t count);

/* FitnessGoal(GoalId, UserId, Metric, double Target, DateTime DueOn). */
typedef struct {
    char   *goal_id;       /* owned, non-null */
    char   *user_id;       /* owned, non-null */
    char   *metric;        /* owned, non-null */
    double  target;
    int64_t due_on_ms;
} ca_fitness_goal_t;

void ca_fitness_goal_free(ca_fitness_goal_t *g);
void ca_fitness_goal_free_array(ca_fitness_goal_t *arr, size_t count);

/* ExerciseSet(SetId, WorkoutId, Exercise, int Reps, double WeightKg). */
typedef struct {
    char   *set_id;        /* owned, non-null */
    char   *workout_id;    /* owned, non-null */
    char   *exercise;      /* owned, non-null */
    int     reps;
    double  weight_kg;
} ca_fitness_set_t;

void ca_fitness_set_free(ca_fitness_set_t *s);
void ca_fitness_set_free_array(ca_fitness_set_t *arr, size_t count);

typedef struct ca_fitness_board ca_fitness_board_t;

ca_fitness_board_t *ca_fitness_board_create(void); /* NULL on OOM */
void ca_fitness_board_destroy(ca_fitness_board_t *b);

/* Log(w) — appends. 0 / -1. */
int ca_fitness_board_log(ca_fitness_board_t *b, const ca_fitness_workout_t *w);

/* WorkoutsThisWeek(userId, now_ms) -> fresh owned array ascending by AtUtc
 * (AtUtc >= start-of-week(now)). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_fitness_workout_t *ca_fitness_board_workouts_this_week(
    const ca_fitness_board_t *b, const char *user_id, int64_t now_ms,
    size_t *out_count);

/* TotalCaloriesSince(userId, since_ms) — sum CaloriesBurned since (inclusive). */
double ca_fitness_board_total_calories_since(const ca_fitness_board_t *b,
                                             const char *user_id,
                                             int64_t since_ms);

/* SetGoal(g) — GoalId keyed set. 0 / -1. */
int ca_fitness_board_set_goal(ca_fitness_board_t *b, const ca_fitness_goal_t *g);

/* GoalsFor(userId) -> fresh owned array (insertion order). NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_fitness_goal_t *ca_fitness_board_goals_for(const ca_fitness_board_t *b,
                                              const char *user_id,
                                              size_t *out_count);

/* AddSet(s) — appends. 0 / -1. */
int ca_fitness_board_add_set(ca_fitness_board_t *b, const ca_fitness_set_t *s);

/* SetsFor(workoutId) -> fresh owned array (insertion order). NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_fitness_set_t *ca_fitness_board_sets_for(const ca_fitness_board_t *b,
                                            const char *workout_id,
                                            size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FITNESS_H */
