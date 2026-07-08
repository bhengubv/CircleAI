#ifndef CIRCLE_AI_EMBEDDING_STORE_H
#define CIRCLE_AI_EMBEDDING_STORE_H

/*
 * embedding_store.h — on-device embedding store + index (C11 port).
 *
 * Ports:
 *   - CircleAI.Embeddings.ITextEmbedder                     (seam)
 *   - CircleAI.Embeddings.Local.IEmbeddingEncoder           (seam)
 *   - CircleAI.Embeddings.Local.EmbeddingDocument / EmbeddingSearchHit
 *   - CircleAI.Embeddings.Local.ICircleEmbeddingStore + InMemoryEmbeddingStore
 *   - CircleAI.Embeddings.Local.IEmbeddingIndex + EmbeddingIndexHit + a
 *     deterministic brute-force in-memory index
 *
 * Vectors are TurboQuant-compressed (reusing compression.h ca_turboquant_*) so
 * the store footprint is ~8× smaller than raw FP32. Brute-force cosine search
 * decodes on demand.
 *
 * PERSISTENCE WIRE FORMAT (ca_embedding_store_save/load) — byte-identical to the
 * C# InMemoryEmbeddingStore, which serialises via .NET BinaryWriter:
 *   int32   FileMagic   = 0x4C455143   (4 bytes little-endian)
 *   uint16  FileVersion = 1
 *   uint16  bitsPerDim
 *   int32   dimension
 *   int32   count
 *   repeat count times:
 *     string id            (7-bit-encoded length prefix, then UTF-8)
 *     string text          (same)
 *     int32  metaCount
 *     repeat metaCount:  string key, string value
 *     float  payload.norm  (4 bytes little-endian)
 *     int32  packedLen
 *     bytes  packed        (packedLen raw bytes)
 * .NET BinaryWriter string prefix = ULEB128 of the UTF-8 byte count.
 *
 * In-memory only. Pure C11 + libc; links -lm via compression.h.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * ITextEmbedder / IEmbeddingEncoder seams
 * ===========================================================================
 *
 * Both produce a freshly-malloc'd float array (the store frees it) and set
 * *out_len; return NULL on failure. user is passed through untouched.
 *   - ca_text_embedder2_fn mirrors ITextEmbedder.GenerateAsync.
 *   - ca_embedding_encoder_t couples that with the fixed Dimension of
 *     IEmbeddingEncoder (all vectors from one encoder must agree).
 */
typedef float *(*ca_text_embedder2_fn)(void *user, const char *text, size_t *out_len);

typedef struct {
    int    dimension;              /* IEmbeddingEncoder.Dimension */
    float *(*encode)(void *user, const char *text, size_t *out_len);
    void  *user;
} ca_embedding_encoder_t;

/* ===========================================================================
 * EmbeddingDocument / EmbeddingSearchHit
 * =========================================================================== */

typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_embedding_meta_t;

typedef struct {
    char                *id;        /* owned */
    char                *text;      /* owned */
    ca_embedding_meta_t *metadata;  /* owned; NULL when metadata_count == 0 */
    size_t               metadata_count;
} ca_embedding_document_t;

typedef struct {
    ca_embedding_document_t document; /* owned deep copy */
    float                   score;    /* cosine similarity, higher = closer */
} ca_embedding_search_hit_t;

/* Free an array of search hits (deep). */
void ca_embedding_search_hits_free(ca_embedding_search_hit_t *hits, size_t count);

/* ===========================================================================
 * ICircleEmbeddingStore + InMemoryEmbeddingStore
 * =========================================================================== */

typedef struct ca_embedding_store ca_embedding_store_t;

/* Create a brute-force store over an encoder (borrowed; must outlive the store).
 * bits_per_dim in 1..8 (default 4). Returns NULL on a NULL encoder or bad width. */
ca_embedding_store_t *ca_embedding_store_create(const ca_embedding_encoder_t *encoder,
                                                int bits_per_dim);
void ca_embedding_store_destroy(ca_embedding_store_t *s);

/* Vector dimension (== encoder dimension). */
int    ca_embedding_store_dimension(const ca_embedding_store_t *s);
/* Number of documents currently held. */
size_t ca_embedding_store_count(const ca_embedding_store_t *s);

/* Add (or replace) a document, encoding its text via the store's encoder.
 * metadata may be NULL. Returns false on failure (encode error, OOM). */
bool ca_embedding_store_add(ca_embedding_store_t *s,
                            const char *id, const char *text,
                            const ca_embedding_meta_t *metadata, size_t metadata_count);

/* Add with a caller-supplied vector (length must equal Dimension). */
bool ca_embedding_store_add_vector(ca_embedding_store_t *s,
                                   const char *id, const char *text,
                                   const ca_embedding_meta_t *metadata, size_t metadata_count,
                                   const float *vector, size_t vector_len);

/* Remove by id. Returns true when a document was removed. */
bool ca_embedding_store_remove(ca_embedding_store_t *s, const char *id);

/* Search by text (encoded via the store's encoder). Returns a fresh hit array
 * (caller frees with ca_embedding_search_hits_free), *out_count set, ordered by
 * descending score. Returns NULL with *out_count 0 on empty/failed encode. */
ca_embedding_search_hit_t *ca_embedding_store_search(ca_embedding_store_t *s,
                                                     const char *query_text, int top_k,
                                                     size_t *out_count);

/* Search by a pre-computed query vector (length must equal Dimension). */
ca_embedding_search_hit_t *ca_embedding_store_search_vector(ca_embedding_store_t *s,
                                                            const float *query_vector,
                                                            size_t query_len, int top_k,
                                                            size_t *out_count);

/* Persist to path (atomic write-tmp-then-rename). Returns false on IO error. */
bool ca_embedding_store_save(ca_embedding_store_t *s, const char *path);
/* Load from path, replacing all in-memory state. Returns false on a missing
 * file, bad magic/version, or a bits/dimension mismatch. */
bool ca_embedding_store_load(ca_embedding_store_t *s, const char *path);

/* ===========================================================================
 * IEmbeddingIndex + EmbeddingIndexHit
 * ===========================================================================
 *
 * The store layers documents + metadata + persistence on top; the index is the
 * search primitive. This ships the brute-force in-memory backend (raw FP32,
 * cosine). InternalId is the insertion-order id assigned by add.
 */

typedef struct {
    int64_t internal_id;
    float   score;
} ca_embedding_index_hit_t;

typedef struct ca_embedding_index ca_embedding_index_t;

/* Create a brute-force index of the given dimension (> 0). Returns NULL on a bad
 * dimension / OOM. */
ca_embedding_index_t *ca_embedding_index_create(int dimension);
void ca_embedding_index_destroy(ca_embedding_index_t *idx);

int     ca_embedding_index_dimension(const ca_embedding_index_t *idx);
int64_t ca_embedding_index_count(const ca_embedding_index_t *idx);

/* Append a vector (length must equal Dimension). Returns the assigned internal
 * id (>= 0), or -1 on a length mismatch / OOM. */
int64_t ca_embedding_index_add(ca_embedding_index_t *idx,
                               const float *vector, size_t vector_len);

/* Top-k nearest neighbours by cosine. Returns a fresh array (caller frees with
 * free()), *out_count set, ordered by descending score. Returns NULL with
 * *out_count 0 when empty or on a length mismatch. */
ca_embedding_index_hit_t *ca_embedding_index_search(ca_embedding_index_t *idx,
                                                    const float *query_vector,
                                                    size_t query_len, int top_k,
                                                    size_t *out_count);

/* Persist / reload the raw index (simple self-describing format). */
bool ca_embedding_index_save(ca_embedding_index_t *idx, const char *path);
bool ca_embedding_index_load(ca_embedding_index_t *idx, const char *path);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_EMBEDDING_STORE_H */
