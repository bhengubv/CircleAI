#ifndef CIRCLE_AI_PARENTING_H
#define CIRCLE_AI_PARENTING_H

/*
 * parenting.h — CircleAI.Parenting (C11 port of ParentingPrimitives.cs).
 *
 *   Enum    : DayOfWeek (System.DayOfWeek) { Sunday=0 .. Saturday=6 }.
 *   Records : Child(ChildId, Name, DateTime DateOfBirth, string? Gender);
 *             Milestone(MilestoneId, ChildId, Category, Description,
 *                       DateTimeOffset AchievedAtUtc);
 *             RoutineEntry(Time, Activity);
 *             Routine(ChildId, DayOfWeek DayOfWeek,
 *                     IReadOnlyList<RoutineEntry> Entries).
 *   Board   : IParentingBoard -> InMemoryParentingBoard
 *               AddChild (ChildId keyed), GetChild(id) -> child?,
 *               Children ordered by Name asc, RecordMilestone (per-ChildId list;
 *               throws on whitespace ChildId => rc 2), MilestonesFor(childId)
 *               newest-first by AchievedAtUtc, SetRoutine (keyed childId + dow),
 *               GetRoutine(childId, dow) -> routine?, AgeAsOf(childId, at) =
 *               at - DateOfBirth (throws on unknown => rc 1).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * Gender via has_gender. DateOfBirth / AchievedAtUtc as int64 Unix ms UTC. AgeAsOf
 * yields an int64 ms TimeSpan. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_DOW_SUNDAY    = 0,
    CA_DOW_MONDAY    = 1,
    CA_DOW_TUESDAY   = 2,
    CA_DOW_WEDNESDAY = 3,
    CA_DOW_THURSDAY  = 4,
    CA_DOW_FRIDAY    = 5,
    CA_DOW_SATURDAY  = 6
} ca_day_of_week_t;

/* Child(ChildId, Name, DateTime DateOfBirth, string? Gender). */
typedef struct {
    char   *child_id;   /* owned, non-null */
    char   *name;       /* owned, non-null */
    int64_t date_of_birth_ms;
    bool    has_gender; /* false == C# null Gender */
    char   *gender;     /* owned, valid only when has_gender */
} ca_par_child_t;

void ca_par_child_free(ca_par_child_t *c);
void ca_par_child_free_array(ca_par_child_t *arr, size_t count);

/* Milestone(MilestoneId, ChildId, Category, Description,
 * DateTimeOffset AchievedAtUtc). */
typedef struct {
    char   *milestone_id; /* owned, non-null */
    char   *child_id;     /* owned, non-null */
    char   *category;     /* owned, non-null */
    char   *description;  /* owned, non-null */
    int64_t achieved_at_utc_ms;
} ca_par_milestone_t;

void ca_par_milestone_free(ca_par_milestone_t *m);
void ca_par_milestone_free_array(ca_par_milestone_t *arr, size_t count);

/* RoutineEntry(Time, Activity). */
typedef struct {
    char *time;     /* owned, non-null */
    char *activity; /* owned, non-null */
} ca_par_routine_entry_t;

/* Routine(ChildId, DayOfWeek, IReadOnlyList<RoutineEntry> Entries). */
typedef struct {
    char                   *child_id;     /* owned, non-null */
    ca_day_of_week_t        day_of_week;
    ca_par_routine_entry_t *entries;      /* owned (may be NULL when count 0) */
    size_t                  entry_count;
} ca_par_routine_t;

void ca_par_routine_free(ca_par_routine_t *r);

typedef struct ca_par_board ca_par_board_t;

ca_par_board_t *ca_par_board_create(void); /* NULL on OOM */
void ca_par_board_destroy(ca_par_board_t *b);

/* AddChild(c) — ChildId keyed set. 0 / -1 on bad args/OOM. */
int ca_par_board_add_child(ca_par_board_t *b, const ca_par_child_t *c);

/* GetChild(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_par_board_get_child(const ca_par_board_t *b, const char *id,
                            ca_par_child_t *out);

/* Children -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_par_child_t *ca_par_board_children(const ca_par_board_t *b,
                                      size_t *out_count);

/* RecordMilestone(m) — appends to the ChildId's list. 0 on success, -1 on bad
 * args/OOM, 2 when ChildId is whitespace (ArgumentException). */
int ca_par_board_record_milestone(ca_par_board_t *b,
                                  const ca_par_milestone_t *m);

/* MilestonesFor(childId) -> fresh owned array (*out_count) newest-first by
 * AchievedAtUtc. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_par_milestone_t *ca_par_board_milestones_for(const ca_par_board_t *b,
                                                const char *child_id,
                                                size_t *out_count);

/* SetRoutine(r) — keyed (ChildId, DayOfWeek) (replace). 0 / -1. */
int ca_par_board_set_routine(ca_par_board_t *b, const ca_par_routine_t *r);

/* GetRoutine(childId, dow) -> fresh owned copy into *out, true; false on miss/
 * bad args. */
bool ca_par_board_get_routine(const ca_par_board_t *b, const char *child_id,
                              ca_day_of_week_t dow, ca_par_routine_t *out);

/* AgeAsOf(childId, at_ms) -> at - DateOfBirth (ms) into *out_span_ms. 0 on
 * success, -1 on bad args, 1 when the child is unknown (InvalidOperationException). */
int ca_par_board_age_as_of(const ca_par_board_t *b, const char *child_id,
                           int64_t at_ms, int64_t *out_span_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PARENTING_H */
