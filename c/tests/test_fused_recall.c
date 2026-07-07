/*
 * test_fused_recall.c — FusedRecall: RRF order, cross-source reinforcement,
 * cold-start degradation, confidence gate, empty-query short-circuit, dedup.
 * Mirrors the Rust suite fused_recall_test.rs (and TS/Go) 1:1.
 *
 * Uses function-pointer test doubles: a fake episodic search returning a fixed
 * pre-ranked list, a fake hippo returning a fixed hit list, and a throwing hippo.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* ── Fake episodic: returns a fixed list of user-texts as entries ── */
typedef struct { const char **texts; size_t count; } fake_episodic_t;

static char *tdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1; char *p = (char *)malloc(n); if (p) memcpy(p, s, n); return p;
}

static ca_episodic_entry_t *fake_episodic_search(void *user, const float *qe, size_t ql,
                                                 int top_k, size_t *out_count) {
    (void)qe; (void)ql;
    fake_episodic_t *fe = (fake_episodic_t *)user;
    size_t limit = (size_t)top_k < fe->count ? (size_t)top_k : fe->count;
    *out_count = 0;
    if (limit == 0) return NULL;
    ca_episodic_entry_t *arr = (ca_episodic_entry_t *)calloc(limit, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < limit; ++i) {
        arr[i].id = tdup("id");
        arr[i].user_text = tdup(fe->texts[i]);
        arr[i].assistant_text = tdup("");
        arr[i].recorded_at_ms = 1735689600000LL;
    }
    *out_count = limit;
    return arr;
}

/* ── Fake hippo: returns fixed hits (id,text,optional confidence) ── */
typedef struct { const char *id; const char *text; const char *confidence; } graph_hit_spec_t;
typedef struct { const graph_hit_spec_t *specs; size_t count; } fake_hippo_t;

static ca_memory_hit_t *fake_hippo_recall(void *user, const char *query, int top_k,
                                          size_t *out_count) {
    (void)query;
    fake_hippo_t *fh = (fake_hippo_t *)user;
    size_t limit = (size_t)top_k < fh->count ? (size_t)top_k : fh->count;
    *out_count = 0;
    if (limit == 0) return NULL;
    ca_memory_hit_t *arr = (ca_memory_hit_t *)calloc(limit, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < limit; ++i) {
        arr[i].item.id = tdup(fh->specs[i].id);
        arr[i].item.text = tdup(fh->specs[i].text);
        arr[i].score = 1.0;
        if (fh->specs[i].confidence) {
            char **k = (char **)calloc(1, sizeof(char *));
            char **v = (char **)calloc(1, sizeof(char *));
            k[0] = tdup("confidence");
            v[0] = tdup(fh->specs[i].confidence);
            arr[i].item.meta_keys = k;
            arr[i].item.meta_values = v;
            arr[i].item.meta_count = 1;
        }
    }
    *out_count = limit;
    return arr;
}

/* ── Throwing hippo: signals error via *out_count = SIZE_MAX ── */
static ca_memory_hit_t *throwing_hippo_recall(void *user, const char *query, int top_k,
                                             size_t *out_count) {
    (void)user; (void)query; (void)top_k;
    *out_count = SIZE_MAX;
    return NULL;
}

static bool texts_contain(char **texts, size_t n, const char *needle) {
    for (size_t i = 0; i < n; ++i) if (strcmp(texts[i], needle) == 0) return true;
    return false;
}

/* Collect hit texts into a fresh owned array. */
static char **hit_texts(const ca_memory_hit_t *hits, size_t n) {
    char **t = (char **)calloc(n ? n : 1, sizeof(char *));
    for (size_t i = 0; i < n; ++i) t[i] = tdup(hits[i].item.text);
    return t;
}
static void free_texts(char **t, size_t n) { for (size_t i = 0; i < n; ++i) free(t[i]); free(t); }

int main(void) {
    /* ── a memory surfaced by both sources outranks one from only one ── */
    {
        const char *etexts[] = {"A", "B", "C"};
        fake_episodic_t fe = { etexts, 3 };
        graph_hit_spec_t gspec[] = { {"g", "B", NULL} }; /* reinforces B */
        fake_hippo_t fh = { gspec, 1 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       fake_hippo_recall, &fh, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(n == 3);
        assert(strcmp(t[0], "B") == 0);
        assert(strcmp(t[1], "A") == 0);
        assert(strcmp(t[2], "C") == 0);
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── cold-start (no graph) yields the episodic order unchanged ── */
    {
        const char *etexts[] = {"A", "B", "C"};
        fake_episodic_t fe = { etexts, 3 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       NULL, NULL, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(n == 3);
        assert(strcmp(t[0], "A") == 0 && strcmp(t[1], "B") == 0 && strcmp(t[2], "C") == 0);
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── RRF respects top_k ── */
    {
        const char *etexts[] = {"A", "B", "C"};
        fake_episodic_t fe = { etexts, 3 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe, NULL, NULL, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 2, &n);
        char **t = hit_texts(hits, n);
        assert(n == 2);
        assert(strcmp(t[0], "A") == 0 && strcmp(t[1], "B") == 0);
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── drops graph hits below the confidence threshold ── */
    {
        fake_episodic_t fe = { NULL, 0 };
        graph_hit_spec_t gspec[] = { {"low", "LOW", "0.2"}, {"high", "HIGH", "0.9"} };
        fake_hippo_t fh = { gspec, 2 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       fake_hippo_recall, &fh, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(!texts_contain(t, n, "LOW"));
        assert(texts_contain(t, n, "HIGH"));
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── keeps graph hits that carry no confidence metadata ── */
    {
        fake_episodic_t fe = { NULL, 0 };
        graph_hit_spec_t gspec[] = { {"g", "NOCONF", NULL} };
        fake_hippo_t fh = { gspec, 1 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       fake_hippo_recall, &fh, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(n == 1);
        assert(strcmp(t[0], "NOCONF") == 0);
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── skips the graph entirely for an empty query ── */
    {
        const char *etexts[] = {"A"};
        fake_episodic_t fe = { etexts, 1 };
        graph_hit_spec_t gspec[] = { {"g", "GRAPH", NULL} };
        fake_hippo_t fh = { gspec, 1 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       fake_hippo_recall, &fh, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "   ", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(n == 1);
        assert(strcmp(t[0], "A") == 0);
        assert(!texts_contain(t, n, "GRAPH"));
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── degrades to episodic when the graph errors ── */
    {
        const char *etexts[] = {"A"};
        fake_episodic_t fe = { etexts, 1 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       throwing_hippo_recall, NULL, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        char **t = hit_texts(hits, n);
        assert(n == 1);
        assert(strcmp(t[0], "A") == 0);
        free_texts(t, n);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    /* ── fuses two hits with the same normalised text into one entry ── */
    {
        const char *etexts[] = {"Durban  Weather"};
        fake_episodic_t fe = { etexts, 1 };
        graph_hit_spec_t gspec[] = { {"g", "durban weather", NULL} }; /* same key */
        fake_hippo_t fh = { gspec, 1 };
        ca_fused_recall_t *fr = ca_fused_recall_create(fake_episodic_search, &fe,
                                                       fake_hippo_recall, &fh, NULL);
        size_t n = 0;
        ca_memory_hit_t *hits = ca_fused_recall_recall(fr, "q", NULL, 0, 5, &n);
        assert(n == 1);
        ca_memory_hit_free_array(hits, n);
        ca_fused_recall_destroy(fr);
    }

    printf("All fused recall tests passed.\n");
    return 0;
}
