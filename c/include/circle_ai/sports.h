#ifndef CIRCLE_AI_SPORTS_H
#define CIRCLE_AI_SPORTS_H

/*
 * sports.h — CircleAI.Sports (C11 port of SportsPrimitives.cs).
 *
 *   Enum    : DistanceKind { Run, Bike, Swim, Walk, Row }.
 *   Records : Activity(ActivityId, UserId, DistanceKind Kind, double DistanceKm,
 *                      TimeSpan Duration, DateTimeOffset AtUtc);
 *             PersonalBest(UserId, DistanceKind Kind, double DistanceKm,
 *                      TimeSpan Time, DateTimeOffset AchievedUtc);
 *             TrainingSession(SessionId, UserId, Plan, DateTimeOffset ScheduledUtc,
 *                      bool Completed).
 *   Board   : ISportsBoard -> InMemorySportsBoard
 *               Log (appends), History(userId, limit=50) newest-first by AtUtc,
 *               TotalKmThisWeek(userId, kind, now) — sum DistanceKm since the
 *               Sunday-start of now's week, Best(userId, kind, distanceKm) — the
 *               fastest Activity of that kind >= distanceKm (the returned
 *               PersonalBest carries the *query* distanceKm, not the hit's),
 *               Schedule (SessionId keyed), Complete(sessionId) — flips Completed,
 *               Upcoming(userId, now) — incomplete sessions with ScheduledUtc >= now
 *               ordered ascending.
 *
 * The C# Upcoming reads DateTimeOffset.UtcNow; to stay deterministic the port takes
 * an explicit now_ms (as pets.h UpcomingAppointments does). TimeSpan carried as
 * .NET ticks (100ns). DateTimeOffset as Unix ms UTC. Week start uses C#'s
 * now.Date.AddDays(-(int)now.DayOfWeek): the midnight of the Sunday beginning now's
 * UTC calendar week.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_DISTANCE_KIND_RUN = 0,
    CA_DISTANCE_KIND_BIKE = 1,
    CA_DISTANCE_KIND_SWIM = 2,
    CA_DISTANCE_KIND_WALK = 3,
    CA_DISTANCE_KIND_ROW = 4
} ca_distance_kind_t;

/* Activity(ActivityId, UserId, DistanceKind, double DistanceKm,
 * TimeSpan Duration, DateTimeOffset AtUtc). */
typedef struct {
    char   *activity_id;    /* owned, non-null */
    char   *user_id;        /* owned, non-null */
    ca_distance_kind_t kind;
    double  distance_km;
    int64_t duration_ticks; /* TimeSpan ticks (100ns) */
    int64_t at_utc_ms;      /* DateTimeOffset as Unix ms UTC */
} ca_sports_activity_t;

void ca_sports_activity_free(ca_sports_activity_t *a);
void ca_sports_activity_free_array(ca_sports_activity_t *arr, size_t count);

/* PersonalBest(UserId, DistanceKind, double DistanceKm, TimeSpan Time,
 * DateTimeOffset AchievedUtc). */
typedef struct {
    char   *user_id;        /* owned, non-null */
    ca_distance_kind_t kind;
    double  distance_km;
    int64_t time_ticks;     /* TimeSpan ticks (100ns) */
    int64_t achieved_utc_ms;
} ca_sports_personal_best_t;

void ca_sports_personal_best_free(ca_sports_personal_best_t *p);

/* TrainingSession(SessionId, UserId, Plan, DateTimeOffset ScheduledUtc,
 * bool Completed). */
typedef struct {
    char   *session_id;     /* owned, non-null */
    char   *user_id;        /* owned, non-null */
    char   *plan;           /* owned, non-null */
    int64_t scheduled_utc_ms;
    bool    completed;
} ca_sports_session_t;

void ca_sports_session_free(ca_sports_session_t *s);
void ca_sports_session_free_array(ca_sports_session_t *arr, size_t count);

typedef struct ca_sports_board ca_sports_board_t;

ca_sports_board_t *ca_sports_board_create(void); /* NULL on OOM */
void ca_sports_board_destroy(ca_sports_board_t *b);

/* Log(a) — appends. 0 / -1 on bad args / OOM. */
int ca_sports_board_log(ca_sports_board_t *b, const ca_sports_activity_t *a);

/* History(userId, limit) -> fresh owned array (*out_count) newest-first by AtUtc,
 * first `limit`. limit must be > 0 (SIZE_MAX on limit<=0 / bad args). Use 50 for
 * the C# default. NULL + 0 empty. */
ca_sports_activity_t *ca_sports_board_history(const ca_sports_board_t *b,
                                              const char *user_id, int limit,
                                              size_t *out_count);

/* TotalKmThisWeek(userId, kind, now_ms) — sum DistanceKm over the user's
 * activities of `kind` with AtUtc >= start-of-week(now). */
double ca_sports_board_total_km_this_week(const ca_sports_board_t *b,
                                          const char *user_id,
                                          ca_distance_kind_t kind,
                                          int64_t now_ms);

/* Best(userId, kind, distanceKm) -> writes the PersonalBest into *out and returns
 * true; false (C# null) when no qualifying activity. The fastest (min Duration)
 * activity of `kind` with DistanceKm >= distanceKm; *out carries the query
 * distanceKm (mirrors the C#). */
bool ca_sports_board_best(const ca_sports_board_t *b, const char *user_id,
                          ca_distance_kind_t kind, double distance_km,
                          ca_sports_personal_best_t *out);

/* Schedule(s) — SessionId keyed set. 0 / -1. */
int ca_sports_board_schedule(ca_sports_board_t *b, const ca_sports_session_t *s);

/* Complete(sessionId) — sets Completed=true. 0 on success, -1 on bad args,
 * -2 when the session is unknown (C# InvalidOperationException). */
int ca_sports_board_complete(ca_sports_board_t *b, const char *session_id);

/* Upcoming(userId, now_ms) -> fresh owned array (*out_count): incomplete sessions
 * with ScheduledUtc >= now, ordered by ScheduledUtc asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_sports_session_t *ca_sports_board_upcoming(const ca_sports_board_t *b,
                                              const char *user_id, int64_t now_ms,
                                              size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SPORTS_H */
