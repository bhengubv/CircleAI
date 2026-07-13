#ifndef CIRCLE_AI_FAITH_H
#define CIRCLE_AI_FAITH_H

/*
 * faith.h — CircleAI.Faith (C11 port of FaithPrimitives.cs).
 *
 *   Records : FaithService(ServiceId, CommunityName, Title, DateTimeOffset
 *                       StartUtc, Location);
 *             PrayerRequest(RequestId, Author, Body, DateTimeOffset SubmittedUtc,
 *                       bool IsAnonymous);
 *             ScriptureReference(ReferenceId, Tradition, Book, int Chapter,
 *                       int Verse, Text).
 *   Board   : IFaithBoard -> InMemoryFaithBoard
 *               Schedule (ServiceId keyed), ServicesBetween(start, end) inclusive
 *               ordered by StartUtc asc, SubmitPrayer (appends), RecentPrayers
 *               (limit=20) newest-first by SubmittedUtc, AddScripture (ReferenceId
 *               keyed), Lookup(tradition, book, chapter, verse) — first ordinal
 *               match, ByTradition(tradition) (OrdinalIgnoreCase; insertion order).
 *
 * DateTimeOffset as Unix ms UTC. Lookup uses ordinal equality on Tradition/Book
 * (the C# uses ==); ByTradition uses OrdinalIgnoreCase.
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

/* FaithService(ServiceId, CommunityName, Title, DateTimeOffset StartUtc,
 * Location). */
typedef struct {
    char   *service_id;     /* owned, non-null */
    char   *community_name; /* owned, non-null */
    char   *title;          /* owned, non-null */
    int64_t start_utc_ms;
    char   *location;       /* owned, non-null */
} ca_faith_service_t;

void ca_faith_service_free(ca_faith_service_t *s);
void ca_faith_service_free_array(ca_faith_service_t *arr, size_t count);

/* PrayerRequest(RequestId, Author, Body, DateTimeOffset SubmittedUtc,
 * bool IsAnonymous). */
typedef struct {
    char   *request_id;   /* owned, non-null */
    char   *author;       /* owned, non-null */
    char   *body;         /* owned, non-null */
    int64_t submitted_utc_ms;
    bool    is_anonymous;
} ca_faith_prayer_t;

void ca_faith_prayer_free(ca_faith_prayer_t *p);
void ca_faith_prayer_free_array(ca_faith_prayer_t *arr, size_t count);

/* ScriptureReference(ReferenceId, Tradition, Book, int Chapter, int Verse,
 * Text). */
typedef struct {
    char   *reference_id; /* owned, non-null */
    char   *tradition;    /* owned, non-null */
    char   *book;         /* owned, non-null */
    int     chapter;
    int     verse;
    char   *text;         /* owned, non-null */
} ca_faith_scripture_t;

void ca_faith_scripture_free(ca_faith_scripture_t *s);
void ca_faith_scripture_free_array(ca_faith_scripture_t *arr, size_t count);

typedef struct ca_faith_board ca_faith_board_t;

ca_faith_board_t *ca_faith_board_create(void); /* NULL on OOM */
void ca_faith_board_destroy(ca_faith_board_t *b);

/* Schedule(s) — ServiceId keyed set. 0 / -1. */
int ca_faith_board_schedule(ca_faith_board_t *b, const ca_faith_service_t *s);

/* ServicesBetween(start_ms, end_ms) -> fresh owned array (StartUtc in [start,end])
 * ordered by StartUtc asc. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_faith_service_t *ca_faith_board_services_between(const ca_faith_board_t *b,
                                                    int64_t start_ms,
                                                    int64_t end_ms,
                                                    size_t *out_count);

/* SubmitPrayer(p) — appends. 0 / -1. */
int ca_faith_board_submit_prayer(ca_faith_board_t *b,
                                 const ca_faith_prayer_t *p);

/* RecentPrayers(limit) -> fresh owned array newest-first by SubmittedUtc, first
 * `limit`. limit must be > 0 (SIZE_MAX on limit<=0 / bad args). Use 20 for the C#
 * default. NULL + 0 empty. */
ca_faith_prayer_t *ca_faith_board_recent_prayers(const ca_faith_board_t *b,
                                                 int limit, size_t *out_count);

/* AddScripture(s) — ReferenceId keyed set. 0 / -1. */
int ca_faith_board_add_scripture(ca_faith_board_t *b,
                                 const ca_faith_scripture_t *s);

/* Lookup(tradition, book, chapter, verse) -> writes the first ordinal-matching
 * reference into *out, true; false (C# null) on miss/bad args. */
bool ca_faith_board_lookup(const ca_faith_board_t *b, const char *tradition,
                           const char *book, int chapter, int verse,
                           ca_faith_scripture_t *out);

/* ByTradition(tradition) -> fresh owned array (insertion order) with Tradition
 * matching (OrdinalIgnoreCase). NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_faith_scripture_t *ca_faith_board_by_tradition(const ca_faith_board_t *b,
                                                  const char *tradition,
                                                  size_t *out_count);

/* ServiceCount — number of scheduled services. NULL board → 0. */
size_t ca_faith_board_service_count(const ca_faith_board_t *b);

/* RemoveService(serviceId) — drop a service by id. Returns true if present. */
bool ca_faith_board_remove_service(ca_faith_board_t *b, const char *service_id);

/* ServicesAt(location) -> fresh owned array of services whose Location matches
 * (OrdinalIgnoreCase), ordered by StartUtc ascending. NULL + 0 empty; NULL +
 * SIZE_MAX on error. */
ca_faith_service_t *ca_faith_board_services_at(const ca_faith_board_t *b,
                                               const char *location,
                                               size_t *out_count);

/* PrayersByAuthor(author) -> fresh owned array of NON-anonymous prayers whose
 * Author matches (OrdinalIgnoreCase), ordered by SubmittedUtc descending
 * (anonymous prayers are excluded regardless of author). NULL + 0 empty; NULL +
 * SIZE_MAX on error. */
ca_faith_prayer_t *ca_faith_board_prayers_by_author(const ca_faith_board_t *b,
                                                    const char *author,
                                                    size_t *out_count);

/* AnonymousPrayerCount — number of prayers flagged IsAnonymous. NULL board → 0. */
size_t ca_faith_board_anonymous_prayer_count(const ca_faith_board_t *b);

/* ChapterVerses(tradition, book, chapter) -> fresh owned array of references in
 * that tradition (OrdinalIgnoreCase) + book (OrdinalIgnoreCase) + chapter,
 * ordered by Verse ascending. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_faith_scripture_t *ca_faith_board_chapter_verses(const ca_faith_board_t *b,
                                                    const char *tradition,
                                                    const char *book,
                                                    int chapter,
                                                    size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FAITH_H */
