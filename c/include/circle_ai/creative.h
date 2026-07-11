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

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CREATIVE_H */
