#ifndef CIRCLE_AI_KIDS_H
#define CIRCLE_AI_KIDS_H

/*
 * kids.h — CircleAI.Kids (C11 port of KidsPrimitives.cs).
 *
 *   Enum    : AgeAppropriateness { Toddler, Preschool, EarlyPrimary, LatePrimary,
 *                       PreTeen, Teen }.
 *   Records : KidsContent(ContentId, Title, AgeAppropriateness AgeBand, Kind,
 *                       IReadOnlyList<string> Tags);
 *             DailyTime(KidName, TimeSpan ScreenLimit, TimeSpan ReadingLimit);
 *             TimeLog(KidName, Kind, TimeSpan Duration, DateTimeOffset AtUtc).
 *   Board   : IKidsBoard -> InMemoryKidsBoard
 *               AddContent (ContentId keyed), ContentFor(band) ordered by Title
 *               asc, SetLimits (KidName keyed), LimitsFor(kidName), RecordTime
 *               (appends), UsedToday(kidName, kind, now) — summed Duration of that
 *               kid+kind on now's UTC calendar day, OverLimit(kidName, kind, now)
 *               — UsedToday > the kind's cap ("screen"->ScreenLimit,
 *               "reading"->ReadingLimit (CI), else TimeSpan.MaxValue); false when
 *               no limits set.
 *
 * TimeSpan carried as .NET ticks (100ns); TimeSpan.MaxValue == INT64_MAX ticks.
 * DateTimeOffset as Unix ms UTC. Day match uses AtUtc.Date == now.Date.
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

typedef enum {
    CA_AGE_TODDLER = 0,
    CA_AGE_PRESCHOOL = 1,
    CA_AGE_EARLY_PRIMARY = 2,
    CA_AGE_LATE_PRIMARY = 3,
    CA_AGE_PRE_TEEN = 4,
    CA_AGE_TEEN = 5
} ca_age_appropriateness_t;

/* KidsContent(ContentId, Title, AgeAppropriateness AgeBand, Kind, Tags[]). */
typedef struct {
    char   *content_id; /* owned, non-null */
    char   *title;      /* owned, non-null */
    ca_age_appropriateness_t age_band;
    char   *kind;       /* owned, non-null */
    char  **tags;       /* owned array of owned strings (may be NULL if 0) */
    size_t  tag_count;
} ca_kids_content_t;

void ca_kids_content_free(ca_kids_content_t *c);
void ca_kids_content_free_array(ca_kids_content_t *arr, size_t count);

/* DailyTime(KidName, TimeSpan ScreenLimit, TimeSpan ReadingLimit). */
typedef struct {
    char   *kid_name;             /* owned, non-null */
    int64_t screen_limit_ticks;   /* TimeSpan ticks (100ns) */
    int64_t reading_limit_ticks;
} ca_kids_daily_time_t;

void ca_kids_daily_time_free(ca_kids_daily_time_t *d);

/* TimeLog(KidName, Kind, TimeSpan Duration, DateTimeOffset AtUtc). */
typedef struct {
    char   *kid_name;      /* owned, non-null */
    char   *kind;          /* owned, non-null */
    int64_t duration_ticks; /* TimeSpan ticks (100ns) */
    int64_t at_utc_ms;
} ca_kids_time_log_t;

typedef struct ca_kids_board ca_kids_board_t;

ca_kids_board_t *ca_kids_board_create(void); /* NULL on OOM */
void ca_kids_board_destroy(ca_kids_board_t *b);

/* AddContent(c) — ContentId keyed set. 0 / -1. */
int ca_kids_board_add_content(ca_kids_board_t *b, const ca_kids_content_t *c);

/* ContentFor(band) -> fresh owned array ordered by Title asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_kids_content_t *ca_kids_board_content_for(const ca_kids_board_t *b,
                                             ca_age_appropriateness_t band,
                                             size_t *out_count);

/* SetLimits(d) — KidName keyed set. 0 / -1. */
int ca_kids_board_set_limits(ca_kids_board_t *b, const ca_kids_daily_time_t *d);

/* LimitsFor(kidName) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_kids_board_limits_for(const ca_kids_board_t *b, const char *kid_name,
                              ca_kids_daily_time_t *out);

/* RecordTime(t) — appends. 0 / -1. */
int ca_kids_board_record_time(ca_kids_board_t *b, const ca_kids_time_log_t *t);

/* UsedToday(kidName, kind, now_ms) — summed Duration ticks of that kid+kind whose
 * AtUtc falls on now's UTC calendar day. Kind matched by ordinal equality (the
 * C# uses l.Kind == kind). */
int64_t ca_kids_board_used_today(const ca_kids_board_t *b, const char *kid_name,
                                 const char *kind, int64_t now_ms);

/* OverLimit(kidName, kind, now_ms) — UsedToday > the kind's cap. Returns false
 * when no limits are set for the kid. */
bool ca_kids_board_over_limit(const ca_kids_board_t *b, const char *kid_name,
                              const char *kind, int64_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_KIDS_H */
