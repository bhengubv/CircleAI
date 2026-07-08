#ifndef CIRCLE_AI_SHARD_KV_H
#define CIRCLE_AI_SHARD_KV_H

/*
 * shard_kv.h — (3.3.0) Shard-style KV-cache compression (C11 port).
 *
 * Faithful port of CircleAI.Core.Compression.ShardKvCodec + ShardCompressedFrame.
 * Compresses K via centre → fast Walsh-Hadamard → project to top-rank PCA axes
 * → int8 quantise, and V via nearest-codeword vector quantisation.
 *
 * WIRE FORMAT (byte-identical to C#, Kotlin and Swift ports):
 *   CompressedK: [0..3]  = scale, float32 little-endian
 *                [4..]   = kRank int8 quantised projections (one byte each)
 *   CompressedV: 1, 2 or 4 bytes = codeword index, uint little-endian
 *                (1 byte when vCodewords <= 256, 2 when <= 65536, else 4)
 *   KPrincipalAxes: kRank*kDim float32 row-major, materialised in the frame so
 *                   the decoder can stand alone.
 *
 * The V codebook is NOT carried in the frame — the decoder looks codewords up by
 * index. Byte-for-byte cross-language decode therefore requires an identical
 * seeded codebook, so ca_dotnet_random reproduces System.Random(seed) (Knuth's
 * subtractive PRNG) exactly, matching the other language ports.
 *
 * Numeric fidelity: every C# `float` maps to a C `float` at the same point; the
 * norm-free math accumulates in `float` (projections) exactly like C#.
 *
 * In-memory only. Pure C11 + libc; links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * DotNetRandom — byte-faithful System.Random(seed)
 * ===========================================================================
 *
 * Reproduces the .NET legacy subtractive generator so a seeded codebook is
 * byte-identical across the C#, Kotlin, Swift and C codecs. Exposed for tests.
 */

typedef struct {
    int32_t seed_array[56];
    int32_t inext;
    int32_t inextp;
} ca_dotnet_random_t;

/* Initialise with the seed passed to System.Random(int). */
void   ca_dotnet_random_init(ca_dotnet_random_t *rng, int32_t seed);
/* Equivalent to Random.NextDouble() — a double in [0.0, 1.0). */
double ca_dotnet_random_next_double(ca_dotnet_random_t *rng);

/* ===========================================================================
 * ShardCompressedFrame
 * =========================================================================== */

typedef struct {
    uint8_t *compressed_k;     /* owned: 4 + kRank bytes */
    size_t   compressed_k_len;
    uint8_t *compressed_v;     /* owned: 1, 2 or 4 bytes */
    size_t   compressed_v_len;
    float   *k_principal_axes; /* owned: kRank*kDim floats, row-major */
    size_t   k_principal_axes_len;
    int      k_original_dim;
    int      v_original_dim;
} ca_shard_frame_t;

/* Free a frame's owned buffers (does not free the struct itself). */
void ca_shard_frame_free(ca_shard_frame_t *frame);

/* ===========================================================================
 * ShardKvCodec — opaque handle
 * =========================================================================== */

typedef struct ca_shard_kv_codec ca_shard_kv_codec_t;

/*
 * Create a codec. Returns NULL on an invalid argument:
 *   kDim  > 0
 *   kRank in 1..kDim
 *   vDim  > 0
 *   vCodewords a power of two > 1
 * v_codebook_seed seeds the deterministic initial codebook.
 */
ca_shard_kv_codec_t *ca_shard_kv_codec_create(int k_dim, int k_rank, int v_dim,
                                              int v_codewords, int v_codebook_seed);
void ca_shard_kv_codec_destroy(ca_shard_kv_codec_t *c);

/* Number of K samples fed to ObserveK (running-mean count). */
int64_t ca_shard_kv_codec_samples_observed(const ca_shard_kv_codec_t *c);

/* Update the online K mean estimate with one sample (len must equal kDim).
 * Returns false on a dimension mismatch. */
bool ca_shard_kv_codec_observe_k(ca_shard_kv_codec_t *c, const float *k, size_t len);

/* Replace the PCA axes with a (kRank, kDim) row-major matrix. Returns false on a
 * shape mismatch. */
bool ca_shard_kv_codec_set_principal_axes(ca_shard_kv_codec_t *c,
                                          const float *axes, size_t rows, size_t cols);

/* Replace the V codebook with count codewords, each of length dim (flat,
 * row-major: codebook[c*dim + i]). Returns false on a size/dim mismatch. */
bool ca_shard_kv_codec_set_v_codebook(ca_shard_kv_codec_t *c,
                                      const float *codebook, size_t count, size_t dim);

/* Encode one (K, V) pair into *out (caller frees with ca_shard_frame_free).
 * Returns false on a dimension mismatch. */
bool ca_shard_kv_codec_encode(ca_shard_kv_codec_t *c,
                              const float *k, size_t k_len,
                              const float *v, size_t v_len,
                              ca_shard_frame_t *out);

/* Decode a frame into fresh K and V arrays (caller frees each with free()).
 * *out_k_len set to kDim, *out_v_len to vDim. Returns false (and leaves the
 * out pointers NULL) on a codec/frame dimension mismatch. */
bool ca_shard_kv_codec_decode(ca_shard_kv_codec_t *c, const ca_shard_frame_t *frame,
                              float **out_k, size_t *out_k_len,
                              float **out_v, size_t *out_v_len);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SHARD_KV_H */
