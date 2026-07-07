#ifndef CIRCLE_AI_MEMORY_BRAIN_H
#define CIRCLE_AI_MEMORY_BRAIN_H

/*
 * memory_brain.h — CircleAI memory-brain (C11 port).
 *
 * In-memory episodic store, personal knowledge graph, HippoRAG multi-hop recall
 * (Personalised PageRank), Reciprocal-Rank-Fusion recall, and the heuristic
 * knowledge-graph extractor. Ported from the C# reference (CircleAI.Memory /
 * CircleAI.Domain / CircleAI.Companion) and mirroring the Swift / Rust / Go /
 * TypeScript ports 1:1. No database — dynamic arrays with linear search; the test
 * graphs are tiny.
 *
 * Memory ownership contract (uniform across this header):
 *   - Every struct that owns strings owns COPIES made with strdup; each has a
 *     matching *_free / *_destroy that frees them. NULL-safe.
 *   - Functions that RETURN arrays of owning structs hand the caller a freshly
 *     allocated deep copy (mirrors Swift/Rust returning an owned Array/Vec). The
 *     caller frees the array with the documented *_free_array helper.
 *   - Input structs passed BY POINTER are copied internally; the caller keeps
 *     ownership of what it passed and may free it after the call returns.
 *
 * Pure C11 + libc. Links against -lm.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Shared recall currency — MemoryItem / MemoryHit
 * ===========================================================================
 *
 * A MemoryItem carries optional string metadata as parallel key/value arrays
 * (linear search; the sets are tiny). metadata_count == 0 means "no metadata".
 */

typedef struct {
    char   *id;             /* owned */
    char   *text;           /* owned */
    char  **meta_keys;      /* owned array of owned strings, or NULL */
    char  **meta_values;    /* owned array of owned strings, or NULL */
    size_t  meta_count;
} ca_memory_item_t;

typedef struct {
    ca_memory_item_t item;  /* owned */
    double           score;
} ca_memory_hit_t;

/* Deep-free the contents of a single item / hit (does NOT free the struct). */
void ca_memory_item_free(ca_memory_item_t *item);
void ca_memory_hit_free(ca_memory_hit_t *hit);

/* Free an array of hits (each hit's contents + the array itself). NULL-safe. */
void ca_memory_hit_free_array(ca_memory_hit_t *hits, size_t count);

/* Look up a metadata value by key (case-sensitive). Returns a borrowed pointer
 * into the item, or NULL if absent. */
const char *ca_memory_item_get_meta(const ca_memory_item_t *item, const char *key);

/* ===========================================================================
 * Knowledge graph — nodes + triples
 * =========================================================================== */

typedef struct {
    char   *id;             /* owned */
    char   *kind;           /* owned */
    char   *name;           /* owned */
    char  **prop_keys;      /* owned array, or NULL */
    char  **prop_values;    /* owned array, or NULL */
    size_t  prop_count;
} ca_knowledge_node_t;

typedef struct {
    char   *subject;        /* owned */
    char   *predicate;      /* owned */
    char   *object;         /* owned */
    char   *source;         /* owned, or NULL */
    double  confidence;
    int64_t recorded_at_ms; /* Unix ms UTC */
} ca_knowledge_triple_t;

void ca_knowledge_node_free(ca_knowledge_node_t *node);
void ca_knowledge_triple_free(ca_knowledge_triple_t *triple);
void ca_knowledge_triple_free_array(ca_knowledge_triple_t *triples, size_t count);

/* Opaque in-memory knowledge graph. Triples keyed by (subject,predicate,object):
 * re-adding the same triple replaces its provenance (INSERT OR REPLACE). */
typedef struct ca_knowledge_graph ca_knowledge_graph_t;

ca_knowledge_graph_t *ca_knowledge_graph_create(void);
void                  ca_knowledge_graph_destroy(ca_knowledge_graph_t *kg);

/* Insert or replace a node by id. Copies all fields. Returns true on success,
 * false on a NULL/blank id. */
bool ca_knowledge_graph_upsert_node(ca_knowledge_graph_t *kg,
                                    const char *id, const char *kind, const char *name,
                                    const char *const *prop_keys,
                                    const char *const *prop_values,
                                    size_t prop_count);

/* Fetch a node by id into *out (deep copy the caller must ca_knowledge_node_free).
 * Returns true if found. */
bool ca_knowledge_graph_get_node(const ca_knowledge_graph_t *kg, const char *id,
                                 ca_knowledge_node_t *out);

/* Add or replace a triple with provenance. source may be NULL. confidence must
 * be in [0,1] and all of s/p/o non-empty, else returns false. */
bool ca_knowledge_graph_add_triple(ca_knowledge_graph_t *kg,
                                   const char *subject, const char *predicate,
                                   const char *object, const char *source,
                                   double confidence);

/* Return a deep copy of every triple. *out_count set to the length. Caller frees
 * with ca_knowledge_triple_free_array. Returns NULL when empty (count 0). */
ca_knowledge_triple_t *ca_knowledge_graph_all_triples(const ca_knowledge_graph_t *kg,
                                                      size_t *out_count);

/* Return a deep copy of the triples whose subject == subject. */
ca_knowledge_triple_t *ca_knowledge_graph_read_triples(const ca_knowledge_graph_t *kg,
                                                       const char *subject,
                                                       size_t *out_count);

/* ===========================================================================
 * HippoRAG store — Personalised PageRank multi-hop recall
 * ===========================================================================
 *
 * Wraps a knowledge graph (borrowed, NOT owned — the caller keeps it alive for
 * the store's lifetime). Defaults: walk_iterations 32, damping 0.85.
 */

typedef struct ca_hippo_store ca_hippo_store_t;

ca_hippo_store_t *ca_hippo_store_create(ca_knowledge_graph_t *kg /* borrowed */);
ca_hippo_store_t *ca_hippo_store_create_tuned(ca_knowledge_graph_t *kg /* borrowed */,
                                              int walk_iterations, double damping);
void              ca_hippo_store_destroy(ca_hippo_store_t *store);

const char *ca_hippo_store_backend_id(const ca_hippo_store_t *store);

/* Ensure a memory item exists as a graph node (adds memory_text + metadata
 * triples). Returns true on success. */
bool ca_hippo_store_index(ca_hippo_store_t *store, const ca_memory_item_t *item);

/* Seed a Personalised PageRank walk from the query's terms; return up to top_k
 * reached nodes (seeds excluded) as a fresh hit array (caller frees with
 * ca_memory_hit_free_array). *out_count set to the length; returns NULL when
 * there are no results (count 0). Returns NULL and sets *out_count to SIZE_MAX
 * on an invalid argument (blank query or top_k <= 0). */
ca_memory_hit_t *ca_hippo_store_multi_hop_recall(ca_hippo_store_t *store,
                                                 const char *query, int top_k,
                                                 size_t *out_count);

/* ===========================================================================
 * Episodic memory entry + in-memory store
 * =========================================================================== */

typedef struct {
    char   *id;             /* owned; UUID or label */
    int64_t recorded_at_ms; /* Unix ms UTC */
    char   *user_text;      /* owned */
    char   *assistant_text; /* owned */
    char   *app_context;    /* owned, or NULL */
    float  *embedding;      /* owned, or NULL; L2-normalised */
    size_t  embedding_len;
} ca_episodic_entry_t;

/* Free the contents of an entry (not the struct). NULL-safe. */
void ca_episodic_entry_free(ca_episodic_entry_t *entry);
void ca_episodic_entry_free_array(ca_episodic_entry_t *entries, size_t count);

typedef struct ca_episodic_store ca_episodic_store_t;

/* Create a store capped at max_entries (FIFO eviction). max_entries must be > 0,
 * else returns NULL. */
ca_episodic_store_t *ca_episodic_store_create(size_t max_entries);
void                 ca_episodic_store_destroy(ca_episodic_store_t *store);

/* Append a copy of entry, evicting the oldest once over capacity. Returns true. */
bool ca_episodic_store_add(ca_episodic_store_t *store, const ca_episodic_entry_t *entry);

/* Cosine (== dot, both L2-normalised) top_k search. When query_embedding is NULL
 * or query_len == 0, falls back to recency (newest-first). Only entries whose
 * embedding dimension matches the query participate in cosine ranking. Returns a
 * fresh deep-copied array (caller frees with ca_episodic_entry_free_array). */
ca_episodic_entry_t *ca_episodic_store_search(const ca_episodic_store_t *store,
                                              const float *query_embedding,
                                              size_t query_len, int top_k,
                                              size_t *out_count);

/* Most-recent count entries, newest-first. */
ca_episodic_entry_t *ca_episodic_store_get_recent(const ca_episodic_store_t *store,
                                                  int count, size_t *out_count);

size_t ca_episodic_store_count(const ca_episodic_store_t *store);

/* Remove entries recorded strictly before cutoff_ms; return the number removed. */
size_t ca_episodic_store_prune_older_than(ca_episodic_store_t *store, int64_t cutoff_ms);

/* ===========================================================================
 * Episodic search seam (for FusedRecall test doubles)
 * ===========================================================================
 *
 * FusedRecall consumes episodic results through a function pointer so tests can
 * inject a pre-ranked fake. The callback returns a fresh deep-copied entry array
 * (the fusion frees it with ca_episodic_entry_free_array) and sets *out_count.
 */

typedef ca_episodic_entry_t *(*ca_episodic_search_fn)(
    void *user, const float *query_embedding, size_t query_len, int top_k,
    size_t *out_count);

/* Adapter so a concrete ca_episodic_store_t can be used as the search seam. */
ca_episodic_entry_t *ca_episodic_store_search_adapter(
    void *user /* ca_episodic_store_t* */, const float *query_embedding,
    size_t query_len, int top_k, size_t *out_count);

/* ===========================================================================
 * HippoRAG recall seam (for FusedRecall test doubles)
 * ===========================================================================
 *
 * Returns a fresh hit array (fusion frees it) and sets *out_count. To signal an
 * error (degrade-to-episodic), set *out_count to SIZE_MAX and return NULL.
 */

typedef ca_memory_hit_t *(*ca_hippo_recall_fn)(
    void *user, const char *query, int top_k, size_t *out_count);

/* Adapter so a concrete ca_hippo_store_t can be used as the recall seam. */
ca_memory_hit_t *ca_hippo_store_recall_adapter(
    void *user /* ca_hippo_store_t* */, const char *query, int top_k,
    size_t *out_count);

/* ===========================================================================
 * Fused recall — Reciprocal Rank Fusion
 * ===========================================================================
 *
 * Defaults: candidate_pool_size 20, rrf_k 60, graph_confidence_threshold 0.4.
 * The graph seam is optional (NULL fn == cold-start → pure episodic).
 */

typedef struct {
    int    candidate_pool_size; /* 0 → default 20 */
    int    rrf_k;               /* 0 → default 60 */
    double graph_confidence_threshold; /* 0 → default 0.4 */
} ca_fused_recall_options_t;

typedef struct ca_fused_recall ca_fused_recall_t;

/* Create a fused-recall engine over an episodic search seam and an optional graph
 * recall seam. Any seam's user pointer is borrowed. opts may be NULL (defaults);
 * any zero field falls back to its default. */
ca_fused_recall_t *ca_fused_recall_create(
    ca_episodic_search_fn episodic_fn, void *episodic_user,
    ca_hippo_recall_fn graph_fn, void *graph_user,
    const ca_fused_recall_options_t *opts);
void ca_fused_recall_destroy(ca_fused_recall_t *fr);

/* Recall the top_k most relevant memories. query drives the graph; the embedding
 * drives episodic cosine (NULL → recency). Returns a fresh hit array (caller
 * frees with ca_memory_hit_free_array). Returns NULL + *out_count == SIZE_MAX on
 * an invalid argument (top_k <= 0). */
ca_memory_hit_t *ca_fused_recall_recall(ca_fused_recall_t *fr,
                                        const char *query,
                                        const float *query_embedding,
                                        size_t query_len, int top_k,
                                        size_t *out_count);

/* ===========================================================================
 * Knowledge-graph extractor — turn → triples
 * ===========================================================================
 *
 * The heuristic extractor is model-free and stateless, so it is exposed as a
 * plain function plus a function-pointer seam the encoder consumes.
 */

/* Extract bidirectional mentions/seenin triples from a turn. source_episode_id
 * may be NULL (falls back to user_text as the memory id). Returns a fresh triple
 * array (caller frees with ca_knowledge_triple_free_array); *out_count set. On an
 * internal error a NULL return with *out_count == SIZE_MAX signals failure (the
 * heuristic extractor never fails; test doubles use this to exercise the drain). */
ca_knowledge_triple_t *ca_kg_extract_from_turn(const char *user_text,
                                               const char *assistant_text,
                                               const char *source_episode_id,
                                               size_t *out_count);

/* The extractor seam consumed by the encoder. */
typedef ca_knowledge_triple_t *(*ca_kg_extractor_fn)(
    void *user, const char *user_text, const char *assistant_text,
    const char *source_episode_id, size_t *out_count);

/* Adapter wrapping the heuristic extractor as a seam (user is ignored). */
ca_knowledge_triple_t *ca_kg_extractor_heuristic_adapter(
    void *user, const char *user_text, const char *assistant_text,
    const char *source_episode_id, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MEMORY_BRAIN_H */
