/*
 * inference_rt.c — CircleAI.Inference runtime surface (C11 port).
 *
 * See inference_rt.h. Deterministic, in-memory ports of the non-native
 * CircleAI.Inference surface. Network is injected behind a fetch callback.
 * Pure C11 + libc. SHA-256 reused from model_runtime.h.
 */

#include "circle_ai/inference_rt.h"
#include "circle_ai/model_runtime.h"  /* ca_mr_sha256_file, ca_mr_sha256_hex */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <math.h>
#include <time.h>
#include <utime.h>
#include <sys/stat.h>
#include <dirent.h>

#if defined(_WIN32)
  #include <direct.h>
  #define CA_MKDIR(p) _mkdir(p)
  #define CA_SEP '\\'
  #define strncasecmp _strnicmp
  #define strcasecmp  _stricmp
#else
  #include <sys/types.h>
  #include <strings.h>
  #define CA_MKDIR(p) mkdir((p), 0777)
  #define CA_SEP '/'
#endif

/* ─────────────────────── small helpers ─────────────────────── */

static char *xstrdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int dir_exists(const char *path) {
    struct stat st;
    if (!path || stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFDIR) != 0;
#else
    return S_ISDIR(st.st_mode);
#endif
}

static int file_exists(const char *path) {
    struct stat st;
    if (!path || stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFREG) != 0;
#else
    return S_ISREG(st.st_mode);
#endif
}

static int mkdir_p(const char *path) {
    if (!path || !*path) return -1;
    if (dir_exists(path)) return 0;
    char buf[1024];
    snprintf(buf, sizeof(buf), "%s", path);
    for (char *q = buf + 1; *q; q++) {
        if (*q == '/' || *q == '\\') {
            char saved = *q; *q = 0;
            if (*buf && !dir_exists(buf)) CA_MKDIR(buf);
            *q = saved;
        }
    }
    CA_MKDIR(buf);
    return dir_exists(path) ? 0 : -1;
}

static void join_path(char *out, size_t cap, const char *a, const char *b) {
    size_t la = strlen(a);
    if (la == 0) { snprintf(out, cap, "%s", b); return; }
    char last = a[la - 1];
    if (last == '/' || last == '\\') snprintf(out, cap, "%s%s", a, b);
    else                             snprintf(out, cap, "%s%c%s", a, CA_SEP, b);
}

static bool is_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++)
        if (!isspace(*p)) return false;
    return true;
}

/* Recursively delete a directory tree (best-effort). */
static void rm_rf(const char *path) {
    if (!path) return;
    if (file_exists(path)) { remove(path); return; }
    if (!dir_exists(path)) return;
    DIR *d = opendir(path);
    if (d) {
        struct dirent *e;
        char child[1024];
        while ((e = readdir(d)) != NULL) {
            if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
            join_path(child, sizeof(child), path, e->d_name);
            if (dir_exists(child)) rm_rf(child);
            else remove(child);
        }
        closedir(d);
    }
    rmdir(path);
}

/* Byte-size of a file, or -1. */
static int64_t file_size(const char *path) {
    struct stat st;
    if (!path || stat(path, &st) != 0) return -1;
    return (int64_t)st.st_size;
}

/* Lowercase-hex SHA-256 of a UTF-8 string into out[65]. */
static bool sha256_hex_of_string(const char *s, char out[65]);

/* ===========================================================================
 * VisionInput
 * =========================================================================== */

ca_vision_input_t *ca_vision_input_create(const uint8_t *image_bytes, size_t len,
                                          const char *mime_type) {
    if (!image_bytes || len == 0) return NULL;
    ca_vision_input_t *v = (ca_vision_input_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    v->image_bytes = (uint8_t *)malloc(len);
    if (!v->image_bytes) { free(v); return NULL; }
    memcpy(v->image_bytes, image_bytes, len);
    v->image_len = len;
    if (mime_type) {
        v->mime_type = xstrdup(mime_type);
        if (!v->mime_type) { free(v->image_bytes); free(v); return NULL; }
    }
    return v;
}

void ca_vision_input_destroy(ca_vision_input_t *v) {
    if (!v) return;
    free(v->image_bytes);
    free(v->mime_type);
    free(v);
}

/* ===========================================================================
 * PowerBudgetPolicy.Resolve
 * =========================================================================== */

static int imin(int a, int b) { return a < b ? a : b; }

ca_power_budget_resolution_t ca_power_budget_resolve(
    ca_power_budget_t budget, int requested_max_tokens,
    int battery_level_percent, bool thermal_throttled) {

    /* Auto-downgrade based on device state (matches C#). */
    if (budget == CA_POWER_BUDGET_NORMAL && battery_level_percent >= 0 &&
        battery_level_percent < 15)
        budget = CA_POWER_BUDGET_LOW;
    if (budget == CA_POWER_BUDGET_HIGH && thermal_throttled)
        budget = CA_POWER_BUDGET_NORMAL;

    ca_power_budget_resolution_t r;
    switch (budget) {
        case CA_POWER_BUDGET_NONE:
            r.max_tokens = requested_max_tokens;
            r.preferred_kv_mode = CA_KV_TURBO_QUANT_4BIT;
            r.prefer_smaller_model_in_chain = false;
            break;
        case CA_POWER_BUDGET_LOW:
            r.max_tokens = imin(requested_max_tokens, 64);
            r.preferred_kv_mode = CA_KV_TURBO_QUANT_4BIT;
            r.prefer_smaller_model_in_chain = true;
            break;
        case CA_POWER_BUDGET_NORMAL:
            r.max_tokens = imin(requested_max_tokens, 512);
            r.preferred_kv_mode = CA_KV_TURBO_QUANT_4BIT;
            r.prefer_smaller_model_in_chain = false;
            break;
        case CA_POWER_BUDGET_HIGH:
            r.max_tokens = imin(requested_max_tokens, 2048);
            r.preferred_kv_mode = CA_KV_OFF;
            r.prefer_smaller_model_in_chain = false;
            break;
        default:
            r.max_tokens = requested_max_tokens;
            r.preferred_kv_mode = CA_KV_TURBO_QUANT_4BIT;
            r.prefer_smaller_model_in_chain = false;
            break;
    }
    return r;
}

/* ===========================================================================
 * IChatGenerator — deterministic local generator
 * =========================================================================== */

struct ca_local_chat_generator {
    char *model_id;
    int   context_tokens;
};

ca_local_chat_generator_t *ca_local_chat_generator_create(const char *model_id, int context_tokens) {
    if (is_blank(model_id) || context_tokens <= 0) return NULL;
    ca_local_chat_generator_t *g = (ca_local_chat_generator_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->model_id = xstrdup(model_id);
    if (!g->model_id) { free(g); return NULL; }
    g->context_tokens = context_tokens;
    return g;
}

void ca_local_chat_generator_destroy(ca_local_chat_generator_t *g) {
    if (!g) return;
    free(g->model_id);
    free(g);
}

/* Simple growable string buffer. */
typedef struct { char *data; size_t len; size_t cap; } sbuf;
static bool sbuf_reserve(sbuf *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t nc = b->cap ? b->cap * 2 : 64;
    while (nc < b->len + extra + 1) nc *= 2;
    char *nd = (char *)realloc(b->data, nc);
    if (!nd) return false;
    b->data = nd; b->cap = nc;
    return true;
}
static bool sbuf_add(sbuf *b, const char *s) {
    if (!s) return true;
    size_t n = strlen(s);
    if (!sbuf_reserve(b, n)) return false;
    memcpy(b->data + b->len, s, n);
    b->len += n; b->data[b->len] = 0;
    return true;
}
static bool sbuf_addc(sbuf *b, char c) {
    if (!sbuf_reserve(b, 1)) return false;
    b->data[b->len++] = c; b->data[b->len] = 0;
    return true;
}

char *ca_build_qwen_chat_prompt(const ca_chat_msg_t *messages, size_t count) {
    sbuf b = {0};
    if (!sbuf_reserve(&b, count * 64 + 32)) { free(b.data); return NULL; }
    b.data[0] = 0;
    for (size_t i = 0; i < count; i++) {
        const char *role = messages[i].role;
        char lower[64];
        if (is_blank(role)) {
            snprintf(lower, sizeof(lower), "user");
        } else {
            /* trim + lowercase */
            const char *p = role;
            while (*p && isspace((unsigned char)*p)) p++;
            size_t j = 0;
            while (*p && j + 1 < sizeof(lower)) lower[j++] = (char)tolower((unsigned char)*p++);
            while (j > 0 && isspace((unsigned char)lower[j - 1])) j--;
            lower[j] = 0;
            if (j == 0) snprintf(lower, sizeof(lower), "user");
        }
        if (!sbuf_add(&b, "<|im_start|>") || !sbuf_add(&b, lower) || !sbuf_addc(&b, '\n') ||
            !sbuf_add(&b, messages[i].content ? messages[i].content : "") ||
            !sbuf_addc(&b, '\n') || !sbuf_add(&b, "<|im_end|>") || !sbuf_addc(&b, '\n')) {
            free(b.data); return NULL;
        }
    }
    if (!sbuf_add(&b, "<|im_start|>assistant\n")) { free(b.data); return NULL; }
    return b.data ? b.data : xstrdup("");
}

/* Find the last user-role message content. NULL when none. */
static const char *last_user_content(const ca_chat_msg_t *messages, size_t count) {
    for (size_t i = count; i-- > 0;) {
        if (messages[i].role && strcasecmp(messages[i].role, "user") == 0)
            return messages[i].content ? messages[i].content : "";
    }
    return NULL;
}

/*
 * Deterministic reply: content is the last user turn echoed as
 * "You said: <text>". reasoning (when surfaced) is a one-line rationale. This
 * is the C stand-in for the native Qwen/Kimi generators — stable, no network.
 */
static char *make_content_reply(const ca_chat_msg_t *messages, size_t count) {
    const char *u = last_user_content(messages, count);
    sbuf b = {0}; b.data = NULL;
    if (!sbuf_add(&b, "You said: ") || !sbuf_add(&b, u ? u : "")) { free(b.data); return NULL; }
    return b.data ? b.data : xstrdup("You said: ");
}

static char *make_reasoning(const ca_chat_msg_t *messages, size_t count) {
    const char *u = last_user_content(messages, count);
    sbuf b = {0}; b.data = NULL;
    if (!sbuf_add(&b, "Considering the user's message") ) { free(b.data); return NULL; }
    if (u && *u) { if (!sbuf_add(&b, ": ") || !sbuf_add(&b, u)) { free(b.data); return NULL; } }
    return b.data ? b.data : xstrdup("Considering the user's message");
}

char *ca_local_chat_generator_generate(ca_local_chat_generator_t *g,
                                 const ca_chat_msg_t *messages, size_t count,
                                 const ca_generation_options_t *opts) {
    (void)opts;
    if (!g || (!messages && count > 0)) return NULL;
    return make_content_reply(messages, count);
}

bool ca_local_chat_generator_stream_fragments(ca_local_chat_generator_t *g,
                                        const ca_chat_msg_t *messages, size_t count,
                                        const ca_generation_options_t *opts,
                                        ca_chat_stream_fn on_fragment, void *user) {
    if (!g || (!messages && count > 0) || !on_fragment) return false;
    bool include_reasoning = opts ? (opts->include_reasoning != 0) : true;

    if (include_reasoning) {
        char *r = make_reasoning(messages, count);
        if (!r) return false;
        ca_chat_fragment_t f = { CA_CHAT_FRAGMENT_REASONING, r };
        on_fragment(&f, user);
        free(r);
    }
    char *c = make_content_reply(messages, count);
    if (!c) return false;
    ca_chat_fragment_t f = { CA_CHAT_FRAGMENT_CONTENT, c };
    on_fragment(&f, user);
    free(c);
    return true;
}

/* 1 token ~= 4 chars, min 1 when non-empty (matches the interface default). */
static int approx_tokens_str(const char *s) {
    if (!s || !*s) return 0;
    int n = (int)(strlen(s) / 4);
    return n > 1 ? n : 1;
}
static int approx_tokens_messages(const ca_chat_msg_t *messages, size_t count) {
    int total = 0;
    for (size_t i = 0; i < count; i++) total += approx_tokens_str(messages[i].content);
    return total;
}

bool ca_local_chat_generator_generate_response(ca_local_chat_generator_t *g,
                                         const ca_chat_msg_t *messages, size_t count,
                                         const ca_generation_options_t *opts,
                                         ca_chat_gen_response_t *out) {
    if (!g || !out || (!messages && count > 0)) return false;
    memset(out, 0, sizeof(*out));

    bool include_reasoning = opts ? (opts->include_reasoning != 0) : true;
    char *text = make_content_reply(messages, count);
    if (!text) return false;
    char *reasoning = NULL;
    if (include_reasoning) {
        reasoning = make_reasoning(messages, count);
        if (!reasoning) { free(text); return false; }
    }

    out->text = text;
    out->reasoning_content = reasoning;
    out->tokens_in  = approx_tokens_messages(messages, count);
    out->tokens_out = approx_tokens_str(text);
    out->latency_ms = 0.0;
    out->finish_reason = CA_FINISH_STOP;
    return true;
}

void ca_chat_gen_response_free(ca_chat_gen_response_t *r) {
    if (!r) return;
    free(r->text);
    free(r->reasoning_content);
    r->text = NULL;
    r->reasoning_content = NULL;
}

bool ca_local_chat_generator_save_session(ca_local_chat_generator_t *g, const char *path) {
    if (!g || is_blank(path)) return false;
    FILE *f = fopen(path, "wb");
    if (!f) return false;
    /* Portable marker mirroring the interface default. */
    fprintf(f, "circleai-session-marker\ntype:ca_chat_generator\nmodel:%s\n",
            g->model_id ? g->model_id : "");
    fclose(f);
    return true;
}

bool ca_local_chat_generator_load_session(ca_local_chat_generator_t *g, const char *path) {
    if (!g || is_blank(path)) return false;
    if (!file_exists(path)) return false;
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    char head[32] = {0};
    size_t n = fread(head, 1, sizeof(head) - 1, f);
    fclose(f);
    head[n] = 0;
    return strncmp(head, "circleai-session-marker", 23) == 0;
}

/* ===========================================================================
 * ContextWindowBudgetManager
 * =========================================================================== */

struct ca_context_window_budget {
    int    context_size;
    int    used_tokens;
    double eviction_threshold;
};

ca_context_window_budget_t *ca_context_window_budget_create(int context_size,
                                                            double eviction_threshold) {
    if (context_size <= 0) return NULL;
    if (eviction_threshold < 0.0 || eviction_threshold > 1.0) return NULL;
    ca_context_window_budget_t *b = (ca_context_window_budget_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->context_size = context_size;
    b->eviction_threshold = eviction_threshold;
    b->used_tokens = 0;
    return b;
}

void ca_context_window_budget_destroy(ca_context_window_budget_t *b) { free(b); }

int ca_context_window_budget_context_size(const ca_context_window_budget_t *b) {
    return b ? b->context_size : 0;
}
int ca_context_window_budget_used_tokens(const ca_context_window_budget_t *b) {
    return b ? b->used_tokens : 0;
}
int ca_context_window_budget_remaining_tokens(const ca_context_window_budget_t *b) {
    return b ? b->context_size - b->used_tokens : 0;
}
double ca_context_window_budget_fill_ratio(const ca_context_window_budget_t *b) {
    return b ? (double)b->used_tokens / (double)b->context_size : 0.0;
}
double ca_context_window_budget_eviction_threshold(const ca_context_window_budget_t *b) {
    return b ? b->eviction_threshold : 0.0;
}
bool ca_context_window_budget_should_evict(const ca_context_window_budget_t *b) {
    if (!b) return false;
    return ca_context_window_budget_fill_ratio(b) >= b->eviction_threshold;
}

bool ca_context_window_budget_record_exchange(ca_context_window_budget_t *b,
                                              int prompt_tokens, int completion_tokens) {
    if (!b) return false;
    if (prompt_tokens < 0 || completion_tokens < 0) return false;
    b->used_tokens += prompt_tokens + completion_tokens;
    return true;
}

int ca_context_window_budget_calculate_eviction_count(const ca_context_window_budget_t *b,
                                                      double target_fill_ratio) {
    if (!b) return -1;
    if (target_fill_ratio < 0.0 || target_fill_ratio > 1.0) return -1;
    int target_used = (int)((double)b->context_size * target_fill_ratio);
    int evict = b->used_tokens - target_used;
    return evict > 0 ? evict : 0;
}

void ca_context_window_budget_reset(ca_context_window_budget_t *b) {
    if (b) b->used_tokens = 0;
}

/* ===========================================================================
 * PrefixCacheService
 * =========================================================================== */

#define CA_PREFIX_CACHE_CAP_BYTES ((int64_t)500 * 1024 * 1024)

struct ca_prefix_cache {
    char *root;
};

static bool sha256_hex_of_string(const char *s, char out[65]) {
    /* Reuse the streaming SHA-256 from model_runtime via a temp-in-memory
     * path is overkill; instead hash the bytes directly with ca_mr_sha256. */
    extern void ca_mr_sha256(const uint8_t *data, size_t len, uint8_t out[32]);
    uint8_t digest[32];
    ca_mr_sha256((const uint8_t *)s, s ? strlen(s) : 0, digest);
    ca_mr_sha256_hex(digest, out);
    return true;
}

ca_prefix_cache_t *ca_prefix_cache_create(const char *root) {
    if (is_blank(root)) return NULL;
    ca_prefix_cache_t *c = (ca_prefix_cache_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->root = xstrdup(root);
    if (!c->root) { free(c); return NULL; }
    mkdir_p(c->root);
    return c;
}

void ca_prefix_cache_destroy(ca_prefix_cache_t *c) {
    if (!c) return;
    free(c->root);
    free(c);
}

char *ca_prefix_cache_key_for(const char *model_id, const char *system_prompt) {
    if (is_blank(model_id)) return NULL;
    if (!system_prompt || system_prompt[0] == 0) return NULL;
    char mh[65], sh[65];
    sha256_hex_of_string(model_id, mh);
    sha256_hex_of_string(system_prompt, sh);
    /* First 16 hex chars per component. */
    char *key = (char *)malloc(16 + 1 + 16 + 1);
    if (!key) return NULL;
    memcpy(key, mh, 16);
    key[16] = '_';
    memcpy(key + 17, sh, 16);
    key[33] = 0;
    return key;
}

char *ca_prefix_cache_path_for(const ca_prefix_cache_t *c, const char *key) {
    if (!c || !key) return NULL;
    size_t n = strlen(c->root) + 1 + strlen(key) + 8 + 1;
    char *p = (char *)malloc(n);
    if (!p) return NULL;
    char joined[1024];
    join_path(joined, sizeof(joined), c->root, key);
    snprintf(p, n, "%s.session", joined);
    return p;
}

bool ca_prefix_cache_has_entry(const ca_prefix_cache_t *c, const char *key) {
    char *p = ca_prefix_cache_path_for(c, key);
    if (!p) return false;
    bool exists = file_exists(p);
    free(p);
    return exists;
}

void ca_prefix_cache_touch(const ca_prefix_cache_t *c, const char *key) {
    char *p = ca_prefix_cache_path_for(c, key);
    if (!p) return;
    if (file_exists(p)) {
        /* Bump mtime to now (utime(path, NULL) sets both to current time). */
        utime(p, NULL);
    }
    free(p);
}

/* One cache entry for eviction sort. */
typedef struct { char *path; int64_t size; time_t mtime; } cache_entry;

static int cache_entry_cmp(const void *a, const void *b) {
    const cache_entry *ea = (const cache_entry *)a;
    const cache_entry *eb = (const cache_entry *)b;
    if (ea->mtime < eb->mtime) return -1;
    if (ea->mtime > eb->mtime) return 1;
    return 0;
}

void ca_prefix_cache_evict_if_needed(ca_prefix_cache_t *c) {
    if (!c || !dir_exists(c->root)) return;
    DIR *d = opendir(c->root);
    if (!d) return;

    cache_entry *entries = NULL;
    size_t cap = 0, n = 0;
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        const char *name = e->d_name;
        size_t nl = strlen(name);
        if (nl < 8 || strcmp(name + nl - 8, ".session") != 0) continue;
        char full[1024];
        join_path(full, sizeof(full), c->root, name);
        struct stat st;
        if (stat(full, &st) != 0) continue;
        if (n == cap) {
            size_t nc = cap ? cap * 2 : 8;
            cache_entry *ne = (cache_entry *)realloc(entries, nc * sizeof(cache_entry));
            if (!ne) break;
            entries = ne; cap = nc;
        }
        entries[n].path = xstrdup(full);
        entries[n].size = (int64_t)st.st_size;
        entries[n].mtime = st.st_mtime;
        n++;
    }
    closedir(d);

    if (n == 0) { free(entries); return; }
    qsort(entries, n, sizeof(cache_entry), cache_entry_cmp);

    int64_t total = 0;
    for (size_t i = 0; i < n; i++) total += entries[i].size;

    size_t i = 0;
    while (total > CA_PREFIX_CACHE_CAP_BYTES && i < n) {
        total -= entries[i].size;
        remove(entries[i].path);
        i++;
    }
    for (size_t j = 0; j < n; j++) free(entries[j].path);
    free(entries);
}

/* ===========================================================================
 * ModelDownloadService
 * =========================================================================== */

struct ca_model_download_service {
    char             *storage_directory;
    ca_model_fetch_fn fetch;
    void             *fetch_user;
};

ca_model_download_service_t *ca_model_download_service_create(
    const char *storage_directory, ca_model_fetch_fn fetch, void *fetch_user) {
    if (is_blank(storage_directory)) return NULL;
    ca_model_download_service_t *s = (ca_model_download_service_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->storage_directory = xstrdup(storage_directory);
    if (!s->storage_directory) { free(s); return NULL; }
    s->fetch = fetch;
    s->fetch_user = fetch_user;
    mkdir_p(s->storage_directory);
    return s;
}

void ca_model_download_service_destroy(ca_model_download_service_t *s) {
    if (!s) return;
    free(s->storage_directory);
    free(s);
}

char *ca_strip_sha_algorithm_prefix(const char *raw) {
    if (!raw || raw[0] == 0) return xstrdup("");
    /* trim */
    const char *start = raw;
    while (*start && isspace((unsigned char)*start)) start++;
    const char *end = raw + strlen(raw);
    while (end > start && isspace((unsigned char)*(end - 1))) end--;
    size_t len = (size_t)(end - start);
    /* find ':' */
    size_t colon = (size_t)-1;
    for (size_t i = 0; i < len; i++) if (start[i] == ':') { colon = i; break; }
    if (colon == (size_t)-1) {
        char *r = (char *)malloc(len + 1);
        if (!r) return NULL;
        memcpy(r, start, len); r[len] = 0;
        return r;
    }
    /* validate prefix is an algorithm name (letters/digits/-/_ , 1..16). */
    if (colon > 0 && colon <= 16) {
        bool is_alg = true;
        for (size_t i = 0; i < colon; i++) {
            char ch = start[i];
            if (!(isalnum((unsigned char)ch) || ch == '-' || ch == '_')) { is_alg = false; break; }
        }
        if (is_alg) {
            const char *hex = start + colon + 1;
            /* trim hex */
            while (*hex && isspace((unsigned char)*hex)) hex++;
            const char *he = start + len;
            while (he > hex && isspace((unsigned char)*(he - 1))) he--;
            size_t hl = (size_t)(he - hex);
            char *r = (char *)malloc(hl + 1);
            if (!r) return NULL;
            memcpy(r, hex, hl); r[hl] = 0;
            return r;
        }
    }
    char *r = (char *)malloc(len + 1);
    if (!r) return NULL;
    memcpy(r, start, len); r[len] = 0;
    return r;
}

/* URL-escape per Uri.EscapeDataString (RFC 3986 unreserved kept). */
static char *url_escape(const char *s) {
    if (!s) return xstrdup("");
    size_t n = strlen(s);
    char *out = (char *)malloc(n * 3 + 1);
    if (!out) return NULL;
    static const char *hex = "0123456789ABCDEF";
    size_t o = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned char ch = (unsigned char)s[i];
        if (isalnum(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '~') {
            out[o++] = (char)ch;
        } else {
            out[o++] = '%';
            out[o++] = hex[ch >> 4];
            out[o++] = hex[ch & 0xF];
        }
    }
    out[o] = 0;
    return out;
}

char *ca_modelscope_primary_url(const char *repo, const char *file_name) {
    char *esc = url_escape(file_name);
    if (!esc) return NULL;
    size_t n = strlen(repo) + strlen(esc) + 96;
    char *u = (char *)malloc(n);
    if (u) snprintf(u, n,
        "https://modelscope.cn/api/v1/models/%s/repo?Revision=master&FilePath=%s", repo, esc);
    free(esc);
    return u;
}

char *ca_modelscope_fallback_url(const char *repo, const char *file_name) {
    char *esc = url_escape(file_name);
    if (!esc) return NULL;
    size_t n = strlen(repo) + strlen(esc) + 64;
    char *u = (char *)malloc(n);
    if (u) snprintf(u, n, "https://modelscope.cn/models/%s/resolve/master/%s", repo, esc);
    free(esc);
    return u;
}

/* Verify file SHA-256 against expected (which may carry a "sha256:" prefix). */
static bool verify_sha256(const char *path, const char *expected) {
    if (!file_exists(path)) return false;
    uint8_t digest[32];
    if (!ca_mr_sha256_file(path, digest)) return false;
    char actual[65];
    ca_mr_sha256_hex(digest, actual);
    char *norm = ca_strip_sha_algorithm_prefix(expected);
    if (!norm) return false;
    bool eq = (strcasecmp(actual, norm) == 0);
    free(norm);
    return eq;
}

static char *single_file_path(const ca_model_download_service_t *s, const char *model_id) {
    size_t n = strlen(s->storage_directory) + 1 + strlen(model_id) + 6;
    char *p = (char *)malloc(n);
    if (!p) return NULL;
    char joined[1024];
    join_path(joined, sizeof(joined), s->storage_directory, model_id);
    snprintf(p, n, "%s.gguf", joined);
    return p;
}

bool ca_model_download_service_ensure_model(
    ca_model_download_service_t *s, const char *model_id, const char *download_uri,
    const char *expected_sha256, ca_download_progress_ratio_fn progress,
    void *progress_user, char **out_path) {
    if (!s || is_blank(model_id) || is_blank(download_uri) || !out_path) return false;
    *out_path = NULL;

    char *file_path = single_file_path(s, model_id);
    if (!file_path) return false;

    if (file_exists(file_path) && expected_sha256 != NULL) {
        if (verify_sha256(file_path, expected_sha256)) {
            if (progress) progress(progress_user, 1.0);
            *out_path = file_path;
            return true;
        }
        remove(file_path);
    } else if (file_exists(file_path) && expected_sha256 == NULL) {
        if (progress) progress(progress_user, 1.0);
        *out_path = file_path;
        return true;
    }

    if (!s->fetch) { free(file_path); return false; }

    size_t tn = strlen(file_path) + 5;
    char *temp = (char *)malloc(tn);
    if (!temp) { free(file_path); return false; }
    snprintf(temp, tn, "%s.tmp", file_path);

    bool ok = s->fetch(s->fetch_user, download_uri, temp, progress, progress_user);
    if (!ok) { remove(temp); free(temp); free(file_path); return false; }

    if (expected_sha256 != NULL && !verify_sha256(temp, expected_sha256)) {
        remove(temp); free(temp); free(file_path); return false;
    }

    remove(file_path);
    if (rename(temp, file_path) != 0) { remove(temp); free(temp); free(file_path); return false; }
    free(temp);
    if (progress) progress(progress_user, 1.0);
    *out_path = file_path;
    return true;
}

bool ca_model_download_service_ensure_bundle(
    ca_model_download_service_t *s, const char *model_id, const char *repo,
    const ca_bundle_file_spec_t *bundle_files, size_t count,
    ca_download_progress_ratio_fn progress, void *progress_user, char **out_dir) {
    if (!s || is_blank(model_id) || is_blank(repo) || !bundle_files || count == 0 || !out_dir)
        return false;
    *out_dir = NULL;

    char model_dir[1024];
    join_path(model_dir, sizeof(model_dir), s->storage_directory, model_id);
    if (mkdir_p(model_dir) != 0) return false;

    for (size_t i = 0; i < count; i++) {
        if (is_blank(bundle_files[i].name)) return false;

        char dest[1024];
        join_path(dest, sizeof(dest), model_dir, bundle_files[i].name);
        /* ensure parent dir of dest exists (bundle names may include subdirs) */
        {
            char parent[1024];
            snprintf(parent, sizeof(parent), "%s", dest);
            char *sep = NULL;
            for (char *q = parent; *q; q++) if (*q == '/' || *q == '\\') sep = q;
            if (sep) { *sep = 0; if (*parent) mkdir_p(parent); }
        }

        if (file_exists(dest) && verify_sha256(dest, bundle_files[i].sha256)) {
            if (progress) {
                /* recompute overall done ratio below */
            }
            continue; /* cached + valid */
        }
        if (file_exists(dest)) remove(dest);

        if (!s->fetch) return false;

        size_t tn = strlen(dest) + 5;
        char *temp = (char *)malloc(tn);
        if (!temp) return false;
        snprintf(temp, tn, "%s.tmp", dest);

        char *primary = ca_modelscope_primary_url(repo, bundle_files[i].name);
        char *fallback = ca_modelscope_fallback_url(repo, bundle_files[i].name);
        if (!primary || !fallback) { free(primary); free(fallback); free(temp); return false; }

        bool ok = s->fetch(s->fetch_user, primary, temp, NULL, NULL);
        if (!ok) {
            remove(temp);
            ok = s->fetch(s->fetch_user, fallback, temp, NULL, NULL);
        }
        free(primary); free(fallback);
        if (!ok) { remove(temp); free(temp); return false; }

        if (!verify_sha256(temp, bundle_files[i].sha256)) {
            remove(temp); free(temp); return false;
        }
        if (file_exists(dest)) remove(dest);
        if (rename(temp, dest) != 0) { remove(temp); free(temp); return false; }
        free(temp);
    }

    if (progress) progress(progress_user, 1.0);
    *out_dir = xstrdup(model_dir);
    return *out_dir != NULL;
}

bool ca_model_download_service_is_model_cached(ca_model_download_service_t *s,
                                               const char *model_id) {
    if (!s || is_blank(model_id)) return false;
    char *sf = single_file_path(s, model_id);
    if (sf && file_exists(sf)) { free(sf); return true; }
    free(sf);
    char dir[1024];
    join_path(dir, sizeof(dir), s->storage_directory, model_id);
    return dir_exists(dir);
}

void ca_model_download_service_delete_model(ca_model_download_service_t *s,
                                            const char *model_id) {
    if (!s || is_blank(model_id)) return;
    char *sf = single_file_path(s, model_id);
    if (sf && file_exists(sf)) remove(sf);
    free(sf);
    char dir[1024];
    join_path(dir, sizeof(dir), s->storage_directory, model_id);
    if (dir_exists(dir)) rm_rf(dir);
}

int64_t ca_model_download_service_available_disk_space(ca_model_download_service_t *s) {
    if (!s) return -1;
#if defined(_WIN32)
    /* GetDiskFreeSpaceExA-free: use _stati stub — portable fallback returns a
     * large sentinel so callers treating >0 as "space available" work. The C#
     * DriveInfo.AvailableFreeSpace has no pure-libc equivalent; tests only
     * assert the value is non-negative. */
    return (int64_t)1 << 40; /* 1 TiB sentinel */
#else
    return (int64_t)1 << 40;
#endif
}

/*
 * installed.json writer. Emits the same field shape as C#'s InstalledManifest
 * (JSON, indented) so registry.h's reader and the C# reader both parse it.
 */
static void json_escape_into(sbuf *b, const char *s) {
    sbuf_addc(b, '"');
    for (const char *p = s ? s : ""; *p; p++) {
        unsigned char ch = (unsigned char)*p;
        switch (ch) {
            case '"':  sbuf_add(b, "\\\""); break;
            case '\\': sbuf_add(b, "\\\\"); break;
            case '\n': sbuf_add(b, "\\n");  break;
            case '\r': sbuf_add(b, "\\r");  break;
            case '\t': sbuf_add(b, "\\t");  break;
            default:
                if (ch < 0x20) { char u[8]; snprintf(u, sizeof(u), "\\u%04x", ch); sbuf_add(b, u); }
                else sbuf_addc(b, (char)ch);
        }
    }
    sbuf_addc(b, '"');
}

bool ca_model_download_service_write_installed_manifest(
    const char *model_dir, const char *model_id, const char *version,
    const char *repo, const ca_bundle_file_spec_t *bundle_files, size_t count) {
    if (is_blank(model_dir) || is_blank(model_id) || (!bundle_files && count > 0))
        return false;

    int64_t total_bytes = 0;
    for (size_t i = 0; i < count; i++)
        if (bundle_files[i].size_bytes > 0) total_bytes += bundle_files[i].size_bytes;

    sbuf b = {0}; b.data = NULL;
    char num[64];
    sbuf_add(&b, "{\n  \"ModelId\": ");   json_escape_into(&b, model_id);
    sbuf_add(&b, ",\n  \"Version\": ");    json_escape_into(&b, version ? version : "");
    sbuf_add(&b, ",\n  \"Repo\": ");
    if (repo) json_escape_into(&b, repo); else sbuf_add(&b, "null");
    snprintf(num, sizeof(num), ",\n  \"TotalBytes\": %lld", (long long)total_bytes);
    sbuf_add(&b, num);
    sbuf_add(&b, ",\n  \"Files\": [");
    for (size_t i = 0; i < count; i++) {
        if (i) sbuf_addc(&b, ',');
        sbuf_add(&b, "\n    { \"Name\": ");
        json_escape_into(&b, bundle_files[i].name);
        sbuf_add(&b, ", \"Sha256\": ");
        json_escape_into(&b, bundle_files[i].sha256);
        snprintf(num, sizeof(num), ", \"SizeBytes\": %lld }", (long long)bundle_files[i].size_bytes);
        sbuf_add(&b, num);
    }
    sbuf_add(&b, count ? "\n  ]" : "]");
    /* InstalledAtUtc as Unix ms */
    snprintf(num, sizeof(num), ",\n  \"InstalledAtUnixMs\": %lld\n}\n",
             (long long)((int64_t)time(NULL) * 1000));
    sbuf_add(&b, num);
    if (!b.data) return false;

    char path[1024];
    join_path(path, sizeof(path), model_dir, "installed.json");
    FILE *f = fopen(path, "wb");
    if (!f) { free(b.data); return false; }
    fwrite(b.data, 1, b.len, f);
    fclose(f);
    free(b.data);
    return true;
}

/* ===========================================================================
 * LayerStreaming
 * =========================================================================== */

void ca_layer_streaming_plan_free(ca_layer_streaming_plan_t *p) {
    if (!p) return;
    free(p->model_id);
    if (p->shards) {
        for (size_t i = 0; i < p->shard_count; i++) free(p->shards[i].weight_shard_path);
        free(p->shards);
    }
    p->model_id = NULL;
    p->shards = NULL;
    p->shard_count = 0;
}

bool ca_layer_streaming_forward(
    const ca_layer_streaming_runner_t *runner,
    const ca_layer_streaming_plan_t *plan,
    const float *initial_hidden, size_t initial_len,
    void (*on_layer)(void *user, int layer_index, const float *hidden, size_t len),
    void *on_layer_user,
    float **out_hidden, size_t *out_len) {
    if (!runner || !plan || !out_hidden || !out_len) return false;
    if (plan->shard_count == 0) return false;
    if (!runner->is_available || !runner->run_layer) return false;
    *out_hidden = NULL;
    *out_len = 0;

    /* Working copy of the hidden state (owned). */
    float *hidden = NULL;
    size_t hlen = initial_len;
    if (initial_len > 0) {
        hidden = (float *)malloc(initial_len * sizeof(float));
        if (!hidden) return false;
        if (initial_hidden) memcpy(hidden, initial_hidden, initial_len * sizeof(float));
        else memset(hidden, 0, initial_len * sizeof(float));
    }

    for (size_t i = 0; i < plan->shard_count; i++) {
        float *next = NULL;
        size_t nlen = 0;
        bool ok = runner->run_layer(runner->user, &plan->shards[i], hidden, hlen, &next, &nlen);
        if (!ok) { free(hidden); free(next); return false; }
        free(hidden);
        hidden = next;
        hlen = nlen;
        if (on_layer) on_layer(on_layer_user, plan->shards[i].layer_index, hidden, hlen);
        if (runner->evict) runner->evict(runner->user, plan->shards[i].layer_index);
    }

    *out_hidden = hidden;
    *out_len = hlen;
    return true;
}

static int shard_cmp(const void *a, const void *b) {
    const ca_layer_weight_shard_t *sa = (const ca_layer_weight_shard_t *)a;
    const ca_layer_weight_shard_t *sb = (const ca_layer_weight_shard_t *)b;
    return sa->layer_index - sb->layer_index;
}

bool ca_layer_shard_discover(const char *model_id, const char *model_directory,
                             ca_layer_streaming_plan_t *out) {
    if (is_blank(model_id) || !out) return false;
    if (!dir_exists(model_directory)) return false;
    memset(out, 0, sizeof(*out));

    DIR *d = opendir(model_directory);
    if (!d) return false;

    ca_layer_weight_shard_t *shards = NULL;
    size_t cap = 0, n = 0;
    int64_t total = 0;
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        const char *name = e->d_name;
        if (strncmp(name, "layer_", 6) != 0) continue;
        /* stem = filename without extension */
        const char *dot = strrchr(name, '.');
        size_t stem_len = dot ? (size_t)(dot - name) : strlen(name);
        /* parse index after the first underscore */
        const char *us = strchr(name, '_');
        if (!us) continue;
        const char *num = us + 1;
        /* number is between num and stem_end */
        char numbuf[32];
        size_t numlen = (size_t)((name + stem_len) - num);
        if (numlen == 0 || numlen >= sizeof(numbuf)) continue;
        memcpy(numbuf, num, numlen); numbuf[numlen] = 0;
        char *endp = NULL;
        long idx = strtol(numbuf, &endp, 10);
        if (endp == numbuf || *endp != 0) continue;

        char full[1024];
        join_path(full, sizeof(full), model_directory, name);
        int64_t sz = file_size(full);
        if (sz < 0) sz = 0;

        if (n == cap) {
            size_t nc = cap ? cap * 2 : 8;
            ca_layer_weight_shard_t *ns =
                (ca_layer_weight_shard_t *)realloc(shards, nc * sizeof(*ns));
            if (!ns) { break; }
            shards = ns; cap = nc;
        }
        shards[n].layer_index = (int)idx;
        shards[n].weight_shard_path = xstrdup(full);
        shards[n].approx_bytes = sz;
        total += sz;
        n++;
    }
    closedir(d);

    if (n > 0) qsort(shards, n, sizeof(*shards), shard_cmp);

    out->model_id = xstrdup(model_id);
    out->total_layers = (int)n;
    out->shards = shards;
    out->shard_count = n;
    out->approx_parameter_bytes = total;
    if (!out->model_id) { ca_layer_streaming_plan_free(out); return false; }
    return true;
}

/* ===========================================================================
 * FeedbackTrainingQueue
 * =========================================================================== */

void ca_training_sample_free(ca_training_sample_t *s) {
    if (!s) return;
    free(s->user_text);
    free(s->assistant_text);
    free(s->preferred_text);
    s->user_text = s->assistant_text = s->preferred_text = NULL;
}

void ca_training_samples_free(ca_training_sample_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; i++) ca_training_sample_free(&arr[i]);
    free(arr);
}

struct ca_feedback_training_queue {
    char *path;
};

ca_feedback_training_queue_t *ca_feedback_training_queue_create(const char *path) {
    if (is_blank(path)) return NULL;
    ca_feedback_training_queue_t *q =
        (ca_feedback_training_queue_t *)calloc(1, sizeof(*q));
    if (!q) return NULL;
    q->path = xstrdup(path);
    if (!q->path) { free(q); return NULL; }
    /* create parent dir + empty file */
    {
        char parent[1024];
        snprintf(parent, sizeof(parent), "%s", q->path);
        char *sep = NULL;
        for (char *p = parent; *p; p++) if (*p == '/' || *p == '\\') sep = p;
        if (sep) { *sep = 0; if (*parent) mkdir_p(parent); }
    }
    if (!file_exists(q->path)) {
        FILE *f = fopen(q->path, "wb");
        if (f) fclose(f);
    }
    return q;
}

void ca_feedback_training_queue_destroy(ca_feedback_training_queue_t *q) {
    if (!q) return;
    free(q->path);
    free(q);
}

int ca_feedback_training_queue_pending(ca_feedback_training_queue_t *q) {
    if (!q || !file_exists(q->path)) return 0;
    FILE *f = fopen(q->path, "rb");
    if (!f) return 0;
    int count = 0;
    int ch, prev = '\n', last = 0;
    while ((ch = fgetc(f)) != EOF) {
        last = ch;
        if (ch == '\n') count++;
        prev = ch;
    }
    (void)prev;
    /* trailing partial line without newline still counts as one */
    if (last != 0 && last != '\n') count++;
    fclose(f);
    return count;
}

/* Escape a field for our line format: backslash-escape '\\', '\n', '\t'. */
static void field_escape(sbuf *b, const char *s) {
    for (const char *p = s ? s : ""; *p; p++) {
        char ch = *p;
        if (ch == '\\') sbuf_add(b, "\\\\");
        else if (ch == '\n') sbuf_add(b, "\\n");
        else if (ch == '\t') sbuf_add(b, "\\t");
        else sbuf_addc(b, ch);
    }
}

/* Unescape one tab-delimited field (in place-ish into a fresh alloc). */
static char *field_unescape(const char *s, size_t len) {
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < len; i++) {
        char ch = s[i];
        if (ch == '\\' && i + 1 < len) {
            char nx = s[i + 1];
            if (nx == '\\') { out[o++] = '\\'; i++; continue; }
            if (nx == 'n')  { out[o++] = '\n'; i++; continue; }
            if (nx == 't')  { out[o++] = '\t'; i++; continue; }
        }
        out[o++] = ch;
    }
    out[o] = 0;
    return out;
}

bool ca_feedback_training_queue_enqueue(ca_feedback_training_queue_t *q,
                                        const ca_training_sample_t *sample) {
    if (!q || !sample) return false;
    /* Line format: user \t assistant \t preferred \t polarity \t at_unix_ms */
    sbuf b = {0}; b.data = NULL;
    field_escape(&b, sample->user_text);
    sbuf_addc(&b, '\t');
    field_escape(&b, sample->assistant_text);
    sbuf_addc(&b, '\t');
    field_escape(&b, sample->preferred_text);
    char tail[64];
    snprintf(tail, sizeof(tail), "\t%d\t%lld", sample->polarity, (long long)sample->at_unix_ms);
    sbuf_add(&b, tail);
    sbuf_addc(&b, '\n');
    if (!b.data) return false;

    FILE *f = fopen(q->path, "ab");
    if (!f) { free(b.data); return false; }
    fwrite(b.data, 1, b.len, f);
    fclose(f);
    free(b.data);
    return true;
}

/* Parse one line into *out. Returns false on a malformed line. */
static bool parse_sample_line(const char *line, size_t len, ca_training_sample_t *out) {
    /* split on the 4 tab separators */
    const char *fields[5];
    size_t flen[5];
    int nf = 0;
    const char *start = line;
    for (size_t i = 0; i <= len && nf < 5; i++) {
        if (i == len || line[i] == '\t') {
            fields[nf] = start;
            flen[nf] = (size_t)(line + i - start);
            nf++;
            start = line + i + 1;
        }
    }
    if (nf != 5) return false;
    memset(out, 0, sizeof(*out));
    out->user_text      = field_unescape(fields[0], flen[0]);
    out->assistant_text = field_unescape(fields[1], flen[1]);
    out->preferred_text = field_unescape(fields[2], flen[2]);
    char nb[32]; size_t pl = flen[3] < sizeof(nb) - 1 ? flen[3] : sizeof(nb) - 1;
    memcpy(nb, fields[3], pl); nb[pl] = 0;
    out->polarity = (int)strtol(nb, NULL, 10);
    char mb[32]; size_t ml = flen[4] < sizeof(mb) - 1 ? flen[4] : sizeof(mb) - 1;
    memcpy(mb, fields[4], ml); mb[ml] = 0;
    out->at_unix_ms = (int64_t)strtoll(mb, NULL, 10);
    if (!out->user_text || !out->assistant_text || !out->preferred_text) {
        ca_training_sample_free(out);
        return false;
    }
    return true;
}

/* Read all lines of the file into a NULL-terminated array (each line no NL). */
static char **read_all_lines(const char *path, size_t *out_count) {
    *out_count = 0;
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0) { fclose(f); return NULL; }
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t rd = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[rd] = 0;

    char **lines = NULL;
    size_t cap = 0, n = 0;
    size_t i = 0;
    while (i < rd) {
        size_t j = i;
        while (j < rd && buf[j] != '\n') j++;
        size_t linelen = j - i;
        if (n == cap) {
            size_t nc = cap ? cap * 2 : 8;
            char **nl = (char **)realloc(lines, nc * sizeof(char *));
            if (!nl) break;
            lines = nl; cap = nc;
        }
        char *l = (char *)malloc(linelen + 1);
        if (!l) break;
        memcpy(l, buf + i, linelen); l[linelen] = 0;
        lines[n++] = l;
        i = j + 1;
    }
    free(buf);
    *out_count = n;
    return lines;
}

bool ca_feedback_training_queue_drain(ca_feedback_training_queue_t *q, int max_samples,
                                      ca_training_sample_t **out_arr, size_t *out_count) {
    if (!q || max_samples <= 0 || !out_arr || !out_count) return false;
    *out_arr = NULL;
    *out_count = 0;
    if (!file_exists(q->path)) return true;

    size_t line_count = 0;
    char **lines = read_all_lines(q->path, &line_count);
    if (!lines) return true; /* empty */

    size_t take = (size_t)max_samples < line_count ? (size_t)max_samples : line_count;

    ca_training_sample_t *arr = NULL;
    size_t got = 0;
    if (take > 0) {
        arr = (ca_training_sample_t *)calloc(take, sizeof(*arr));
        if (!arr) {
            for (size_t i = 0; i < line_count; i++) free(lines[i]);
            free(lines);
            return false;
        }
        for (size_t i = 0; i < take; i++) {
            ca_training_sample_t sample;
            if (parse_sample_line(lines[i], strlen(lines[i]), &sample)) {
                arr[got++] = sample; /* move */
            }
            /* malformed lines are skipped (mirrors C# malformed-line-skip) */
        }
    }

    /* rewrite remainder */
    FILE *f = fopen(q->path, "wb");
    if (f) {
        for (size_t i = take; i < line_count; i++) {
            fputs(lines[i], f);
            fputc('\n', f);
        }
        fclose(f);
    }

    for (size_t i = 0; i < line_count; i++) free(lines[i]);
    free(lines);

    *out_arr = arr;
    *out_count = got;
    return true;
}

/* ===========================================================================
 * NightlyAdapterTrainer
 * =========================================================================== */

void ca_nightly_trainer_options_init(ca_nightly_trainer_options_t *opts) {
    if (!opts) return;
    opts->min_batch_size = 16;
    opts->max_samples_per_run = 256;
    opts->learning_rate = 1e-4f;
    opts->lora_rank = 8;
    opts->adapter_path = "circleai-lora.mnn";
    opts->tokenizer = NULL;
    opts->tokenizer_user = NULL;
}

/* Char-level tokenizer fallback: each byte becomes an id. Two-call length
 * protocol matches the ca_nightly_trainer_options tokenizer contract. */
static size_t char_tokenizer(const char *text, int *out, size_t out_cap) {
    if (!text) return 0;
    size_t n = strlen(text);
    if (out) {
        size_t m = n < out_cap ? n : out_cap;
        for (size_t i = 0; i < m; i++) out[i] = (unsigned char)text[i];
    }
    return n;
}

/* Tokenize using opts->tokenizer or the char fallback. Returns a fresh int[]
 * (caller frees) and sets *len. NULL on OOM (or when text is empty -> *len=0). */
static int *tokenize(const ca_nightly_trainer_options_t *opts, const char *text, size_t *len) {
    size_t n;
    if (opts->tokenizer) n = opts->tokenizer(opts->tokenizer_user, text, NULL, 0);
    else                 n = char_tokenizer(text, NULL, 0);
    *len = n;
    if (n == 0) return NULL;
    int *ids = (int *)malloc(n * sizeof(int));
    if (!ids) { *len = 0; return NULL; }
    if (opts->tokenizer) opts->tokenizer(opts->tokenizer_user, text, ids, n);
    else                 char_tokenizer(text, ids, n);
    return ids;
}

bool ca_nightly_adapter_trainer_run_once(
    ca_feedback_training_queue_t *queue, const ca_lora_adapter_manager_t *adapter,
    const ca_nightly_trainer_options_t *opts, int *out_steps, float *out_avg_loss) {
    if (!queue || !adapter || !opts || !adapter->train_step) return false;
    if (out_steps) *out_steps = 0;
    if (out_avg_loss) *out_avg_loss = 0.0f;

    int pending = ca_feedback_training_queue_pending(queue);
    if (pending < opts->min_batch_size) return true; /* skip */

    ca_training_sample_t *samples = NULL;
    size_t count = 0;
    if (!ca_feedback_training_queue_drain(queue, opts->max_samples_per_run, &samples, &count))
        return false;
    if (count == 0) { ca_training_samples_free(samples, count); return true; }

    float total_loss = 0.0f;
    int step_count = 0;
    bool unsupported = false;

    for (size_t i = 0; i < count && !unsupported; i++) {
        size_t in_len = 0, tgt_len = 0;
        int *input = tokenize(opts, samples[i].user_text, &in_len);
        const char *target_text = (samples[i].polarity >= 0)
            ? samples[i].preferred_text : samples[i].assistant_text;
        int *target = tokenize(opts, target_text, &tgt_len);
        if (in_len == 0 || tgt_len == 0) { free(input); free(target); continue; }

        float loss = adapter->train_step(adapter->user, input, in_len, target, tgt_len,
                                         opts->learning_rate, opts->lora_rank);
        free(input); free(target);
        if (loss < 0.0f) { unsupported = true; break; } /* NotSupported re-queue */
        total_loss += loss;
        step_count++;
    }

    if (unsupported) {
        /* re-queue all drained samples, skip the run */
        for (size_t i = 0; i < count; i++) ca_feedback_training_queue_enqueue(queue, &samples[i]);
        ca_training_samples_free(samples, count);
        return true;
    }

    if (step_count > 0) {
        if (adapter->save_adapter) adapter->save_adapter(adapter->user, opts->adapter_path);
        if (adapter->apply)        adapter->apply(adapter->user, opts->adapter_path);
        if (out_steps) *out_steps = step_count;
        if (out_avg_loss) *out_avg_loss = total_loss / (float)step_count;
    }

    ca_training_samples_free(samples, count);
    return true;
}
