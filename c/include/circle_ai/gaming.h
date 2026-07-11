#ifndef CIRCLE_AI_GAMING_H
#define CIRCLE_AI_GAMING_H

/*
 * gaming.h — CircleAI.Gaming (C11 port of GamingPrimitives.cs).
 *
 *   Records : GameTitle(TitleId, Name, Genre, Platform);
 *             PlaySession(SessionId, UserId, TitleId, TimeSpan Duration,
 *                         DateTimeOffset AtUtc);
 *             AchievementUnlock(UnlockId, UserId, TitleId, Achievement,
 *                         DateTimeOffset AtUtc).
 *   Board   : IGamingBoard -> InMemoryGamingBoard
 *               AddTitle (TitleId keyed), GetTitle(id), TitlesByGenre(genre)
 *               (OrdinalIgnoreCase; insertion order), RecordSession (appends),
 *               TotalPlayTime(userId, titleId) — sum Duration, Unlock (appends),
 *               AchievementsFor(userId) newest-first by AtUtc, MostPlayed(userId,
 *               topK=5) — titles grouped by TitleId ordered by summed Duration
 *               descending, top-K, dropping titles no longer present.
 *
 * TimeSpan carried as .NET ticks (100ns). DateTimeOffset as Unix ms UTC.
 * MostPlayed's group order mirrors LINQ GroupBy: groups appear in first-seen
 * order, then a stable descending sort by total ticks.
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

/* GameTitle(TitleId, Name, Genre, Platform). */
typedef struct {
    char *title_id;   /* owned, non-null */
    char *name;       /* owned, non-null */
    char *genre;      /* owned, non-null */
    char *platform;   /* owned, non-null */
} ca_gaming_title_t;

void ca_gaming_title_free(ca_gaming_title_t *t);
void ca_gaming_title_free_array(ca_gaming_title_t *arr, size_t count);

/* PlaySession(SessionId, UserId, TitleId, TimeSpan Duration,
 * DateTimeOffset AtUtc). */
typedef struct {
    char   *session_id;    /* owned, non-null */
    char   *user_id;       /* owned, non-null */
    char   *title_id;      /* owned, non-null */
    int64_t duration_ticks; /* TimeSpan ticks (100ns) */
    int64_t at_utc_ms;
} ca_gaming_session_t;

/* AchievementUnlock(UnlockId, UserId, TitleId, Achievement,
 * DateTimeOffset AtUtc). */
typedef struct {
    char   *unlock_id;     /* owned, non-null */
    char   *user_id;       /* owned, non-null */
    char   *title_id;      /* owned, non-null */
    char   *achievement;   /* owned, non-null */
    int64_t at_utc_ms;
} ca_gaming_unlock_t;

void ca_gaming_unlock_free(ca_gaming_unlock_t *u);
void ca_gaming_unlock_free_array(ca_gaming_unlock_t *arr, size_t count);

typedef struct ca_gaming_board ca_gaming_board_t;

ca_gaming_board_t *ca_gaming_board_create(void); /* NULL on OOM */
void ca_gaming_board_destroy(ca_gaming_board_t *b);

/* AddTitle(t) — TitleId keyed set. 0 / -1. */
int ca_gaming_board_add_title(ca_gaming_board_t *b, const ca_gaming_title_t *t);

/* GetTitle(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_gaming_board_get_title(const ca_gaming_board_t *b, const char *id,
                               ca_gaming_title_t *out);

/* TitlesByGenre(genre) -> fresh owned array (insertion order) whose Genre matches
 * (OrdinalIgnoreCase). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_gaming_title_t *ca_gaming_board_titles_by_genre(const ca_gaming_board_t *b,
                                                   const char *genre,
                                                   size_t *out_count);

/* RecordSession(s) — appends. 0 / -1. */
int ca_gaming_board_record_session(ca_gaming_board_t *b,
                                   const ca_gaming_session_t *s);

/* TotalPlayTime(userId, titleId) — summed Duration ticks. */
int64_t ca_gaming_board_total_play_time(const ca_gaming_board_t *b,
                                        const char *user_id,
                                        const char *title_id);

/* Unlock(u) — appends. 0 / -1. */
int ca_gaming_board_unlock(ca_gaming_board_t *b, const ca_gaming_unlock_t *u);

/* AchievementsFor(userId) -> fresh owned array newest-first by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_gaming_unlock_t *ca_gaming_board_achievements_for(const ca_gaming_board_t *b,
                                                     const char *user_id,
                                                     size_t *out_count);

/* MostPlayed(userId, topK) -> fresh owned array of titles, grouped by TitleId,
 * ordered by summed Duration descending, first top_k; titles no longer present
 * are dropped. top_k must be > 0 (SIZE_MAX on top_k<=0 / bad args). Use 5 for the
 * C# default. NULL + 0 empty. */
ca_gaming_title_t *ca_gaming_board_most_played(const ca_gaming_board_t *b,
                                               const char *user_id, int top_k,
                                               size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_GAMING_H */
