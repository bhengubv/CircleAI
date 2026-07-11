/*
 * web.c — CircleAI.Web portable primitives (C11 port).
 *
 * Ports WebPrimitives.cs: RouteDescriptor / PageMetadata / CachedResponse +
 * IWebBoard / InMemoryWebBoard. WebCompanionService.cs + ServiceCollectionExtensions
 * are the Blazor DI adapter and are not portable (left in C#).
 *
 * Pure C11 + libc. Linear arrays (no hashtable), no pthreads. The C# UtcNow reads
 * inside Cache/Lookup become explicit now_ms parameters.
 */

#include "circle_ai/web.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* ── shared helpers (copied from media.c house style) ───────────────────── */

static char *web_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *web_strdup_empty(const char *s) { return web_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace. */
static bool web_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* OrdinalIgnoreCase full-string comparison (StringComparer.OrdinalIgnoreCase). */
static int web_ci_cmp(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a;
    const unsigned char *y = (const unsigned char *)b;
    for (;; ++x, ++y) {
        int ca = tolower(*x), cb = tolower(*y);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
}

/* Deep-copy an owned string array. Returns false on OOM (out zeroed). */
static bool web_dup_str_array(char ***out_arr, size_t *out_n,
                              char *const *src, size_t n) {
    *out_arr = NULL;
    *out_n = 0;
    if (n == 0) return true;
    char **arr = (char **)calloc(n, sizeof(char *));
    if (!arr) return false;
    for (size_t i = 0; i < n; ++i) {
        arr[i] = web_strdup_empty(src ? src[i] : "");
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j) free(arr[j]);
            free(arr);
            return false;
        }
    }
    *out_arr = arr;
    *out_n = n;
    return true;
}
static void web_free_str_array(char **arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * RouteDescriptor
 * =========================================================================== */

void ca_route_descriptor_free(ca_route_descriptor_t *r) {
    if (!r) return;
    free(r->path);
    free(r->method);
    free(r->handler_name);
    web_free_str_array(r->tags, r->tags_count);
    r->path = r->method = r->handler_name = NULL;
    r->tags = NULL;
    r->tags_count = 0;
}
void ca_route_descriptor_free_array(ca_route_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_route_descriptor_free(&arr[i]);
    free(arr);
}

static bool route_copy(ca_route_descriptor_t *dst,
                       const ca_route_descriptor_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->path         = web_strdup_empty(src->path);
    dst->method       = web_strdup_empty(src->method);
    dst->handler_name = web_strdup_empty(src->handler_name);
    if (!dst->path || !dst->method || !dst->handler_name) {
        ca_route_descriptor_free(dst);
        return false;
    }
    if (!web_dup_str_array(&dst->tags, &dst->tags_count,
                           src->tags, src->tags_count)) {
        ca_route_descriptor_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * PageMetadata
 * =========================================================================== */

void ca_page_metadata_free(ca_page_metadata_t *m) {
    if (!m) return;
    free(m->path);
    free(m->title);
    free(m->description);   /* NULL-safe */
    web_free_str_array(m->keywords, m->keywords_count);
    m->path = m->title = m->description = NULL;
    m->keywords = NULL;
    m->keywords_count = 0;
}

static bool metadata_copy(ca_page_metadata_t *dst,
                          const ca_page_metadata_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->path        = web_strdup_empty(src->path);
    dst->title       = web_strdup_empty(src->title);
    dst->description = src->description ? web_strdup(src->description) : NULL;
    if (!dst->path || !dst->title ||
        (src->description && !dst->description)) {
        ca_page_metadata_free(dst);
        return false;
    }
    if (!web_dup_str_array(&dst->keywords, &dst->keywords_count,
                           src->keywords, src->keywords_count)) {
        ca_page_metadata_free(dst);
        return false;
    }
    return true;
}

/* ===========================================================================
 * CachedResponse
 * =========================================================================== */

void ca_cached_response_free(ca_cached_response_t *c) {
    if (!c) return;
    free(c->key);
    free(c->body);          /* NULL-safe */
    free(c->mime);
    c->key = c->mime = NULL;
    c->body = NULL;
    c->body_len = 0;
}

static bool cached_copy(ca_cached_response_t *dst,
                        const ca_cached_response_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->key  = web_strdup_empty(src->key);
    dst->mime = web_strdup_empty(src->mime);
    dst->expires_utc_ms = src->expires_utc_ms;
    if (!dst->key || !dst->mime) {
        ca_cached_response_free(dst);
        return false;
    }
    if (src->body_len > 0) {
        dst->body = (uint8_t *)malloc(src->body_len);
        if (!dst->body) { ca_cached_response_free(dst); return false; }
        if (src->body) memcpy(dst->body, src->body, src->body_len);
        else memset(dst->body, 0, src->body_len);
        dst->body_len = src->body_len;
    }
    return true;
}

/* ===========================================================================
 * InMemoryWebBoard
 * =========================================================================== */

/* One keyed route entry: the composite "<METHOD> <Path>" key + the descriptor. */
typedef struct {
    char                 *key;   /* owned "<METHOD> <Path>" (Ordinal) */
    ca_route_descriptor_t route;
} route_entry_t;

struct ca_web_board {
    route_entry_t        *routes;
    size_t                routes_count, routes_cap;
    ca_page_metadata_t   *meta;      /* keyed by Path (OrdinalIgnoreCase) */
    size_t                meta_count, meta_cap;
    ca_cached_response_t *cache;     /* keyed by Key (Ordinal) */
    size_t                cache_count, cache_cap;
};

ca_web_board_t *ca_web_board_create(void) {
    return (ca_web_board_t *)calloc(1, sizeof(ca_web_board_t));
}
void ca_web_board_destroy(ca_web_board_t *board) {
    if (!board) return;
    for (size_t i = 0; i < board->routes_count; ++i) {
        free(board->routes[i].key);
        ca_route_descriptor_free(&board->routes[i].route);
    }
    free(board->routes);
    for (size_t i = 0; i < board->meta_count; ++i)
        ca_page_metadata_free(&board->meta[i]);
    free(board->meta);
    for (size_t i = 0; i < board->cache_count; ++i)
        ca_cached_response_free(&board->cache[i]);
    free(board->cache);
    free(board);
}

/* Build "<METHOD-ASCII-uppercased> <Path>" (ToUpperInvariant on the method).
 * Freshly-allocated; caller frees. NULL on OOM. */
static char *route_key(const char *method, const char *path) {
    const char *m = method ? method : "";
    const char *p = path ? path : "";
    size_t ml = strlen(m), pl = strlen(p);
    char *key = (char *)malloc(ml + 1 + pl + 1);
    if (!key) return NULL;
    for (size_t i = 0; i < ml; ++i)
        key[i] = (char)toupper((unsigned char)m[i]);
    key[ml] = ' ';
    memcpy(key + ml + 1, p, pl);
    key[ml + 1 + pl] = '\0';
    return key;
}

static size_t route_index_of(const ca_web_board_t *board, const char *key) {
    for (size_t i = 0; i < board->routes_count; ++i)
        if (strcmp(board->routes[i].key, key) == 0) return i;   /* Ordinal */
    return (size_t)-1;
}

int ca_web_board_register(ca_web_board_t *board, const ca_route_descriptor_t *r) {
    if (!board || !r) return -1;   /* ArgumentNullException.ThrowIfNull(r) */

    char *key = route_key(r->method, r->path);
    if (!key) return -1;

    ca_route_descriptor_t copy;
    if (!route_copy(&copy, r)) { free(key); return -1; }

    size_t idx = route_index_of(board, key);
    if (idx != (size_t)-1) {
        /* Dictionary set: replace in place. */
        free(key);
        ca_route_descriptor_free(&board->routes[idx].route);
        board->routes[idx].route = copy;
        return 0;
    }
    if (board->routes_count == board->routes_cap) {
        size_t nc = board->routes_cap ? board->routes_cap * 2 : 4;
        void *n = realloc(board->routes, nc * sizeof(*board->routes));
        if (!n) { free(key); ca_route_descriptor_free(&copy); return -1; }
        board->routes = (route_entry_t *)n;
        board->routes_cap = nc;
    }
    board->routes[board->routes_count].key = key;
    board->routes[board->routes_count].route = copy;
    board->routes_count++;
    return 0;
}

/* Stable ascending sort of an index array by route Path (Ordinal). */
static void routes_sort_by_path(const ca_web_board_t *board, size_t *idx,
                                size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        const char *kp = board->routes[key].route.path;
        size_t j = i;
        while (j > 0 && strcmp(board->routes[idx[j - 1]].route.path, kp) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_route_descriptor_t *ca_web_board_routes_by_method(const ca_web_board_t *board,
                                                    const char *method,
                                                    size_t *out_count) {
    if (!out_count) return NULL;
    if (!board || web_is_ws(method)) { *out_count = (size_t)-1; return NULL; }
    if (board->routes_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(board->routes_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < board->routes_count; ++i)
        if (web_ci_cmp(board->routes[i].route.method, method) == 0)
            idx[n++] = i;

    routes_sort_by_path(board, idx, n);

    ca_route_descriptor_t *out = NULL;
    if (n > 0) {
        out = (ca_route_descriptor_t *)calloc(n, sizeof(*out));
        if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
        for (size_t i = 0; i < n; ++i) {
            if (!route_copy(&out[i], &board->routes[idx[i]].route)) {
                ca_route_descriptor_free_array(out, i);
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

static size_t meta_index_of(const ca_web_board_t *board, const char *path) {
    for (size_t i = 0; i < board->meta_count; ++i)
        if (web_ci_cmp(board->meta[i].path, path) == 0) return i;  /* CI key */
    return (size_t)-1;
}

int ca_web_board_set_metadata(ca_web_board_t *board, const ca_page_metadata_t *m) {
    if (!board || !m) return -1;   /* ArgumentNullException.ThrowIfNull(m) */
    const char *path = m->path ? m->path : "";

    ca_page_metadata_t copy;
    if (!metadata_copy(&copy, m)) return -1;

    size_t idx = meta_index_of(board, path);
    if (idx != (size_t)-1) {
        ca_page_metadata_free(&board->meta[idx]);
        board->meta[idx] = copy;
        return 0;
    }
    if (board->meta_count == board->meta_cap) {
        size_t nc = board->meta_cap ? board->meta_cap * 2 : 4;
        void *n = realloc(board->meta, nc * sizeof(*board->meta));
        if (!n) { ca_page_metadata_free(&copy); return -1; }
        board->meta = (ca_page_metadata_t *)n;
        board->meta_cap = nc;
    }
    board->meta[board->meta_count++] = copy;
    return 0;
}

bool ca_web_board_get_metadata(const ca_web_board_t *board, const char *path,
                              ca_page_metadata_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!board || !path || !out) return false;
    size_t idx = meta_index_of(board, path);
    if (idx == (size_t)-1) return false;   /* GetValueOrDefault -> null */
    return metadata_copy(out, &board->meta[idx]);
}

static size_t cache_index_of(const ca_web_board_t *board, const char *key) {
    for (size_t i = 0; i < board->cache_count; ++i)
        if (strcmp(board->cache[i].key, key) == 0) return i;   /* Ordinal */
    return (size_t)-1;
}

static void cache_remove_at(ca_web_board_t *board, size_t idx) {
    ca_cached_response_free(&board->cache[idx]);
    board->cache[idx] = board->cache[--board->cache_count];
}

int ca_web_board_cache(ca_web_board_t *board, const ca_cached_response_t *c,
                      int64_t now_ms) {
    if (!board || !c) return -1;   /* ArgumentNullException.ThrowIfNull(c) */
    /* if (c.ExpiresUtc <= UtcNow) return;  // already expired; skip */
    if (c->expires_utc_ms <= now_ms) return 0;

    const char *key = c->key ? c->key : "";
    ca_cached_response_t copy;
    if (!cached_copy(&copy, c)) return -1;

    size_t idx = cache_index_of(board, key);
    if (idx != (size_t)-1) {
        ca_cached_response_free(&board->cache[idx]);
        board->cache[idx] = copy;
        return 0;
    }
    if (board->cache_count == board->cache_cap) {
        size_t nc = board->cache_cap ? board->cache_cap * 2 : 4;
        void *n = realloc(board->cache, nc * sizeof(*board->cache));
        if (!n) { ca_cached_response_free(&copy); return -1; }
        board->cache = (ca_cached_response_t *)n;
        board->cache_cap = nc;
    }
    board->cache[board->cache_count++] = copy;
    return 0;
}

bool ca_web_board_lookup(ca_web_board_t *board, const char *key, int64_t now_ms,
                        ca_cached_response_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!board || web_is_ws(key) || !out) return false;  /* ArgumentException(key) */

    size_t idx = cache_index_of(board, key);
    if (idx == (size_t)-1) return false;                 /* not found -> null */
    if (board->cache[idx].expires_utc_ms <= now_ms) {    /* expired -> remove + null */
        cache_remove_at(board, idx);
        return false;
    }
    return cached_copy(out, &board->cache[idx]);
}
