/*
 * test_rag.c — RagContextBuilder + RagPipelineBuilder (C11).
 *
 * Mirrors the verified TypeScript rag.test.ts: empty/blank query, empty store,
 * formatting (UTC timestamp, User/B! labels, app context, truncation), the
 * embedder ranking path, embedder-failure fallback, and the fluent builder.
 *
 * The C# "store throws" resilience test has no C analogue (no exceptions); the
 * embedder-returns-NULL fallback exercises the same degrade-to-recency path.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* 2026-06-01T11:00:00Z in Unix ms */
#define MS_2026_06_01_1100  1780311600000LL
/* 2026-06-01T09:05:00Z */
#define MS_2026_06_01_0905  1780304700000LL

static int count_occurrences(const char *text, const char *token) {
    int count = 0;
    const char *p = text;
    size_t tl = strlen(token);
    while ((p = strstr(p, token)) != NULL) { count++; p += tl; }
    return count;
}

/* Embedder that maps any query to the x-axis [1,0]. */
static float *embed_xaxis(void *user, const char *text, size_t *out_len) {
    (void)user; (void)text;
    float *v = (float *)malloc(2 * sizeof(float));
    v[0] = 1.0f; v[1] = 0.0f;
    *out_len = 2;
    return v;
}

/* Embedder that "throws" — returns NULL → recency fallback. */
static float *embed_fail(void *user, const char *text, size_t *out_len) {
    (void)user; (void)text; (void)out_len;
    return NULL;
}

static ca_episodic_entry_t entry(const char *id, int64_t ms, const char *u,
                                 const char *a, const char *ctx,
                                 const float *emb, size_t emblen) {
    ca_episodic_entry_t e = {0};
    e.id = (char *)id;
    e.recorded_at_ms = ms;
    e.user_text = (char *)u;
    e.assistant_text = (char *)a;
    e.app_context = (char *)ctx;
    e.embedding = (float *)emb;
    e.embedding_len = emblen;
    return e;
}

int main(void) {
    /* ── constructor guard ── */
    assert(ca_rag_context_builder_create(NULL, NULL, NULL, 5, 300) == NULL);

    /* ── empty / blank query ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 5, 300);
        char *r1 = ca_rag_context_builder_build(b, "");
        assert(strcmp(r1, "") == 0); free(r1);
        char *r2 = ca_rag_context_builder_build(b, "   ");
        assert(strcmp(r2, "") == 0); free(r2);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── empty store ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 5, 300);
        char *r = ca_rag_context_builder_build(b, "hello");
        assert(strcmp(r, "") == 0); free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── formatting: header + both texts ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_episodic_entry_t e = entry("e1", MS_2026_06_01_1100,
            "What is SDPKT?", "SDPKT is the TGN wallet.", NULL, NULL, 0);
        ca_episodic_store_add(store, &e);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 3, 300);
        char *r = ca_rag_context_builder_build(b, "tell me about the wallet");
        assert(strcmp(r, "") != 0);
        assert(strstr(r, "What is SDPKT?"));
        assert(strstr(r, "SDPKT is the TGN wallet."));
        assert(strstr(r, "[Relevant past exchanges"));
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── formatting: UTC timestamp + User/B! labels ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_episodic_entry_t e = entry("e1", MS_2026_06_01_0905, "q", "r", NULL, NULL, 0);
        ca_episodic_store_add(store, &e);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 1, 300);
        char *r = ca_rag_context_builder_build(b, "anything");
        assert(strstr(r, "[2026-06-01 09:05 UTC]"));
        assert(strstr(r, "User: q"));
        assert(strstr(r, "B!: r"));
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── respects topK (counts bullet prefixes "• [") ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        for (int i = 0; i < 10; ++i) {
            char u[32], a[32], id[16];
            snprintf(u, sizeof(u), "question %d", i);
            snprintf(a, sizeof(a), "answer %d", i);
            snprintf(id, sizeof(id), "e%d", i);
            ca_episodic_entry_t e = entry(id, 1000 + i, u, a, NULL, NULL, 0);
            ca_episodic_store_add(store, &e);
        }
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 2, 300);
        char *r = ca_rag_context_builder_build(b, "any question");
        assert(count_occurrences(r, "\xE2\x80\xA2 [") == 2); /* "• [" */
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── includes app context ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_episodic_entry_t e = entry("e1", 1000, "bid query", "bid answer", "tgn.bidbaas", NULL, 0);
        ca_episodic_store_add(store, &e);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 3, 300);
        char *r = ca_rag_context_builder_build(b, "bidding");
        assert(strstr(r, "tgn.bidbaas"));
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── truncates long texts to half-budget with ellipsis ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        char longText[501];
        memset(longText, 'x', 500); longText[500] = '\0';
        ca_episodic_entry_t e = entry("e1", 1000, longText, "a", NULL, NULL, 0);
        ca_episodic_store_add(store, &e);
        /* maxCharsPerEntry 100 → half 50 → truncate to 49 chars + "…" */
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, NULL, NULL, 1, 100);
        char *r = ca_rag_context_builder_build(b, "q");
        char x49[64]; memset(x49, 'x', 49); x49[49] = '\0';
        char needle[80]; snprintf(needle, sizeof(needle), "%s\xE2\x80\xA6", x49); /* 49x + … */
        assert(strstr(r, needle));
        char x51[64]; memset(x51, 'x', 51); x51[51] = '\0';
        assert(strstr(r, x51) == NULL);
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── embedder ranking path ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        float near[] = {1.0f, 0.0f};
        float far[]  = {0.0f, 1.0f};
        ca_episodic_entry_t en = entry("near", 1000, "near", "n", NULL, near, 2);
        ca_episodic_entry_t ef = entry("far", 1001, "far", "f", NULL, far, 2);
        ca_episodic_store_add(store, &en);
        ca_episodic_store_add(store, &ef);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, embed_xaxis, NULL, 1, 300);
        char *r = ca_rag_context_builder_build(b, "anything");
        assert(strstr(r, "near"));
        assert(strstr(r, "far") == NULL);
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── embedder throws → recency fallback (still best-effort) ── */
    {
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_episodic_entry_t e = entry("only", 1000, "only", "entry", NULL, NULL, 0);
        ca_episodic_store_add(store, &e);
        ca_rag_context_builder_t *b = ca_rag_context_builder_create(store, embed_fail, NULL, 3, 300);
        char *r = ca_rag_context_builder_build(b, "q");
        assert(strstr(r, "only"));
        free(r);
        ca_rag_context_builder_destroy(b);
        ca_episodic_store_destroy(store);
    }

    /* ── RagPipelineBuilder ── */
    {
        /* build from a store, produce a working builder */
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        ca_episodic_entry_t e = entry("e1", 1000, "hi", "hello", NULL, NULL, 0);
        ca_episodic_store_add(store, &e);
        ca_rag_pipeline_builder_t *pb = ca_rag_pipeline_builder_create();
        assert(ca_rag_pipeline_builder_with_store(pb, store));
        assert(ca_rag_pipeline_builder_with_top_k(pb, 2));
        assert(ca_rag_pipeline_builder_with_max_chars(pb, 500));
        ca_rag_context_builder_t *rag = ca_rag_pipeline_builder_build(pb);
        assert(rag);
        char *ctx = ca_rag_context_builder_build(rag, "greeting");
        assert(strstr(ctx, "hi"));
        free(ctx);
        ca_rag_context_builder_destroy(rag);
        ca_rag_pipeline_builder_destroy(pb);
        ca_episodic_store_destroy(store);
    }
    {
        /* withInMemoryStore wires a fresh (empty) store */
        ca_rag_pipeline_builder_t *pb = ca_rag_pipeline_builder_create();
        assert(ca_rag_pipeline_builder_with_in_memory_store(pb));
        ca_rag_context_builder_t *rag = ca_rag_pipeline_builder_build(pb);
        assert(rag);
        char *ctx = ca_rag_context_builder_build(rag, "nothing stored");
        assert(strcmp(ctx, "") == 0);
        free(ctx);
        ca_rag_context_builder_destroy(rag); /* frees the owned in-memory store */
        ca_rag_pipeline_builder_destroy(pb);
    }
    {
        /* build without a store → NULL */
        ca_rag_pipeline_builder_t *pb = ca_rag_pipeline_builder_create();
        assert(ca_rag_pipeline_builder_build(pb) == NULL);
        ca_rag_pipeline_builder_destroy(pb);
    }
    {
        /* withTopK < 1 rejected, withMaxCharsPerEntry < 50 rejected */
        ca_rag_pipeline_builder_t *pb = ca_rag_pipeline_builder_create();
        assert(ca_rag_pipeline_builder_with_top_k(pb, 0) == NULL);
        assert(ca_rag_pipeline_builder_with_max_chars(pb, 49) == NULL);
        ca_rag_pipeline_builder_destroy(pb);
    }
    {
        /* withEmbedder wires the semantic-ranking seam */
        ca_episodic_store_t *store = ca_episodic_store_create(1024);
        float near[] = {1.0f, 0.0f};
        float far[]  = {0.0f, 1.0f};
        ca_episodic_entry_t en = entry("near", 1000, "near", "n", NULL, near, 2);
        ca_episodic_entry_t ef = entry("far", 1001, "far", "f", NULL, far, 2);
        ca_episodic_store_add(store, &en);
        ca_episodic_store_add(store, &ef);
        ca_rag_pipeline_builder_t *pb = ca_rag_pipeline_builder_create();
        ca_rag_pipeline_builder_with_store(pb, store);
        ca_rag_pipeline_builder_with_embedder(pb, embed_xaxis, NULL);
        ca_rag_pipeline_builder_with_top_k(pb, 1);
        ca_rag_context_builder_t *rag = ca_rag_pipeline_builder_build(pb);
        char *ctx = ca_rag_context_builder_build(rag, "q");
        assert(strstr(ctx, "near"));
        free(ctx);
        ca_rag_context_builder_destroy(rag);
        ca_rag_pipeline_builder_destroy(pb);
        ca_episodic_store_destroy(store);
    }

    printf("test_rag: all assertions passed\n");
    return 0;
}
