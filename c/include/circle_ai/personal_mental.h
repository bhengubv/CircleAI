#ifndef CIRCLE_AI_PERSONAL_MENTAL_H
#define CIRCLE_AI_PERSONAL_MENTAL_H

/*
 * personal_mental.h — CircleAI.Personal.Mental (C11 port of
 * PersonalMentalPrimitives.cs). Per-user mood / journal / coping-strategy board.
 *
 *   Enum    : Mood { VeryLow, Low, Neutral, Good, Great } (ordinal 0..4).
 *   Records : MoodLog(Mood, AtUtc, Note?);
 *             JournalEntry(EntryId, Title, Body, AtUtc);
 *             CopingStrategy(StrategyId, Title, Description, Tags[]).
 *   Board   : IMentalHealthBoard -> InMemoryMentalHealthBoard.
 *             LogMood(m) (appended list), Last7Days(now) (AtUtc >= now-7d,
 *             ordered by AtUtc ascending), AddEntry(e) (EntryId keyed set; empty
 *             EntryId rejected), Entries (ordered by AtUtc descending),
 *             RegisterStrategy(s) (StrategyId keyed set), StrategiesByTag(tag)
 *             (Tags contain tag OrdinalIgnoreCase; empty tag rejected),
 *             AvgMood7Day(now) (mean of (int)Mood over Last7Days, NaN when none).
 *
 * Last7Days / AvgMood7Day take an explicit `now_ms` (the C# uses
 * DateTimeOffset.UtcNow) so results stay deterministic; cutoff = now_ms - 7 days.
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. AtUtc as
 * int64 Unix ms UTC. Note optional (has_note gate). Tags owned string array.
 * Linear arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_MOOD_VERY_LOW = 0,
    CA_MOOD_LOW      = 1,
    CA_MOOD_NEUTRAL  = 2,
    CA_MOOD_GOOD     = 3,
    CA_MOOD_GREAT    = 4
} ca_mood_t;

/* Milliseconds in 7 days — the Last7Days window. */
#define CA_MENTAL_7DAY_MS (7LL * 24LL * 60LL * 60LL * 1000LL)

/* MoodLog(Mood Mood, DateTimeOffset AtUtc, string? Note). */
typedef struct {
    ca_mood_t mood;
    int64_t   at_utc_ms;  /* DateTimeOffset as Unix ms UTC */
    bool      has_note;   /* false == C# null Note */
    char     *note;       /* owned, valid only when has_note */
} ca_mental_mood_log_t;

void ca_mental_mood_log_free(ca_mental_mood_log_t *m);
void ca_mental_mood_log_free_array(ca_mental_mood_log_t *arr, size_t count);

/* JournalEntry(EntryId, Title, Body, DateTimeOffset AtUtc). */
typedef struct {
    char   *entry_id;  /* owned, non-null */
    char   *title;     /* owned, non-null */
    char   *body;      /* owned, non-null */
    int64_t at_utc_ms; /* DateTimeOffset as Unix ms UTC */
} ca_mental_journal_entry_t;

void ca_mental_journal_entry_free(ca_mental_journal_entry_t *e);
void ca_mental_journal_entry_free_array(ca_mental_journal_entry_t *arr,
                                        size_t count);

/* CopingStrategy(StrategyId, Title, Description, IReadOnlyList<string> Tags). */
typedef struct {
    char  *strategy_id;  /* owned, non-null */
    char  *title;        /* owned, non-null */
    char  *description;  /* owned, non-null */
    char **tags;         /* owned string array (may be NULL when count 0) */
    size_t tag_count;
} ca_mental_strategy_t;

void ca_mental_strategy_free(ca_mental_strategy_t *s);
void ca_mental_strategy_free_array(ca_mental_strategy_t *arr, size_t count);

typedef struct ca_mental_board ca_mental_board_t;

/* InMemoryMentalHealthBoard(). NULL on OOM. */
ca_mental_board_t *ca_mental_board_create(void);
void ca_mental_board_destroy(ca_mental_board_t *b);

/* LogMood(m) — deep-copies; appended list. 0 / -1 on bad args/OOM. */
int ca_mental_board_log_mood(ca_mental_board_t *b,
                             const ca_mental_mood_log_t *m);
/* Last7Days(now_ms) -> fresh owned array (*out_count): AtUtc >= now_ms - 7 days,
 * ordered by AtUtc ascending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_mental_mood_log_t *ca_mental_board_last_7_days(const ca_mental_board_t *b,
                                                  int64_t now_ms,
                                                  size_t *out_count);

/* AddEntry(e). 0 on success, -1 on bad args/OOM, 2 when EntryId is null/whitespace
 * (ArgumentException in C#). EntryId keyed set. */
int ca_mental_board_add_entry(ca_mental_board_t *b,
                              const ca_mental_journal_entry_t *e);
/* Entries -> fresh owned array (*out_count) ordered by AtUtc descending.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_mental_journal_entry_t *ca_mental_board_entries(const ca_mental_board_t *b,
                                                   size_t *out_count);

/* RegisterStrategy(s) — deep-copies; StrategyId keyed set. 0 / -1. */
int ca_mental_board_register_strategy(ca_mental_board_t *b,
                                      const ca_mental_strategy_t *s);
/* StrategiesByTag(tag) -> fresh owned array (*out_count): strategies whose Tags
 * contain tag (OrdinalIgnoreCase). tag required (null/whitespace -> SIZE_MAX,
 * mirroring ArgumentException). NULL + 0 when no hits. */
ca_mental_strategy_t *ca_mental_board_strategies_by_tag(const ca_mental_board_t *b,
                                                        const char *tag,
                                                        size_t *out_count);

/* AvgMood7Day(now_ms) -> mean of (int)Mood over Last7Days(now_ms); returns NaN
 * (see isnan) when the window is empty. */
double ca_mental_board_avg_mood_7day(const ca_mental_board_t *b, int64_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PERSONAL_MENTAL_H */
