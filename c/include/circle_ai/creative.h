#ifndef CIRCLE_AI_CREATIVE_H
#define CIRCLE_AI_CREATIVE_H

/*
 * creative.h — CircleAI.Creative (C11 port of CreativePrimitives.cs).
 *
 *   Records : CreativeWork(WorkId, Title, Medium, Author, DateTimeOffset
 *                       CreatedUtc, IReadOnlyList<string> Tags);
 *             Inspiration(InspirationId, PromptText, SourceUrl, DateTimeOffset
 *                       SeenUtc);
 *             Critique(CritiqueId, WorkId, Reviewer, Body, int Score).
 *   Board   : ICreativeBoard -> InMemoryCreativeBoard
 *               AddWork (WorkId keyed), GetWork(id), WorksByTag(tag)
 *               (OrdinalIgnoreCase Tags.Any; insertion order), RecordInspiration
 *               (appends), RecentInspiration(limit=20) newest-first by SeenUtc,
 *               AddCritique (appends), AvgScore(workId) — mean Score over that
 *               work's critiques (0.0 when none, per DefaultIfEmpty(0).Average()).
 *
 * DateTimeOffset as Unix ms UTC.
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

/* CreativeWork(WorkId, Title, Medium, Author, DateTimeOffset CreatedUtc, Tags[]). */
typedef struct {
    char   *work_id; /* owned, non-null */
    char   *title;   /* owned, non-null */
    char   *medium;  /* owned, non-null */
    char   *author;  /* owned, non-null */
    int64_t created_utc_ms;
    char  **tags;    /* owned array of owned strings (may be NULL if 0) */
    size_t  tag_count;
} ca_creative_work_t;

void ca_creative_work_free(ca_creative_work_t *w);
void ca_creative_work_free_array(ca_creative_work_t *arr, size_t count);

/* Inspiration(InspirationId, PromptText, SourceUrl, DateTimeOffset SeenUtc). */
typedef struct {
    char   *inspiration_id; /* owned, non-null */
    char   *prompt_text;    /* owned, non-null */
    char   *source_url;     /* owned, non-null */
    int64_t seen_utc_ms;
} ca_creative_inspiration_t;

void ca_creative_inspiration_free(ca_creative_inspiration_t *i);
void ca_creative_inspiration_free_array(ca_creative_inspiration_t *arr,
                                        size_t count);

/* Critique(CritiqueId, WorkId, Reviewer, Body, int Score). */
typedef struct {
    char   *critique_id; /* owned, non-null */
    char   *work_id;     /* owned, non-null */
    char   *reviewer;    /* owned, non-null */
    char   *body;        /* owned, non-null */
    int     score;
} ca_creative_critique_t;

typedef struct ca_creative_board ca_creative_board_t;

ca_creative_board_t *ca_creative_board_create(void); /* NULL on OOM */
void ca_creative_board_destroy(ca_creative_board_t *b);

/* AddWork(w) — WorkId keyed set. 0 / -1. */
int ca_creative_board_add_work(ca_creative_board_t *b,
                               const ca_creative_work_t *w);

/* GetWork(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_creative_board_get_work(const ca_creative_board_t *b, const char *id,
                                ca_creative_work_t *out);

/* WorksByTag(tag) -> fresh owned array (insertion order) whose Tags contain tag
 * (OrdinalIgnoreCase). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_creative_work_t *ca_creative_board_works_by_tag(const ca_creative_board_t *b,
                                                   const char *tag,
                                                   size_t *out_count);

/* RecordInspiration(i) — appends. 0 / -1. */
int ca_creative_board_record_inspiration(ca_creative_board_t *b,
                                         const ca_creative_inspiration_t *i);

/* RecentInspiration(limit) -> fresh owned array newest-first by SeenUtc, first
 * `limit`. limit must be > 0 (SIZE_MAX on limit<=0 / bad args). Use 20 for the C#
 * default. NULL + 0 empty. */
ca_creative_inspiration_t *ca_creative_board_recent_inspiration(
    const ca_creative_board_t *b, int limit, size_t *out_count);

/* AddCritique(c) — appends. 0 / -1. */
int ca_creative_board_add_critique(ca_creative_board_t *b,
                                   const ca_creative_critique_t *c);

/* AvgScore(workId) — mean Score of that work's critiques; 0.0 when none. */
double ca_creative_board_avg_score(const ca_creative_board_t *b,
                                   const char *work_id);

/* WorkCount — number of works. NULL board → 0. */
size_t ca_creative_board_work_count(const ca_creative_board_t *b);

/* RemoveWork(workId) — drop a work by id; on success also drops every critique
 * whose WorkId matches (Ordinal). Returns true if the work was present. */
bool ca_creative_board_remove_work(ca_creative_board_t *b, const char *work_id);

/* WorksByAuthor(author) -> fresh owned array of works whose Author matches
 * (OrdinalIgnoreCase), ordered by CreatedUtc descending. NULL + 0 empty; NULL +
 * SIZE_MAX on error. */
ca_creative_work_t *ca_creative_board_works_by_author(
    const ca_creative_board_t *b, const char *author, size_t *out_count);

/* WorksByMedium(medium) -> fresh owned array of works whose Medium matches
 * (OrdinalIgnoreCase), ordered by CreatedUtc descending. NULL + 0 empty; NULL +
 * SIZE_MAX on error. */
ca_creative_work_t *ca_creative_board_works_by_medium(
    const ca_creative_board_t *b, const char *medium, size_t *out_count);

/* TopRatedWork() -> the still-present work with the highest mean critique Score
 * (grouped by WorkId Ordinal, ordered by average descending): writes a fresh
 * owned copy into *out and returns true. false (C# null) when no critiques point
 * at a live work / bad args. */
bool ca_creative_board_top_rated_work(const ca_creative_board_t *b,
                                      ca_creative_work_t *out);

/* AllTags() -> fresh owned array of the distinct tags across all works
 * (OrdinalIgnoreCase distinct, the first-seen spelling kept), ordered
 * ascending (OrdinalIgnoreCase). *out_count receives the count. NULL + 0 when
 * there are no tags; NULL + SIZE_MAX on error. Free with cab-style strv free:
 * each element + the block (ca_creative_tags_free). */
char **ca_creative_board_all_tags(const ca_creative_board_t *b,
                                  size_t *out_count);
/* Free an owned tag array returned by ca_creative_board_all_tags. */
void ca_creative_tags_free(char **tags, size_t count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CREATIVE_H */
