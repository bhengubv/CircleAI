#ifndef CIRCLE_AI_CODE_UNDERSTANDING_H
#define CIRCLE_AI_CODE_UNDERSTANDING_H

/*
 * code_understanding.h — CircleAI.CodeUnderstanding (C11 port of Contracts.cs +
 * InMemoryCodeUnderstanding.cs + NullImplementations.cs).
 *
 *   Records : CodeSymbol(Path, Line, Name, Kind);
 *             CodeMatch(Path, Line, Snippet, float Score);
 *             SymbolEdge(From, To, Kind).
 *   Indexer : ICodeIndexer -> IndexStore. The filesystem walk + regex extraction
 *               is the injected boundary; the host adds symbols per repo via
 *               AddSymbol (mirrors what FilesystemCodeIndexer would extract).
 *               CountSymbols(repoPath) returns the count for that repo (0 when
 *               unindexed). BackendId "index-store".
 *   Search  : ICodeSearch -> IndexBackedCodeSearch over an indexer. Search(query,
 *               topK=10) matches symbol Name (OrdinalIgnoreCase Contains), emits
 *               CodeMatch(Path, Line, "<kind> <name>", 1.0), take topK (query
 *               non-null, topK > 0). SemanticSearch falls back to Search.
 *               BackendId "index-backed".
 *   Graph   : ISymbolGraph -> InMemorySymbolGraph. Link(from, to, kind="calls");
 *               CallersOf(s) where edge.To.Name == s.Name; CalleesOf(s) where
 *               edge.From.Name == s.Name. BackendId "in-memory".
 *   Null variants return 0 / empty.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* CodeSymbol(Path, Line, Name, Kind). */
typedef struct {
    char *path; /* owned, non-null */
    int   line;
    char *name; /* owned, non-null */
    char *kind; /* owned, non-null */
} ca_code_symbol_t;

void ca_code_symbol_free(ca_code_symbol_t *s);

/* CodeMatch(Path, Line, Snippet, Score). */
typedef struct {
    char *path;    /* owned, non-null */
    int   line;
    char *snippet; /* owned, non-null */
    float score;
} ca_code_match_t;

void ca_code_match_free(ca_code_match_t *m);
void ca_code_match_free_array(ca_code_match_t *arr, size_t count);

/* SymbolEdge(From, To, Kind). */
typedef struct {
    ca_code_symbol_t from; /* owned */
    ca_code_symbol_t to;   /* owned */
    char            *kind; /* owned, non-null */
} ca_symbol_edge_t;

void ca_symbol_edge_free(ca_symbol_edge_t *e);
void ca_symbol_edge_free_array(ca_symbol_edge_t *arr, size_t count);

/* ── ICodeIndexer -> IndexStore ─────────────────────────────────────────── */

typedef struct ca_code_indexer ca_code_indexer_t;

ca_code_indexer_t *ca_code_indexer_create(void); /* NULL on OOM */
void ca_code_indexer_destroy(ca_code_indexer_t *idx);
const char *ca_code_indexer_backend_id(const ca_code_indexer_t *idx); /* "index-store" */

/* AddSymbol(repoPath, symbol) — appends under repoPath. 0 / -1 on bad args /OOM. */
int ca_code_indexer_add_symbol(ca_code_indexer_t *idx, const char *repo_path,
                               const ca_code_symbol_t *symbol);
/* CountSymbols(repoPath) — count for that repo (0 when unindexed). -1 bad args. */
int ca_code_indexer_count_symbols(const ca_code_indexer_t *idx,
                                  const char *repo_path);

const char *ca_cu_null_indexer_backend_id(void); /* "null" */

/* ── ICodeSearch -> IndexBackedCodeSearch ───────────────────────────────── */

/* Search(query, topK) over the indexer's symbols. NULL + 0 empty; NULL +
 * SIZE_MAX on error (query non-null, top_k > 0). */
ca_code_match_t *ca_code_search(const ca_code_indexer_t *idx, const char *query,
                                int top_k, size_t *out_count);
/* SemanticSearch — falls back to Search. */
ca_code_match_t *ca_code_semantic_search(const ca_code_indexer_t *idx,
                                         const char *query, int top_k,
                                         size_t *out_count);
const char *ca_code_search_backend_id(void); /* "index-backed" */
const char *ca_cu_null_search_backend_id(void); /* "null" */

/* ── ISymbolGraph -> InMemorySymbolGraph ────────────────────────────────── */

typedef struct ca_symbol_graph ca_symbol_graph_t;

ca_symbol_graph_t *ca_symbol_graph_create(void); /* NULL on OOM */
void ca_symbol_graph_destroy(ca_symbol_graph_t *g);
const char *ca_symbol_graph_backend_id(const ca_symbol_graph_t *g); /* "in-memory" */

/* Link(from, to, kind) — kind may be NULL (treated as "calls"). 0 / -1. */
int ca_symbol_graph_link(ca_symbol_graph_t *g, const ca_code_symbol_t *from,
                         const ca_code_symbol_t *to, const char *kind);
/* CallersOf(s) — edges where To.Name == s.Name. NULL + 0 empty; SIZE_MAX error. */
ca_symbol_edge_t *ca_symbol_graph_callers_of(const ca_symbol_graph_t *g,
                                             const ca_code_symbol_t *s,
                                             size_t *out_count);
/* CalleesOf(s) — edges where From.Name == s.Name. */
ca_symbol_edge_t *ca_symbol_graph_callees_of(const ca_symbol_graph_t *g,
                                             const ca_code_symbol_t *s,
                                             size_t *out_count);

const char *ca_cu_null_symbol_graph_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CODE_UNDERSTANDING_H */
