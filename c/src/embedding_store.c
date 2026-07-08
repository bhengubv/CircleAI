/*
 * embedding_store.c — on-device embedding store + brute-force index (C11 port).
 *
 * See embedding_store.h. Ports CircleAI.Embeddings.Local.InMemoryEmbeddingStore
 * (TurboQuant-compressed, brute-force cosine, .NET-BinaryWriter wire format) and
 * a brute-force IEmbeddingIndex backend. Reuses compression.h ca_turboquant_*.
 * In-memory only; pure C11 + libc + -lm.
 */

#include "circle_ai/embedding_store.h"
#include "circle_ai/compression.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#if defined(_WIN32)
  #include <direct.h>
  #define ES_MKDIR(p) _mkdir(p)
#else
  #include <sys/types.h>
  #include <sys/stat.h>
  #define ES_MKDIR(p) mkdir((p), 0777)
#endif

static char *es_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ─────────────────────── document helpers ─────────────────────── */

static bool meta_copy(ca_embedding_document_t *doc,
                      const ca_embedding_meta_t *meta, size_t count) {
    doc->metadata = NULL;
    doc->metadata_count = 0;
    if (!meta || count == 0) return true;
    doc->metadata = (ca_embedding_meta_t *)calloc(count, sizeof(ca_embedding_meta_t));
    if (!doc->metadata) return false;
    for (size_t i = 0; i < count; i++) {
        doc->metadata[i].key = es_strdup(meta[i].key);
        doc->metadata[i].value = es_strdup(meta[i].value);
        if ((meta[i].key && !doc->metadata[i].key) ||
            (meta[i].value && !doc->metadata[i].value)) {
            for (size_t j = 0; j <= i; j++) { free(doc->metadata[j].key); free(doc->metadata[j].value); }
            free(doc->metadata); doc->metadata = NULL;
            return false;
        }
    }
    doc->metadata_count = count;
    return true;
}

static void doc_free_fields(ca_embedding_document_t *doc) {
    free(doc->id);
    free(doc->text);
    for (size_t i = 0; i < doc->metadata_count; i++) {
        free(doc->metadata[i].key);
        free(doc->metadata[i].value);
    }
    free(doc->metadata);
    memset(doc, 0, sizeof(*doc));
}

static bool doc_deep_copy(ca_embedding_document_t *dst, const ca_embedding_document_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = es_strdup(src->id);
    dst->text = es_strdup(src->text);
    if ((src->id && !dst->id) || (src->text && !dst->text)) { doc_free_fields(dst); return false; }
    if (!meta_copy(dst, src->metadata, src->metadata_count)) { doc_free_fields(dst); return false; }
    return true;
}

void ca_embedding_search_hits_free(ca_embedding_search_hit_t *hits, size_t count) {
    if (!hits) return;
    for (size_t i = 0; i < count; i++) doc_free_fields(&hits[i].document);
    free(hits);
}

/* ═══════════════════════ InMemoryEmbeddingStore ═══════════════════════ */

typedef struct {
    ca_embedding_document_t  document;
    ca_turboquant_payload_t  payload;   /* norm + packed indices */
} store_entry_t;

struct ca_embedding_store {
    const ca_embedding_encoder_t *encoder; /* borrowed */
    int             bits_per_dim;
    store_entry_t  *entries;
    size_t          count;
    size_t          cap;
};

ca_embedding_store_t *ca_embedding_store_create(const ca_embedding_encoder_t *encoder,
                                                int bits_per_dim) {
    if (!encoder) return NULL;
    if (bits_per_dim < 1 || bits_per_dim > 8) return NULL;
    ca_embedding_store_t *s = (ca_embedding_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->encoder = encoder;
    s->bits_per_dim = bits_per_dim;
    return s;
}

static void store_entry_free(store_entry_t *e) {
    doc_free_fields(&e->document);
    ca_turboquant_payload_free(&e->payload);
}

void ca_embedding_store_destroy(ca_embedding_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; i++) store_entry_free(&s->entries[i]);
    free(s->entries);
    free(s);
}

int ca_embedding_store_dimension(const ca_embedding_store_t *s) {
    return s ? s->encoder->dimension : 0;
}

size_t ca_embedding_store_count(const ca_embedding_store_t *s) {
    return s ? s->count : 0;
}

static store_entry_t *store_find(ca_embedding_store_t *s, const char *id) {
    for (size_t i = 0; i < s->count; i++)
        if (strcmp(s->entries[i].document.id, id) == 0) return &s->entries[i];
    return NULL;
}

static bool store_put(ca_embedding_store_t *s, const char *id, const char *text,
                      const ca_embedding_meta_t *meta, size_t meta_count,
                      const ca_turboquant_payload_t *payload_moved) {
    store_entry_t *ex = store_find(s, id);
    if (ex) {
        /* replace: rebuild document + payload */
        store_entry_free(ex);
        memset(ex, 0, sizeof(*ex));
        ex->document.id = es_strdup(id);
        ex->document.text = es_strdup(text);
        if (!ex->document.id || !ex->document.text) return false;
        if (!meta_copy(&ex->document, meta, meta_count)) return false;
        ex->payload = *payload_moved;
        return true;
    }
    if (s->count >= s->cap) {
        size_t nc = s->cap == 0 ? 8 : s->cap * 2;
        store_entry_t *g = (store_entry_t *)realloc(s->entries, nc * sizeof(store_entry_t));
        if (!g) return false;
        s->entries = g; s->cap = nc;
    }
    store_entry_t *e = &s->entries[s->count];
    memset(e, 0, sizeof(*e));
    e->document.id = es_strdup(id);
    e->document.text = es_strdup(text);
    if (!e->document.id || !e->document.text) { doc_free_fields(&e->document); return false; }
    if (!meta_copy(&e->document, meta, meta_count)) { doc_free_fields(&e->document); return false; }
    e->payload = *payload_moved;
    s->count++;
    return true;
}

bool ca_embedding_store_add_vector(ca_embedding_store_t *s,
                                   const char *id, const char *text,
                                   const ca_embedding_meta_t *meta, size_t meta_count,
                                   const float *vector, size_t vector_len) {
    if (!s || !id || !text || !vector) return false;
    if ((int)vector_len != s->encoder->dimension) return false;
    ca_turboquant_payload_t payload;
    if (!ca_turboquant_encode(vector, vector_len, s->bits_per_dim, &payload)) return false;
    if (!store_put(s, id, text, meta, meta_count, &payload)) {
        ca_turboquant_payload_free(&payload);
        return false;
    }
    return true;
}

bool ca_embedding_store_add(ca_embedding_store_t *s,
                            const char *id, const char *text,
                            const ca_embedding_meta_t *meta, size_t meta_count) {
    if (!s || !id || !text) return false;
    size_t vlen = 0;
    float *vec = s->encoder->encode(s->encoder->user, text, &vlen);
    if (!vec) return false;
    bool ok = ca_embedding_store_add_vector(s, id, text, meta, meta_count, vec, vlen);
    free(vec);
    return ok;
}

bool ca_embedding_store_remove(ca_embedding_store_t *s, const char *id) {
    if (!s || !id) return false;
    for (size_t i = 0; i < s->count; i++) {
        if (strcmp(s->entries[i].document.id, id) == 0) {
            store_entry_free(&s->entries[i]);
            /* shift down to preserve order */
            memmove(&s->entries[i], &s->entries[i+1],
                    (s->count - i - 1) * sizeof(store_entry_t));
            s->count--;
            return true;
        }
    }
    return false;
}

static float norm_safe(const float *v, int n) {
    double sum = 0;
    for (int i = 0; i < n; i++) sum += (double)v[i] * v[i];
    return (float)sqrt(sum);
}

/* scored candidate used for top-k selection */
typedef struct { float score; size_t idx; } cand_t;

/* descending by score; ties by ascending ordinal id (matches ScoreComparer
 * ordering as observed in the final OrderByDescending). */
static int cand_cmp_desc(const void *a, const void *b, void *ctx) {
    const cand_t *ca = (const cand_t *)a;
    const cand_t *cb = (const cand_t *)b;
    if (ca->score < cb->score) return 1;
    if (ca->score > cb->score) return -1;
    ca_embedding_store_t *s = (ca_embedding_store_t *)ctx;
    return strcmp(s->entries[ca->idx].document.id, s->entries[cb->idx].document.id);
}

/* portable qsort_r shim (signatures differ across platforms); do a simple
 * insertion sort — candidate counts are small (== store size, bounded use). */
static void cand_sort(cand_t *arr, size_t n, ca_embedding_store_t *s) {
    for (size_t i = 1; i < n; i++) {
        cand_t key = arr[i];
        size_t j = i;
        while (j > 0 && cand_cmp_desc(&arr[j-1], &key, s) > 0) {
            arr[j] = arr[j-1];
            j--;
        }
        arr[j] = key;
    }
}

ca_embedding_search_hit_t *ca_embedding_store_search_vector(ca_embedding_store_t *s,
                                                            const float *query_vector,
                                                            size_t query_len, int top_k,
                                                            size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || !query_vector || !out_count) return NULL;
    int dim = s->encoder->dimension;
    if ((int)query_len != dim) return NULL;
    if (top_k <= 0) return NULL;
    if (s->count == 0) return NULL;

    /* normalise query */
    float *q = (float *)malloc((size_t)dim * sizeof(float));
    if (!q) return NULL;
    memcpy(q, query_vector, (size_t)dim * sizeof(float));
    float qn = norm_safe(q, dim);
    if (qn > 0) for (int i = 0; i < dim; i++) q[i] /= qn;

    cand_t *cands = (cand_t *)malloc(s->count * sizeof(cand_t));
    if (!cands) { free(q); return NULL; }
    size_t nc = 0;
    for (size_t e = 0; e < s->count; e++) {
        float *decoded = ca_turboquant_decode(&s->entries[e].payload, dim, s->bits_per_dim);
        if (!decoded) continue;
        float en = norm_safe(decoded, dim);
        if (en <= 0) { free(decoded); continue; }
        float dot = 0;
        for (int i = 0; i < dim; i++) dot += q[i] * (decoded[i] / en);
        free(decoded);
        cands[nc].score = dot;
        cands[nc].idx = e;
        nc++;
    }
    free(q);

    if (nc == 0) { free(cands); return NULL; }
    cand_sort(cands, nc, s);

    size_t k = (size_t)top_k < nc ? (size_t)top_k : nc;
    ca_embedding_search_hit_t *hits =
        (ca_embedding_search_hit_t *)calloc(k, sizeof(ca_embedding_search_hit_t));
    if (!hits) { free(cands); return NULL; }
    for (size_t i = 0; i < k; i++) {
        if (!doc_deep_copy(&hits[i].document, &s->entries[cands[i].idx].document)) {
            for (size_t j = 0; j < i; j++) doc_free_fields(&hits[j].document);
            free(hits); free(cands); return NULL;
        }
        hits[i].score = cands[i].score;
    }
    free(cands);
    *out_count = k;
    return hits;
}

ca_embedding_search_hit_t *ca_embedding_store_search(ca_embedding_store_t *s,
                                                     const char *query_text, int top_k,
                                                     size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || !query_text || !out_count) return NULL;
    size_t vlen = 0;
    float *vec = s->encoder->encode(s->encoder->user, query_text, &vlen);
    if (!vec) return NULL;
    ca_embedding_search_hit_t *hits =
        ca_embedding_store_search_vector(s, vec, vlen, top_k, out_count);
    free(vec);
    return hits;
}

/* ─────────────────────── .NET BinaryWriter/Reader wire format ─────────────────────── */

static void w_u32(FILE *f, uint32_t v) {
    uint8_t b[4] = { (uint8_t)v, (uint8_t)(v>>8), (uint8_t)(v>>16), (uint8_t)(v>>24) };
    fwrite(b, 1, 4, f);
}
static void w_u16(FILE *f, uint16_t v) {
    uint8_t b[2] = { (uint8_t)v, (uint8_t)(v>>8) };
    fwrite(b, 1, 2, f);
}
static void w_f32(FILE *f, float v) {
    uint32_t bits; memcpy(&bits, &v, 4); w_u32(f, bits);
}
/* .NET BinaryWriter.Write(string): ULEB128 UTF-8 byte count, then UTF-8 bytes. */
static void w_str(FILE *f, const char *s) {
    size_t len = s ? strlen(s) : 0;
    size_t v = len;
    do {
        uint8_t byte = (uint8_t)(v & 0x7F);
        v >>= 7;
        if (v != 0) byte |= 0x80;
        fputc(byte, f);
    } while (v != 0);
    if (len) fwrite(s, 1, len, f);
}

static bool r_u32(FILE *f, uint32_t *out) {
    uint8_t b[4];
    if (fread(b, 1, 4, f) != 4) return false;
    *out = (uint32_t)b[0] | ((uint32_t)b[1]<<8) | ((uint32_t)b[2]<<16) | ((uint32_t)b[3]<<24);
    return true;
}
static bool r_u16(FILE *f, uint16_t *out) {
    uint8_t b[2];
    if (fread(b, 1, 2, f) != 2) return false;
    *out = (uint16_t)((uint16_t)b[0] | ((uint16_t)b[1]<<8));
    return true;
}
static bool r_f32(FILE *f, float *out) {
    uint32_t bits;
    if (!r_u32(f, &bits)) return false;
    memcpy(out, &bits, 4);
    return true;
}
/* Read a .NET 7-bit-encoded-int (ULEB128) length, then the string bytes. */
static bool r_str(FILE *f, char **out) {
    size_t len = 0;
    int shift = 0;
    for (;;) {
        int ch = fgetc(f);
        if (ch == EOF) return false;
        len |= (size_t)(ch & 0x7F) << shift;
        if ((ch & 0x80) == 0) break;
        shift += 7;
        if (shift > 63) return false;
    }
    char *buf = (char *)malloc(len + 1);
    if (!buf) return false;
    if (len && fread(buf, 1, len, f) != len) { free(buf); return false; }
    buf[len] = 0;
    *out = buf;
    return true;
}

#define STORE_FILE_MAGIC   0x4C455143u
#define STORE_FILE_VERSION 1

bool ca_embedding_store_save(ca_embedding_store_t *s, const char *path) {
    if (!s || !path) return false;
    /* ensure parent dir */
    {
        char dir[1024]; snprintf(dir, sizeof(dir), "%s", path);
        char *last = NULL;
        for (char *q = dir; *q; q++) if (*q == '/' || *q == '\\') last = q;
        if (last) { *last = 0; if (*dir) ES_MKDIR(dir); }
    }
    char tmp[1100];
    snprintf(tmp, sizeof(tmp), "%s.tmp", path);
    FILE *f = fopen(tmp, "wb");
    if (!f) return false;

    w_u32(f, STORE_FILE_MAGIC);
    w_u16(f, STORE_FILE_VERSION);
    w_u16(f, (uint16_t)s->bits_per_dim);
    w_u32(f, (uint32_t)s->encoder->dimension);
    w_u32(f, (uint32_t)s->count);
    for (size_t i = 0; i < s->count; i++) {
        store_entry_t *e = &s->entries[i];
        w_str(f, e->document.id);
        w_str(f, e->document.text);
        w_u32(f, (uint32_t)e->document.metadata_count);
        for (size_t m = 0; m < e->document.metadata_count; m++) {
            w_str(f, e->document.metadata[m].key);
            w_str(f, e->document.metadata[m].value);
        }
        w_f32(f, e->payload.norm);
        w_u32(f, (uint32_t)e->payload.packed_len);
        if (e->payload.packed_len) fwrite(e->payload.packed_indices, 1, e->payload.packed_len, f);
    }
    if (fclose(f) != 0) { remove(tmp); return false; }

    remove(path);
    if (rename(tmp, path) != 0) { remove(tmp); return false; }
    return true;
}

bool ca_embedding_store_load(ca_embedding_store_t *s, const char *path) {
    if (!s || !path) return false;
    FILE *f = fopen(path, "rb");
    if (!f) return false;

    uint32_t magic; uint16_t version, file_bits; uint32_t file_dim, count;
    if (!r_u32(f, &magic) || magic != STORE_FILE_MAGIC) { fclose(f); return false; }
    if (!r_u16(f, &version) || version != STORE_FILE_VERSION) { fclose(f); return false; }
    if (!r_u16(f, &file_bits) || (int)file_bits != s->bits_per_dim) { fclose(f); return false; }
    if (!r_u32(f, &file_dim) || (int)file_dim != s->encoder->dimension) { fclose(f); return false; }
    if (!r_u32(f, &count)) { fclose(f); return false; }

    /* clear */
    for (size_t i = 0; i < s->count; i++) store_entry_free(&s->entries[i]);
    s->count = 0;

    bool ok = true;
    for (uint32_t i = 0; i < count && ok; i++) {
        char *id = NULL, *text = NULL;
        uint32_t meta_count = 0;
        ca_embedding_meta_t *meta = NULL;
        float norm = 0; uint32_t packed_len = 0; uint8_t *packed = NULL;

        if (!r_str(f, &id) || !r_str(f, &text) || !r_u32(f, &meta_count)) { ok = false; }
        if (ok && meta_count > 0) {
            meta = (ca_embedding_meta_t *)calloc(meta_count, sizeof(ca_embedding_meta_t));
            if (!meta) ok = false;
            for (uint32_t m = 0; ok && m < meta_count; m++) {
                if (!r_str(f, &meta[m].key) || !r_str(f, &meta[m].value)) ok = false;
            }
        }
        if (ok && (!r_f32(f, &norm) || !r_u32(f, &packed_len))) ok = false;
        if (ok && packed_len > 0) {
            packed = (uint8_t *)malloc(packed_len);
            if (!packed || fread(packed, 1, packed_len, f) != packed_len) ok = false;
        }

        if (ok) {
            ca_turboquant_payload_t payload;
            payload.norm = norm;
            payload.packed_indices = packed; /* transfer ownership */
            payload.packed_len = packed_len;
            packed = NULL;
            if (!store_put(s, id, text, meta, meta_count, &payload)) {
                ca_turboquant_payload_free(&payload);
                ok = false;
            }
        }

        free(id); free(text); free(packed);
        for (uint32_t m = 0; m < meta_count; m++) { if (meta) { free(meta[m].key); free(meta[m].value); } }
        free(meta);
    }
    fclose(f);
    return ok;
}

/* ═══════════════════════ IEmbeddingIndex (brute-force) ═══════════════════════ */

struct ca_embedding_index {
    int      dimension;
    float   *vectors;   /* count * dimension, row-major */
    int64_t *ids;       /* internal ids, insertion order */
    size_t   count;
    size_t   cap;
    int64_t  next_id;
};

ca_embedding_index_t *ca_embedding_index_create(int dimension) {
    if (dimension <= 0) return NULL;
    ca_embedding_index_t *idx = (ca_embedding_index_t *)calloc(1, sizeof(*idx));
    if (!idx) return NULL;
    idx->dimension = dimension;
    return idx;
}

void ca_embedding_index_destroy(ca_embedding_index_t *idx) {
    if (!idx) return;
    free(idx->vectors);
    free(idx->ids);
    free(idx);
}

int ca_embedding_index_dimension(const ca_embedding_index_t *idx) {
    return idx ? idx->dimension : 0;
}

int64_t ca_embedding_index_count(const ca_embedding_index_t *idx) {
    return idx ? (int64_t)idx->count : 0;
}

int64_t ca_embedding_index_add(ca_embedding_index_t *idx,
                               const float *vector, size_t vector_len) {
    if (!idx || !vector) return -1;
    if ((int)vector_len != idx->dimension) return -1;
    if (idx->count >= idx->cap) {
        size_t nc = idx->cap == 0 ? 8 : idx->cap * 2;
        float *gv = (float *)realloc(idx->vectors, nc * (size_t)idx->dimension * sizeof(float));
        if (!gv) return -1;
        idx->vectors = gv; /* assign the grown block before the next realloc */
        int64_t *gi = (int64_t *)realloc(idx->ids, nc * sizeof(int64_t));
        if (!gi) return -1; /* vectors already grown; capacity update deferred keeps state valid */
        idx->ids = gi; idx->cap = nc;
    }
    memcpy(idx->vectors + idx->count * (size_t)idx->dimension, vector,
           (size_t)idx->dimension * sizeof(float));
    int64_t id = idx->next_id++;
    idx->ids[idx->count] = id;
    idx->count++;
    return id;
}

typedef struct { float score; int64_t id; } idx_cand_t;

static void idx_cand_sort(idx_cand_t *arr, size_t n) {
    /* descending by score, ties by ascending id */
    for (size_t i = 1; i < n; i++) {
        idx_cand_t key = arr[i];
        size_t j = i;
        while (j > 0 && (arr[j-1].score < key.score ||
                        (arr[j-1].score == key.score && arr[j-1].id > key.id))) {
            arr[j] = arr[j-1]; j--;
        }
        arr[j] = key;
    }
}

ca_embedding_index_hit_t *ca_embedding_index_search(ca_embedding_index_t *idx,
                                                    const float *query_vector,
                                                    size_t query_len, int top_k,
                                                    size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!idx || !query_vector || !out_count) return NULL;
    if ((int)query_len != idx->dimension) return NULL;
    if (top_k <= 0 || idx->count == 0) return NULL;

    int dim = idx->dimension;
    float qn = norm_safe(query_vector, dim);

    idx_cand_t *cands = (idx_cand_t *)malloc(idx->count * sizeof(idx_cand_t));
    if (!cands) return NULL;
    size_t nc = 0;
    for (size_t e = 0; e < idx->count; e++) {
        const float *v = idx->vectors + e * (size_t)dim;
        float en = norm_safe(v, dim);
        float score;
        if (qn <= 0 || en <= 0) {
            score = 0.0f;
        } else {
            float dot = 0;
            for (int i = 0; i < dim; i++) dot += query_vector[i] * v[i];
            score = dot / (qn * en);
        }
        cands[nc].score = score;
        cands[nc].id = idx->ids[e];
        nc++;
    }
    idx_cand_sort(cands, nc);

    size_t k = (size_t)top_k < nc ? (size_t)top_k : nc;
    ca_embedding_index_hit_t *hits =
        (ca_embedding_index_hit_t *)malloc(k * sizeof(ca_embedding_index_hit_t));
    if (!hits) { free(cands); return NULL; }
    for (size_t i = 0; i < k; i++) {
        hits[i].internal_id = cands[i].id;
        hits[i].score = cands[i].score;
    }
    free(cands);
    *out_count = k;
    return hits;
}

#define INDEX_FILE_MAGIC 0x58444943u /* 'CIDX' little-endian */

bool ca_embedding_index_save(ca_embedding_index_t *idx, const char *path) {
    if (!idx || !path) return false;
    FILE *f = fopen(path, "wb");
    if (!f) return false;
    w_u32(f, INDEX_FILE_MAGIC);
    w_u32(f, (uint32_t)idx->dimension);
    w_u32(f, (uint32_t)idx->count);
    /* next_id as two u32 (low, high) */
    w_u32(f, (uint32_t)(idx->next_id & 0xFFFFFFFFu));
    w_u32(f, (uint32_t)((uint64_t)idx->next_id >> 32));
    for (size_t e = 0; e < idx->count; e++) {
        w_u32(f, (uint32_t)(idx->ids[e] & 0xFFFFFFFFu));
        w_u32(f, (uint32_t)((uint64_t)idx->ids[e] >> 32));
        for (int i = 0; i < idx->dimension; i++)
            w_f32(f, idx->vectors[e * (size_t)idx->dimension + i]);
    }
    if (fclose(f) != 0) { remove(path); return false; }
    return true;
}

bool ca_embedding_index_load(ca_embedding_index_t *idx, const char *path) {
    if (!idx || !path) return false;
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    uint32_t magic, dim, count, lo, hi;
    if (!r_u32(f, &magic) || magic != INDEX_FILE_MAGIC) { fclose(f); return false; }
    if (!r_u32(f, &dim) || (int)dim != idx->dimension) { fclose(f); return false; }
    if (!r_u32(f, &count)) { fclose(f); return false; }
    if (!r_u32(f, &lo) || !r_u32(f, &hi)) { fclose(f); return false; }

    idx->count = 0;
    bool ok = true;
    for (uint32_t e = 0; e < count && ok; e++) {
        uint32_t ilo, ihi;
        if (!r_u32(f, &ilo) || !r_u32(f, &ihi)) { ok = false; break; }
        float *tmp = (float *)malloc((size_t)dim * sizeof(float));
        if (!tmp) { ok = false; break; }
        for (uint32_t i = 0; i < dim && ok; i++) if (!r_f32(f, &tmp[i])) ok = false;
        if (ok) {
            if (ca_embedding_index_add(idx, tmp, dim) < 0) ok = false;
            else idx->ids[idx->count - 1] = (int64_t)((uint64_t)ilo | ((uint64_t)ihi << 32));
        }
        free(tmp);
    }
    idx->next_id = (int64_t)((uint64_t)lo | ((uint64_t)hi << 32));
    fclose(f);
    return ok;
}
