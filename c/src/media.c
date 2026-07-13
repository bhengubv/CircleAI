/*
 * media.c — CircleAI.Media + CircleAI.MediaHub (C11 port).
 *
 * CircleAI.Media    : MediaKind, MediaAsset, InMemoryMediaLibrary.
 * CircleAI.MediaHub : MediaItem, PlaybackPosition, InMemory/Null MediaLibrary,
 *                     InMemory/Null SyncedPlayback (broadcast/subscribe pub-sub).
 *
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads.
 */

#include "circle_ai/media.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── shared helpers ─────────────────────────────────────────────────────── */

static char *md_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *md_strdup_empty(const char *s) { return md_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace. */
static bool md_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* OrdinalIgnoreCase substring test: does needle occur in hay (ASCII CI)? An empty
 * needle matches (string.Contains(""), which is always true in C#). */
static bool md_ci_contains(const char *hay, const char *needle) {
    if (!hay || !needle) return false;
    if (*needle == '\0') return true;
    size_t nl = strlen(needle);
    for (const char *h = hay; *h; ++h) {
        size_t k = 0;
        while (k < nl && h[k] &&
               tolower((unsigned char)h[k]) == tolower((unsigned char)needle[k]))
            k++;
        if (k == nl) return true;
    }
    return false;
}

/* OrdinalIgnoreCase full-string comparison (StringComparer.OrdinalIgnoreCase). */
static int md_ci_cmp(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (;; ++x, ++y) {
        int ca = tolower(*x), cb = tolower(*y);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
}

/* ===========================================================================
 * CircleAI.Media — MediaAsset
 * =========================================================================== */

void ca_media_asset_free(ca_media_asset_t *a) {
    if (!a) return;
    free(a->asset_id);
    free(a->title);
    free(a->mime);
    a->asset_id = a->title = a->mime = NULL;
}
void ca_media_asset_free_array(ca_media_asset_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_media_asset_free(&arr[i]);
    free(arr);
}

/* Deep-copy src into dst (dst assumed uninitialised). false on OOM. */
static bool media_asset_copy(ca_media_asset_t *dst, const ca_media_asset_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->asset_id = md_strdup_empty(src->asset_id);
    dst->title    = md_strdup_empty(src->title);
    dst->mime     = md_strdup_empty(src->mime);
    dst->kind          = src->kind;
    dst->has_duration  = src->has_duration;
    dst->duration_ticks = src->duration_ticks;
    dst->bytes          = src->bytes;
    dst->created_at_utc_ms = src->created_at_utc_ms;
    if (!dst->asset_id || !dst->title || !dst->mime) {
        ca_media_asset_free(dst);
        return false;
    }
    return true;
}

/* ── InMemoryMediaLibrary ───────────────────────────────────────────────── */

struct ca_media_library {
    ca_media_asset_t *items;
    size_t            count, cap;
};

ca_media_library_t *ca_media_library_create(void) {
    return (ca_media_library_t *)calloc(1, sizeof(ca_media_library_t));
}
void ca_media_library_destroy(ca_media_library_t *lib) {
    if (!lib) return;
    for (size_t i = 0; i < lib->count; ++i) ca_media_asset_free(&lib->items[i]);
    free(lib->items);
    free(lib);
}

/* Find index of an asset by id (Ordinal). SIZE_MAX if absent. */
static size_t media_index_of(const ca_media_library_t *lib, const char *id) {
    for (size_t i = 0; i < lib->count; ++i)
        if (strcmp(lib->items[i].asset_id, id) == 0) return i;
    return (size_t)-1;
}

int ca_media_library_add(ca_media_library_t *lib, const ca_media_asset_t *asset) {
    if (!lib || !asset) return -1;
    /* ArgumentException("AssetId required") on null/whitespace. */
    if (md_is_ws(asset->asset_id)) return -1;

    size_t idx = media_index_of(lib, asset->asset_id);
    ca_media_asset_t copy;
    if (!media_asset_copy(&copy, asset)) return -1;

    if (idx != (size_t)-1) {
        /* Dictionary set: replace in place. */
        ca_media_asset_free(&lib->items[idx]);
        lib->items[idx] = copy;
        return 0;
    }
    if (lib->count == lib->cap) {
        size_t nc = lib->cap ? lib->cap * 2 : 4;
        void *n = realloc(lib->items, nc * sizeof(*lib->items));
        if (!n) { ca_media_asset_free(&copy); return -1; }
        lib->items = (ca_media_asset_t *)n;
        lib->cap = nc;
    }
    lib->items[lib->count++] = copy;
    return 0;
}

bool ca_media_library_get(const ca_media_library_t *lib, const char *id,
                          ca_media_asset_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!lib || !id || !out) return false;
    size_t idx = media_index_of(lib, id);
    if (idx == (size_t)-1) return false;   /* GetValueOrDefault -> null */
    return media_asset_copy(out, &lib->items[idx]);
}

/* Stable descending sort by created_at_utc_ms of an index array.
 * OrderByDescending is a stable sort in LINQ; we mirror that with a stable
 * insertion sort over the collected indices. */
static void sort_desc_by_created(const ca_media_library_t *lib, size_t *idx,
                                 size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kc = lib->items[key].created_at_utc_ms;
        size_t j = i;
        /* shift while the predecessor is strictly older (keeps equal keys in
         * their original relative order — stable). */
        while (j > 0 && lib->items[idx[j - 1]].created_at_utc_ms < kc) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Materialise the selected indices [0,n) into a fresh owned asset array. */
static ca_media_asset_t *materialise(const ca_media_library_t *lib,
                                     const size_t *idx, size_t n,
                                     size_t *out_count) {
    if (n == 0) { *out_count = 0; return NULL; }
    ca_media_asset_t *out = (ca_media_asset_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!media_asset_copy(&out[i], &lib->items[idx[i]])) {
            ca_media_asset_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

ca_media_asset_t *ca_media_library_list_by_kind(const ca_media_library_t *lib,
                                                ca_media_kind_t kind,
                                                size_t *out_count) {
    if (!out_count) return NULL;
    if (!lib) { *out_count = (size_t)-1; return NULL; }
    if (lib->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(lib->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < lib->count; ++i)
        if (lib->items[i].kind == kind) idx[n++] = i;

    sort_desc_by_created(lib, idx, n);
    ca_media_asset_t *out = materialise(lib, idx, n, out_count);
    free(idx);
    return out;
}

ca_media_asset_t *ca_media_library_search(const ca_media_library_t *lib,
                                          const char *q, int top_k,
                                          size_t *out_count) {
    if (!out_count) return NULL;
    /* q null -> ArgumentNullException; topK <= 0 -> ArgumentOutOfRangeException. */
    if (!lib || !q || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (lib->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(lib->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < lib->count; ++i)
        if (md_ci_contains(lib->items[i].title, q)) idx[n++] = i;

    sort_desc_by_created(lib, idx, n);
    if (n > (size_t)top_k) n = (size_t)top_k;   /* Take(topK) after ordering */
    ca_media_asset_t *out = materialise(lib, idx, n, out_count);
    free(idx);
    return out;
}

size_t ca_media_library_count(const ca_media_library_t *lib) {
    return lib ? lib->count : 0;
}

bool ca_media_library_remove(ca_media_library_t *lib, const char *id) {
    /* !string.IsNullOrEmpty(id) && _items.TryRemove(id, out _) */
    if (!lib || !id || id[0] == '\0') return false;
    size_t idx = media_index_of(lib, id);
    if (idx == (size_t)-1) return false;
    ca_media_asset_free(&lib->items[idx]);
    for (size_t i = idx; i + 1 < lib->count; ++i)
        lib->items[i] = lib->items[i + 1];
    lib->count--;
    return true;
}

int64_t ca_media_library_total_bytes(const ca_media_library_t *lib) {
    if (!lib) return 0;
    int64_t sum = 0;
    for (size_t i = 0; i < lib->count; ++i) sum += lib->items[i].bytes;
    return sum;
}

/* Does `s` start with `prefix` (ASCII OrdinalIgnoreCase)? */
static bool md_ci_starts_with(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    for (; *prefix; ++s, ++prefix)
        if (tolower((unsigned char)*s) != tolower((unsigned char)*prefix))
            return false;
    return true;
}

ca_media_asset_t *ca_media_library_by_mime(const ca_media_library_t *lib,
                                           const char *mime_prefix,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!lib) { *out_count = (size_t)-1; return NULL; }
    /* string.IsNullOrEmpty(mimePrefix) -> Array.Empty. */
    if (!mime_prefix || mime_prefix[0] == '\0') { *out_count = 0; return NULL; }
    if (lib->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(lib->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < lib->count; ++i)
        if (md_ci_starts_with(lib->items[i].mime, mime_prefix)) idx[n++] = i;

    sort_desc_by_created(lib, idx, n);
    ca_media_asset_t *out = materialise(lib, idx, n, out_count);
    free(idx);
    return out;
}

/* ===========================================================================
 * CircleAI.MediaHub — MediaItem + PlaybackPosition records
 * =========================================================================== */

void ca_mediahub_item_free(ca_mediahub_item_t *i) {
    if (!i) return;
    free(i->item_id);
    free(i->title);
    free(i->kind);
    free(i->mime_type);
    i->item_id = i->title = i->kind = i->mime_type = NULL;
}
void ca_mediahub_item_free_array(ca_mediahub_item_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mediahub_item_free(&arr[i]);
    free(arr);
}

static bool mediahub_item_copy(ca_mediahub_item_t *dst,
                               const ca_mediahub_item_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->item_id   = md_strdup_empty(src->item_id);
    dst->title     = md_strdup_empty(src->title);
    dst->kind      = md_strdup_empty(src->kind);
    dst->mime_type = md_strdup_empty(src->mime_type);
    dst->duration_ticks = src->duration_ticks;
    if (!dst->item_id || !dst->title || !dst->kind || !dst->mime_type) {
        ca_mediahub_item_free(dst);
        return false;
    }
    return true;
}

void ca_mediahub_position_free(ca_mediahub_position_t *p) {
    if (!p) return;
    free(p->item_id);
    p->item_id = NULL;
}

static bool mediahub_position_copy(ca_mediahub_position_t *dst,
                                   const ca_mediahub_position_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->item_id       = md_strdup_empty(src->item_id);
    dst->position_ticks = src->position_ticks;
    dst->at_utc_ms      = src->at_utc_ms;
    if (!dst->item_id) return false;
    return true;
}

/* ===========================================================================
 * CircleAI.MediaHub.IMediaLibrary — InMemory + Null
 * =========================================================================== */

struct ca_mediahub_library {
    bool               is_null;
    ca_mediahub_item_t *items;
    size_t             count, cap;
};

ca_mediahub_library_t *ca_mediahub_library_create(void) {
    return (ca_mediahub_library_t *)calloc(1, sizeof(ca_mediahub_library_t));
}
ca_mediahub_library_t *ca_mediahub_null_library_create(void) {
    ca_mediahub_library_t *l = (ca_mediahub_library_t *)calloc(1, sizeof(*l));
    if (l) l->is_null = true;
    return l;
}
void ca_mediahub_library_destroy(ca_mediahub_library_t *lib) {
    if (!lib) return;
    for (size_t i = 0; i < lib->count; ++i) ca_mediahub_item_free(&lib->items[i]);
    free(lib->items);
    free(lib);
}
const char *ca_mediahub_library_backend_id(const ca_mediahub_library_t *lib) {
    if (!lib) return NULL;
    return lib->is_null ? "null" : "in-memory";
}

static size_t mediahub_index_of(const ca_mediahub_library_t *lib, const char *id) {
    for (size_t i = 0; i < lib->count; ++i)
        if (strcmp(lib->items[i].item_id, id) == 0) return i;
    return (size_t)-1;
}

int ca_mediahub_library_add(ca_mediahub_library_t *lib,
                            const ca_mediahub_item_t *item) {
    if (!lib || !item || lib->is_null) return -1;
    if (!item->item_id) return -1;   /* ItemId keys the dictionary */

    size_t idx = mediahub_index_of(lib, item->item_id);
    ca_mediahub_item_t copy;
    if (!mediahub_item_copy(&copy, item)) return -1;
    if (idx != (size_t)-1) {
        ca_mediahub_item_free(&lib->items[idx]);
        lib->items[idx] = copy;
        return 0;
    }
    if (lib->count == lib->cap) {
        size_t nc = lib->cap ? lib->cap * 2 : 4;
        void *n = realloc(lib->items, nc * sizeof(*lib->items));
        if (!n) { ca_mediahub_item_free(&copy); return -1; }
        lib->items = (ca_mediahub_item_t *)n;
        lib->cap = nc;
    }
    lib->items[lib->count++] = copy;
    return 0;
}

bool ca_mediahub_library_get(const ca_mediahub_library_t *lib, const char *id,
                             ca_mediahub_item_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!lib || !out) return false;
    if (lib->is_null) return false;             /* NullMediaLibrary -> null */
    if (md_is_ws(id)) return false;             /* ArgumentException("id required") */
    size_t idx = mediahub_index_of(lib, id);
    if (idx == (size_t)-1) return false;
    return mediahub_item_copy(out, &lib->items[idx]);
}

/* Stable ascending sort of an index array by Title (OrdinalIgnoreCase). */
static void mediahub_sort_by_title(const ca_mediahub_library_t *lib, size_t *idx,
                                   size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        const char *kt = lib->items[key].title;
        size_t j = i;
        while (j > 0 && md_ci_cmp(lib->items[idx[j - 1]].title, kt) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_mediahub_item_t *ca_mediahub_library_search(const ca_mediahub_library_t *lib,
                                               const char *query, int top_k,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!lib) { *out_count = (size_t)-1; return NULL; }
    /* NullMediaLibrary.SearchAsync never validates — it always returns empty
     * (Array.Empty). Only the real InMemoryMediaLibrary throws on a null query /
     * topK <= 0, so the arg guards apply to the in-memory backend only. */
    if (lib->is_null) { *out_count = 0; return NULL; }
    if (!query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (lib->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(lib->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < lib->count; ++i)
        if (md_ci_contains(lib->items[i].title, query)) idx[n++] = i;

    mediahub_sort_by_title(lib, idx, n);
    if (n > (size_t)top_k) n = (size_t)top_k;

    ca_mediahub_item_t *out = NULL;
    if (n > 0) {
        out = (ca_mediahub_item_t *)calloc(n, sizeof(*out));
        if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
        for (size_t i = 0; i < n; ++i) {
            if (!mediahub_item_copy(&out[i], &lib->items[idx[i]])) {
                ca_mediahub_item_free_array(out, i);
                free(idx);
                *out_count = (size_t)-1;
                return NULL;
            }
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* ===========================================================================
 * CircleAI.MediaHub.ISyncedPlayback — InMemory + Null
 * =========================================================================== */

/* Unbounded FIFO of PlaybackPosition copies (subscriber cursor). Mirrors the
 * unbounded C# Channel: writes are retained until read. */
typedef struct {
    ca_mediahub_position_t *items;
    size_t head, count, cap;
} pos_fifo_t;

static bool pos_fifo_push(pos_fifo_t *q, ca_mediahub_position_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live; q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            void *ni = realloc(q->items, nc * sizeof(*q->items));
            if (!ni) return false;
            q->items = (ca_mediahub_position_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool pos_fifo_pop(pos_fifo_t *q, ca_mediahub_position_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void pos_fifo_free(pos_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_mediahub_position_free(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

struct ca_mediahub_playback_sub {
    ca_mediahub_playback_t         *owner;
    char                           *session_id;  /* owned */
    ca_mediahub_position_handler_fn handler;
    void                           *ctx;
    pos_fifo_t                      queue;
};

/* One session: a member set (owned string array) + a subscriber list. */
typedef struct {
    char                        *session_id;   /* owned */
    char                       **members;      /* owned strings */
    size_t                       member_count, member_cap;
    ca_mediahub_playback_sub_t **subs;         /* borrowed pointers (owned tokens) */
    size_t                       sub_count, sub_cap;
} session_t;

struct ca_mediahub_playback {
    bool       is_null;
    session_t *sessions;
    size_t     count, cap;
};

ca_mediahub_playback_t *ca_mediahub_playback_create(void) {
    return (ca_mediahub_playback_t *)calloc(1, sizeof(ca_mediahub_playback_t));
}
ca_mediahub_playback_t *ca_mediahub_null_playback_create(void) {
    ca_mediahub_playback_t *p = (ca_mediahub_playback_t *)calloc(1, sizeof(*p));
    if (p) p->is_null = true;
    return p;
}

static void session_destroy(session_t *s) {
    free(s->session_id);
    for (size_t i = 0; i < s->member_count; ++i) free(s->members[i]);
    free(s->members);
    /* Free the subscription tokens the session still owns. */
    for (size_t i = 0; i < s->sub_count; ++i) {
        ca_mediahub_playback_sub_t *sub = s->subs[i];
        pos_fifo_free(&sub->queue);
        free(sub->session_id);
        free(sub);
    }
    free(s->subs);
}

void ca_mediahub_playback_destroy(ca_mediahub_playback_t *pb) {
    if (!pb) return;
    for (size_t i = 0; i < pb->count; ++i) session_destroy(&pb->sessions[i]);
    free(pb->sessions);
    free(pb);
}
const char *ca_mediahub_playback_backend_id(const ca_mediahub_playback_t *pb) {
    if (!pb) return NULL;
    return pb->is_null ? "null" : "in-memory";
}

static session_t *session_find(ca_mediahub_playback_t *pb, const char *sid) {
    for (size_t i = 0; i < pb->count; ++i)
        if (strcmp(pb->sessions[i].session_id, sid) == 0) return &pb->sessions[i];
    return NULL;
}

/* GetOrAdd(sessionId). NULL on OOM. */
static session_t *session_get_or_add(ca_mediahub_playback_t *pb, const char *sid) {
    session_t *s = session_find(pb, sid);
    if (s) return s;
    if (pb->count == pb->cap) {
        size_t nc = pb->cap ? pb->cap * 2 : 4;
        void *n = realloc(pb->sessions, nc * sizeof(*pb->sessions));
        if (!n) return NULL;
        pb->sessions = (session_t *)n;
        pb->cap = nc;
    }
    s = &pb->sessions[pb->count];
    memset(s, 0, sizeof(*s));
    s->session_id = md_strdup(sid);
    if (!s->session_id) return NULL;
    pb->count++;
    return s;
}

int ca_mediahub_playback_join(ca_mediahub_playback_t *pb, const char *session_id,
                              const char *user_id) {
    if (!pb) return -1;
    if (pb->is_null) return 0;                       /* Null -> CompletedTask */
    if (md_is_ws(session_id) || md_is_ws(user_id)) return -1;

    session_t *s = session_get_or_add(pb, session_id);
    if (!s) return -1;
    /* HashSet<string>.Add — idempotent. */
    for (size_t i = 0; i < s->member_count; ++i)
        if (strcmp(s->members[i], user_id) == 0) return 0;

    if (s->member_count == s->member_cap) {
        size_t nc = s->member_cap ? s->member_cap * 2 : 4;
        void *n = realloc(s->members, nc * sizeof(*s->members));
        if (!n) return -1;
        s->members = (char **)n;
        s->member_cap = nc;
    }
    char *u = md_strdup(user_id);
    if (!u) return -1;
    s->members[s->member_count++] = u;
    return 0;
}

size_t ca_mediahub_playback_member_count(const ca_mediahub_playback_t *pb,
                                         const char *session_id) {
    if (!pb || !session_id) return 0;
    /* const-correct lookup. */
    for (size_t i = 0; i < pb->count; ++i)
        if (strcmp(pb->sessions[i].session_id, session_id) == 0)
            return pb->sessions[i].member_count;
    return 0;
}

int ca_mediahub_playback_broadcast(ca_mediahub_playback_t *pb,
                                   const char *session_id,
                                   const ca_mediahub_position_t *pos) {
    if (!pb || !pos || !pos->item_id) return -1;   /* ArgumentNullException(pos) */
    if (md_is_ws(session_id)) return -1;
    if (pb->is_null) return 0;

    session_t *s = session_find(pb, session_id);
    if (!s) return 0;   /* unknown session: C# early return */

    /* Snapshot the subscriber list (state.Subscribers.ToArray()) so a handler
     * that unsubscribes during delivery cannot corrupt the iteration. */
    size_t nsub = s->sub_count;
    if (nsub == 0) return 0;
    ca_mediahub_playback_sub_t **snapshot =
        (ca_mediahub_playback_sub_t **)malloc(nsub * sizeof(*snapshot));
    if (!snapshot) return -1;
    memcpy(snapshot, s->subs, nsub * sizeof(*snapshot));

    int delivered = 0;
    for (size_t i = 0; i < nsub; ++i) {
        ca_mediahub_playback_sub_t *sub = snapshot[i];
        /* Buffer a fresh copy on the cursor. */
        ca_mediahub_position_t copy;
        if (mediahub_position_copy(&copy, pos)) {
            if (!pos_fifo_push(&sub->queue, copy))
                ca_mediahub_position_free(&copy);
        }
        /* Synchronous handler with a borrowed position (await sub(pos)). */
        if (sub->handler) sub->handler(sub->ctx, pos);
        delivered++;
    }
    free(snapshot);
    return delivered;
}

ca_mediahub_playback_sub_t *ca_mediahub_playback_subscribe(
    ca_mediahub_playback_t *pb, const char *session_id,
    ca_mediahub_position_handler_fn handler, void *ctx) {
    if (!pb || !handler || md_is_ws(session_id)) return NULL;

    ca_mediahub_playback_sub_t *sub =
        (ca_mediahub_playback_sub_t *)calloc(1, sizeof(*sub));
    if (!sub) return NULL;
    sub->owner      = pb;
    sub->session_id = md_strdup(session_id);
    sub->handler    = handler;
    sub->ctx        = ctx;
    if (!sub->session_id) { free(sub); return NULL; }

    if (pb->is_null) return sub;   /* Null: live token, never attached to a session */

    session_t *s = session_get_or_add(pb, session_id);
    if (!s) { free(sub->session_id); free(sub); return NULL; }
    if (s->sub_count == s->sub_cap) {
        size_t nc = s->sub_cap ? s->sub_cap * 2 : 4;
        void *n = realloc(s->subs, nc * sizeof(*s->subs));
        if (!n) { free(sub->session_id); free(sub); return NULL; }
        s->subs = (ca_mediahub_playback_sub_t **)n;
        s->sub_cap = nc;
    }
    s->subs[s->sub_count++] = sub;
    return sub;
}

void ca_mediahub_playback_unsubscribe(ca_mediahub_playback_t *pb,
                                      ca_mediahub_playback_sub_t *sub) {
    if (!pb || !sub) return;
    /* Remove from the session's subscriber list if present (SubscriptionToken
     * .Dispose removes the handler). */
    session_t *s = session_find(pb, sub->session_id);
    if (s) {
        for (size_t i = 0; i < s->sub_count; ++i) {
            if (s->subs[i] == sub) {
                s->subs[i] = s->subs[--s->sub_count];
                break;
            }
        }
    }
    pos_fifo_free(&sub->queue);
    free(sub->session_id);
    free(sub);
}

bool ca_mediahub_playback_sub_next(ca_mediahub_playback_sub_t *sub,
                                   ca_mediahub_position_t *out) {
    if (!sub || !out) return false;
    return pos_fifo_pop(&sub->queue, out);
}
size_t ca_mediahub_playback_sub_pending(const ca_mediahub_playback_sub_t *sub) {
    return sub ? (sub->queue.count - sub->queue.head) : 0;
}
