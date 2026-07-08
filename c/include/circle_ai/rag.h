#ifndef CIRCLE_AI_RAG_H
#define CIRCLE_AI_RAG_H

/*
 * rag.h — retrieval-augmented context assembly (C11 port).
 *
 * Ported from CircleAI.Memory (C#) and mirroring the verified TypeScript
 * reference (memory/rag.ts) 1:1:
 *   - ITextEmbedder — the semantic-ranking seam (a fn pointer here)
 *   - RagContextBuilder — retrieves the most relevant episodes and formats them
 *     as a compact context block for injection into the B! system prompt
 *   - RagPipelineBuilder — fluent factory with sensible defaults
 *
 * RAG is strictly best-effort: any retrieval / embedding failure degrades to an
 * empty string and must never block inference.
 *
 * Reuses ca_episodic_entry_t + ca_episodic_store_t (memory_brain.h) as the
 * episodic source. In-memory only. Pure C11 + libc; -lm via the store.
 */

#include <stddef.h>
#include <stdbool.h>

#include "memory_brain.h"   /* ca_episodic_store_t, ca_episodic_entry_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * ITextEmbedder seam
 * ===========================================================================
 *
 * Produce an embedding for text. Return a freshly malloc'd float array (the
 * builder frees it) and set *out_len; return NULL to signal failure (the
 * builder degrades to recency ranking). user is passed through untouched.
 */
typedef float *(*ca_text_embedder_fn)(void *user, const char *text, size_t *out_len);

/* ===========================================================================
 * RagContextBuilder
 * =========================================================================== */

typedef struct ca_rag_context_builder ca_rag_context_builder_t;

/*
 * Create a builder over an episodic store (borrowed — kept alive by the caller).
 * embedder may be NULL (recency ranking). top_k is floored at 1 (0/neg → 5 is
 * NOT applied here; the C#/TS floor to max(1,topK) — pass 5 for the default).
 * max_chars_per_entry is floored at 50. Returns NULL on a NULL store.
 *
 * To mirror the C#/TS defaults, pass top_k=5, max_chars_per_entry=300.
 */
ca_rag_context_builder_t *ca_rag_context_builder_create(
    ca_episodic_store_t *store /* borrowed */,
    ca_text_embedder_fn embedder, void *embedder_user,
    int top_k, int max_chars_per_entry);
void ca_rag_context_builder_destroy(ca_rag_context_builder_t *b);

/*
 * Build a context block for query. Returns a freshly malloc'd NUL-terminated
 * string the caller frees with free(). Returns an empty string (a malloc'd "")
 * when query is blank, the store is empty, or retrieval fails — RAG never
 * throws. Never returns NULL except on allocation failure.
 */
char *ca_rag_context_builder_build(ca_rag_context_builder_t *b, const char *query);

/* ===========================================================================
 * RagPipelineBuilder — fluent factory
 * =========================================================================== */

typedef struct ca_rag_pipeline_builder ca_rag_pipeline_builder_t;

ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_create(void);
void                       ca_rag_pipeline_builder_destroy(ca_rag_pipeline_builder_t *pb);

/* Set the episodic store (borrowed). Returns pb for chaining (NULL on error). */
ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_store(
    ca_rag_pipeline_builder_t *pb, ca_episodic_store_t *store /* borrowed */);

/* Create + own an in-memory episodic store (capacity 1024) and use it. The
 * built builder does NOT own it; the pipeline builder owns it and frees it on
 * destroy UNLESS ownership was transferred by build() — see below. */
ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_in_memory_store(
    ca_rag_pipeline_builder_t *pb);

/* Set the embedder seam. Returns pb (NULL on error). */
ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_embedder(
    ca_rag_pipeline_builder_t *pb, ca_text_embedder_fn embedder, void *embedder_user);

/* topK (>=1, else NULL). */
ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_top_k(
    ca_rag_pipeline_builder_t *pb, int top_k);

/* maxCharsPerEntry (>=50, else NULL). */
ca_rag_pipeline_builder_t *ca_rag_pipeline_builder_with_max_chars(
    ca_rag_pipeline_builder_t *pb, int max_chars);

/*
 * Build the RagContextBuilder. Returns NULL if no store was configured. If the
 * pipeline builder created an in-memory store, ownership of that store transfers
 * to the returned builder, which frees it on ca_rag_context_builder_destroy.
 */
ca_rag_context_builder_t *ca_rag_pipeline_builder_build(ca_rag_pipeline_builder_t *pb);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_RAG_H */
