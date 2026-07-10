#ifndef CIRCLE_AI_MEDIA_H
#define CIRCLE_AI_MEDIA_H

/*
 * media.h — CircleAI.Media + CircleAI.MediaHub (C11 port).
 *
 * Ports two closely-related media verticals 1:1:
 *
 *   CircleAI.Media (MediaPrimitives.cs) — the audio/video/image asset catalog:
 *     Enum      : MediaKind { Audio, Video, Image }.
 *     Record    : MediaAsset(AssetId, Title, Kind, Duration?, Bytes, Mime,
 *                 CreatedAtUtc). Duration is optional (has_duration gate).
 *     Library   : IMediaLibrary — InMemoryMediaLibrary. Add (AssetId required),
 *                 Get(id) -> asset?, ListByKind(kind) ordered by CreatedAtUtc
 *                 descending, Search(q, topK=20) title-substring (OrdinalIgnore-
 *                 Case) ordered by CreatedAtUtc descending, top-K.
 *
 *   CircleAI.MediaHub (Contracts.cs + InMemoryMediaHub.cs + NullImplementations)
 *   — the media-server layer:
 *     Records   : MediaItem(ItemId, Title, Kind, Duration, MimeType);
 *                 PlaybackPosition(ItemId, Position, AtUtc).
 *     Library   : IMediaLibrary (async) — BackendId; GetAsync(id); SearchAsync(
 *                 query, topK=20) title-substring (OrdinalIgnoreCase) ordered by
 *                 Title (OrdinalIgnoreCase ascending), top-K. Ships InMemory +
 *                 Null.
 *     Playback  : ISyncedPlayback — BackendId; JoinSession(sessionId, userId);
 *                 BroadcastPosition(sessionId, pos) fan-out to subscribers;
 *                 Subscribe(sessionId, handler) -> disposable token. Ships
 *                 InMemory (broadcast/subscribe pub-sub) + Null.
 *
 * The C# async methods complete synchronously (ValueTask). The C# Channel /
 * subscriber lists are unbounded; broadcast snapshots the subscriber list, then
 * invokes each handler outside the snapshot (mirrors state.Subscribers.ToArray()
 * before the await loop) so a handler that unsubscribes mid-broadcast is safe and
 * no publish is dropped.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. TimeSpan/DateTimeOffset carried as int64
 * ticks/Unix-ms passed in (see field docs). Ordinal string comparison == byte
 * compare; OrdinalIgnoreCase == ASCII-lowercased byte compare.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * CircleAI.Media — MediaKind + MediaAsset + InMemoryMediaLibrary
 * =========================================================================== */

typedef enum {
    CA_MEDIA_KIND_AUDIO = 0,
    CA_MEDIA_KIND_VIDEO = 1,
    CA_MEDIA_KIND_IMAGE = 2
} ca_media_kind_t;

/* MediaAsset(AssetId, Title, Kind, TimeSpan? Duration, long Bytes, string Mime,
 * DateTimeOffset CreatedAtUtc). Duration optional: has_duration==false means the
 * C# null. duration_ticks is .NET TimeSpan ticks (100ns units). created_at_utc_ms
 * carries DateTimeOffset ordering (Unix ms UTC). */
typedef struct {
    char   *asset_id;         /* owned, non-null */
    char   *title;            /* owned, non-null */
    ca_media_kind_t kind;
    bool    has_duration;
    int64_t duration_ticks;   /* valid only when has_duration */
    int64_t bytes;
    char   *mime;             /* owned, non-null */
    int64_t created_at_utc_ms;
} ca_media_asset_t;

/* Deep-free the owned fields of a single asset (does not free the struct). */
void ca_media_asset_free(ca_media_asset_t *a);
/* Free an owned array of assets (each field + the block). */
void ca_media_asset_free_array(ca_media_asset_t *arr, size_t count);

typedef struct ca_media_library ca_media_library_t;

/* InMemoryMediaLibrary(). NULL on OOM. */
ca_media_library_t *ca_media_library_create(void);
void ca_media_library_destroy(ca_media_library_t *lib);

/* Add(asset) — deep-copies. AssetId required (non-null / non-whitespace) or the
 * add is rejected. Assigning an existing AssetId replaces it (dictionary set).
 * Returns 0 on success, -1 on bad args / OOM. */
int ca_media_library_add(ca_media_library_t *lib, const ca_media_asset_t *asset);

/* Get(id) -> writes a fresh owned copy into *out and returns true; returns false
 * (the C# null) when absent or on bad args. */
bool ca_media_library_get(const ca_media_library_t *lib, const char *id,
                          ca_media_asset_t *out);

/* ListByKind(kind) -> fresh owned array (*out_count) ordered by CreatedAtUtc
 * descending. NULL + *out_count 0 when empty; NULL + SIZE_MAX on error. */
ca_media_asset_t *ca_media_library_list_by_kind(const ca_media_library_t *lib,
                                                ca_media_kind_t kind,
                                                size_t *out_count);

/* Search(q, topK) -> fresh owned array (*out_count): Title contains q
 * (OrdinalIgnoreCase), ordered by CreatedAtUtc descending, first top_k. q must be
 * non-null; top_k must be > 0. NULL + SIZE_MAX on error (q NULL / top_k <= 0);
 * NULL + 0 when no hits. Use top_k 20 for the C# default. */
ca_media_asset_t *ca_media_library_search(const ca_media_library_t *lib,
                                          const char *q, int top_k,
                                          size_t *out_count);

/* Number of assets currently held. */
size_t ca_media_library_count(const ca_media_library_t *lib);

/* ===========================================================================
 * CircleAI.MediaHub — MediaItem + PlaybackPosition
 * =========================================================================== */

/* MediaItem(ItemId, Title, Kind, TimeSpan Duration, MimeType). Kind here is a
 * free-form string (C# string, not the Media enum). duration_ticks is TimeSpan
 * ticks. */
typedef struct {
    char   *item_id;        /* owned, non-null */
    char   *title;          /* owned, non-null */
    char   *kind;           /* owned, non-null */
    int64_t duration_ticks;
    char   *mime_type;      /* owned, non-null */
} ca_mediahub_item_t;

void ca_mediahub_item_free(ca_mediahub_item_t *i);
void ca_mediahub_item_free_array(ca_mediahub_item_t *arr, size_t count);

/* PlaybackPosition(ItemId, TimeSpan Position, DateTimeOffset AtUtc). */
typedef struct {
    char   *item_id;        /* owned, non-null */
    int64_t position_ticks; /* TimeSpan ticks */
    int64_t at_utc_ms;      /* DateTimeOffset as Unix ms UTC */
} ca_mediahub_position_t;

void ca_mediahub_position_free(ca_mediahub_position_t *p);

/* ===========================================================================
 * CircleAI.MediaHub.IMediaLibrary — InMemory + Null
 * =========================================================================== */

typedef struct ca_mediahub_library ca_mediahub_library_t;

/* InMemoryMediaLibrary() (BackendId "in-memory"). NULL on OOM. */
ca_mediahub_library_t *ca_mediahub_library_create(void);
/* NullMediaLibrary (BackendId "null"; GetAsync -> null; SearchAsync -> empty). */
ca_mediahub_library_t *ca_mediahub_null_library_create(void);
void ca_mediahub_library_destroy(ca_mediahub_library_t *lib);

/* BackendId ("in-memory" or "null"). */
const char *ca_mediahub_library_backend_id(const ca_mediahub_library_t *lib);

/* Add(item) — deep-copies; assigning an existing ItemId replaces it. In-memory
 * only (a no-op reject on the Null library). 0 / -1 on bad args / OOM. */
int ca_mediahub_library_add(ca_mediahub_library_t *lib,
                            const ca_mediahub_item_t *item);

/* GetAsync(id) -> writes a fresh owned copy into *out and returns true; returns
 * false (C# null) when absent. id required (non-null / non-whitespace): a
 * whitespace/NULL id is an error and returns false with *out zeroed. */
bool ca_mediahub_library_get(const ca_mediahub_library_t *lib, const char *id,
                             ca_mediahub_item_t *out);

/* SearchAsync(query, topK) -> fresh owned array (*out_count): Title contains
 * query (OrdinalIgnoreCase), ordered by Title (OrdinalIgnoreCase ascending),
 * first top_k. query must be non-null; top_k must be > 0. NULL + SIZE_MAX on
 * error; NULL + 0 when no hits (or on the Null library). Use top_k 20 for the C#
 * default. */
ca_mediahub_item_t *ca_mediahub_library_search(const ca_mediahub_library_t *lib,
                                               const char *query, int top_k,
                                               size_t *out_count);

/* ===========================================================================
 * CircleAI.MediaHub.ISyncedPlayback — InMemory + Null
 *
 * The C# EventHandler-style Subscribe returns an IDisposable; here Subscribe
 * returns an owned token you dispose with ca_mediahub_playback_unsubscribe.
 * Handlers receive a borrowed PlaybackPosition (valid for the call only).
 * BroadcastPosition invokes every live subscriber for the session synchronously,
 * snapshotting the subscriber list first so a handler may unsubscribe safely.
 * =========================================================================== */

typedef struct ca_mediahub_playback ca_mediahub_playback_t;
typedef struct ca_mediahub_playback_sub ca_mediahub_playback_sub_t;

typedef void (*ca_mediahub_position_handler_fn)(void *ctx,
                                                const ca_mediahub_position_t *pos);

/* InMemorySyncedPlayback() (BackendId "in-memory"). NULL on OOM. */
ca_mediahub_playback_t *ca_mediahub_playback_create(void);
/* NullSyncedPlayback (BackendId "null"; Join/Broadcast no-op; Subscribe empty). */
ca_mediahub_playback_t *ca_mediahub_null_playback_create(void);
void ca_mediahub_playback_destroy(ca_mediahub_playback_t *pb);

const char *ca_mediahub_playback_backend_id(const ca_mediahub_playback_t *pb);

/* JoinSessionAsync(sessionId, userId). Both required (non-null / non-whitespace).
 * Adds userId to the session's member set (idempotent). 0 / -1 on bad args/OOM.
 * On the Null playback this is a no-op returning 0. */
int ca_mediahub_playback_join(ca_mediahub_playback_t *pb, const char *session_id,
                              const char *user_id);

/* Number of members in a session (0 if unknown). */
size_t ca_mediahub_playback_member_count(const ca_mediahub_playback_t *pb,
                                         const char *session_id);

/* BroadcastPositionAsync(sessionId, pos). sessionId required; pos required (its
 * ItemId non-null). When the session is unknown this returns 0 (C# early return).
 * Delivers a borrowed copy to every live subscriber for the session (and buffers
 * a fresh copy on each subscriber's cursor). Returns the number of subscribers
 * notified, or -1 on bad args. No-op (returns 0) on the Null playback. */
int ca_mediahub_playback_broadcast(ca_mediahub_playback_t *pb,
                                   const char *session_id,
                                   const ca_mediahub_position_t *pos);

/* Subscribe(sessionId, handler) -> owned token (dispose to unsubscribe). handler
 * required. sessionId required (non-null / non-whitespace). NULL on bad args/OOM.
 * On the Null playback returns a live-but-inert token you still dispose. */
ca_mediahub_playback_sub_t *ca_mediahub_playback_subscribe(
    ca_mediahub_playback_t *pb, const char *session_id,
    ca_mediahub_position_handler_fn handler, void *ctx);

/* Dispose the subscription (removes the handler from the session). */
void ca_mediahub_playback_unsubscribe(ca_mediahub_playback_t *pb,
                                      ca_mediahub_playback_sub_t *sub);

/* Drain the next buffered position from a subscription's cursor into *out
 * (freshly owned; caller frees with ca_mediahub_position_free). Returns true if a
 * position was produced, false when the cursor is empty. Lets a test read what a
 * broadcast delivered without a callback. */
bool ca_mediahub_playback_sub_next(ca_mediahub_playback_sub_t *sub,
                                   ca_mediahub_position_t *out);
/* Buffered (undrained) positions on the cursor. */
size_t ca_mediahub_playback_sub_pending(const ca_mediahub_playback_sub_t *sub);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MEDIA_H */
