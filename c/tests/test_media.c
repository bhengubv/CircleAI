/*
 * test_media.c — CircleAI.Media + CircleAI.MediaHub (C11 port).
 *
 * Verifies MediaKind/MediaAsset/InMemoryMediaLibrary (Media) and
 * MediaItem/PlaybackPosition/InMemory+Null MediaLibrary/InMemory+Null
 * SyncedPlayback (MediaHub) against the C# reference (MediaPrimitives.cs,
 * Contracts.cs, InMemoryMediaHub.cs, NullImplementations.cs).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── helpers ────────────────────────────────────────────────────────────── */

static ca_media_asset_t mk_asset(const char *id, const char *title,
                                 ca_media_kind_t kind, int64_t created_ms) {
    ca_media_asset_t a;
    memset(&a, 0, sizeof(a));
    a.asset_id = (char *)id;
    a.title = (char *)title;
    a.kind = kind;
    a.has_duration = true;
    a.duration_ticks = 10000000LL; /* 1s */
    a.bytes = 1234;
    a.mime = (char *)"audio/mpeg";
    a.created_at_utc_ms = created_ms;
    return a;
}

/* ── CircleAI.Media : InMemoryMediaLibrary ──────────────────────────────── */

static void test_media_library(void) {
    ca_media_library_t *lib = ca_media_library_create();
    assert(lib);
    assert(ca_media_library_count(lib) == 0);

    /* Add rejects null/whitespace AssetId. */
    ca_media_asset_t bad = mk_asset("   ", "x", CA_MEDIA_KIND_AUDIO, 100);
    assert(ca_media_library_add(lib, &bad) == -1);
    ca_media_asset_t bad2 = mk_asset(NULL, "x", CA_MEDIA_KIND_AUDIO, 100);
    assert(ca_media_library_add(lib, &bad2) == -1);
    assert(ca_media_library_count(lib) == 0);

    /* Add three assets: two audio (different times), one video. */
    ca_media_asset_t a1 = mk_asset("a1", "Sunrise Session", CA_MEDIA_KIND_AUDIO, 100);
    ca_media_asset_t a2 = mk_asset("a2", "Sunset Session",  CA_MEDIA_KIND_AUDIO, 300);
    ca_media_asset_t v1 = mk_asset("v1", "Sunny Video",     CA_MEDIA_KIND_VIDEO, 200);
    assert(ca_media_library_add(lib, &a1) == 0);
    assert(ca_media_library_add(lib, &a2) == 0);
    assert(ca_media_library_add(lib, &v1) == 0);
    assert(ca_media_library_count(lib) == 3);

    /* Get -> deep copy. */
    ca_media_asset_t got;
    assert(ca_media_library_get(lib, "a1", &got));
    assert(strcmp(got.asset_id, "a1") == 0 && strcmp(got.title, "Sunrise Session") == 0);
    assert(got.kind == CA_MEDIA_KIND_AUDIO && got.has_duration && got.bytes == 1234);
    ca_media_asset_free(&got);
    assert(!ca_media_library_get(lib, "nope", &got));  /* null */

    /* Add replaces on same id. */
    ca_media_asset_t a1b = mk_asset("a1", "Renamed", CA_MEDIA_KIND_IMAGE, 999);
    assert(ca_media_library_add(lib, &a1b) == 0);
    assert(ca_media_library_count(lib) == 3);  /* replaced, not appended */
    assert(ca_media_library_get(lib, "a1", &got));
    assert(strcmp(got.title, "Renamed") == 0 && got.kind == CA_MEDIA_KIND_IMAGE);
    ca_media_asset_free(&got);
    /* restore a1 as audio@100 for the ordering tests. */
    assert(ca_media_library_add(lib, &a1) == 0);

    /* ListByKind(Audio) -> a2(300) then a1(100), CreatedAtUtc descending. */
    size_t n = 0;
    ca_media_asset_t *arr = ca_media_library_list_by_kind(lib, CA_MEDIA_KIND_AUDIO, &n);
    assert(n == 2 && arr);
    assert(strcmp(arr[0].asset_id, "a2") == 0);   /* newest first */
    assert(strcmp(arr[1].asset_id, "a1") == 0);
    ca_media_asset_free_array(arr, n);

    /* ListByKind(Image) -> none. */
    arr = ca_media_library_list_by_kind(lib, CA_MEDIA_KIND_IMAGE, &n);
    assert(n == 0 && arr == NULL);

    /* Search "sun" (OrdinalIgnoreCase) -> all three titles contain it, ordered by
     * CreatedAtUtc descending: a2(300), v1(200), a1(100). */
    arr = ca_media_library_search(lib, "sun", 20, &n);
    assert(n == 3);
    assert(strcmp(arr[0].asset_id, "a2") == 0);
    assert(strcmp(arr[1].asset_id, "v1") == 0);
    assert(strcmp(arr[2].asset_id, "a1") == 0);
    ca_media_asset_free_array(arr, n);

    /* Search case-insensitive + topK truncation. */
    arr = ca_media_library_search(lib, "SESSION", 1, &n);
    assert(n == 1 && strcmp(arr[0].asset_id, "a2") == 0);  /* newest of the 2 */
    ca_media_asset_free_array(arr, n);

    /* Search miss. */
    arr = ca_media_library_search(lib, "zzz", 20, &n);
    assert(n == 0 && arr == NULL);

    /* Search error paths: q NULL, topK <= 0 -> SIZE_MAX. */
    arr = ca_media_library_search(lib, NULL, 20, &n);
    assert(n == (size_t)-1 && arr == NULL);
    arr = ca_media_library_search(lib, "sun", 0, &n);
    assert(n == (size_t)-1 && arr == NULL);

    ca_media_library_destroy(lib);
    printf("  media_library: ok\n");
}

/* ── CircleAI.MediaHub : IMediaLibrary (async) ──────────────────────────── */

static ca_mediahub_item_t mk_item(const char *id, const char *title) {
    ca_mediahub_item_t i;
    memset(&i, 0, sizeof(i));
    i.item_id = (char *)id;
    i.title = (char *)title;
    i.kind = (char *)"audio";
    i.duration_ticks = 20000000LL;
    i.mime_type = (char *)"audio/ogg";
    return i;
}

static void test_mediahub_library(void) {
    ca_mediahub_library_t *lib = ca_mediahub_library_create();
    assert(lib);
    assert(strcmp(ca_mediahub_library_backend_id(lib), "in-memory") == 0);

    ca_mediahub_item_t b = mk_item("b", "Banana Tune");
    ca_mediahub_item_t a = mk_item("a", "Apple Tune");
    ca_mediahub_item_t c = mk_item("c", "Cherry Track");
    assert(ca_mediahub_library_add(lib, &b) == 0);
    assert(ca_mediahub_library_add(lib, &a) == 0);
    assert(ca_mediahub_library_add(lib, &c) == 0);

    /* GetAsync -> copy; whitespace id -> null; miss -> null. */
    ca_mediahub_item_t got;
    assert(ca_mediahub_library_get(lib, "a", &got));
    assert(strcmp(got.title, "Apple Tune") == 0 && strcmp(got.kind, "audio") == 0);
    ca_mediahub_item_free(&got);
    assert(!ca_mediahub_library_get(lib, "   ", &got));
    assert(!ca_mediahub_library_get(lib, "zz", &got));

    /* SearchAsync "tune" -> Apple, Banana ordered by Title ascending (OrdinalIC). */
    size_t n = 0;
    ca_mediahub_item_t *arr = ca_mediahub_library_search(lib, "tune", 20, &n);
    assert(n == 2);
    assert(strcmp(arr[0].item_id, "a") == 0);  /* "Apple" < "Banana" */
    assert(strcmp(arr[1].item_id, "b") == 0);
    ca_mediahub_item_free_array(arr, n);

    /* topK truncation keeps the ascending-title winner. */
    arr = ca_mediahub_library_search(lib, "tune", 1, &n);
    assert(n == 1 && strcmp(arr[0].item_id, "a") == 0);
    ca_mediahub_item_free_array(arr, n);

    /* error paths. */
    arr = ca_mediahub_library_search(lib, NULL, 20, &n);
    assert(n == (size_t)-1);
    arr = ca_mediahub_library_search(lib, "tune", -1, &n);
    assert(n == (size_t)-1);

    ca_mediahub_library_destroy(lib);

    /* Null library. */
    ca_mediahub_library_t *nul = ca_mediahub_null_library_create();
    assert(strcmp(ca_mediahub_library_backend_id(nul), "null") == 0);
    assert(!ca_mediahub_library_get(nul, "a", &got));  /* always null */
    arr = ca_mediahub_library_search(nul, "x", 20, &n);
    assert(n == 0 && arr == NULL);                     /* always empty */
    assert(ca_mediahub_library_add(nul, &a) == -1);    /* not addable */
    ca_mediahub_library_destroy(nul);

    printf("  mediahub_library: ok\n");
}

/* ── CircleAI.MediaHub : ISyncedPlayback ────────────────────────────────── */

static int g_pos_hits;
static char g_last_item[64];
static void pos_cb(void *ctx, const ca_mediahub_position_t *p) {
    (void)ctx;
    assert(p && p->item_id);
    g_pos_hits++;
    snprintf(g_last_item, sizeof(g_last_item), "%s", p->item_id);
}

static ca_mediahub_position_t mk_pos(const char *item, int64_t ticks, int64_t at) {
    ca_mediahub_position_t p;
    memset(&p, 0, sizeof(p));
    p.item_id = (char *)item;
    p.position_ticks = ticks;
    p.at_utc_ms = at;
    return p;
}

static void test_synced_playback(void) {
    ca_mediahub_playback_t *pb = ca_mediahub_playback_create();
    assert(pb);
    assert(strcmp(ca_mediahub_playback_backend_id(pb), "in-memory") == 0);

    /* Join validation. */
    assert(ca_mediahub_playback_join(pb, "  ", "u1") == -1);
    assert(ca_mediahub_playback_join(pb, "s1", "") == -1);

    /* Join is idempotent per user. */
    assert(ca_mediahub_playback_join(pb, "s1", "alice") == 0);
    assert(ca_mediahub_playback_join(pb, "s1", "bob") == 0);
    assert(ca_mediahub_playback_join(pb, "s1", "alice") == 0);  /* dup */
    assert(ca_mediahub_playback_member_count(pb, "s1") == 2);
    assert(ca_mediahub_playback_member_count(pb, "unknown") == 0);

    /* Broadcast to a session with no subscribers -> 0 delivered. */
    ca_mediahub_position_t p = mk_pos("track-9", 5000, 111);
    assert(ca_mediahub_playback_broadcast(pb, "s1", &p) == 0);

    /* Broadcast to an unknown session -> 0 (early return). */
    assert(ca_mediahub_playback_broadcast(pb, "ghost", &p) == 0);

    /* Broadcast validation: NULL pos, whitespace session. */
    assert(ca_mediahub_playback_broadcast(pb, "s1", NULL) == -1);
    assert(ca_mediahub_playback_broadcast(pb, "  ", &p) == -1);

    /* Subscribe two handlers, broadcast, both fire + both buffer. */
    g_pos_hits = 0; g_last_item[0] = '\0';
    ca_mediahub_playback_sub_t *s1 =
        ca_mediahub_playback_subscribe(pb, "s1", pos_cb, NULL);
    ca_mediahub_playback_sub_t *s2 =
        ca_mediahub_playback_subscribe(pb, "s1", pos_cb, NULL);
    assert(s1 && s2);

    int delivered = ca_mediahub_playback_broadcast(pb, "s1", &p);
    assert(delivered == 2);
    assert(g_pos_hits == 2);                            /* both handlers fired */
    assert(strcmp(g_last_item, "track-9") == 0);
    assert(ca_mediahub_playback_sub_pending(s1) == 1);
    assert(ca_mediahub_playback_sub_pending(s2) == 1);

    /* Drain s1's cursor -> the broadcast copy. */
    ca_mediahub_position_t out;
    assert(ca_mediahub_playback_sub_next(s1, &out));
    assert(strcmp(out.item_id, "track-9") == 0 && out.position_ticks == 5000
           && out.at_utc_ms == 111);
    ca_mediahub_position_free(&out);
    assert(ca_mediahub_playback_sub_pending(s1) == 0);
    assert(!ca_mediahub_playback_sub_next(s1, &out));   /* empty now */

    /* Unsubscribe s1 -> only s2 gets the next broadcast. */
    ca_mediahub_playback_unsubscribe(pb, s1);
    g_pos_hits = 0;
    ca_mediahub_position_t p2 = mk_pos("track-42", 6000, 222);
    delivered = ca_mediahub_playback_broadcast(pb, "s1", &p2);
    assert(delivered == 1 && g_pos_hits == 1);
    assert(ca_mediahub_playback_sub_pending(s2) == 2);  /* two broadcasts buffered */

    ca_mediahub_playback_unsubscribe(pb, s2);
    ca_mediahub_playback_destroy(pb);

    /* Null playback: join/broadcast no-op; subscribe returns an inert token. */
    ca_mediahub_playback_t *nul = ca_mediahub_null_playback_create();
    assert(strcmp(ca_mediahub_playback_backend_id(nul), "null") == 0);
    assert(ca_mediahub_playback_join(nul, "s", "u") == 0);
    assert(ca_mediahub_playback_broadcast(nul, "s", &p) == 0);
    ca_mediahub_playback_sub_t *ns = ca_mediahub_playback_subscribe(nul, "s", pos_cb, NULL);
    assert(ns);
    g_pos_hits = 0;
    assert(ca_mediahub_playback_broadcast(nul, "s", &p) == 0);
    assert(g_pos_hits == 0);                             /* never fires */
    assert(ca_mediahub_playback_sub_pending(ns) == 0);
    ca_mediahub_playback_unsubscribe(nul, ns);
    ca_mediahub_playback_destroy(nul);

    printf("  synced_playback: ok\n");
}

int main(void) {
    test_media_library();
    test_mediahub_library();
    test_synced_playback();
    printf("test_media: all assertions passed\n");
    return 0;
}
