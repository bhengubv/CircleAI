/*
 * test_shard_kv.c — ShardKvCodec + ShardCompressedFrame + DotNetRandom (C11).
 *
 * Mirrors Circle33ShardKvCodecTests.cs: V round-trip via an explicit codebook,
 * K approximate recovery, ObserveK running mean, constructor guards, dim
 * mismatches, axes/codebook shape guards, compression size, 2-byte V index.
 * Adds a DotNetRandom byte-parity check against known System.Random(seed=0)
 * output so the seeded codebook matches the C#/Kotlin/Swift ports.
 */

#include "circle_ai/shard_kv.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>

/* First three System.Random(0).NextDouble() values (known reference). */
static void test_dotnet_random_parity(void) {
    ca_dotnet_random_t rng;
    ca_dotnet_random_init(&rng, 0);
    double a = ca_dotnet_random_next_double(&rng);
    double b = ca_dotnet_random_next_double(&rng);
    double c = ca_dotnet_random_next_double(&rng);
    /* Reference values from .NET Framework/Core Random(0): */
    assert(fabs(a - 0.7262432699679598) < 1e-12);
    assert(fabs(b - 0.8173253595909687) < 1e-12);
    assert(fabs(c - 0.7680226893946634) < 1e-12);
}

static void test_roundtrip_v_exact_codeword(void) {
    ca_shard_kv_codec_t *c = ca_shard_kv_codec_create(16, 8, 8, 16, 7);
    assert(c);
    float k[16] = {0};
    float v[8] = { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f, 0.7f, -0.8f };

    /* Place v in the codebook so VQ is exact (flat 16*8). */
    float codebook[16 * 8];
    memset(codebook, 0, sizeof(codebook));
    memcpy(&codebook[5 * 8], v, 8 * sizeof(float));
    assert(ca_shard_kv_codec_set_v_codebook(c, codebook, 16, 8));

    ca_shard_frame_t frame;
    assert(ca_shard_kv_codec_encode(c, k, 16, v, 8, &frame));

    float *dk = NULL, *dv = NULL; size_t dkl = 0, dvl = 0;
    assert(ca_shard_kv_codec_decode(c, &frame, &dk, &dkl, &dv, &dvl));
    assert(dvl == 8);
    for (int i = 0; i < 8; i++) assert(fabsf(v[i] - dv[i]) < 1e-3f);

    free(dk); free(dv);
    ca_shard_frame_free(&frame);
    ca_shard_kv_codec_destroy(c);
}

static void test_k_recovers_approx(void) {
    ca_shard_kv_codec_t *c = ca_shard_kv_codec_create(8, 8, 4, 4, 0);
    assert(c);
    float k[8] = { 1, 2, 3, 4, -1, -2, -3, -4 };
    float v[4] = { 0.5f, -0.5f, 0.25f, -0.25f };

    ca_shard_frame_t frame;
    assert(ca_shard_kv_codec_encode(c, k, 8, v, 4, &frame));
    float *dk = NULL, *dv = NULL; size_t dkl = 0, dvl = 0;
    assert(ca_shard_kv_codec_decode(c, &frame, &dk, &dkl, &dv, &dvl));

    double err = 0;
    for (int i = 0; i < 8; i++) err += fabs(dk[i] - k[i]);
    assert(err / 8 < 0.5);

    free(dk); free(dv);
    ca_shard_frame_free(&frame);
    ca_shard_kv_codec_destroy(c);
}

static void test_observe_k_running_mean(void) {
    ca_shard_kv_codec_t *c = ca_shard_kv_codec_create(4, 2, 2, 4, 0);
    assert(c);
    assert(ca_shard_kv_codec_samples_observed(c) == 0);
    float a[4] = { 1, 2, 3, 4 };
    float b[4] = { 3, 4, 5, 6 };
    assert(ca_shard_kv_codec_observe_k(c, a, 4));
    assert(ca_shard_kv_codec_observe_k(c, b, 4));
    assert(ca_shard_kv_codec_samples_observed(c) == 2);
    ca_shard_kv_codec_destroy(c);
}

static void test_constructor_guards(void) {
    assert(ca_shard_kv_codec_create(0, 1, 4, 4, 0) == NULL);   /* kDim <= 0 */
    assert(ca_shard_kv_codec_create(4, 5, 4, 4, 0) == NULL);   /* kRank > kDim */
    assert(ca_shard_kv_codec_create(4, 2, 4, 7, 0) == NULL);   /* codewords not pow2 */
    assert(ca_shard_kv_codec_create(4, 2, 4, 1, 0) == NULL);   /* codewords <= 1 */
    assert(ca_shard_kv_codec_create(4, 0, 4, 4, 0) == NULL);   /* kRank <= 0 */
    assert(ca_shard_kv_codec_create(4, 2, 0, 4, 0) == NULL);   /* vDim <= 0 */
}

static void test_dim_and_shape_guards(void) {
    ca_shard_kv_codec_t *c = ca_shard_kv_codec_create(4, 2, 4, 4, 0);
    assert(c);
    float k4[4] = {0}, v4[4] = {0}, k3[3] = {0}, v3[3] = {0};
    ca_shard_frame_t frame;
    assert(!ca_shard_kv_codec_encode(c, k3, 3, v4, 4, &frame)); /* K dim mismatch */
    assert(!ca_shard_kv_codec_encode(c, k4, 4, v3, 3, &frame)); /* V dim mismatch */

    float axes_bad[3 * 4] = {0};
    assert(!ca_shard_kv_codec_set_principal_axes(c, axes_bad, 3, 4)); /* wrong rows */
    float axes_ok[2 * 4] = {0};
    assert(ca_shard_kv_codec_set_principal_axes(c, axes_ok, 2, 4));

    float cb_bad[3 * 4] = {0};
    assert(!ca_shard_kv_codec_set_v_codebook(c, cb_bad, 3, 4)); /* wrong count */
    ca_shard_kv_codec_destroy(c);
}

static void test_compression_and_index_width(void) {
    /* Compression: raw (64+64)*4 = 512; enc = (4+16) + 1 = 21 << 51. */
    ca_shard_kv_codec_t *c = ca_shard_kv_codec_create(64, 16, 64, 256, 0);
    assert(c);
    float k[64], v[64];
    for (int i = 0; i < 64; i++) { k[i] = i * 0.01f; v[i] = -i * 0.01f; }
    ca_shard_frame_t frame;
    assert(ca_shard_kv_codec_encode(c, k, 64, v, 64, &frame));
    size_t raw = (64 + 64) * sizeof(float);
    size_t enc = frame.compressed_k_len + frame.compressed_v_len;
    assert(frame.compressed_k_len == 16 + 4); /* kRank(16) int8 + 4-byte scale */
    assert(frame.compressed_v_len == 1);      /* 256 codewords -> 1 byte */
    assert(enc < raw / 10);
    ca_shard_frame_free(&frame);
    ca_shard_kv_codec_destroy(c);

    /* 1024 codewords -> 2-byte index. */
    ca_shard_kv_codec_t *c2 = ca_shard_kv_codec_create(4, 2, 4, 1024, 0);
    assert(c2);
    float z4[4] = {0};
    ca_shard_frame_t f2;
    assert(ca_shard_kv_codec_encode(c2, z4, 4, z4, 4, &f2));
    assert(f2.compressed_v_len == 2);
    ca_shard_frame_free(&f2);
    ca_shard_kv_codec_destroy(c2);
}

int main(void) {
    test_dotnet_random_parity();
    test_roundtrip_v_exact_codeword();
    test_k_recovers_approx();
    test_observe_k_running_mean();
    test_constructor_guards();
    test_dim_and_shape_guards();
    test_compression_and_index_width();
    printf("test_shard_kv: all assertions passed\n");
    return 0;
}
