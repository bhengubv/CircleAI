/*
 * test_compression.c — TurboQuant codec + compressed store decorators (C11).
 *
 * Mirrors the verified TypeScript compression.test.ts. The encoded WIRE payload
 * is asserted BYTE-IDENTICAL to the pinned C# ground truth (BitPacker hex,
 * EmbeddingPayloadCodec hex + base64, stored norm). Reconstructed vectors are
 * lossy, so they are checked by cosine tolerance like the TS suite.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── helpers ── */

static void to_hex(const uint8_t *b, size_t n, char *out) {
    static const char *h = "0123456789abcdef";
    for (size_t i = 0; i < n; ++i) { out[i*2] = h[b[i]>>4]; out[i*2+1] = h[b[i]&0xf]; }
    out[n*2] = '\0';
}

/* Deterministic Mulberry32 PRNG — same constants as the TS test. */
static uint32_t mulberry_state;
static void mulberry_seed(uint32_t s) { mulberry_state = s; }
static double mulberry_next(void) {
    mulberry_state = (uint32_t)(mulberry_state + 0x6D2B79F5u);
    uint32_t a = mulberry_state;
    uint32_t t = a ^ (a >> 15);
    t = (uint32_t)(t * (1u | a));
    uint32_t u = t ^ (t >> 7);
    u = (uint32_t)(u * (61u | t));
    t = u ^ t;
    t = t ^ (t >> 14);
    return (double)t / 4294967296.0;
}

/* randomUnit(dim, seed) — matches the TS helper (v[i] = rng()*2-1, then L2). */
static float *random_unit(int dim, uint32_t seed) {
    mulberry_seed(seed);
    float *v = (float *)malloc((size_t)dim * sizeof(float));
    double sumSq = 0.0;
    for (int i = 0; i < dim; ++i) { v[i] = (float)(mulberry_next() * 2.0 - 1.0); sumSq += (double)v[i]*v[i]; }
    double inv = 1.0 / sqrt(sumSq);
    for (int i = 0; i < dim; ++i) v[i] = (float)(v[i] * inv);
    return v;
}

static double cosine(const float *a, const float *b, int n) {
    double dot=0, ma=0, mb=0;
    for (int i = 0; i < n; ++i) { dot += (double)a[i]*b[i]; ma += (double)a[i]*a[i]; mb += (double)b[i]*b[i]; }
    double denom = sqrt(ma)*sqrt(mb);
    return denom < 1e-30 ? 0.0 : dot/denom;
}

static bool feq32(float a, float b) {
    /* exact FP32 equality for pinned values */
    return a == b;
}

int main(void) {
    char hexbuf[512];

    /* ── C# wire-format parity: BitPacker ── */
    {
        uint16_t a1[] = {0,3,1,2,3,0,2,1};
        uint16_t a2[] = {0,7,3,5,1,6,2,4};
        uint16_t a3[] = {15,0,8,7,1,14,9,6};
        size_t l;
        uint8_t *p;
        p = ca_bitpacker_pack(a1, 8, 2, &l); to_hex(p, l, hexbuf); assert(strcmp(hexbuf,"9c63")==0); free(p);
        p = ca_bitpacker_pack(a2, 8, 3, &l); to_hex(p, l, hexbuf); assert(strcmp(hexbuf,"f81a8b")==0); free(p);
        p = ca_bitpacker_pack(a3, 8, 4, &l); to_hex(p, l, hexbuf); assert(strcmp(hexbuf,"0f78e169")==0); free(p);
    }

    /* ── C# wire-format parity: codebook centroids (FP32-exact) ── */
    {
        ca_beta_codebook_t cb;
        assert(ca_beta_codebook_get(2, 8, &cb));
        assert(cb.centroids_len == 4);
        assert(feq32(cb.centroids[0], -0.5048246383666992f));
        assert(feq32(cb.centroids[1], -0.15792210400104523f));
        assert(feq32(cb.centroids[2],  0.15792210400104523f));
        assert(feq32(cb.centroids[3],  0.5048246383666992f));

        ca_beta_codebook_t cb4;
        assert(ca_beta_codebook_get(4, 16, &cb4));
        assert(cb4.centroids_len == 16);
        const float expect4[16] = {
            -0.6039019227027893f, -0.4742901921272278f, -0.37855634093284607f, -0.2978082597255707f,
            -0.2253989577293396f, -0.1580331176519394f, -0.09372113645076752f, -0.031065061688423157f,
             0.031065061688423157f, 0.09372113645076752f, 0.1580331176519394f, 0.2253989577293396f,
             0.2978082597255707f, 0.37855634093284607f, 0.4742901921272278f, 0.6039019227027893f };
        for (int i = 0; i < 16; ++i) assert(feq32(cb4.centroids[i], expect4[i]));
    }

    /* ── C# wire-format parity: payloads (hex + base64 + norm) ── */
    {
        float v8[] = {0.1f,-0.2f,0.3f,-0.4f,0.5f,-0.6f,0.7f,-0.8f};
        size_t l;
        uint8_t *p;

        p = ca_embedding_payload_encode(v8, 8, 2, &l); to_hex(p, l, hexbuf);
        assert(strcmp(hexbuf, "54513301020000000800000011d2b63fd079")==0); free(p);
        p = ca_embedding_payload_encode(v8, 8, 4, &l); to_hex(p, l, hexbuf);
        assert(strcmp(hexbuf, "54513301040000000800000011d2b63f33c7a55e")==0); free(p);

        char *b = ca_embedding_payload_encode_base64(v8, 8, 2);
        assert(strcmp(b, "VFEzAQIAAAAIAAAAEdK2P9B5")==0); free(b);
        b = ca_embedding_payload_encode_base64(v8, 8, 4);
        assert(strcmp(b, "VFEzAQQAAAAIAAAAEdK2PzPHpV4=")==0); free(b);

        ca_turboquant_payload_t tp;
        assert(ca_turboquant_encode(v8, 8, 2, &tp));
        assert(feq32(tp.norm, 1.4282857179641724f));
        ca_turboquant_payload_free(&tp);

        /* tiny 4-dim vector */
        float v4[] = {1,2,3,4};
        p = ca_embedding_payload_encode(v4, 4, 2, &l); to_hex(p, l, hexbuf);
        assert(strcmp(hexbuf, "5451330102000000040000006f45af409c")==0); free(p);
        b = ca_embedding_payload_encode_base64(v4, 4, 2);
        assert(strcmp(b, "VFEzAQIAAAAEAAAAb0WvQJw=")==0); free(b);
        assert(ca_turboquant_encode(v4, 4, 2, &tp));
        assert(feq32(tp.norm, 5.4772257804870605f));
        ca_turboquant_payload_free(&tp);
    }

    /* ── rotation matrix row 0 (dim 8) FP32-exact ── */
    {
        const float *m = ca_orthogonal_rotation_matrix(8);
        const float expect[8] = {
            0.32915404438972473f, -0.15729351341724396f, -0.6576523184776306f, 0.4990078806877136f,
            -0.2985365092754364f, -0.17185114324092865f, 0.024059195071458817f, 0.2572260797023773f };
        for (int i = 0; i < 8; ++i) assert(feq32(m[i], expect[i]));
    }

    /* ── BitPacker round-trip at 1/2/3/4/8 bits ── */
    {
        int bits_list[] = {1,2,3,4,8};
        for (int bi = 0; bi < 5; ++bi) {
            int bits = bits_list[bi];
            int max = (1 << bits) - 1;
            mulberry_seed((uint32_t)(123 + bits));
            uint16_t idx[256];
            for (int i = 0; i < 256; ++i) idx[i] = (uint16_t)((int)(mulberry_next() * (max + 1)));
            size_t l;
            uint8_t *packed = ca_bitpacker_pack(idx, 256, bits, &l);
            assert(packed);
            uint16_t *un = ca_bitpacker_unpack(packed, l, 256, bits);
            assert(un);
            for (int i = 0; i < 256; ++i) assert(un[i] == idx[i]);
            free(packed); free(un);
        }
        /* 1536 @ 2 bits = 384 bytes */
        uint16_t *zeros = (uint16_t *)calloc(1536, sizeof(uint16_t));
        size_t l;
        uint8_t *packed = ca_bitpacker_pack(zeros, 1536, 2, &l);
        assert(l == 384);
        free(packed); free(zeros);
        /* overflow / invalid width rejected */
        uint16_t four[] = {4};
        assert(ca_bitpacker_pack(four, 1, 2, &l) == NULL);
        uint16_t z1[] = {0};
        assert(ca_bitpacker_pack(z1, 1, 0, &l) == NULL);
        assert(ca_bitpacker_pack(z1, 1, 17, &l) == NULL);
    }

    /* ── OrthogonalRotation: preserves norm, round-trips, cached ── */
    {
        int dim = 64;
        float *v = random_unit(dim, 42);
        float r[64];
        ca_orthogonal_rotation_rotate(dim, v, r);
        double sqA=0, sqR=0;
        for (int i=0;i<dim;++i){ sqA += (double)v[i]*v[i]; sqR += (double)r[i]*r[i]; }
        assert(fabs(sqrt(sqR)-sqrt(sqA)) < 1e-3);
        float v2[64];
        ca_orthogonal_rotation_unrotate(dim, r, v2);
        for (int i=0;i<dim;++i) assert(fabs(v2[i]-v[i]) < 1e-3);
        free(v);
        /* cached: same pointer */
        const float *a = ca_orthogonal_rotation_matrix(32);
        const float *b = ca_orthogonal_rotation_matrix(32);
        assert(a == b);
    }

    /* ── BetaLloydMaxCodebook: sizes, monotonic, binFor ── */
    {
        int pairs[4][2] = {{1,16},{2,64},{3,128},{4,256}};
        for (int i=0;i<4;++i){
            ca_beta_codebook_t cb;
            assert(ca_beta_codebook_get(pairs[i][0], pairs[i][1], &cb));
            size_t n = (size_t)1 << pairs[i][0];
            assert(cb.centroids_len == n);
            assert(cb.boundaries_len == n-1);
        }
        ca_beta_codebook_t cb;
        assert(ca_beta_codebook_get(4, 128, &cb));
        for (size_t i=1;i<cb.centroids_len;++i) assert(cb.centroids[i] > cb.centroids[i-1]);
        /* binFor round-trips through boundaries */
        assert(ca_beta_codebook_get(2, 64, &cb));
        for (size_t i=0;i<cb.boundaries_len;++i) {
            assert(ca_beta_codebook_bin_for(cb.boundaries[i]-1e-6f, cb.boundaries, cb.boundaries_len) == (uint16_t)i);
            assert(ca_beta_codebook_bin_for(cb.boundaries[i]+1e-6f, cb.boundaries, cb.boundaries_len) == (uint16_t)(i+1));
        }
    }

    /* ── TurboQuantCodec end-to-end: geometry preserved ── */
    {
        struct { int dim, bits; double floor; } cases[] = {
            {64,4,0.99},{128,4,0.99},{256,3,0.96},{512,2,0.85} };
        for (int c=0;c<4;++c) {
            float *v = random_unit(cases[c].dim, 42);
            ca_turboquant_payload_t tp;
            assert(ca_turboquant_encode(v, (size_t)cases[c].dim, cases[c].bits, &tp));
            float *rec = ca_turboquant_decode(&tp, cases[c].dim, cases[c].bits);
            assert(rec);
            double cos = cosine(v, rec, cases[c].dim);
            assert(cos >= cases[c].floor);
            free(v); free(rec); ca_turboquant_payload_free(&tp);
        }
        /* zero vector → zeros */
        float z[64] = {0};
        ca_turboquant_payload_t tp;
        assert(ca_turboquant_encode(z, 64, 2, &tp));
        float *rec = ca_turboquant_decode(&tp, 64, 2);
        for (int i=0;i<64;++i) assert(rec[i] == 0.0f);
        free(rec); ca_turboquant_payload_free(&tp);
        /* payload size + ratio */
        assert(ca_turboquant_payload_byte_count(1536, 2) == 384);
        assert(ca_turboquant_compression_ratio(1536,2) == 15.835051546391753);
        /* invalid args */
        float v32[32] = {0}; v32[0]=1;
        assert(!ca_turboquant_encode(v32, 32, 0, &tp));
        assert(!ca_turboquant_encode(v32, 32, 9, &tp));
        float one[1] = {1};
        assert(!ca_turboquant_encode(one, 1, 2, &tp));
        /* deterministic across runs */
        float *vv = random_unit(128, 7);
        ca_turboquant_payload_t a, b;
        assert(ca_turboquant_encode(vv, 128, 3, &a));
        assert(ca_turboquant_encode(vv, 128, 3, &b));
        assert(a.norm == b.norm);
        assert(a.packed_len == b.packed_len);
        assert(memcmp(a.packed_indices, b.packed_indices, a.packed_len) == 0);
        free(vv); ca_turboquant_payload_free(&a); ca_turboquant_payload_free(&b);
    }

    /* ── EmbeddingPayloadCodec: round-trip, header, guards ── */
    {
        float *v = random_unit(128, 42);
        size_t el;
        uint8_t *enc = ca_embedding_payload_encode(v, 128, 4, &el);
        assert(enc);
        size_t dl;
        float *dec = ca_embedding_payload_decode(enc, el, &dl);
        assert(dec && dl == 128);
        assert(cosine(v, dec, 128) >= 0.99);
        free(dec);
        assert(ca_embedding_payload_is_encoded(enc, el));
        free(enc); free(v);

        uint8_t not_enc[] = {0,1,2};
        assert(!ca_embedding_payload_is_encoded(not_enc, 3));
        /* too-short payload */
        uint8_t tiny[] = {1,2,3};
        assert(ca_embedding_payload_decode(tiny, 3, &dl) == NULL);
        /* right length, wrong magic */
        uint8_t bad[20] = {0};
        assert(ca_embedding_payload_decode(bad, 20, &dl) == NULL);

        /* base64 round-trip */
        float *v2 = random_unit(64, 7);
        char *b64 = ca_embedding_payload_encode_base64(v2, 64, 3);
        assert(b64);
        float *back = ca_embedding_payload_decode_base64(b64, &dl);
        assert(back && dl == 64);
        assert(cosine(v2, back, 64) >= 0.96);
        free(back); free(b64); free(v2);
    }

    /* ── base64 primitive sanity (RFC 4648) ── */
    {
        char *b = ca_base64_encode((const uint8_t*)"", 0); assert(strcmp(b,"")==0); free(b);
        b = ca_base64_encode((const uint8_t*)"f", 1); assert(strcmp(b,"Zg==")==0); free(b);
        b = ca_base64_encode((const uint8_t*)"fo", 2); assert(strcmp(b,"Zm8=")==0); free(b);
        b = ca_base64_encode((const uint8_t*)"foo", 3); assert(strcmp(b,"Zm9v")==0); free(b);
        b = ca_base64_encode((const uint8_t*)"foobar", 6); assert(strcmp(b,"Zm9vYmFy")==0); free(b);
        size_t dl; uint8_t *d = ca_base64_decode("Zm9vYmFy", &dl);
        assert(dl == 6 && memcmp(d, "foobar", 6) == 0); free(d);
        d = ca_base64_decode("Zg==", &dl); assert(dl==1 && d[0]=='f'); free(d);
    }

    /* ── CompressedEpisodicMemoryStore ── */
    {
        /* stores embedding as a compressed tag, not a float array */
        ca_episodic_store_t *inner = ca_episodic_store_create(1024);
        ca_compressed_episodic_store_t *outer = ca_compressed_episodic_store_create(inner, 2);
        float *emb = random_unit(128, 1);
        ca_episodic_entry_t e = {0};
        e.id = "e1"; e.recorded_at_ms = 1735689600000LL; /* 2026-01-01 */
        e.user_text = "hello"; e.assistant_text = "hi";
        e.embedding = emb; e.embedding_len = 128;
        assert(ca_compressed_episodic_store_add(outer, &e));
        free(emb);

        size_t n;
        ca_episodic_entry_t *raw = ca_episodic_store_get_recent(inner, 1, &n);
        assert(n == 1);
        assert(raw[0].embedding == NULL);
        assert(ca_episodic_entry_get_tag(&raw[0], CA_COMPRESSED_TAG_KEY) != NULL);
        ca_episodic_entry_free_array(raw, n);
        ca_compressed_episodic_store_destroy(outer);
        ca_episodic_store_destroy(inner);
    }
    {
        /* getRecent rehydrates the embedding (cosine ≥ 0.99 at 4-bit) */
        ca_episodic_store_t *inner = ca_episodic_store_create(1024);
        ca_compressed_episodic_store_t *outer = ca_compressed_episodic_store_create(inner, 4);
        float *original = random_unit(64, 1);
        ca_episodic_entry_t e = {0};
        e.id = "e1"; e.recorded_at_ms = 1735689600000LL;
        e.user_text = "u"; e.assistant_text = "a";
        e.embedding = original; e.embedding_len = 64;
        assert(ca_compressed_episodic_store_add(outer, &e));

        size_t n;
        ca_episodic_entry_t *got = ca_compressed_episodic_store_get_recent(outer, 1, &n);
        assert(n == 1);
        assert(got[0].embedding && got[0].embedding_len == 64);
        assert(cosine(original, got[0].embedding, 64) >= 0.99);
        ca_episodic_entry_free_array(got, n);
        free(original);
        ca_compressed_episodic_store_destroy(outer);
        ca_episodic_store_destroy(inner);
    }
    {
        /* search ranks by cosine through compression */
        ca_episodic_store_t *inner = ca_episodic_store_create(1024);
        ca_compressed_episodic_store_t *outer = ca_compressed_episodic_store_create(inner, 4);
        float *v1 = random_unit(64, 1);
        float *v2 = random_unit(64, 2);
        ca_episodic_entry_t e1 = {0}, e2 = {0};
        e1.id="n"; e1.recorded_at_ms=1735689600000LL; e1.user_text="near"; e1.assistant_text="a"; e1.embedding=v1; e1.embedding_len=64;
        e2.id="f"; e2.recorded_at_ms=1735689600001LL; e2.user_text="far";  e2.assistant_text="a"; e2.embedding=v2; e2.embedding_len=64;
        assert(ca_compressed_episodic_store_add(outer, &e1));
        assert(ca_compressed_episodic_store_add(outer, &e2));
        size_t n;
        ca_episodic_entry_t *res = ca_compressed_episodic_store_search(outer, v1, 64, 2, &n);
        assert(n == 2);
        assert(strcmp(res[0].user_text, "near") == 0);
        ca_episodic_entry_free_array(res, n);
        free(v1); free(v2);
        ca_compressed_episodic_store_destroy(outer);
        ca_episodic_store_destroy(inner);
    }
    {
        /* null query returns recency (topK respected) */
        ca_episodic_store_t *inner = ca_episodic_store_create(1024);
        ca_compressed_episodic_store_t *outer = ca_compressed_episodic_store_create(inner, 4);
        float *v1 = random_unit(32, 1);
        float *v2 = random_unit(32, 2);
        ca_episodic_entry_t o = {0}, nw = {0};
        o.id="old"; o.recorded_at_ms=1735689600000LL; o.user_text="old"; o.assistant_text="a"; o.embedding=v1; o.embedding_len=32;
        nw.id="new"; nw.recorded_at_ms=1748736000000LL; nw.user_text="new"; nw.assistant_text="a"; nw.embedding=v2; nw.embedding_len=32;
        assert(ca_compressed_episodic_store_add(outer, &o));
        assert(ca_compressed_episodic_store_add(outer, &nw));
        size_t n;
        ca_episodic_entry_t *res = ca_compressed_episodic_store_search(outer, NULL, 0, 1, &n);
        assert(n == 1);
        assert(strcmp(res[0].user_text, "new") == 0);
        ca_episodic_entry_free_array(res, n);
        free(v1); free(v2);
        ca_compressed_episodic_store_destroy(outer);
        ca_episodic_store_destroy(inner);
    }
    {
        /* entry without embedding passes through unchanged */
        ca_episodic_store_t *inner = ca_episodic_store_create(1024);
        ca_compressed_episodic_store_t *outer = ca_compressed_episodic_store_create(inner, 2);
        ca_episodic_entry_t e = {0};
        e.id="u"; e.recorded_at_ms=1735689600000LL; e.user_text="u"; e.assistant_text="a";
        assert(ca_compressed_episodic_store_add(outer, &e));
        size_t n;
        ca_episodic_entry_t *raw = ca_episodic_store_get_recent(inner, 1, &n);
        assert(n == 1);
        assert(raw[0].embedding == NULL);
        assert(ca_episodic_entry_get_tag(&raw[0], CA_COMPRESSED_TAG_KEY) == NULL);
        ca_episodic_entry_free_array(raw, n);
        ca_compressed_episodic_store_destroy(outer);
        ca_episodic_store_destroy(inner);
        /* invalid bit width rejected */
        ca_episodic_store_t *inner2 = ca_episodic_store_create(1024);
        assert(ca_compressed_episodic_store_create(inner2, 9) == NULL);
        ca_episodic_store_destroy(inner2);
    }

    /* ── CompressedMultimodalMemoryStore ── */
    {
        /* round-trips embedding + metadata (cosine ≥ 0.99 at 4-bit, seed 42) */
        ca_multimodal_store_t *inner = ca_multimodal_store_create();
        ca_compressed_multimodal_store_t *outer = ca_compressed_multimodal_store_create(inner, 4);
        float *emb = random_unit(128, 42);
        ca_multimodal_entry_t e = {0};
        e.id="m1"; e.recorded_at_ms=1735689600000LL; e.modality=CA_MEDIA_IMAGE;
        e.caption="a sunny beach"; e.embedding=emb; e.embedding_len=128;
        e.source_sha256="deadbeef"; e.reference_count=1;
        e.has_width=true; e.width_px=1920; e.has_height=true; e.height_px=1080;
        assert(ca_compressed_multimodal_store_add(outer, &e));
        free(emb);

        ca_multimodal_entry_t got;
        assert(ca_compressed_multimodal_store_get_by_hash(outer, "deadbeef", &got));
        assert(strcmp(got.caption, "a sunny beach") == 0);
        assert(got.has_width && got.width_px == 1920);
        assert(got.has_height && got.height_px == 1080);
        assert(got.embedding && got.embedding_len == 128);
        float *emb_check = random_unit(128, 42);
        assert(cosine(emb_check, got.embedding, 128) >= 0.99);
        free(emb_check);
        ca_multimodal_entry_free(&got);
        ca_compressed_multimodal_store_destroy(outer);
        ca_multimodal_store_destroy(inner);
    }
    {
        /* inner store sees null embedding + compressed tag */
        ca_multimodal_store_t *inner = ca_multimodal_store_create();
        ca_compressed_multimodal_store_t *outer = ca_compressed_multimodal_store_create(inner, 2);
        float *emb = random_unit(64, 1);
        ca_multimodal_entry_t e = {0};
        e.id="m"; e.recorded_at_ms=1735689600000LL; e.modality=CA_MEDIA_IMAGE;
        e.caption="x"; e.embedding=emb; e.embedding_len=64; e.source_sha256="abc"; e.reference_count=1;
        assert(ca_compressed_multimodal_store_add(outer, &e));
        free(emb);
        ca_multimodal_entry_t raw;
        assert(ca_multimodal_store_get_by_hash(inner, "abc", &raw));
        assert(raw.embedding == NULL);
        assert(ca_multimodal_entry_get_tag(&raw, CA_COMPRESSED_TAG_KEY) != NULL);
        ca_multimodal_entry_free(&raw);
        ca_compressed_multimodal_store_destroy(outer);
        ca_multimodal_store_destroy(inner);
    }
    {
        /* search ranks by cosine; reinforce + count delegate through decorator */
        ca_multimodal_store_t *inner = ca_multimodal_store_create();
        ca_compressed_multimodal_store_t *outer = ca_compressed_multimodal_store_create(inner, 4);
        float *v1 = random_unit(64, 1);
        float *v2 = random_unit(64, 2);
        ca_multimodal_entry_t e1 = {0}, e2 = {0};
        e1.id="a"; e1.recorded_at_ms=1735689600000LL; e1.modality=CA_MEDIA_IMAGE; e1.caption="near"; e1.embedding=v1; e1.embedding_len=64; e1.source_sha256="a"; e1.reference_count=1;
        e2.id="b"; e2.recorded_at_ms=1735689600001LL; e2.modality=CA_MEDIA_IMAGE; e2.caption="far";  e2.embedding=v2; e2.embedding_len=64; e2.source_sha256="b"; e2.reference_count=1;
        assert(ca_compressed_multimodal_store_add(outer, &e1));
        assert(ca_compressed_multimodal_store_add(outer, &e2));
        size_t n;
        ca_multimodal_entry_t *res = ca_compressed_multimodal_store_search(outer, v1, 64, 2, &n);
        assert(n == 2);
        assert(strcmp(res[0].caption, "near") == 0);
        ca_multimodal_entry_free_array(res, n);
        free(v1); free(v2);

        /* reinforce path */
        ca_multimodal_store_t *inner2 = ca_multimodal_store_create();
        ca_compressed_multimodal_store_t *outer2 = ca_compressed_multimodal_store_create(inner2, 4);
        float *vx = random_unit(32, 1);
        ca_multimodal_entry_t ex = {0};
        ex.id="x"; ex.recorded_at_ms=1735689600000LL; ex.modality=CA_MEDIA_IMAGE; ex.caption="x"; ex.embedding=vx; ex.embedding_len=32; ex.source_sha256="x"; ex.reference_count=1;
        assert(ca_compressed_multimodal_store_add(outer2, &ex));
        free(vx);
        ca_compressed_multimodal_store_reinforce(outer2, "x");
        ca_multimodal_entry_t gotx;
        assert(ca_compressed_multimodal_store_get_by_hash(outer2, "x", &gotx));
        assert(gotx.reference_count == 2);
        ca_multimodal_entry_free(&gotx);
        assert(ca_compressed_multimodal_store_count(outer2) == 1);
        ca_compressed_multimodal_store_destroy(outer2);
        ca_multimodal_store_destroy(inner2);

        ca_compressed_multimodal_store_destroy(outer);
        ca_multimodal_store_destroy(inner);
    }

    /* leak-clean the internal caches */
    ca_orthogonal_rotation_clear_cache();
    ca_beta_codebook_clear_cache();

    printf("test_compression: all assertions passed\n");
    return 0;
}
