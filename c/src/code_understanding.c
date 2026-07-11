/*
 * code_understanding.c — CircleAI.CodeUnderstanding (C11 port).
 *
 * The indexer keeps symbols per repo path (the filesystem walk + regex is the
 * injected boundary; the host adds the symbols a real FilesystemCodeIndexer
 * would extract). Search matches symbol Name (OrdinalIgnoreCase Contains) and
 * emits "<kind> <name>" snippets. The symbol graph stores edges flat and filters
 * callers/callees by name. Deterministic. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/code_understanding.h"
#include "board_common.h"
#include <stdio.h>

/* ── CodeSymbol ─────────────────────────────────────────────────────────── */

void ca_code_symbol_free(ca_code_symbol_t *s) {
    if (!s) return;
    free(s->path);
    free(s->name);
    free(s->kind);
    s->path = s->name = s->kind = NULL;
}
static bool symbol_copy(ca_code_symbol_t *dst, const ca_code_symbol_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->line = src->line;
    dst->path = cab_strdup_empty(src->path);
    dst->name = cab_strdup_empty(src->name);
    dst->kind = cab_strdup_empty(src->kind);
    if (!dst->path || !dst->name || !dst->kind) { ca_code_symbol_free(dst); return false; }
    return true;
}

/* ── CodeMatch ──────────────────────────────────────────────────────────── */

void ca_code_match_free(ca_code_match_t *m) {
    if (!m) return;
    free(m->path);
    free(m->snippet);
    m->path = m->snippet = NULL;
}
void ca_code_match_free_array(ca_code_match_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_code_match_free(&arr[i]);
    free(arr);
}

/* ── SymbolEdge ─────────────────────────────────────────────────────────── */

void ca_symbol_edge_free(ca_symbol_edge_t *e) {
    if (!e) return;
    ca_code_symbol_free(&e->from);
    ca_code_symbol_free(&e->to);
    free(e->kind);
    e->kind = NULL;
}
void ca_symbol_edge_free_array(ca_symbol_edge_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_symbol_edge_free(&arr[i]);
    free(arr);
}
static bool edge_copy(ca_symbol_edge_t *dst, const ca_symbol_edge_t *src) {
    memset(dst, 0, sizeof(*dst));
    if (!symbol_copy(&dst->from, &src->from)) return false;
    if (!symbol_copy(&dst->to, &src->to)) { ca_symbol_edge_free(dst); return false; }
    dst->kind = cab_strdup_empty(src->kind);
    if (!dst->kind) { ca_symbol_edge_free(dst); return false; }
    return true;
}

/* ── IndexStore (ICodeIndexer) ──────────────────────────────────────────── */

typedef struct {
    char             *repo;    /* owned */
    ca_code_symbol_t *symbols; /* owned */
    size_t            count, cap;
} repo_index_t;

struct ca_code_indexer {
    repo_index_t *repos;
    size_t        count, cap;
};

ca_code_indexer_t *ca_code_indexer_create(void) {
    return (ca_code_indexer_t *)calloc(1, sizeof(ca_code_indexer_t));
}
void ca_code_indexer_destroy(ca_code_indexer_t *idx) {
    if (!idx) return;
    for (size_t i = 0; i < idx->count; ++i) {
        free(idx->repos[i].repo);
        for (size_t j = 0; j < idx->repos[i].count; ++j)
            ca_code_symbol_free(&idx->repos[i].symbols[j]);
        free(idx->repos[i].symbols);
    }
    free(idx->repos);
    free(idx);
}
const char *ca_code_indexer_backend_id(const ca_code_indexer_t *idx) {
    (void)idx; return "index-store";
}

static repo_index_t *repo_find_or_add(ca_code_indexer_t *idx, const char *repo) {
    for (size_t i = 0; i < idx->count; ++i)
        if (cab_ord_eq(idx->repos[i].repo, repo)) return &idx->repos[i];
    if (idx->count == idx->cap) {
        size_t nc = idx->cap ? idx->cap * 2 : 4;
        void *n = realloc(idx->repos, nc * sizeof(repo_index_t));
        if (!n) return NULL;
        idx->repos = (repo_index_t *)n;
        idx->cap = nc;
    }
    repo_index_t *r = &idx->repos[idx->count];
    memset(r, 0, sizeof(*r));
    r->repo = cab_strdup_empty(repo);
    if (!r->repo) return NULL;
    idx->count++;
    return r;
}

int ca_code_indexer_add_symbol(ca_code_indexer_t *idx, const char *repo_path,
                               const ca_code_symbol_t *symbol) {
    if (!idx || cab_is_ws(repo_path) || !symbol) return -1;
    repo_index_t *r = repo_find_or_add(idx, repo_path);
    if (!r) return -1;
    ca_code_symbol_t copy;
    if (!symbol_copy(&copy, symbol)) return -1;
    if (r->count == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 8;
        void *n = realloc(r->symbols, nc * sizeof(ca_code_symbol_t));
        if (!n) { ca_code_symbol_free(&copy); return -1; }
        r->symbols = (ca_code_symbol_t *)n;
        r->cap = nc;
    }
    r->symbols[r->count++] = copy;
    return 0;
}

int ca_code_indexer_count_symbols(const ca_code_indexer_t *idx,
                                  const char *repo_path) {
    if (!idx || cab_is_ws(repo_path)) return -1;
    for (size_t i = 0; i < idx->count; ++i)
        if (cab_ord_eq(idx->repos[i].repo, repo_path))
            return (int)idx->repos[i].count;
    return 0;
}

const char *ca_cu_null_indexer_backend_id(void) { return "null"; }

/* ── IndexBackedCodeSearch ──────────────────────────────────────────────── */

const char *ca_code_search_backend_id(void) { return "index-backed"; }
const char *ca_cu_null_search_backend_id(void) { return "null"; }

ca_code_match_t *ca_code_search(const ca_code_indexer_t *idx, const char *query,
                                int top_k, size_t *out_count) {
    if (!out_count) return NULL;
    if (!idx || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }

    /* first pass: count matches up to top_k (SelectMany over all repos, take k) */
    size_t matched = 0;
    for (size_t i = 0; i < idx->count && matched < (size_t)top_k; ++i) {
        for (size_t j = 0; j < idx->repos[i].count && matched < (size_t)top_k; ++j) {
            if (cab_ci_contains(idx->repos[i].symbols[j].name, query)) matched++;
        }
    }
    if (matched == 0) { *out_count = 0; return NULL; }

    ca_code_match_t *out = (ca_code_match_t *)calloc(matched, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < idx->count && k < matched; ++i) {
        for (size_t j = 0; j < idx->repos[i].count && k < matched; ++j) {
            const ca_code_symbol_t *sym = &idx->repos[i].symbols[j];
            if (!cab_ci_contains(sym->name, query)) continue;
            out[k].path = cab_strdup_empty(sym->path);
            out[k].line = sym->line;
            out[k].score = 1.0f;
            size_t snlen = strlen(sym->kind) + 1 + strlen(sym->name) + 1;
            out[k].snippet = (char *)malloc(snlen);
            if (!out[k].path || !out[k].snippet) {
                ca_code_match_free_array(out, k + 1);
                *out_count = (size_t)-1;
                return NULL;
            }
            snprintf(out[k].snippet, snlen, "%s %s", sym->kind, sym->name);
            k++;
        }
    }
    *out_count = matched;
    return out;
}

ca_code_match_t *ca_code_semantic_search(const ca_code_indexer_t *idx,
                                         const char *query, int top_k,
                                         size_t *out_count) {
    return ca_code_search(idx, query, top_k, out_count);
}

/* ── InMemorySymbolGraph ────────────────────────────────────────────────── */

struct ca_symbol_graph {
    ca_symbol_edge_t *edges;
    size_t            count, cap;
};

ca_symbol_graph_t *ca_symbol_graph_create(void) {
    return (ca_symbol_graph_t *)calloc(1, sizeof(ca_symbol_graph_t));
}
void ca_symbol_graph_destroy(ca_symbol_graph_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->count; ++i) ca_symbol_edge_free(&g->edges[i]);
    free(g->edges);
    free(g);
}
const char *ca_symbol_graph_backend_id(const ca_symbol_graph_t *g) {
    (void)g; return "in-memory";
}

int ca_symbol_graph_link(ca_symbol_graph_t *g, const ca_code_symbol_t *from,
                         const ca_code_symbol_t *to, const char *kind) {
    if (!g || !from || !to) return -1;
    ca_symbol_edge_t edge;
    memset(&edge, 0, sizeof(edge));
    edge.kind = cab_strdup_empty(kind ? kind : "calls");
    if (!edge.kind) return -1;
    if (!symbol_copy(&edge.from, from)) { free(edge.kind); return -1; }
    if (!symbol_copy(&edge.to, to)) { ca_symbol_edge_free(&edge); return -1; }
    if (g->count == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 4;
        void *n = realloc(g->edges, nc * sizeof(*g->edges));
        if (!n) { ca_symbol_edge_free(&edge); return -1; }
        g->edges = (ca_symbol_edge_t *)n;
        g->cap = nc;
    }
    g->edges[g->count++] = edge;
    return 0;
}

/* dir 0 = callers (match To.Name), 1 = callees (match From.Name). */
static ca_symbol_edge_t *graph_filter(const ca_symbol_graph_t *g,
                                      const ca_code_symbol_t *s, int dir,
                                      size_t *out_count) {
    if (!out_count) return NULL;
    if (!g || !s) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < g->count; ++i) {
        const char *nm = dir == 0 ? g->edges[i].to.name : g->edges[i].from.name;
        if (cab_ord_eq(nm, s->name)) n++;
    }
    if (n == 0) { *out_count = 0; return NULL; }
    ca_symbol_edge_t *out = (ca_symbol_edge_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < g->count; ++i) {
        const char *nm = dir == 0 ? g->edges[i].to.name : g->edges[i].from.name;
        if (!cab_ord_eq(nm, s->name)) continue;
        if (!edge_copy(&out[k], &g->edges[i])) {
            ca_symbol_edge_free_array(out, k);
            *out_count = (size_t)-1;
            return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

ca_symbol_edge_t *ca_symbol_graph_callers_of(const ca_symbol_graph_t *g,
                                             const ca_code_symbol_t *s,
                                             size_t *out_count) {
    return graph_filter(g, s, 0, out_count);
}
ca_symbol_edge_t *ca_symbol_graph_callees_of(const ca_symbol_graph_t *g,
                                             const ca_code_symbol_t *s,
                                             size_t *out_count) {
    return graph_filter(g, s, 1, out_count);
}

const char *ca_cu_null_symbol_graph_backend_id(void) { return "null"; }
