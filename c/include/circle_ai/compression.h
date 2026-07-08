#ifndef CIRCLE_AI_COMPRESSION_H
#define CIRCLE_AI_COMPRESSION_H

/*
 * compression.h — TurboQuant embedding compression + compressed store
 * decorators (C11 port).
 *
 * Ported EXACTLY from the C# reference so a payload encoded by any language in
 * the SDK decodes byte-identically in every other:
 *   - CircleAI.Core.Compression.BitPacker
 *   - CircleAI.Core.Compression.OrthogonalRotation (+ SeededGaussian / SplitMix64)
 *   - CircleAI.Core.Compression.BetaLloydMaxCodebook
 *   - CircleAI.Core.Compression.TurboQuantCodec (+ TurboQuantPayload)
 *   - CircleAI.Memory.Compression.EmbeddingPayloadCodec
 *   - CircleAI.Memory.Compression.CompressedEpisodicMemoryStore
 *   - CircleAI.Memory.Compression.CompressedMultimodalMemoryStore
 * and mirroring the verified TypeScript reference (memory/compression.ts) 1:1.
 *
 * Numeric fidelity (why the bytes match with NO shim):
 *   - The SplitMix64 PRNG state is a native uint64_t (C has real 64-bit ints).
 *   - Every place C# stores a `float` (norm, matrix cells, centroids, deltas) we
 *     use C `float` (32-bit) at the SAME point, and accumulate the norm in
 *     `double` then cast to `float`, exactly like C#.
 *   - The wire format writes float32/uint32 little-endian byte-by-byte.
 *
 * Wire format of a payload (EmbeddingPayloadCodec):
 *   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
 *   bytes [4..7]   = bit-width as uint32 little-endian
 *   bytes [8..11]  = dimension as uint32 little-endian
 *   bytes [12..15] = norm as float32 little-endian
 *   bytes [16..]   = packed indices
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "memory_brain.h"   /* ca_episodic_store_t, ca_episodic_entry_t */
#include "multimodal.h"     /* ca_multimodal_store_t, ca_multimodal_entry_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * BitPacker
 * =========================================================================== */

/* Pack count indices at bits_per_index (1..16) LSB-first into a fresh byte
 * buffer. *out_len set to (count*bits+7)/8. Returns NULL on an invalid width, an
 * overflowing index, or allocation failure (with *out_len == 0). */
uint8_t *ca_bitpacker_pack(const uint16_t *indices, size_t count,
                           int bits_per_index, size_t *out_len);

/* Unpack count indices of bits_per_index each from packed (packed_len bytes) into
 * a fresh uint16_t array. Returns NULL on an invalid width or a too-small buffer. */
uint16_t *ca_bitpacker_unpack(const uint8_t *packed, size_t packed_len,
                              size_t count, int bits_per_index);

/* ===========================================================================
 * OrthogonalRotation
 * =========================================================================== */

/* The fixed rotation seed shared across every CircleAI process. */
#define CA_ROTATION_SEED 0xC1C1EA10C1C1EA10ULL

/* Return the dim*dim orthogonal matrix (row-major float32), cached per dim. The
 * pointer is owned by an internal cache — DO NOT free it; it lives until
 * ca_orthogonal_rotation_clear_cache(). Returns NULL on dim <= 0 / OOM. */
const float *ca_orthogonal_rotation_matrix(int dim);

/* output[i] = Σ R[i,j]*vector[j], fp32 accumulation like C#. */
void ca_orthogonal_rotation_rotate(int dim, const float *vector, float *output);
/* Inverse: multiply the transpose. */
void ca_orthogonal_rotation_unrotate(int dim, const float *vector, float *output);

/* Free the per-dimension matrix cache (optional; for leak-clean shutdown). */
void ca_orthogonal_rotation_clear_cache(void);

/* ===========================================================================
 * BetaLloydMaxCodebook
 * =========================================================================== */

/* boundaries has length 2^bits - 1; centroids has length 2^bits. Both owned by
 * an internal cache — DO NOT free. */
typedef struct {
    const float *boundaries;
    size_t       boundaries_len;
    const float *centroids;
    size_t       centroids_len;
} ca_beta_codebook_t;

/* Get (and cache) the codebook for (bits in 1..8, dim > 1). Returns false on an
 * invalid argument. */
bool ca_beta_codebook_get(int bits, int dim, ca_beta_codebook_t *out);

/* Bin index for value against boundaries (linear scan). */
uint16_t ca_beta_codebook_bin_for(float value, const float *boundaries, size_t n);

/* Free the codebook cache (optional; for leak-clean shutdown). */
void ca_beta_codebook_clear_cache(void);

/* ===========================================================================
 * TurboQuantCodec
 * =========================================================================== */

typedef struct {
    float    norm;
    uint8_t *packed_indices;  /* owned */
    size_t   packed_len;
} ca_turboquant_payload_t;

void ca_turboquant_payload_free(ca_turboquant_payload_t *p);

/* Encode a float vector (length > 1) at bits_per_dim (1..8). Fills *out (owned by
 * the caller — free with ca_turboquant_payload_free). Returns false on an
 * invalid argument. */
bool ca_turboquant_encode(const float *vector, size_t dim, int bits_per_dim,
                          ca_turboquant_payload_t *out);

/* Decode a payload into a fresh float array of length dim (caller frees with
 * free()). Returns NULL on an invalid argument. */
float *ca_turboquant_decode(const ca_turboquant_payload_t *payload,
                            int dim, int bits_per_dim);

/* Bytes-per-vector at (dim, bits_per_dim), excluding the 4-byte norm header. */
size_t ca_turboquant_payload_byte_count(int dim, int bits_per_dim);
/* Compression ratio vs raw FP32 (incl. norm). */
double ca_turboquant_compression_ratio(int dim, int bits_per_dim);

/* ===========================================================================
 * EmbeddingPayloadCodec
 * =========================================================================== */

/* The 4-byte magic header "TQ3\1". */
extern const uint8_t CA_TQ_MAGIC[4];

/* Encode vector (length > 1) at bits_per_dim into the self-describing wire
 * payload. Fills *out_len; returns a fresh byte buffer (caller frees) or NULL. */
uint8_t *ca_embedding_payload_encode(const float *vector, size_t dim,
                                     int bits_per_dim, size_t *out_len);

/* Decode a wire payload into a fresh float array; *out_len set to dim. Returns
 * NULL on a too-short / bad-magic / invalid payload. */
float *ca_embedding_payload_decode(const uint8_t *bytes, size_t len, size_t *out_len);

/* True when bytes begins with the magic header. */
bool ca_embedding_payload_is_encoded(const uint8_t *bytes, size_t len);

/* Encode + standard-base64. Returns a fresh NUL-terminated string (caller frees)
 * or NULL. */
char *ca_embedding_payload_encode_base64(const float *vector, size_t dim, int bits_per_dim);

/* Standard-base64-decode + decode. Returns a fresh float array; *out_len set.
 * Returns NULL on a malformed base64 or payload. */
float *ca_embedding_payload_decode_base64(const char *base64, size_t *out_len);

/* Standalone standard base64 (RFC 4648, '+' '/' '='), exposed for tests. Return
 * fresh NUL-terminated string / byte buffer; caller frees. */
char    *ca_base64_encode(const uint8_t *data, size_t len);
uint8_t *ca_base64_decode(const char *b64, size_t *out_len);

/* Tag key under which the compressed embedding is stored. */
#define CA_COMPRESSED_TAG_KEY "x-tq-embedding"

/* ===========================================================================
 * CompressedEpisodicMemoryStore — decorator
 * ===========================================================================
 *
 * Wraps a ca_episodic_store_t: on add, an entry with an embedding of length > 1
 * has its embedding dropped and stored as a base64 TurboQuant payload in the tag
 * CA_COMPRESSED_TAG_KEY; reads/search rehydrate it. The inner store is borrowed.
 */

typedef struct ca_compressed_episodic_store ca_compressed_episodic_store_t;

/* bits_per_dim in 1..8 (default 2). Returns NULL on a NULL inner or bad width. */
ca_compressed_episodic_store_t *ca_compressed_episodic_store_create(
    ca_episodic_store_t *inner /* borrowed */, int bits_per_dim);
void ca_compressed_episodic_store_destroy(ca_compressed_episodic_store_t *s);

/* Add: compresses the embedding into a tag (if len > 1) and forwards to inner. */
bool ca_compressed_episodic_store_add(ca_compressed_episodic_store_t *s,
                                      const ca_episodic_entry_t *entry);

/* Search: rehydrates embeddings on the read path and ranks by cosine (recency
 * when query NULL). top_k <= 0 → 5. Fresh deep-copied array (caller frees with
 * ca_episodic_entry_free_array). */
ca_episodic_entry_t *ca_compressed_episodic_store_search(
    ca_compressed_episodic_store_t *s, const float *query, size_t query_len,
    int top_k, size_t *out_count);

/* Most-recent count, rehydrated. */
ca_episodic_entry_t *ca_compressed_episodic_store_get_recent(
    ca_compressed_episodic_store_t *s, int count, size_t *out_count);

size_t ca_compressed_episodic_store_count(const ca_compressed_episodic_store_t *s);
size_t ca_compressed_episodic_store_prune_older_than(ca_compressed_episodic_store_t *s,
                                                     int64_t cutoff_ms);

/* ===========================================================================
 * CompressedMultimodalMemoryStore — decorator
 * =========================================================================== */

typedef struct ca_compressed_multimodal_store ca_compressed_multimodal_store_t;

ca_compressed_multimodal_store_t *ca_compressed_multimodal_store_create(
    ca_multimodal_store_t *inner /* borrowed */, int bits_per_dim);
void ca_compressed_multimodal_store_destroy(ca_compressed_multimodal_store_t *s);

bool ca_compressed_multimodal_store_add(ca_compressed_multimodal_store_t *s,
                                        const ca_multimodal_entry_t *entry);
bool ca_compressed_multimodal_store_get_by_hash(ca_compressed_multimodal_store_t *s,
                                                const char *source_sha256,
                                                ca_multimodal_entry_t *out);
void ca_compressed_multimodal_store_reinforce(ca_compressed_multimodal_store_t *s,
                                              const char *source_sha256);
ca_multimodal_entry_t *ca_compressed_multimodal_store_search(
    ca_compressed_multimodal_store_t *s, const float *query, size_t query_len,
    int top_k, size_t *out_count);
ca_multimodal_entry_t *ca_compressed_multimodal_store_get_recent(
    ca_compressed_multimodal_store_t *s, int count, size_t *out_count);
size_t ca_compressed_multimodal_store_count(const ca_compressed_multimodal_store_t *s);
size_t ca_compressed_multimodal_store_prune_older_than(ca_compressed_multimodal_store_t *s,
                                                       int64_t cutoff_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_COMPRESSION_H */
