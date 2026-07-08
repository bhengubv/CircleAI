/*
 * multimodal.c — compressed semantic media memory (C11 port).
 *
 * Ported from CircleAI.Memory.Multimodal (C#) mirroring the verified TypeScript
 * reference 1:1. In-memory only: dynamic arrays + linear search, keyed by
 * SHA-256 (case-insensitive). Includes a self-contained FIPS 180-4 SHA-256.
 * Pure C11 + libc; -lm for cosine.
 */

#include "circle_ai/multimodal.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <time.h>
#include <math.h>

/* ===========================================================================
 * SHA-256 (FIPS 180-4)
 * =========================================================================== */

typedef struct {
    uint32_t h[8];
    uint64_t total_len;   /* bytes hashed so far */
    uint8_t  buf[64];
    size_t   buf_len;
} sha256_ctx;

static uint32_t sha_rotr(uint32_t x, unsigned n) { return (x >> n) | (x << (32 - n)); }

static const uint32_t SHA_K[64] = {
    0x428a2f98u,0x71374491u,0xb5c0fbcfu,0xe9b5dba5u,0x3956c25bu,0x59f111f1u,0x923f82a4u,0xab1c5ed5u,
    0xd807aa98u,0x12835b01u,0x243185beu,0x550c7dc3u,0x72be5d74u,0x80deb1feu,0x9bdc06a7u,0xc19bf174u,
    0xe49b69c1u,0xefbe4786u,0x0fc19dc6u,0x240ca1ccu,0x2de92c6fu,0x4a7484aau,0x5cb0a9dcu,0x76f988dau,
    0x983e5152u,0xa831c66du,0xb00327c8u,0xbf597fc7u,0xc6e00bf3u,0xd5a79147u,0x06ca6351u,0x14292967u,
    0x27b70a85u,0x2e1b2138u,0x4d2c6dfcu,0x53380d13u,0x650a7354u,0x766a0abbu,0x81c2c92eu,0x92722c85u,
    0xa2bfe8a1u,0xa81a664bu,0xc24b8b70u,0xc76c51a3u,0xd192e819u,0xd6990624u,0xf40e3585u,0x106aa070u,
    0x19a4c116u,0x1e376c08u,0x2748774cu,0x34b0bcb5u,0x391c0cb3u,0x4ed8aa4au,0x5b9cca4fu,0x682e6ff3u,
    0x748f82eeu,0x78a5636fu,0x84c87814u,0x8cc70208u,0x90befffau,0xa4506cebu,0xbef9a3f7u,0xc67178f2u
};

static void sha256_init(sha256_ctx *c) {
    c->h[0]=0x6a09e667u; c->h[1]=0xbb67ae85u; c->h[2]=0x3c6ef372u; c->h[3]=0xa54ff53au;
    c->h[4]=0x510e527fu; c->h[5]=0x9b05688cu; c->h[6]=0x1f83d9abu; c->h[7]=0x5be0cd19u;
    c->total_len = 0;
    c->buf_len = 0;
}

static void sha256_block(sha256_ctx *c, const uint8_t *p) {
    uint32_t w[64];
    for (int i = 0; i < 16; ++i)
        w[i] = ((uint32_t)p[i*4] << 24) | ((uint32_t)p[i*4+1] << 16) |
               ((uint32_t)p[i*4+2] << 8) | (uint32_t)p[i*4+3];
    for (int i = 16; i < 64; ++i) {
        uint32_t s0 = sha_rotr(w[i-15],7) ^ sha_rotr(w[i-15],18) ^ (w[i-15] >> 3);
        uint32_t s1 = sha_rotr(w[i-2],17) ^ sha_rotr(w[i-2],19) ^ (w[i-2] >> 10);
        w[i] = w[i-16] + s0 + w[i-7] + s1;
    }
    uint32_t a=c->h[0],b=c->h[1],cc=c->h[2],d=c->h[3],e=c->h[4],f=c->h[5],g=c->h[6],hh=c->h[7];
    for (int i = 0; i < 64; ++i) {
        uint32_t S1 = sha_rotr(e,6) ^ sha_rotr(e,11) ^ sha_rotr(e,25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = hh + S1 + ch + SHA_K[i] + w[i];
        uint32_t S0 = sha_rotr(a,2) ^ sha_rotr(a,13) ^ sha_rotr(a,22);
        uint32_t maj = (a & b) ^ (a & cc) ^ (b & cc);
        uint32_t t2 = S0 + maj;
        hh=g; g=f; f=e; e=d+t1; d=cc; cc=b; b=a; a=t1+t2;
    }
    c->h[0]+=a; c->h[1]+=b; c->h[2]+=cc; c->h[3]+=d;
    c->h[4]+=e; c->h[5]+=f; c->h[6]+=g; c->h[7]+=hh;
}

static void sha256_update(sha256_ctx *c, const uint8_t *data, size_t len) {
    c->total_len += len;
    while (len > 0) {
        size_t take = 64 - c->buf_len;
        if (take > len) take = len;
        memcpy(c->buf + c->buf_len, data, take);
        c->buf_len += take;
        data += take;
        len -= take;
        if (c->buf_len == 64) {
            sha256_block(c, c->buf);
            c->buf_len = 0;
        }
    }
}

static void sha256_final(sha256_ctx *c, uint8_t out[32]) {
    uint64_t bit_len = c->total_len * 8;
    uint8_t pad = 0x80;
    sha256_update(c, &pad, 1);
    uint8_t zero = 0x00;
    while (c->buf_len != 56) sha256_update(c, &zero, 1);
    uint8_t lenbuf[8];
    for (int i = 0; i < 8; ++i) lenbuf[i] = (uint8_t)(bit_len >> (56 - i*8));
    sha256_update(c, lenbuf, 8);
    /* buf_len is now 0 (block flushed). */
    for (int i = 0; i < 8; ++i) {
        out[i*4]   = (uint8_t)(c->h[i] >> 24);
        out[i*4+1] = (uint8_t)(c->h[i] >> 16);
        out[i*4+2] = (uint8_t)(c->h[i] >> 8);
        out[i*4+3] = (uint8_t)(c->h[i]);
    }
}

char *ca_sha256_hex(const uint8_t *data, size_t len, char out_hex[65]) {
    sha256_ctx c;
    sha256_init(&c);
    if (data && len) sha256_update(&c, data, len);
    uint8_t digest[32];
    sha256_final(&c, digest);
    static const char *hexd = "0123456789abcdef";
    for (int i = 0; i < 32; ++i) {
        out_hex[i*2]   = hexd[digest[i] >> 4];
        out_hex[i*2+1] = hexd[digest[i] & 0xf];
    }
    out_hex[64] = '\0';
    return out_hex;
}

/* ===========================================================================
 * small shared helpers
 * =========================================================================== */

static char *mm_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static float *mm_dup_floats(const float *v, size_t n) {
    if (!v || n == 0) return NULL;
    float *p = (float *)malloc(n * sizeof(float));
    if (p) memcpy(p, v, n * sizeof(float));
    return p;
}

static int64_t mm_now_ms(void) { return (int64_t)time(NULL) * 1000; }

static bool mm_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) if (!isspace((unsigned char)*s)) return false;
    return true;
}

/* case-insensitive equality (ASCII) */
static bool mm_ci_eq(const char *a, const char *b) {
    if (!a || !b) return a == b;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

/* Full cosine dot/(||a||*||b||); 0 on mismatch or near-zero denominator. Matches
 * the C#/TS store CosineSimilarity.Score. */
static double mm_cosine(const float *a, size_t alen, const float *b, size_t blen) {
    if (alen != blen || alen == 0) return 0.0;
    double dot = 0.0, ma = 0.0, mb = 0.0;
    for (size_t i = 0; i < alen; ++i) {
        dot += (double)a[i] * b[i];
        ma  += (double)a[i] * a[i];
        mb  += (double)b[i] * b[i];
    }
    double denom = sqrt(ma) * sqrt(mb);
    /* Number.EPSILON in the TS; use DBL epsilon-ish guard. */
    if (denom < 2.220446049250313e-16) return 0.0;
    return dot / denom;
}

/* ===========================================================================
 * MultimodalMemoryEntry
 * =========================================================================== */

static void mm_copy_entry(ca_multimodal_entry_t *dst, const ca_multimodal_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id                = mm_dup(src->id);
    dst->recorded_at_ms    = src->recorded_at_ms;
    dst->modality          = src->modality;
    dst->caption           = mm_dup(src->caption);
    dst->embedding         = mm_dup_floats(src->embedding, src->embedding_len);
    dst->embedding_len     = src->embedding ? src->embedding_len : 0;
    dst->source_sha256     = mm_dup(src->source_sha256);
    dst->source_mime_type  = mm_dup(src->source_mime_type);
    dst->source_byte_count = src->source_byte_count;
    dst->source_uri        = mm_dup(src->source_uri);
    dst->has_width  = src->has_width;  dst->width_px  = src->width_px;
    dst->has_height = src->has_height; dst->height_px = src->height_px;
    dst->has_duration = src->has_duration; dst->duration_ms = src->duration_ms;
    dst->reference_count   = src->reference_count;
    if (src->tag_count > 0 && src->tag_keys && src->tag_values) {
        dst->tag_keys   = (char **)calloc(src->tag_count, sizeof(char *));
        dst->tag_values = (char **)calloc(src->tag_count, sizeof(char *));
        dst->tag_count  = src->tag_count;
        for (size_t i = 0; i < src->tag_count; ++i) {
            dst->tag_keys[i]   = mm_dup(src->tag_keys[i]);
            dst->tag_values[i] = mm_dup(src->tag_values[i]);
        }
    }
}

void ca_multimodal_entry_free(ca_multimodal_entry_t *e) {
    if (!e) return;
    free(e->id);
    free(e->caption);
    free(e->embedding);
    free(e->source_sha256);
    free(e->source_mime_type);
    free(e->source_uri);
    if (e->tag_keys)   for (size_t i = 0; i < e->tag_count; ++i) free(e->tag_keys[i]);
    if (e->tag_values) for (size_t i = 0; i < e->tag_count; ++i) free(e->tag_values[i]);
    free(e->tag_keys);
    free(e->tag_values);
    memset(e, 0, sizeof(*e));
}

void ca_multimodal_entry_free_array(ca_multimodal_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_multimodal_entry_free(&arr[i]);
    free(arr);
}

const char *ca_multimodal_entry_get_tag(const ca_multimodal_entry_t *e, const char *key) {
    if (!e || !key || !e->tag_keys) return NULL;
    for (size_t i = 0; i < e->tag_count; ++i)
        if (e->tag_keys[i] && strcmp(e->tag_keys[i], key) == 0) return e->tag_values[i];
    return NULL;
}

/* ===========================================================================
 * CaptionResult
 * =========================================================================== */

void ca_caption_result_free(ca_caption_result_t *r) {
    if (!r) return;
    free(r->caption);
    free(r->embedding);
    memset(r, 0, sizeof(*r));
}

/* ===========================================================================
 * MIME detection + heuristic captioner
 * =========================================================================== */

const char *ca_detect_mime(const uint8_t *bytes, size_t len, const char *declared) {
    if (!mm_is_blank(declared)) return declared;
    if (bytes && len >= 4) {
        if (bytes[0]==0xFF && bytes[1]==0xD8) return "image/jpeg";
        if (bytes[0]==0x89 && bytes[1]==0x50 && bytes[2]==0x4E && bytes[3]==0x47) return "image/png";
        if (bytes[0]==0x47 && bytes[1]==0x49 && bytes[2]==0x46) return "image/gif";
        if (bytes[0]==0x52 && bytes[1]==0x49 && bytes[2]==0x46 && bytes[3]==0x46) return "audio/wav";
        if (bytes[0]==0x25 && bytes[1]==0x50 && bytes[2]==0x44 && bytes[3]==0x46) return "application/pdf";
    }
    return "application/octet-stream";
}

static bool heuristic_can_caption(void *user, ca_media_modality_t modality, const char *mime) {
    (void)user; (void)modality; (void)mime;
    return true;
}

static bool heuristic_caption(void *user, ca_media_modality_t modality,
                              const uint8_t *bytes, size_t len, const char *mime,
                              ca_caption_result_t *out) {
    (void)user;
    const char *detected = ca_detect_mime(bytes, len, mime);
    const char *label;
    switch (modality) {
        case CA_MEDIA_IMAGE:         label = "Image";    break;
        case CA_MEDIA_AUDIO:         label = "Audio";    break;
        case CA_MEDIA_VIDEO:         label = "Video";    break;
        case CA_MEDIA_TEXT_DOCUMENT: label = "Document"; break;
        default:                     label = "Media";    break;
    }
    /* "[<Label> — no captioner wired. <detected>, <len> bytes.]" — the em dash
     * is UTF-8 U+2014 (0xE2 0x80 0x94), matching the C#/TS literal. */
    int needed = snprintf(NULL, 0, "[%s \xE2\x80\x94 no captioner wired. %s, %zu bytes.]",
                          label, detected, len);
    if (needed < 0) return false;
    out->caption = (char *)malloc((size_t)needed + 1);
    if (!out->caption) return false;
    snprintf(out->caption, (size_t)needed + 1,
             "[%s \xE2\x80\x94 no captioner wired. %s, %zu bytes.]", label, detected, len);
    out->embedding = NULL;
    out->embedding_len = 0;
    out->has_width = out->has_height = out->has_duration = false;
    out->width_px = out->height_px = 0;
    out->duration_ms = 0;
    return true;
}

ca_captioner_t ca_heuristic_captioner(void) {
    ca_captioner_t c;
    c.user = NULL;
    c.can_caption = heuristic_can_caption;
    c.caption = heuristic_caption;
    return c;
}

/* ===========================================================================
 * InMemoryMultimodalMemoryStore
 * =========================================================================== */

struct ca_multimodal_store {
    ca_multimodal_entry_t *items;
    size_t                 count;
    size_t                 cap;
};

ca_multimodal_store_t *ca_multimodal_store_create(void) {
    return (ca_multimodal_store_t *)calloc(1, sizeof(ca_multimodal_store_t));
}

void ca_multimodal_store_destroy(ca_multimodal_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_multimodal_entry_free(&store->items[i]);
    free(store->items);
    free(store);
}

static bool mm_store_reserve(ca_multimodal_store_t *s, size_t need) {
    if (need <= s->cap) return true;
    size_t ncap = s->cap ? s->cap * 2 : 8;
    while (ncap < need) ncap *= 2;
    ca_multimodal_entry_t *n = (ca_multimodal_entry_t *)realloc(s->items, ncap * sizeof(*n));
    if (!n) return false;
    s->items = n;
    s->cap = ncap;
    return true;
}

/* index of an entry by hash (case-insensitive), or -1. */
static long mm_index_of(const ca_multimodal_store_t *s, const char *hash) {
    for (size_t i = 0; i < s->count; ++i)
        if (mm_ci_eq(s->items[i].source_sha256, hash)) return (long)i;
    return -1;
}

bool ca_multimodal_store_add(ca_multimodal_store_t *store, const ca_multimodal_entry_t *entry) {
    if (!store || !entry) return false;
    if (mm_is_blank(entry->source_sha256)) return false;
    long existing = mm_index_of(store, entry->source_sha256);
    if (existing >= 0) {
        /* upsert — replace the record for this hash */
        ca_multimodal_entry_free(&store->items[existing]);
        mm_copy_entry(&store->items[existing], entry);
        return true;
    }
    if (!mm_store_reserve(store, store->count + 1)) return false;
    mm_copy_entry(&store->items[store->count], entry);
    store->count++;
    return true;
}

bool ca_multimodal_store_get_by_hash(const ca_multimodal_store_t *store,
                                     const char *source_sha256,
                                     ca_multimodal_entry_t *out) {
    if (!store || !source_sha256 || !out) return false;
    long i = mm_index_of(store, source_sha256);
    if (i < 0) return false;
    mm_copy_entry(out, &store->items[i]);
    return true;
}

void ca_multimodal_store_reinforce(ca_multimodal_store_t *store, const char *source_sha256) {
    if (!store || !source_sha256) return;
    long i = mm_index_of(store, source_sha256);
    if (i >= 0) store->items[i].reference_count++;
}

/* scored-entry sort helper (stable insertion sort, score desc). */
typedef struct { size_t idx; double score; } mm_scored_t;

static void mm_sort_scored_desc(mm_scored_t *a, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        mm_scored_t key = a[i];
        size_t j = i;
        while (j > 0 && a[j-1].score < key.score) { a[j] = a[j-1]; --j; }
        a[j] = key;
    }
}

/* recency sort helper (stable, recorded_at desc). */
static void mm_sort_recency_desc(size_t *order, const ca_multimodal_store_t *s, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = order[i];
        int64_t kv = s->items[key].recorded_at_ms;
        size_t j = i;
        while (j > 0 && s->items[order[j-1]].recorded_at_ms < kv) { order[j] = order[j-1]; --j; }
        order[j] = key;
    }
}

static ca_multimodal_entry_t *mm_collect(const ca_multimodal_store_t *s,
                                         const size_t *idx, size_t take, size_t *out_count) {
    if (take == 0) { if (out_count) *out_count = 0; return NULL; }
    ca_multimodal_entry_t *out = (ca_multimodal_entry_t *)malloc(take * sizeof(*out));
    if (!out) { if (out_count) *out_count = 0; return NULL; }
    for (size_t i = 0; i < take; ++i) mm_copy_entry(&out[i], &s->items[idx[i]]);
    if (out_count) *out_count = take;
    return out;
}

ca_multimodal_entry_t *ca_multimodal_store_search(const ca_multimodal_store_t *store,
                                                  const float *query, size_t query_len,
                                                  int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || store->count == 0) return NULL;
    if (top_k <= 0) top_k = 5;
    size_t n = store->count;

    if (!query || query_len == 0) {
        size_t *order = (size_t *)malloc(n * sizeof(size_t));
        if (!order) return NULL;
        for (size_t i = 0; i < n; ++i) order[i] = i;
        mm_sort_recency_desc(order, store, n);
        size_t take = (size_t)top_k < n ? (size_t)top_k : n;
        ca_multimodal_entry_t *out = mm_collect(store, order, take, out_count);
        free(order);
        return out;
    }

    /* cosine ranking over entries with a matching-dim embedding */
    mm_scored_t *scored = (mm_scored_t *)malloc(n * sizeof(*scored));
    if (!scored) return NULL;
    size_t m = 0;
    for (size_t i = 0; i < n; ++i) {
        const ca_multimodal_entry_t *e = &store->items[i];
        if (e->embedding && e->embedding_len > 0) {
            scored[m].idx = i;
            scored[m].score = mm_cosine(query, query_len, e->embedding, e->embedding_len);
            m++;
        }
    }
    mm_sort_scored_desc(scored, m);
    size_t take = (size_t)top_k < m ? (size_t)top_k : m;
    size_t *idx = (size_t *)malloc((take ? take : 1) * sizeof(size_t));
    if (!idx) { free(scored); return NULL; }
    for (size_t i = 0; i < take; ++i) idx[i] = scored[i].idx;
    ca_multimodal_entry_t *out = mm_collect(store, idx, take, out_count);
    free(idx);
    free(scored);
    return out;
}

ca_multimodal_entry_t *ca_multimodal_store_get_recent(const ca_multimodal_store_t *store,
                                                      int count, size_t *out_count) {
    if (count <= 0) count = 10;
    return ca_multimodal_store_search(store, NULL, 0, count, out_count);
}

size_t ca_multimodal_store_prune_older_than(ca_multimodal_store_t *store, int64_t cutoff_ms) {
    if (!store) return 0;
    size_t removed = 0;
    size_t w = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->items[i].recorded_at_ms < cutoff_ms) {
            ca_multimodal_entry_free(&store->items[i]);
            removed++;
        } else {
            if (w != i) store->items[w] = store->items[i];
            w++;
        }
    }
    store->count = w;
    return removed;
}

size_t ca_multimodal_store_count(const ca_multimodal_store_t *store) {
    return store ? store->count : 0;
}

/* ===========================================================================
 * MultimodalMemoryIngester
 * =========================================================================== */

struct ca_multimodal_ingester {
    ca_captioner_t        *captioners;
    size_t                 captioner_count;
    ca_multimodal_store_t *store;  /* borrowed */
};

void ca_ingestion_result_free(ca_ingestion_result_t *r) {
    if (!r) return;
    ca_multimodal_entry_free(&r->entry);
    r->was_deduplicated = false;
}

ca_multimodal_ingester_t *ca_multimodal_ingester_create(
    const ca_captioner_t *captioners, size_t captioner_count,
    ca_multimodal_store_t *store) {
    if (!captioners || captioner_count == 0 || !store) return NULL;
    ca_multimodal_ingester_t *ing = (ca_multimodal_ingester_t *)calloc(1, sizeof(*ing));
    if (!ing) return NULL;
    ing->captioners = (ca_captioner_t *)malloc(captioner_count * sizeof(ca_captioner_t));
    if (!ing->captioners) { free(ing); return NULL; }
    memcpy(ing->captioners, captioners, captioner_count * sizeof(ca_captioner_t));
    ing->captioner_count = captioner_count;
    ing->store = store;
    return ing;
}

void ca_multimodal_ingester_destroy(ca_multimodal_ingester_t *ing) {
    if (!ing) return;
    free(ing->captioners);
    free(ing);
}

static const ca_captioner_t *mm_pick_captioner(const ca_multimodal_ingester_t *ing,
                                               ca_media_modality_t modality, const char *mime) {
    for (size_t i = 0; i < ing->captioner_count; ++i) {
        const ca_captioner_t *c = &ing->captioners[i];
        if (c->can_caption && c->can_caption(c->user, modality, mime)) return c;
    }
    /* last registered captioner accepts everything (heuristic fallback). */
    return &ing->captioners[ing->captioner_count - 1];
}

bool ca_multimodal_ingester_ingest(ca_multimodal_ingester_t *ing,
                                   ca_media_modality_t modality,
                                   const uint8_t *bytes, size_t len,
                                   const ca_ingest_options_t *opts,
                                   ca_ingestion_result_t *out) {
    if (!ing || !out) return false;
    memset(out, 0, sizeof(*out));
    if (!bytes || len == 0) return false;

    const char *mime = opts ? opts->mime_type : NULL;
    const char *uri  = opts ? opts->source_uri : NULL;

    char hash[65];
    ca_sha256_hex(bytes, len, hash);

    /* dedup */
    ca_multimodal_entry_t existing;
    if (ca_multimodal_store_get_by_hash(ing->store, hash, &existing)) {
        ca_multimodal_store_reinforce(ing->store, hash);
        /* return the reinforced (post-increment) record */
        ca_multimodal_entry_free(&existing);
        if (!ca_multimodal_store_get_by_hash(ing->store, hash, &out->entry)) return false;
        out->was_deduplicated = true;
        return true;
    }

    const ca_captioner_t *cap = mm_pick_captioner(ing, modality, mime);
    ca_caption_result_t cr;
    memset(&cr, 0, sizeof(cr));
    if (!cap->caption || !cap->caption(cap->user, modality, bytes, len, mime, &cr)) {
        ca_caption_result_free(&cr);
        return false;
    }

    /* build the entry */
    ca_multimodal_entry_t e;
    memset(&e, 0, sizeof(e));
    char idbuf[65];
    /* Deterministic-enough id: reuse the content hash (host may override). The
     * C#/TS default a fresh UUID; the id is not asserted for parity. */
    ca_sha256_hex(bytes, len, idbuf);
    e.id                = mm_dup(idbuf);
    e.recorded_at_ms    = mm_now_ms();
    e.modality          = modality;
    e.caption           = cr.caption ? mm_dup(cr.caption) : mm_dup("");
    e.embedding         = mm_dup_floats(cr.embedding, cr.embedding_len);
    e.embedding_len     = cr.embedding ? cr.embedding_len : 0;
    e.source_sha256     = mm_dup(hash);
    e.source_mime_type  = mm_dup(mime);
    e.source_byte_count = (int64_t)len;
    e.source_uri        = mm_dup(uri);
    e.has_width  = cr.has_width;  e.width_px  = cr.width_px;
    e.has_height = cr.has_height; e.height_px = cr.height_px;
    e.has_duration = cr.has_duration; e.duration_ms = cr.duration_ms;
    e.reference_count   = 1;
    if (opts && opts->tag_count > 0 && opts->tag_keys && opts->tag_values) {
        e.tag_keys   = (char **)calloc(opts->tag_count, sizeof(char *));
        e.tag_values = (char **)calloc(opts->tag_count, sizeof(char *));
        e.tag_count  = opts->tag_count;
        for (size_t i = 0; i < opts->tag_count; ++i) {
            e.tag_keys[i]   = mm_dup(opts->tag_keys[i]);
            e.tag_values[i] = mm_dup(opts->tag_values[i]);
        }
    }
    ca_caption_result_free(&cr);

    if (!ca_multimodal_store_add(ing->store, &e)) {
        ca_multimodal_entry_free(&e);
        return false;
    }
    /* hand the caller a deep copy (the store owns its own copy) */
    mm_copy_entry(&out->entry, &e);
    out->was_deduplicated = false;
    ca_multimodal_entry_free(&e);
    return true;
}
