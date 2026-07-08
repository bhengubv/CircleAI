/*
 * shard_kv.c — (3.3.0) ShardKvCodec + ShardCompressedFrame (C11 port).
 *
 * Byte-faithful port of CircleAI.Core.Compression.ShardKvCodec. See shard_kv.h
 * for the wire format. Ported from the C# reference and matching the Kotlin/Swift
 * ports; System.Random(seed) is reproduced exactly (ca_dotnet_random).
 *
 * Rounding: C#'s (int)Math.Round(double) uses banker's rounding (ToEven). C's
 * rint() is round-half-to-even under the default FP rounding mode, so int8
 * quantisation matches byte-for-byte.
 */

#include "circle_ai/shard_kv.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <float.h>

/* ─────────────────────── DotNetRandom ─────────────────────── */

void ca_dotnet_random_init(ca_dotnet_random_t *rng, int32_t seed) {
    const int32_t mbig  = 2147483647;      /* int.MaxValue */
    const int32_t mseed = 161803398;
    /* subtraction = (seed == int.MinValue) ? int.MaxValue : abs(seed) */
    int32_t subtraction = (seed == (-2147483647 - 1)) ? 2147483647
                                                      : (seed < 0 ? -seed : seed);
    int32_t mj = mseed - subtraction;
    rng->seed_array[55] = mj;
    int32_t mk = 1;
    for (int i = 1; i < 55; i++) {
        int ii = (21 * i) % 55;
        rng->seed_array[ii] = mk;
        mk = mj - mk;
        if (mk < 0) mk += mbig;
        mj = rng->seed_array[ii];
    }
    for (int k = 1; k < 5; k++) {
        for (int i = 1; i < 56; i++) {
            rng->seed_array[i] -= rng->seed_array[1 + (i + 30) % 55];
            if (rng->seed_array[i] < 0) rng->seed_array[i] += mbig;
        }
    }
    rng->inext  = 0;
    rng->inextp = 21;
}

static int32_t dotnet_internal_sample(ca_dotnet_random_t *rng) {
    const int32_t mbig = 2147483647;
    int32_t loc_inext  = rng->inext;
    int32_t loc_inextp = rng->inextp;
    if (++loc_inext  >= 56) loc_inext  = 1;
    if (++loc_inextp >= 56) loc_inextp = 1;
    int32_t ret = rng->seed_array[loc_inext] - rng->seed_array[loc_inextp];
    if (ret == mbig) ret--;
    if (ret < 0) ret += mbig;
    rng->seed_array[loc_inext] = ret;
    rng->inext  = loc_inext;
    rng->inextp = loc_inextp;
    return ret;
}

double ca_dotnet_random_next_double(ca_dotnet_random_t *rng) {
    return (double)dotnet_internal_sample(rng) * (1.0 / 2147483647.0);
}

/* ─────────────────────── ShardCompressedFrame ─────────────────────── */

void ca_shard_frame_free(ca_shard_frame_t *frame) {
    if (!frame) return;
    free(frame->compressed_k);
    free(frame->compressed_v);
    free(frame->k_principal_axes);
    memset(frame, 0, sizeof(*frame));
}

/* ─────────────────────── ShardKvCodec ─────────────────────── */

struct ca_shard_kv_codec {
    int      k_dim;
    int      k_rank;
    int      v_dim;
    int      v_codewords;
    float  **v_codebook;       /* v_codewords rows of v_dim floats */
    float   *hadamard_scratch; /* pow2_ceil(k_dim) floats */
    size_t   hadamard_len;
    float   *k_center;         /* k_dim */
    float   *k_axes;           /* k_rank * k_dim, row-major */
    int64_t  samples_observed;
};

static int pow2_ceil(int v) {
    int p = 1;
    while (p < v) p <<= 1;
    return p;
}

static float **seed_codebook(int dim, int count, int seed) {
    ca_dotnet_random_t rng;
    ca_dotnet_random_init(&rng, seed);
    float **cb = (float **)malloc((size_t)count * sizeof(float *));
    if (!cb) return NULL;
    for (int c = 0; c < count; c++) {
        cb[c] = (float *)malloc((size_t)dim * sizeof(float));
        if (!cb[c]) {
            for (int j = 0; j < c; j++) free(cb[j]);
            free(cb);
            return NULL;
        }
        for (int i = 0; i < dim; i++) {
            /* uniform [-1, 1] */
            cb[c][i] = (float)(ca_dotnet_random_next_double(&rng) * 2.0 - 1.0);
        }
    }
    return cb;
}

ca_shard_kv_codec_t *ca_shard_kv_codec_create(int k_dim, int k_rank, int v_dim,
                                              int v_codewords, int v_codebook_seed) {
    if (k_dim  <= 0) return NULL;
    if (k_rank <= 0 || k_rank > k_dim) return NULL;
    if (v_dim  <= 0) return NULL;
    if (v_codewords <= 1 || (v_codewords & (v_codewords - 1)) != 0) return NULL;

    ca_shard_kv_codec_t *c = (ca_shard_kv_codec_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->k_dim = k_dim;
    c->k_rank = k_rank;
    c->v_dim = v_dim;
    c->v_codewords = v_codewords;
    c->hadamard_len = (size_t)pow2_ceil(k_dim);

    c->k_center = (float *)calloc((size_t)k_dim, sizeof(float));
    c->k_axes   = (float *)calloc((size_t)k_rank * (size_t)k_dim, sizeof(float));
    c->hadamard_scratch = (float *)calloc(c->hadamard_len, sizeof(float));
    c->v_codebook = seed_codebook(v_dim, v_codewords, v_codebook_seed);

    if (!c->k_center || !c->k_axes || !c->hadamard_scratch || !c->v_codebook) {
        ca_shard_kv_codec_destroy(c);
        return NULL;
    }

    /* Initialise PCA axes to identity-top-rank for sane defaults before training. */
    for (int r = 0; r < k_rank; r++) {
        c->k_axes[r * k_dim + r] = 1.0f;
    }
    return c;
}

void ca_shard_kv_codec_destroy(ca_shard_kv_codec_t *c) {
    if (!c) return;
    if (c->v_codebook) {
        for (int i = 0; i < c->v_codewords; i++) free(c->v_codebook[i]);
        free(c->v_codebook);
    }
    free(c->k_center);
    free(c->k_axes);
    free(c->hadamard_scratch);
    free(c);
}

int64_t ca_shard_kv_codec_samples_observed(const ca_shard_kv_codec_t *c) {
    return c ? c->samples_observed : 0;
}

bool ca_shard_kv_codec_observe_k(ca_shard_kv_codec_t *c, const float *k, size_t len) {
    if (!c || !k || (int)len != c->k_dim) return false;
    c->samples_observed++;
    for (int i = 0; i < c->k_dim; i++) {
        /* Running mean. */
        c->k_center[i] += (k[i] - c->k_center[i]) / (float)c->samples_observed;
    }
    return true;
}

bool ca_shard_kv_codec_set_principal_axes(ca_shard_kv_codec_t *c,
                                          const float *axes, size_t rows, size_t cols) {
    if (!c || !axes) return false;
    if ((int)rows != c->k_rank || (int)cols != c->k_dim) return false;
    memcpy(c->k_axes, axes, (size_t)c->k_rank * (size_t)c->k_dim * sizeof(float));
    return true;
}

bool ca_shard_kv_codec_set_v_codebook(ca_shard_kv_codec_t *c,
                                      const float *codebook, size_t count, size_t dim) {
    if (!c || !codebook) return false;
    if ((int)count != c->v_codewords) return false;
    if ((int)dim != c->v_dim) return false;
    for (int i = 0; i < c->v_codewords; i++) {
        memcpy(c->v_codebook[i], codebook + (size_t)i * dim, (size_t)c->v_dim * sizeof(float));
    }
    return true;
}

static void apply_hadamard_in_place(ca_shard_kv_codec_t *c, float *buffer, size_t buf_len) {
    /* Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch. */
    size_t n = c->hadamard_len;
    memset(c->hadamard_scratch, 0, n * sizeof(float));
    size_t copy = buf_len < n ? buf_len : n;
    memcpy(c->hadamard_scratch, buffer, copy * sizeof(float));

    for (size_t h = 1; h < n; h <<= 1) {
        for (size_t i = 0; i < n; i += h * 2) {
            for (size_t j = i; j < i + h; j++) {
                float x = c->hadamard_scratch[j];
                float y = c->hadamard_scratch[j + h];
                c->hadamard_scratch[j]     = x + y;
                c->hadamard_scratch[j + h] = x - y;
            }
        }
    }
    memcpy(buffer, c->hadamard_scratch, copy * sizeof(float));
}

/* Little-endian writers (portable regardless of host endianness). */
static void write_f32_le(uint8_t *p, float f) {
    uint32_t bits;
    memcpy(&bits, &f, 4);
    p[0] = (uint8_t)(bits & 0xFF);
    p[1] = (uint8_t)((bits >> 8) & 0xFF);
    p[2] = (uint8_t)((bits >> 16) & 0xFF);
    p[3] = (uint8_t)((bits >> 24) & 0xFF);
}
static float read_f32_le(const uint8_t *p) {
    uint32_t bits = (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
                    ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
    float f;
    memcpy(&f, &bits, 4);
    return f;
}

bool ca_shard_kv_codec_encode(ca_shard_kv_codec_t *c,
                              const float *k, size_t k_len,
                              const float *v, size_t v_len,
                              ca_shard_frame_t *out) {
    if (!c || !k || !v || !out) return false;
    if ((int)k_len != c->k_dim) return false;
    if ((int)v_len != c->v_dim) return false;

    memset(out, 0, sizeof(*out));

    /* K: centre → Hadamard → project to top-rank principal axes → quantise int8. */
    float *centred = (float *)malloc((size_t)c->k_dim * sizeof(float));
    if (!centred) return false;
    for (int i = 0; i < c->k_dim; i++) centred[i] = k[i] - c->k_center[i];
    apply_hadamard_in_place(c, centred, (size_t)c->k_dim);

    float *projected = (float *)malloc((size_t)c->k_rank * sizeof(float));
    if (!projected) { free(centred); return false; }
    for (int r = 0; r < c->k_rank; r++) {
        float dot = 0.0f;
        for (int i = 0; i < c->k_dim; i++) dot += centred[i] * c->k_axes[r * c->k_dim + i];
        projected[r] = dot;
    }
    free(centred);

    /* Find scale that fits all components into int8 dynamic range. */
    float max_abs = 1e-9f;
    for (int r = 0; r < c->k_rank; r++) {
        float a = fabsf(projected[r]);
        if (a > max_abs) max_abs = a;
    }
    float scale = max_abs / 127.0f;

    out->compressed_k_len = (size_t)c->k_rank + 4; /* +4 for the scale float32 LE */
    out->compressed_k = (uint8_t *)malloc(out->compressed_k_len);
    if (!out->compressed_k) { free(projected); return false; }
    write_f32_le(out->compressed_k, scale);
    for (int r = 0; r < c->k_rank; r++) {
        int q = (int)rintf(projected[r] / scale); /* banker's rounding, matches Math.Round */
        if (q < -127) q = -127;
        if (q >  127) q =  127;
        out->compressed_k[4 + r] = (uint8_t)((int8_t)q);
    }
    free(projected);

    /* V: nearest-codeword VQ. */
    int best_idx = 0;
    float best_dist = FLT_MAX; /* float.MaxValue */
    for (int cw = 0; cw < c->v_codewords; cw++) {
        float d = 0.0f;
        const float *word = c->v_codebook[cw];
        for (int i = 0; i < c->v_dim; i++) {
            float diff = v[i] - word[i];
            d += diff * diff;
        }
        if (d < best_dist) { best_dist = d; best_idx = cw; }
    }

    int idx_bytes = c->v_codewords <= 256 ? 1 : (c->v_codewords <= 65536 ? 2 : 4);
    out->compressed_v_len = (size_t)idx_bytes;
    out->compressed_v = (uint8_t *)malloc(out->compressed_v_len);
    if (!out->compressed_v) { free(out->compressed_k); out->compressed_k = NULL; return false; }
    switch (idx_bytes) {
        case 1:
            out->compressed_v[0] = (uint8_t)best_idx;
            break;
        case 2:
            out->compressed_v[0] = (uint8_t)((uint16_t)best_idx & 0xFF);
            out->compressed_v[1] = (uint8_t)(((uint16_t)best_idx >> 8) & 0xFF);
            break;
        case 4:
            out->compressed_v[0] = (uint8_t)((uint32_t)best_idx & 0xFF);
            out->compressed_v[1] = (uint8_t)(((uint32_t)best_idx >> 8) & 0xFF);
            out->compressed_v[2] = (uint8_t)(((uint32_t)best_idx >> 16) & 0xFF);
            out->compressed_v[3] = (uint8_t)(((uint32_t)best_idx >> 24) & 0xFF);
            break;
        default: break;
    }

    /* Materialise the PCA axes once in the frame so the decoder can stand alone. */
    out->k_principal_axes_len = (size_t)c->k_rank * (size_t)c->k_dim;
    out->k_principal_axes = (float *)malloc(out->k_principal_axes_len * sizeof(float));
    if (!out->k_principal_axes) {
        free(out->compressed_k); out->compressed_k = NULL;
        free(out->compressed_v); out->compressed_v = NULL;
        return false;
    }
    for (int r = 0; r < c->k_rank; r++) {
        for (int i = 0; i < c->k_dim; i++) {
            out->k_principal_axes[r * c->k_dim + i] = c->k_axes[r * c->k_dim + i];
        }
    }
    out->k_original_dim = c->k_dim;
    out->v_original_dim = c->v_dim;
    return true;
}

bool ca_shard_kv_codec_decode(ca_shard_kv_codec_t *c, const ca_shard_frame_t *frame,
                              float **out_k, size_t *out_k_len,
                              float **out_v, size_t *out_v_len) {
    if (out_k) *out_k = NULL;
    if (out_v) *out_v = NULL;
    if (!c || !frame || !out_k || !out_v || !out_k_len || !out_v_len) return false;
    if (frame->k_original_dim != c->k_dim) return false;
    if (frame->v_original_dim != c->v_dim) return false;

    /* K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter. */
    float scale = read_f32_le(frame->compressed_k);
    float *projected = (float *)malloc((size_t)c->k_rank * sizeof(float));
    if (!projected) return false;
    for (int r = 0; r < c->k_rank; r++) {
        projected[r] = (float)((int8_t)frame->compressed_k[4 + r]) * scale;
    }

    float *k = (float *)malloc((size_t)c->k_dim * sizeof(float));
    if (!k) { free(projected); return false; }
    for (int i = 0; i < c->k_dim; i++) {
        float acc = 0.0f;
        for (int r = 0; r < c->k_rank; r++) {
            acc += projected[r] * frame->k_principal_axes[r * c->k_dim + i];
        }
        k[i] = acc;
    }
    free(projected);
    apply_hadamard_in_place(c, k, (size_t)c->k_dim); /* Hadamard is self-inverse up to 1/n. */
    for (int i = 0; i < c->k_dim; i++) k[i] = k[i] / (float)c->k_dim + c->k_center[i];

    /* V decode: read index, copy codeword. */
    int idx_bytes = c->v_codewords <= 256 ? 1 : (c->v_codewords <= 65536 ? 2 : 4);
    int idx = 0;
    switch (idx_bytes) {
        case 1: idx = frame->compressed_v[0]; break;
        case 2: idx = (int)((uint16_t)frame->compressed_v[0] |
                            ((uint16_t)frame->compressed_v[1] << 8)); break;
        case 4: idx = (int)((uint32_t)frame->compressed_v[0] |
                            ((uint32_t)frame->compressed_v[1] << 8) |
                            ((uint32_t)frame->compressed_v[2] << 16) |
                            ((uint32_t)frame->compressed_v[3] << 24)); break;
        default: idx = 0; break;
    }
    if (idx < 0 || idx >= c->v_codewords) { free(k); return false; }

    float *v = (float *)malloc((size_t)c->v_dim * sizeof(float));
    if (!v) { free(k); return false; }
    memcpy(v, c->v_codebook[idx], (size_t)c->v_dim * sizeof(float));

    *out_k = k; *out_k_len = (size_t)c->k_dim;
    *out_v = v; *out_v_len = (size_t)c->v_dim;
    return true;
}
