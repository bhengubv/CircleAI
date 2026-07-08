/*
 * model_runtime.c — Core model-management runtime (C11 port).
 *
 * See model_runtime.h. Faithful port of CircleAI.Core's loader/manager/
 * downloader/source stack, with the network abstracted behind an injected
 * in-memory source seam. Pure C11 + libc; self-contained SHA-256.
 */

#include "circle_ai/model_runtime.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <sys/stat.h>

#if defined(_WIN32)
  #include <direct.h>
  #define CA_MKDIR(p) _mkdir(p)
  #define CA_SEP '\\'
  #define strncasecmp _strnicmp
  #define strcasecmp  _stricmp
#else
  #include <sys/types.h>
  #include <strings.h>   /* strcasecmp / strncasecmp */
  #define CA_MKDIR(p) mkdir((p), 0777)
  #define CA_SEP '/'
#endif

/* ─────────────────────── small helpers ─────────────────────── */

static char *ca_strdup2(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int dir_exists(const char *path) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFDIR) != 0;
#else
    return S_ISDIR(st.st_mode);
#endif
}

static int file_exists(const char *path) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
#if defined(_WIN32)
    return (st.st_mode & _S_IFREG) != 0;
#else
    return S_ISREG(st.st_mode);
#endif
}

static int mkdir_p(const char *path) {
    if (!path || !*path) return -1;
    if (dir_exists(path)) return 0;
    /* create parents */
    char buf[1024];
    snprintf(buf, sizeof(buf), "%s", path);
    for (char *q = buf + 1; *q; q++) {
        if (*q == '/' || *q == '\\') {
            char saved = *q; *q = 0;
            if (!dir_exists(buf)) CA_MKDIR(buf);
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
    if (last == '/' || last == '\\')
        snprintf(out, cap, "%s%s", a, b);
    else
        snprintf(out, cap, "%s%c%s", a, CA_SEP, b);
}

static bool is_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++)
        if (!isspace(*p)) return false;
    return true;
}

/* ─────────────────────── SHA-256 ─────────────────────── */

typedef struct {
    uint32_t state[8];
    uint64_t bitlen;
    uint8_t  buf[64];
    size_t   buflen;
} sha256_ctx;

static uint32_t rotr32(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

static void sha256_init(sha256_ctx *c) {
    c->state[0] = 0x6a09e667; c->state[1] = 0xbb67ae85;
    c->state[2] = 0x3c6ef372; c->state[3] = 0xa54ff53a;
    c->state[4] = 0x510e527f; c->state[5] = 0x9b05688c;
    c->state[6] = 0x1f83d9ab; c->state[7] = 0x5be0cd19;
    c->bitlen = 0; c->buflen = 0;
}

static const uint32_t SHA256_K[64] = {
    0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
    0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
    0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
    0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
    0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
    0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
    0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
    0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
};

static void sha256_block(sha256_ctx *c, const uint8_t *p) {
    uint32_t w[64];
    for (int i = 0; i < 16; i++)
        w[i] = ((uint32_t)p[i*4] << 24) | ((uint32_t)p[i*4+1] << 16) |
               ((uint32_t)p[i*4+2] << 8) | (uint32_t)p[i*4+3];
    for (int i = 16; i < 64; i++) {
        uint32_t s0 = rotr32(w[i-15],7) ^ rotr32(w[i-15],18) ^ (w[i-15] >> 3);
        uint32_t s1 = rotr32(w[i-2],17) ^ rotr32(w[i-2],19) ^ (w[i-2] >> 10);
        w[i] = w[i-16] + s0 + w[i-7] + s1;
    }
    uint32_t a=c->state[0],b=c->state[1],cc=c->state[2],d=c->state[3];
    uint32_t e=c->state[4],f=c->state[5],g=c->state[6],h=c->state[7];
    for (int i = 0; i < 64; i++) {
        uint32_t S1 = rotr32(e,6) ^ rotr32(e,11) ^ rotr32(e,25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = h + S1 + ch + SHA256_K[i] + w[i];
        uint32_t S0 = rotr32(a,2) ^ rotr32(a,13) ^ rotr32(a,22);
        uint32_t maj = (a & b) ^ (a & cc) ^ (b & cc);
        uint32_t t2 = S0 + maj;
        h=g; g=f; f=e; e=d+t1; d=cc; cc=b; b=a; a=t1+t2;
    }
    c->state[0]+=a; c->state[1]+=b; c->state[2]+=cc; c->state[3]+=d;
    c->state[4]+=e; c->state[5]+=f; c->state[6]+=g; c->state[7]+=h;
}

static void sha256_update(sha256_ctx *c, const uint8_t *data, size_t len) {
    c->bitlen += (uint64_t)len * 8;
    while (len > 0) {
        size_t take = 64 - c->buflen;
        if (take > len) take = len;
        memcpy(c->buf + c->buflen, data, take);
        c->buflen += take; data += take; len -= take;
        if (c->buflen == 64) { sha256_block(c, c->buf); c->buflen = 0; }
    }
}

static void sha256_final(sha256_ctx *c, uint8_t out[32]) {
    uint64_t bl = c->bitlen;
    uint8_t pad = 0x80;
    sha256_update(c, &pad, 1);
    uint8_t zero = 0;
    while (c->buflen != 56) sha256_update(c, &zero, 1);
    uint8_t lenbuf[8];
    for (int i = 0; i < 8; i++) lenbuf[i] = (uint8_t)(bl >> (56 - i*8));
    sha256_update(c, lenbuf, 8);
    for (int i = 0; i < 8; i++) {
        out[i*4]   = (uint8_t)(c->state[i] >> 24);
        out[i*4+1] = (uint8_t)(c->state[i] >> 16);
        out[i*4+2] = (uint8_t)(c->state[i] >> 8);
        out[i*4+3] = (uint8_t)(c->state[i]);
    }
}

void ca_mr_sha256(const uint8_t *data, size_t len, uint8_t out[32]) {
    sha256_ctx c; sha256_init(&c);
    sha256_update(&c, data, len);
    sha256_final(&c, out);
}

bool ca_mr_sha256_file(const char *path, uint8_t out[32]) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    sha256_ctx c; sha256_init(&c);
    uint8_t buf[8192];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) sha256_update(&c, buf, n);
    fclose(f);
    sha256_final(&c, out);
    return true;
}

void ca_mr_sha256_hex(const uint8_t digest[32], char out[65]) {
    static const char *hex = "0123456789abcdef";
    for (int i = 0; i < 32; i++) {
        out[i*2]   = hex[(digest[i] >> 4) & 0xF];
        out[i*2+1] = hex[digest[i] & 0xF];
    }
    out[64] = 0;
}

/* Accept "sha256:<hex>" or bare hex, case-insensitive equality with actual hex. */
static bool checksum_matches(const char *expected, const char *actual_hex) {
    if (!expected) return false;
    /* trim leading/trailing whitespace */
    while (*expected && isspace((unsigned char)*expected)) expected++;
    const char *prefix = "sha256:";
    size_t plen = strlen(prefix);
    if (strncasecmp(expected, prefix, plen) == 0) {
        expected += plen;
        while (*expected && isspace((unsigned char)*expected)) expected++;
    }
    /* compare ignoring trailing whitespace on expected */
    size_t a = 0;
    while (expected[a] && !isspace((unsigned char)expected[a])) a++;
    size_t b = strlen(actual_hex);
    if (a != b) return false;
    for (size_t i = 0; i < a; i++) {
        if (tolower((unsigned char)expected[i]) != tolower((unsigned char)actual_hex[i]))
            return false;
    }
    return true;
}

static bool verify_file_checksum(const char *path, const char *expected_checksum) {
    uint8_t digest[32];
    if (!ca_mr_sha256_file(path, digest)) return false;
    char hex[65];
    ca_mr_sha256_hex(digest, hex);
    return checksum_matches(expected_checksum, hex);
}

/* ═══════════════════════ IModelSource (in-memory seam) ═══════════════════════ */

typedef struct {
    char    *url;   /* owned */
    uint8_t *data;  /* owned */
    size_t   len;
} source_entry_t;

struct ca_model_source {
    char           *name;
    bool            available;
    bool            enforce_modelscope; /* ModelScopeSource host rule */
    source_entry_t *entries;
    size_t          count;
    size_t          cap;
};

const char *ca_model_source_name(const ca_model_source_t *s) {
    return s ? s->name : NULL;
}

bool ca_model_source_is_available(ca_model_source_t *s) {
    return s && s->available;
}

/* host contains needle (case-insensitive), where host is the substring of url
 * between "://" and the next '/'. Simplistic but matches our URL shapes. */
static bool url_host_contains(const char *url, const char *needle) {
    const char *scheme = strstr(url, "://");
    const char *host = scheme ? scheme + 3 : url;
    const char *slash = strchr(host, '/');
    size_t hlen = slash ? (size_t)(slash - host) : strlen(host);
    size_t nlen = strlen(needle);
    if (nlen == 0 || nlen > hlen) return false;
    for (size_t i = 0; i + nlen <= hlen; i++) {
        size_t j = 0;
        for (; j < nlen; j++)
            if (tolower((unsigned char)host[i+j]) != tolower((unsigned char)needle[j])) break;
        if (j == nlen) return true;
    }
    return false;
}

/* host ends with suffix (case-insensitive). */
static bool url_host_ends_with(const char *url, const char *suffix) {
    const char *scheme = strstr(url, "://");
    const char *host = scheme ? scheme + 3 : url;
    const char *slash = strchr(host, '/');
    size_t hlen = slash ? (size_t)(slash - host) : strlen(host);
    size_t slen = strlen(suffix);
    if (slen > hlen) return false;
    const char *tail = host + (hlen - slen);
    for (size_t i = 0; i < slen; i++)
        if (tolower((unsigned char)tail[i]) != tolower((unsigned char)suffix[i])) return false;
    return true;
}

bool ca_model_source_download(ca_model_source_t *s, const char *url,
                              const char *local_path,
                              ca_download_progress_fn progress, void *progress_user) {
    if (!s || is_blank(url) || is_blank(local_path)) return false;
    if (s->enforce_modelscope && !url_host_ends_with(url, "modelscope.cn")) return false;

    const source_entry_t *hit = NULL;
    for (size_t i = 0; i < s->count; i++) {
        if (strcmp(s->entries[i].url, url) == 0) { hit = &s->entries[i]; break; }
    }
    if (!hit) return false;

    /* ensure parent dir */
    char dir[1024];
    snprintf(dir, sizeof(dir), "%s", local_path);
    char *last_sep = NULL;
    for (char *q = dir; *q; q++) if (*q == '/' || *q == '\\') last_sep = q;
    if (last_sep) { *last_sep = 0; if (*dir) mkdir_p(dir); }

    FILE *f = fopen(local_path, "wb");
    if (!f) return false;
    if (hit->len > 0 && fwrite(hit->data, 1, hit->len, f) != hit->len) {
        fclose(f); return false;
    }
    fclose(f);

    if (progress) {
        const char *fname = local_path;
        for (const char *q = local_path; *q; q++) if (*q == '/' || *q == '\\') fname = q + 1;
        ca_download_progress_report_t rep = {
            fname, (int64_t)hit->len, (int64_t)hit->len, 0.0, 0.0
        };
        progress(progress_user, &rep);
    }
    return true;
}

void ca_model_source_destroy(ca_model_source_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; i++) {
        free(s->entries[i].url);
        free(s->entries[i].data);
    }
    free(s->entries);
    free(s->name);
    free(s);
}

ca_model_source_t *ca_inmemory_model_source_create(const char *name) {
    ca_model_source_t *s = (ca_model_source_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->name = ca_strdup2(name ? name : "InMemory");
    s->available = true;
    if (!s->name) { free(s); return NULL; }
    return s;
}

bool ca_inmemory_model_source_add(ca_model_source_t *s, const char *url,
                                  const uint8_t *data, size_t len) {
    if (!s || !url) return false;
    /* replace existing */
    for (size_t i = 0; i < s->count; i++) {
        if (strcmp(s->entries[i].url, url) == 0) {
            uint8_t *nd = NULL;
            if (len > 0) { nd = (uint8_t *)malloc(len); if (!nd) return false; memcpy(nd, data, len); }
            free(s->entries[i].data);
            s->entries[i].data = nd;
            s->entries[i].len = len;
            return true;
        }
    }
    if (s->count >= s->cap) {
        size_t nc = s->cap == 0 ? 4 : s->cap * 2;
        source_entry_t *g = (source_entry_t *)realloc(s->entries, nc * sizeof(source_entry_t));
        if (!g) return false;
        s->entries = g; s->cap = nc;
    }
    source_entry_t *e = &s->entries[s->count];
    e->url = ca_strdup2(url);
    e->data = NULL; e->len = len;
    if (len > 0) { e->data = (uint8_t *)malloc(len); if (!e->data) { free(e->url); return false; } memcpy(e->data, data, len); }
    if (!e->url) { free(e->data); return false; }
    s->count++;
    return true;
}

void ca_inmemory_model_source_set_available(ca_model_source_t *s, bool available) {
    if (s) s->available = available;
}

ca_model_source_t *ca_modelscope_source_create(void) {
    ca_model_source_t *s = ca_inmemory_model_source_create("ModelScope");
    if (s) s->enforce_modelscope = true;
    return s;
}

ca_model_source_t *ca_huggingface_source_create(void) {
    /* Tombstone: the C# type throws on construction; here we refuse to build. */
    return NULL;
}

/* ═══════════════════════ ModelDownloader ═══════════════════════ */

struct ca_model_downloader {
    ca_model_source_t     **sources;
    size_t                  source_count;
    bool                    owns_sources;
    const ca_model_info_t  *registry;
    size_t                  registry_count;
    ca_download_progress_fn progress_fn;
    void                   *progress_user;
};

ca_model_downloader_t *ca_model_downloader_create(
    ca_model_source_t **sources, size_t source_count, bool owns_sources,
    const struct ca_model_info *registry, size_t registry_count) {
    if (!sources || source_count == 0) return NULL;
    ca_model_downloader_t *d = (ca_model_downloader_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->sources = sources;
    d->source_count = source_count;
    d->owns_sources = owns_sources;
    d->registry = registry;
    d->registry_count = registry_count;
    return d;
}

void ca_model_downloader_destroy(ca_model_downloader_t *d) {
    if (!d) return;
    if (d->owns_sources) {
        for (size_t i = 0; i < d->source_count; i++) ca_model_source_destroy(d->sources[i]);
    }
    free(d);
}

void ca_model_downloader_set_progress(ca_model_downloader_t *d,
                                      ca_download_progress_fn fn, void *user) {
    if (!d) return;
    d->progress_fn = fn;
    d->progress_user = user;
}

/* Heuristic: match by name-substring in host, else the modelscope fallback. */
static ca_model_source_t *match_source(ca_model_downloader_t *d, const char *url) {
    for (size_t i = 0; i < d->source_count; i++) {
        if (url_host_contains(url, ca_model_source_name(d->sources[i])))
            return d->sources[i];
    }
    if (url_host_contains(url, "modelscope")) {
        for (size_t i = 0; i < d->source_count; i++) {
            if (strcasecmp(ca_model_source_name(d->sources[i]), "ModelScope") == 0)
                return d->sources[i];
        }
    }
    return NULL;
}

static void cleanup_partial(const char *path) {
    if (file_exists(path)) remove(path);
}

bool ca_model_downloader_download_from_candidates(
    ca_model_downloader_t *d, const char **candidate_urls, size_t candidate_count,
    const char *local_file_path, char **out_winner,
    ca_download_progress_fn progress, void *progress_user) {
    if (out_winner) *out_winner = NULL;
    if (!d || !candidate_urls || candidate_count == 0 || is_blank(local_file_path))
        return false;

    /* ensure parent dir */
    char dir[1024];
    snprintf(dir, sizeof(dir), "%s", local_file_path);
    char *last_sep = NULL;
    for (char *q = dir; *q; q++) if (*q == '/' || *q == '\\') last_sep = q;
    if (last_sep) { *last_sep = 0; if (*dir) mkdir_p(dir); }

    for (size_t i = 0; i < candidate_count; i++) {
        const char *url = candidate_urls[i];
        if (is_blank(url)) continue;
        ca_model_source_t *src = match_source(d, url);
        if (!src) continue;
        if (ca_model_source_download(src, url, local_file_path, progress, progress_user)) {
            if (out_winner) *out_winner = ca_strdup2(ca_model_source_name(src));
            return true;
        }
        cleanup_partial(local_file_path);
    }
    return false;
}

static const ca_model_info_t *dl_registry_find(const ca_model_info_t *reg, size_t n,
                                               const char *id) {
    for (size_t i = 0; i < n; i++) {
        /* embedded registry is case-insensitive (OrdinalIgnoreCase in C#) */
        if (reg[i].name && strcasecmp(reg[i].name, id) == 0) return &reg[i];
    }
    return NULL;
}

bool ca_model_downloader_download_model(ca_model_downloader_t *d,
                                        const char *model_id, const char *local_path) {
    if (!d || is_blank(model_id) || is_blank(local_path)) return false;
    const ca_model_info_t *e = dl_registry_find(d->registry, d->registry_count, model_id);
    if (!e) return false;                       /* KeyNotFoundException */

    mkdir_p(local_path);

    /* bundle entries can't be serviced by this single-file downloader */
    if (ca_model_info_is_bundle(e)) return false;
    if (!e->file_name) return false;

    char target[1200];
    join_path(target, sizeof(target), local_path, e->file_name);

    const char *candidates[2];
    size_t nc = 0;
    if (!is_blank(e->primary_url))  candidates[nc++] = e->primary_url;
    if (!is_blank(e->fallback_url)) candidates[nc++] = e->fallback_url;
    if (nc == 0) return false;                  /* no URL configured */

    char *winner = NULL;
    bool ok = ca_model_downloader_download_from_candidates(
        d, candidates, nc, target, &winner, d->progress_fn, d->progress_user);
    free(winner);
    if (!ok) cleanup_partial(target);
    return ok;
}

/* ═══════════════════════ LocalModelManager ═══════════════════════ */

struct ca_local_model_manager {
    char                  *models_directory;
    ca_model_downloader_t *downloader; /* borrowed */
};

ca_local_model_manager_t *ca_local_model_manager_create(
    const char *models_directory, ca_model_downloader_t *downloader) {
    ca_local_model_manager_t *m = (ca_local_model_manager_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->models_directory = ca_strdup2(models_directory ? models_directory : "Models");
    m->downloader = downloader;
    if (!m->models_directory) { free(m); return NULL; }
    if (!dir_exists(m->models_directory)) mkdir_p(m->models_directory);
    return m;
}

void ca_local_model_manager_destroy(ca_local_model_manager_t *m) {
    if (!m) return;
    free(m->models_directory);
    free(m);
}

static char *sanitize_model_id(const char *id) {
    char *s = ca_strdup2(id);
    if (!s) return NULL;
    for (char *p = s; *p; p++) if (*p == '/' || *p == '\\') *p = '_';
    return s;
}

bool ca_local_model_manager_verify(const char *model_path,
                                   const uint8_t *expected_checksum) {
    if (!model_path || !expected_checksum) return false;
    uint8_t digest[32];
    if (!ca_mr_sha256_file(model_path, digest)) return false;
    return memcmp(digest, expected_checksum, 32) == 0;
}

bool ca_local_model_manager_get_model_path(
    ca_local_model_manager_t *m, const char *model_id,
    const uint8_t *expected_checksum, char **out_path) {
    if (out_path) *out_path = NULL;
    if (!m || is_blank(model_id) || !out_path) return false;

    char *safe = sanitize_model_id(model_id);
    if (!safe) return false;
    char model_dir[1024];
    join_path(model_dir, sizeof(model_dir), m->models_directory, safe);
    free(safe);

    char weight[1200];
    join_path(weight, sizeof(weight), model_dir, "pytorch_model.bin");

    if (!dir_exists(model_dir) || !file_exists(weight)) {
        if (!m->downloader) return false;
        if (!ca_model_downloader_download_model(m->downloader, model_id, model_dir))
            return false;
    }

    if (expected_checksum) {
        if (!ca_local_model_manager_verify(weight, expected_checksum)) return false;
    }

    *out_path = ca_strdup2(model_dir);
    return *out_path != NULL;
}

/* ═══════════════════════ SafeModelHandle ═══════════════════════ */

struct ca_safe_model_handle {
    void                  *native_handle;
    ca_release_callback_fn release_callback;
    bool                   released;
};

ca_safe_model_handle_t *ca_safe_model_handle_create(void *native_handle,
                                                    ca_release_callback_fn release_callback) {
    if (!release_callback) return NULL;
    ca_safe_model_handle_t *h = (ca_safe_model_handle_t *)calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->native_handle = native_handle;
    h->release_callback = release_callback;
    return h;
}

bool ca_safe_model_handle_is_invalid(const ca_safe_model_handle_t *h) {
    return !h || h->native_handle == NULL;
}

void *ca_safe_model_handle_get(const ca_safe_model_handle_t *h) {
    return h ? h->native_handle : NULL;
}

void ca_safe_model_handle_destroy(ca_safe_model_handle_t *h) {
    if (!h) return;
    if (!h->released && h->native_handle != NULL) {
        h->release_callback(h->native_handle);
        h->native_handle = NULL;
        h->released = true;
    }
    free(h);
}

/* ═══════════════════════ LocalModelLoader ═══════════════════════ */

bool ca_model_info_is_bundle(const ca_model_info_t *info) {
    return info && info->bundle_files != NULL && info->bundle_count > 0;
}

struct ca_local_model_loader {
    char                  *model_dir;
    const ca_model_info_t *registry;
    size_t                 registry_count;
    ca_model_source_t     *source; /* borrowed */
};

#define BUNDLE_ANCHOR "llm.mnn.weight"

ca_local_model_loader_t *ca_local_model_loader_create(
    const char *model_dir,
    const ca_model_info_t *registry, size_t registry_count,
    ca_model_source_t *source) {
    ca_local_model_loader_t *l = (ca_local_model_loader_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->model_dir = ca_strdup2(model_dir ? model_dir : "Models");
    l->registry = registry;
    l->registry_count = registry_count;
    l->source = source;
    if (!l->model_dir) { free(l); return NULL; }
    if (!dir_exists(l->model_dir)) mkdir_p(l->model_dir);
    return l;
}

void ca_local_model_loader_destroy(ca_local_model_loader_t *l) {
    if (!l) return;
    free(l->model_dir);
    free(l);
}

static const ca_model_info_t *loader_find(const ca_local_model_loader_t *l,
                                          const char *name) {
    for (size_t i = 0; i < l->registry_count; i++) {
        /* registry keys are case-insensitive in C# (OrdinalIgnoreCase) */
        if (l->registry[i].name && strcasecmp(l->registry[i].name, name) == 0)
            return &l->registry[i];
    }
    return NULL;
}

bool ca_local_model_loader_get_model_path(const ca_local_model_loader_t *l,
                                          const char *model_name, char **out_path) {
    if (out_path) *out_path = NULL;
    if (!l || !model_name || !out_path) return false;
    const ca_model_info_t *info = loader_find(l, model_name);
    if (!info) return false;

    char buf[1200];
    if (ca_model_info_is_bundle(info)) {
        char sub[1024];
        join_path(sub, sizeof(sub), l->model_dir, model_name);
        join_path(buf, sizeof(buf), sub, BUNDLE_ANCHOR);
    } else {
        if (!info->file_name) return false;
        join_path(buf, sizeof(buf), l->model_dir, info->file_name);
    }
    *out_path = ca_strdup2(buf);
    return *out_path != NULL;
}

/* checksum is NULL or starts with "sha256:TBD" → skip verification. */
static bool checksum_is_tbd(const char *checksum) {
    if (!checksum) return true;
    return strncmp(checksum, "sha256:TBD", 10) == 0;
}

bool ca_local_model_loader_model_exists(const ca_local_model_loader_t *l,
                                        const char *model_name) {
    if (!l || !model_name) return false;
    const ca_model_info_t *info = loader_find(l, model_name);
    if (!info) return false;

    char *path = NULL;
    if (!ca_local_model_loader_get_model_path(l, model_name, &path)) return false;
    bool ok = false;
    if (file_exists(path)) {
        if (ca_model_info_is_bundle(info)) {
            /* find the anchor file's SHA */
            const char *anchor_sha = NULL;
            for (size_t i = 0; i < info->bundle_count; i++) {
                if (strcasecmp(info->bundle_files[i].name, BUNDLE_ANCHOR) == 0) {
                    anchor_sha = info->bundle_files[i].sha256; break;
                }
            }
            if (anchor_sha) ok = verify_file_checksum(path, anchor_sha);
        } else {
            ok = info->checksum != NULL && verify_file_checksum(path, info->checksum);
        }
    }
    free(path);
    return ok;
}

bool ca_local_model_loader_download_model(ca_local_model_loader_t *l,
                                          const char *model_name, char **out_path) {
    if (out_path) *out_path = NULL;
    if (!l || !model_name || !out_path) return false;
    const ca_model_info_t *info = loader_find(l, model_name);
    if (!info) return false;                       /* "not supported" */
    if (ca_model_info_is_bundle(info)) return false; /* routed elsewhere in C# */
    if (!info->file_name) return false;

    char local_path[1200];
    join_path(local_path, sizeof(local_path), l->model_dir, info->file_name);

    if (file_exists(local_path)) {
        if (checksum_is_tbd(info->checksum)) { *out_path = ca_strdup2(local_path); return *out_path != NULL; }
        if (verify_file_checksum(local_path, info->checksum)) { *out_path = ca_strdup2(local_path); return *out_path != NULL; }
        remove(local_path);
    }

    if (!l->source) return false;

    const char *urls[2] = { info->primary_url, info->fallback_url };
    for (int i = 0; i < 2; i++) {
        if (is_blank(urls[i])) continue;
        if (!ca_model_source_download(l->source, urls[i], local_path, NULL, NULL)) continue;
        if (checksum_is_tbd(info->checksum)) { *out_path = ca_strdup2(local_path); return *out_path != NULL; }
        if (verify_file_checksum(local_path, info->checksum)) { *out_path = ca_strdup2(local_path); return *out_path != NULL; }
        remove(local_path); /* failed checksum — try next source */
    }
    return false;
}
