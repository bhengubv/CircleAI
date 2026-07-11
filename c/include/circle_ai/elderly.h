#ifndef CIRCLE_AI_ELDERLY_H
#define CIRCLE_AI_ELDERLY_H

/*
 * elderly.h — CircleAI.Elderly (C11 port of ElderlyPrimitives.cs).
 *
 *   Records : CarePlan(PlanId, ResidentName, IReadOnlyList<string>
 *                      MedicalConditions, IReadOnlyList<string> Allergies,
 *                      CarerNotes);
 *             MedReminder(ReminderId, ResidentName, Medication,
 *                         TimeSpan DailyAt, bool Active);
 *             CheckIn(CheckInId, ResidentName, DateTimeOffset AtUtc, Status,
 *                     string? Note).
 *   Board   : IElderlyCareBoard -> InMemoryElderlyCareBoard
 *               SetPlan (ResidentName keyed), GetPlan(resident) -> plan?,
 *               AddReminder (ReminderId keyed), DeactivateReminder(reminderId)
 *               — throws on unknown (rc 1), ActiveRemindersFor(resident) where
 *               ResidentName == resident (ordinal) && Active in insertion order,
 *               RecordCheckIn (appends), LatestCheckIn(resident) — newest by
 *               AtUtc (or none), MissedCheckIn(resident, since) = latest is null
 *               || latest.AtUtc < since.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * Note via has_note. DailyAt (TimeSpan) as int64 ms since midnight. AtUtc as int64
 * Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* CarePlan(PlanId, ResidentName, IReadOnlyList<string> MedicalConditions,
 * IReadOnlyList<string> Allergies, CarerNotes). */
typedef struct {
    char  *plan_id;              /* owned, non-null */
    char  *resident_name;        /* owned, non-null */
    char **medical_conditions;   /* owned array (may be NULL when count 0) */
    size_t medical_condition_count;
    char **allergies;            /* owned array (may be NULL when count 0) */
    size_t allergy_count;
    char  *carer_notes;          /* owned, non-null */
} ca_eld_care_plan_t;

void ca_eld_care_plan_free(ca_eld_care_plan_t *p);

/* MedReminder(ReminderId, ResidentName, Medication, TimeSpan DailyAt,
 * bool Active). */
typedef struct {
    char   *reminder_id;   /* owned, non-null */
    char   *resident_name; /* owned, non-null */
    char   *medication;    /* owned, non-null */
    int64_t daily_at_ms;   /* TimeSpan as ms since midnight */
    bool    active;
} ca_eld_reminder_t;

void ca_eld_reminder_free(ca_eld_reminder_t *r);
void ca_eld_reminder_free_array(ca_eld_reminder_t *arr, size_t count);

/* CheckIn(CheckInId, ResidentName, DateTimeOffset AtUtc, Status, string? Note). */
typedef struct {
    char   *check_in_id;   /* owned, non-null */
    char   *resident_name; /* owned, non-null */
    int64_t at_utc_ms;
    char   *status;        /* owned, non-null */
    bool    has_note;      /* false == C# null Note */
    char   *note;          /* owned, valid only when has_note */
} ca_eld_check_in_t;

void ca_eld_check_in_free(ca_eld_check_in_t *c);

typedef struct ca_eld_board ca_eld_board_t;

ca_eld_board_t *ca_eld_board_create(void); /* NULL on OOM */
void ca_eld_board_destroy(ca_eld_board_t *b);

/* SetPlan(p) — ResidentName keyed set. 0 / -1 on bad args/OOM. */
int ca_eld_board_set_plan(ca_eld_board_t *b, const ca_eld_care_plan_t *p);

/* GetPlan(resident) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_eld_board_get_plan(const ca_eld_board_t *b, const char *resident,
                           ca_eld_care_plan_t *out);

/* AddReminder(r) — ReminderId keyed set. 0 / -1. */
int ca_eld_board_add_reminder(ca_eld_board_t *b, const ca_eld_reminder_t *r);

/* DeactivateReminder(reminderId) — sets Active=false. 0 on success, -1 on bad
 * args, 1 when unknown (InvalidOperationException). */
int ca_eld_board_deactivate_reminder(ca_eld_board_t *b, const char *reminder_id);

/* ActiveRemindersFor(resident) -> fresh owned array (*out_count): ResidentName ==
 * resident (ordinal) && Active in insertion order. NULL + 0 when empty; NULL +
 * SIZE_MAX on error. */
ca_eld_reminder_t *ca_eld_board_active_reminders_for(const ca_eld_board_t *b,
                                                     const char *resident,
                                                     size_t *out_count);

/* RecordCheckIn(c) — appends. 0 / -1. */
int ca_eld_board_record_check_in(ca_eld_board_t *b, const ca_eld_check_in_t *c);

/* LatestCheckIn(resident) -> newest (by AtUtc) check-in into *out, true; false
 * (C# null) when none/bad args. */
bool ca_eld_board_latest_check_in(const ca_eld_board_t *b, const char *resident,
                                  ca_eld_check_in_t *out);

/* MissedCheckIn(resident, since_ms) = latest is null || latest.AtUtc < since. */
bool ca_eld_board_missed_check_in(const ca_eld_board_t *b, const char *resident,
                                  int64_t since_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_ELDERLY_H */
