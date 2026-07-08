/*
 * test_embedding_store.c — InMemoryEmbeddingStore + IEmbeddingIndex (C11).
 *
 * Mirrors CircleAI.Embeddings.Local: add/count/dimension, add-with-vector,
 * remove, brute-force cosine search (by text + by vector, top-k ranking),
 * TurboQuant compression round-trip, and .NET-BinaryWriter persistence
 * (save then load preserves documents + search behaviour). Plus the
 * brute-force IEmbeddingIndex (add/count/search/save/load).
 *
 * TurboQuant is lossy, so assertions check RANKING and document identity, not
 * exact float scores.
 */

#include "circle_ai/embedding_store.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <assert.h>
#include <time.h>

#if defined(_WIN32)
  #include <direct.h>
  #define MKDIR(p) _mkdir(p)
  #define SEP "\\"
#else
  #include <sys/stat.h>
  #include <sys/types.h>
  #define MKDIR(p) mkdir((p), 0777)
  #define SEP "/"
#endif

#define DIM 8

/* Deterministic encoder: map text -> a fixed unit vector chosen by first char.
 * 'a' -> e0, 'b' -> e1, etc. so cosine ranking is predictable. */
static float *enc_axis(void *user, const char *text, size_t *out_len) {
    (void)user;
    float *v = (float *)calloc(DIM, sizeof(float));
    if (!v) return NULL;
    int idx = 0;
    if (text && text[0] >= 'a' && text[0] <= 'z') idx = (text[0] - 'a') % DIM;
    v[idx] = 1.0f;
    *out_len = DIM;
    return v;
}

static const char *tmpdir(char *buf, size_t cap, const char *label) {
    const char *base =
#if defined(_WIN32)
        getenv("TEMP");
    if (!base) base = "C:\\Temp";
#else
        "/tmp";
#endif
    static int counter = 0;
    counter++;
    snprintf(buf, cap, "%s%scircleai-c-es-%s-%d-%ld", base, SEP, label, counter, (long)time(NULL));
    MKDIR(buf);
    return buf;
}

static void test_add_count_remove(void) {
    ca_embedding_encoder_t encoder = { DIM, enc_axis, NULL };
    ca_embedding_store_t *s = ca_embedding_store_create(&encoder, 4);
    assert(s);
    assert(ca_embedding_store_dimension(s) == DIM);
    assert(ca_embedding_store_count(s) == 0);

    assert(ca_embedding_store_add(s, "d1", "apple", NULL, 0));
    assert(ca_embedding_store_add(s, "d2", "banana", NULL, 0));
    assert(ca_embedding_store_count(s) == 2);

    /* replace existing id -> count unchanged */
    assert(ca_embedding_store_add(s, "d1", "avocado", NULL, 0));
    assert(ca_embedding_store_count(s) == 2);

    assert(ca_embedding_store_remove(s, "d2"));
    assert(ca_embedding_store_count(s) == 1);
    assert(!ca_embedding_store_remove(s, "nope"));

    ca_embedding_store_destroy(s);
}

static void test_bad_construction(void) {
    ca_embedding_encoder_t encoder = { DIM, enc_axis, NULL };
    assert(ca_embedding_store_create(NULL, 4) == NULL);
    assert(ca_embedding_store_create(&encoder, 0) == NULL);
    assert(ca_embedding_store_create(&encoder, 9) == NULL);
}

static void test_search_ranking(void) {
    ca_embedding_encoder_t encoder = { DIM, enc_axis, NULL };
    ca_embedding_store_t *s = ca_embedding_store_create(&encoder, 4);
    assert(s);

    /* two axis-aligned docs */
    float ax[DIM] = {0}; ax[0] = 1.0f; /* like 'a' */
    float bx[DIM] = {0}; bx[1] = 1.0f; /* like 'b' */
    assert(ca_embedding_store_add_vector(s, "near", "near", NULL, 0, ax, DIM));
    assert(ca_embedding_store_add_vector(s, "far",  "far",  NULL, 0, bx, DIM));

    /* query along e0 -> "near" ranks first */
    size_t n = 0;
    ca_embedding_search_hit_t *hits = ca_embedding_store_search_vector(s, ax, DIM, 2, &n);
    assert(hits && n == 2);
    assert(strcmp(hits[0].document.id, "near") == 0);
    assert(hits[0].score > hits[1].score);
    ca_embedding_search_hits_free(hits, n);

    /* top_k = 1 */
    hits = ca_embedding_store_search_vector(s, ax, DIM, 1, &n);
    assert(hits && n == 1);
    assert(strcmp(hits[0].document.id, "near") == 0);
    ca_embedding_search_hits_free(hits, n);

    /* search by text: "apple" -> e0 -> near */
    hits = ca_embedding_store_search(s, "apple", 1, &n);
    assert(hits && n == 1);
    assert(strcmp(hits[0].document.id, "near") == 0);
    ca_embedding_search_hits_free(hits, n);

    /* dimension mismatch */
    float wrong[3] = {0};
    assert(ca_embedding_store_search_vector(s, wrong, 3, 1, &n) == NULL);

    ca_embedding_store_destroy(s);
}

static void test_metadata_and_persistence(void) {
    ca_embedding_encoder_t encoder = { DIM, enc_axis, NULL };
    ca_embedding_store_t *s = ca_embedding_store_create(&encoder, 4);
    assert(s);

    ca_embedding_meta_t meta[2] = {
        { (char *)"app", (char *)"tgn.bidbaas" },
        { (char *)"lang", (char *)"en" },
    };
    float ax[DIM] = {0}; ax[0] = 1.0f;
    float bx[DIM] = {0}; bx[2] = 1.0f;
    assert(ca_embedding_store_add_vector(s, "doc-a", "alpha text", meta, 2, ax, DIM));
    assert(ca_embedding_store_add_vector(s, "doc-b", "charlie text", NULL, 0, bx, DIM));
    assert(ca_embedding_store_count(s) == 2);

    char dir[1024]; tmpdir(dir, sizeof(dir), "persist");
    char path[1200]; snprintf(path, sizeof(path), "%s%sstore.celq", dir, SEP);
    assert(ca_embedding_store_save(s, path));

    /* load into a fresh store with the SAME encoder + bits */
    ca_embedding_store_t *s2 = ca_embedding_store_create(&encoder, 4);
    assert(s2);
    assert(ca_embedding_store_load(s2, path));
    assert(ca_embedding_store_count(s2) == 2);

    /* search survives: query e0 -> doc-a */
    size_t n = 0;
    ca_embedding_search_hit_t *hits = ca_embedding_store_search_vector(s2, ax, DIM, 1, &n);
    assert(hits && n == 1);
    assert(strcmp(hits[0].document.id, "doc-a") == 0);
    /* metadata restored */
    assert(hits[0].document.metadata_count == 2);
    int seen_app = 0, seen_lang = 0;
    for (size_t i = 0; i < hits[0].document.metadata_count; i++) {
        if (strcmp(hits[0].document.metadata[i].key, "app") == 0 &&
            strcmp(hits[0].document.metadata[i].value, "tgn.bidbaas") == 0) seen_app = 1;
        if (strcmp(hits[0].document.metadata[i].key, "lang") == 0 &&
            strcmp(hits[0].document.metadata[i].value, "en") == 0) seen_lang = 1;
    }
    assert(seen_app && seen_lang);
    assert(strcmp(hits[0].document.text, "alpha text") == 0);
    ca_embedding_search_hits_free(hits, n);

    /* bits mismatch on load -> fail */
    ca_embedding_store_t *s3 = ca_embedding_store_create(&encoder, 2);
    assert(!ca_embedding_store_load(s3, path));
    ca_embedding_store_destroy(s3);

    ca_embedding_store_destroy(s2);
    ca_embedding_store_destroy(s);
}

static void test_index(void) {
    assert(ca_embedding_index_create(0) == NULL);
    ca_embedding_index_t *idx = ca_embedding_index_create(DIM);
    assert(idx);
    assert(ca_embedding_index_dimension(idx) == DIM);
    assert(ca_embedding_index_count(idx) == 0);

    float e0[DIM] = {0}; e0[0] = 1.0f;
    float e1[DIM] = {0}; e1[1] = 1.0f;
    float e2[DIM] = {0}; e2[2] = 1.0f;
    int64_t id0 = ca_embedding_index_add(idx, e0, DIM);
    int64_t id1 = ca_embedding_index_add(idx, e1, DIM);
    int64_t id2 = ca_embedding_index_add(idx, e2, DIM);
    assert(id0 == 0 && id1 == 1 && id2 == 2);
    assert(ca_embedding_index_count(idx) == 3);

    /* length mismatch */
    float wrong[3] = {0};
    assert(ca_embedding_index_add(idx, wrong, 3) == -1);

    /* query e0 -> id0 first with score ~1 */
    size_t n = 0;
    ca_embedding_index_hit_t *hits = ca_embedding_index_search(idx, e0, DIM, 2, &n);
    assert(hits && n == 2);
    assert(hits[0].internal_id == 0);
    assert(fabsf(hits[0].score - 1.0f) < 1e-4f);
    assert(hits[0].score >= hits[1].score);
    free(hits);

    /* save + load */
    char dir[1024]; tmpdir(dir, sizeof(dir), "index");
    char path[1200]; snprintf(path, sizeof(path), "%s%sidx.bin", dir, SEP);
    assert(ca_embedding_index_save(idx, path));

    ca_embedding_index_t *idx2 = ca_embedding_index_create(DIM);
    assert(ca_embedding_index_load(idx2, path));
    assert(ca_embedding_index_count(idx2) == 3);
    hits = ca_embedding_index_search(idx2, e2, DIM, 1, &n);
    assert(hits && n == 1 && hits[0].internal_id == 2);
    free(hits);
    /* next id preserved: a new add continues the sequence */
    int64_t id3 = ca_embedding_index_add(idx2, e0, DIM);
    assert(id3 == 3);
    ca_embedding_index_destroy(idx2);

    ca_embedding_index_destroy(idx);
}

int main(void) {
    test_add_count_remove();
    test_bad_construction();
    test_search_ranking();
    test_metadata_and_persistence();
    test_index();
    printf("test_embedding_store: all assertions passed\n");
    return 0;
}
