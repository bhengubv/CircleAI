#ifndef CIRCLE_AI_WEB_H
#define CIRCLE_AI_WEB_H

/*
 * web.h — CircleAI.Web portable primitives (C11 port).
 *
 * Ports the PORTABLE part of CircleAI.Web — WebPrimitives.cs:
 *     Record : RouteDescriptor(Path, Method, HandlerName, Tags[])
 *              -> ca_route_descriptor_t.
 *     Record : PageMetadata(Path, Title, Description?, Keywords[])
 *              -> ca_page_metadata_t.
 *     Record : CachedResponse(Key, byte[] Body, Mime, DateTimeOffset ExpiresUtc)
 *              -> ca_cached_response_t (ExpiresUtc as int64 Unix ms UTC).
 *     IWebBoard / InMemoryWebBoard -> ca_web_board_t.
 *
 * NOT ported (not portable — left in C#): WebCompanionService.cs and
 * ServiceCollectionExtensions. Those are a Blazor / Microsoft.Extensions.
 * DependencyInjection adapter with no libc-expressible surface.
 *
 * Explicit clock: the C# board read DateTimeOffset.UtcNow inside Cache/Lookup to
 * decide expiry. C has no ambient UTC clock, so Cache and Lookup take an explicit
 * now_ms (Unix ms UTC) parameter — the caller supplies "now". This is the only
 * semantic change; everything else mirrors the C# 1:1.
 *
 * Conventions: ca_ prefix, _t types, opaque handle, strdup-owning fields with
 * matching *_free / *_free_array, deep-copy out-params, errors via NULL / -1 /
 * false / count SIZE_MAX. Description may be NULL (the C# string?). Ordinal string
 * comparison == byte compare; OrdinalIgnoreCase == ASCII-lowercased byte compare.
 * ToUpperInvariant on the route key == ASCII-uppercase the method. Linear arrays,
 * no hashtable, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * RouteDescriptor / PageMetadata / CachedResponse
 * =========================================================================== */

/* RouteDescriptor(Path, Method, HandlerName, IReadOnlyList<string> Tags). */
typedef struct {
    char   *path;          /* owned, non-null */
    char   *method;        /* owned, non-null */
    char   *handler_name;  /* owned, non-null */
    char  **tags;          /* owned array of owned strings (may be NULL when 0) */
    size_t  tags_count;
} ca_route_descriptor_t;

void ca_route_descriptor_free(ca_route_descriptor_t *r);
void ca_route_descriptor_free_array(ca_route_descriptor_t *arr, size_t count);

/* PageMetadata(Path, Title, Description?, IReadOnlyList<string> Keywords).
 * description may be NULL (the C# string?). */
typedef struct {
    char   *path;            /* owned, non-null */
    char   *title;           /* owned, non-null */
    char   *description;     /* owned; NULL ok */
    char  **keywords;        /* owned array of owned strings (may be NULL when 0) */
    size_t  keywords_count;
} ca_page_metadata_t;

void ca_page_metadata_free(ca_page_metadata_t *m);

/* CachedResponse(Key, byte[] Body, Mime, DateTimeOffset ExpiresUtc).
 * expires_utc_ms carries ExpiresUtc as Unix ms UTC. body may be NULL when
 * body_len == 0 (an empty payload). */
typedef struct {
    char    *key;             /* owned, non-null */
    uint8_t *body;            /* owned; may be NULL when body_len == 0 */
    size_t   body_len;
    char    *mime;            /* owned, non-null */
    int64_t  expires_utc_ms;
} ca_cached_response_t;

void ca_cached_response_free(ca_cached_response_t *c);

/* ===========================================================================
 * IWebBoard / InMemoryWebBoard
 * =========================================================================== */

typedef struct ca_web_board ca_web_board_t;

/* InMemoryWebBoard(). NULL on OOM. */
ca_web_board_t *ca_web_board_create(void);
void ca_web_board_destroy(ca_web_board_t *board);

/* Register(r): key = "<Method-ASCII-uppercased> <Path>"; an existing key is
 * replaced (dictionary set, Ordinal key). Deep-copies r. Returns 0 on success,
 * -1 on NULL args (C# ArgumentNullException) / OOM. */
int ca_web_board_register(ca_web_board_t *board, const ca_route_descriptor_t *r);

/* RoutesByMethod(method): fresh owned array (*out_count) of routes whose Method
 * equals method (OrdinalIgnoreCase), ordered by Path ascending (Ordinal). method
 * must be non-null / non-whitespace (C# ArgumentException) -> NULL + SIZE_MAX.
 * NULL + 0 when no match. Caller frees with ca_route_descriptor_free_array. */
ca_route_descriptor_t *ca_web_board_routes_by_method(const ca_web_board_t *board,
                                                    const char *method,
                                                    size_t *out_count);

/* SetMetadata(m): key by Path (OrdinalIgnoreCase); an existing key is replaced.
 * Deep-copies m. Returns 0 on success, -1 on NULL args / OOM. */
int ca_web_board_set_metadata(ca_web_board_t *board, const ca_page_metadata_t *m);

/* GetMetadata(path): writes a fresh owned copy into *out and returns true; returns
 * false (the C# null) when absent or on bad args (with *out zeroed). Caller frees
 * *out with ca_page_metadata_free. */
bool ca_web_board_get_metadata(const ca_web_board_t *board, const char *path,
                              ca_page_metadata_t *out);

/* Cache(c) with explicit now_ms: when c.ExpiresUtc <= now the entry is skipped
 * (not stored), mirroring the C# early return. Otherwise store by Key (Ordinal),
 * replacing an existing key. Deep-copies c. Returns 0 on success (including the
 * skip), -1 on NULL args / OOM. */
int ca_web_board_cache(ca_web_board_t *board, const ca_cached_response_t *c,
                      int64_t now_ms);

/* Lookup(key) with explicit now_ms: key must be non-null / non-whitespace (C#
 * ArgumentException) -> false with *out zeroed. When absent -> false. When expired
 * (ExpiresUtc <= now) the entry is removed and false is returned. Otherwise writes
 * a fresh owned copy into *out and returns true. Caller frees *out with
 * ca_cached_response_free. */
bool ca_web_board_lookup(ca_web_board_t *board, const char *key, int64_t now_ms,
                        ca_cached_response_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WEB_H */
