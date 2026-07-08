/*
 * rag.c — retrieval-augmented context assembly (C11 port).
 *
 * Ported from CircleAI.Memory.RagContextBuilder / RagPipelineBuilder (C#)
 * mirroring the verified TypeScript reference 1:1. Best-effort: any failure
 * degrades to an empty string. Reuses ca_episodic_store_t. Pure C11 + libc.
 */

#include "circle_ai/rag.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <time.h>

/* ── a tiny growable string buffer ── */

typedef struct { char *p; size_t len, cap; } rag_sb;

static bool sb_reserve(rag_sb *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t ncap = b->cap ? b->cap * 2 : 256;
    while (ncap < b->len + extra + 1) ncap *= 2;
    char *n = (char *)realloc(b->p, ncap);
    if (!n) return false;
    b->p = n;
    b->cap = ncap;
    return true;
}

static bool sb_append(rag_sb *b, const char *s) {
    size_t n = strlen(s);
    if (!sb_reserve(b, n)) return false;
    memcpy(b->p + b->len, s, n);
    b->len += n;
    b->p[b->len] = '\0';
    return true;
}

static bool sb_append_n(rag_sb *b, const char *s, size_t n) {
    if (!sb_reserve(b, n)) return false;
    memcpy(b->p + b->len, s, n);
    b->len += n;
    b->p[b->len] = '\0';
    return true;
}

static char *dup_empty(void) {
    char *p = (char *)malloc(1);
    if (p) p[0] = '\0';
    return p;
}

static bool rag_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) if (!isspace((unsigned char)*s)) return false;
    return true;
}

/* Truncate text to maxLen chars, replacing the last kept char with a UTF-8
 * ellipsis (…), matching the C# `text[..(maxLen-1)] + "…"`. Byte-based, which
 * is equivalent for the ASCII inputs the suite uses. Writes into sb. */
static bool rag_append_truncated(rag_sb *b, const char *text, int max_len) {
    if (!text || text[0] == '\0') return true;
    size_t tl = strlen(text);
    if ((int)tl <= max_len) return sb_append(b, text);
    if (max_len <= 0) return true;
    if (!sb_append_n(b, text, (size_t)(max_len - 1))) return false;
    return sb_append(b, "\xE2\x80\xA6"); /* U+2026 … */
}

/* Format a Unix-ms UTC instant as "yyyy-MM-dd HH:mm" into buf (>= 17 bytes). */
static void rag_format_when(int64_t ms, char *buf, size_t buf_len) {
    time_t secs = (time_t)(ms / 1000);
    struct tm tmv;
#if defined(_WIN32)
    gmtime_s(&tmv, &secs);
#else
    gmtime_r(&secs, &tmv);
#endif
    snprintf(buf, buf_len, "%04d-%02d-%02d %02d:%02d",
             tmv.tm_year + 1900, tmv.tm_mon + 1, tmv.tm_mday,
             tmv.tm_hour, tmv.tm_min);
}

/* ===========================================================================
 * RagContextBuilder
 * =========================================================================== */

struct ca_rag_context_builder {
    ca_episodic_store_t *store;        /* borrowed (unless owns_store) */
    bool                 owns_store;   /* set when built from with_in_memory_store */
    ca_text_embedder_fn  embedder;
    void                *embedder_user;
    int                  top_k;
    int                  max_chars_per_entry;
};

ca_rag_context_builder_t *ca_rag_context_builder_create(
    ca_episodic_store_t *store,
    ca_text_embedder_fn embedder, void *embedder_user,
    int top_k, int max_chars_per_entry) {
    if (!store) return NULL;
    ca_rag_context_builder_t *b = (ca_rag_context_builder_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->store = store;
    b->owns_store = false;
    b->embedder = embedder;
    b->embedder_user = embedder_user;
    b->top_k = top_k < 1 ? 1 : top_k;
    b->max_chars_per_entry = max_chars_per_entry < 50 ? 50 : max_chars_per_entry;
    return b;
}

void ca_rag_context_builder_destroy(ca_rag_context_builder_t *b) {
    if (!b) return;
    if (b->owns_store) ca_episodic_store_destroy(b->store);
    free(b);
}

char *ca_rag_context_builder_build(ca_rag_context_builder_t *b, const char *query) {
    if (!b) return dup_empty();
    if (rag_is_blank(query)) return dup_empty();

    /* optional embedding (non-fatal on failure) */
    float *qemb = NULL;
    size_t qlen = 0;
    if (b->embedder) {
        qemb = b->embedder(b->embedder_user, query, &qlen);
        if (!qemb) qlen = 0; /* embedder failure → recency */
    }

    size_t n = 0;
    ca_episodic_entry_t *entries =
        ca_episodic_store_search(b->store, qemb, qlen, b->top_k, &n);
    free(qemb);

    if (n == 0 || !entries) {
        ca_episodic_entry_free_array(entries, n);
        return dup_empty();
    }

    rag_sb sb = {0};
    if (!sb_append(&sb, "[Relevant past exchanges \xE2\x80\x94 for context only]\n")) {
        free(sb.p);
        ca_episodic_entry_free_array(entries, n);
        return dup_empty();
    }

    int half = b->max_chars_per_entry / 2;   /* integer division, like C#/TS */
    for (size_t i = 0; i < n; ++i) {
        const ca_episodic_entry_t *e = &entries[i];
        char when[24];
        rag_format_when(e->recorded_at_ms, when, sizeof(when));

        sb_append(&sb, "\xE2\x80\xA2 ["); /* "• [" */
        sb_append(&sb, when);
        sb_append(&sb, " UTC] ");
        if (!rag_is_blank(e->app_context)) {
            sb_append(&sb, "(");
            sb_append(&sb, e->app_context);
            sb_append(&sb, ") ");
        }
        sb_append(&sb, "User: ");
        rag_append_truncated(&sb, e->user_text, half);
        sb_append(&sb, "\n");
        sb_append(&sb, "  B!: ");
        rag_append_truncated(&sb, e->assistant_text, half);
        sb_append(&sb, "\n");
    }

    ca_episodic_entry_free_array(entries, n);

    if (!sb.p) return dup_empty();
    return sb.p; /* NUL-terminated; caller frees */
}

/* ===========================================================================
 * RagPipelineBuilder
 * =========================================================================== */

struct ca_rag_pipeline_builder {
    ca_episodic_store_t *store;         /* borrowed OR owned (owns_store) */
    bool                 owns_store;
    ca_text_embedder_fn  embedder;
    void                *embedder_user;
    int                  top_k;
    int                  max_chars_per_entry;
};

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_create(void) {
    ca_rag_pipeline_builder_t *pb = (ca_rag_pipeline_builder_t *)calloc(1, sizeof(*pb));
    if (!pb) return NULL;
    pb->top_k = 5;
    pb->max_chars_per_entry = 300;
    return pb;
}

void ca_rag_pipeline_builder_destroy(ca_rag_pipeline_builder_t *pb) {
    if (!pb) return;
    if (pb->owns_store) ca_episodic_store_destroy(pb->store);
    free(pb);
}

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_store(
    ca_rag_pipeline_builder_t *pb, ca_episodic_store_t *store) {
    if (!pb || !store) return NULL;
    if (pb->owns_store) { ca_episodic_store_destroy(pb->store); pb->owns_store = false; }
    pb->store = store;
    return pb;
}

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_in_memory_store(
    ca_rag_pipeline_builder_t *pb) {
    if (!pb) return NULL;
    if (pb->owns_store) { ca_episodic_store_destroy(pb->store); pb->owns_store = false; }
    ca_episodic_store_t *s = ca_episodic_store_create(1024);
    if (!s) return NULL;
    pb->store = s;
    pb->owns_store = true;
    return pb;
}

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_embedder(
    ca_rag_pipeline_builder_t *pb, ca_text_embedder_fn embedder, void *embedder_user) {
    if (!pb || !embedder) return NULL;
    pb->embedder = embedder;
    pb->embedder_user = embedder_user;
    return pb;
}

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_top_k(
    ca_rag_pipeline_builder_t *pb, int top_k) {
    if (!pb || top_k < 1) return NULL;
    pb->top_k = top_k;
    return pb;
}

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_max_chars(
    ca_rag_pipeline_builder_t *pb, int max_chars) {
    if (!pb || max_chars < 50) return NULL;
    pb->max_chars_per_entry = max_chars;
    return pb;
}

ca_rag_context_builder_t *ca_rag_pipeline_builder_build(ca_rag_pipeline_builder_t *pb) {
    if (!pb || !pb->store) return NULL;
    ca_rag_context_builder_t *b = ca_rag_context_builder_create(
        pb->store, pb->embedder, pb->embedder_user, pb->top_k, pb->max_chars_per_entry);
    if (!b) return NULL;
    /* transfer store ownership if the pipeline builder created it */
    if (pb->owns_store) {
        b->owns_store = true;
        pb->owns_store = false;
        pb->store = NULL;
    }
    return b;
}
