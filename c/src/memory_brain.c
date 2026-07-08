/*
 * memory_brain.c — CircleAI memory-brain (C11 port).
 *
 * Episodic store, knowledge graph, HippoRAG (Personalised PageRank), fused RRF
 * recall, and the heuristic KG extractor. Ported from the C# reference and
 * mirroring the Swift/Rust/Go/TS ports 1:1. In-memory; dynamic arrays + linear
 * search (the graphs are tiny). Pure C11 + libc, links -lm.
 */

#include "circle_ai/memory_brain.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <time.h>

/* ===========================================================================
 * Small shared helpers
 * =========================================================================== */

/* strdup is POSIX; provide a portable duplicate so we do not depend on it. */
static char *ca_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* Millisecond wall clock (best-effort; only ordering / recency matters). */
static int64_t ca_now_ms(void) {
    return (int64_t)time(NULL) * 1000;
}

/* ASCII lowercase in place. */
static void ca_lower_inplace(char *s) {
    for (; s && *s; ++s) *s = (char)tolower((unsigned char)*s);
}

/* Trim leading/trailing ASCII whitespace; returns true if the result is empty. */
static bool ca_is_blank(const char *s) {
    if (!s) return true;
    for (; *s; ++s) {
        if (!isspace((unsigned char)*s)) return false;
    }
    return true;
}

/* Case-insensitive equality (ASCII). NULLs compare as unequal unless both NULL. */
static bool ca_eq_ci(const char *a, const char *b) {
    if (a == b) return true;
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        ++a; ++b;
    }
    return *a == *b;
}

/* Duplicate a parallel key/value string array (deep). Sets out arrays; on count 0
 * they are set to NULL. Returns false on allocation failure. */
static bool ca_dup_kv(const char *const *keys, const char *const *vals, size_t count,
                      char ***out_keys, char ***out_vals) {
    *out_keys = NULL;
    *out_vals = NULL;
    if (count == 0) return true;
    char **k = (char **)calloc(count, sizeof(char *));
    char **v = (char **)calloc(count, sizeof(char *));
    if (!k || !v) { free(k); free(v); return false; }
    for (size_t i = 0; i < count; ++i) {
        k[i] = ca_dup(keys ? keys[i] : NULL);
        v[i] = ca_dup(vals ? vals[i] : NULL);
    }
    *out_keys = k;
    *out_vals = v;
    return true;
}

static void ca_free_kv(char **keys, char **vals, size_t count) {
    if (keys) { for (size_t i = 0; i < count; ++i) free(keys[i]); free(keys); }
    if (vals) { for (size_t i = 0; i < count; ++i) free(vals[i]); free(vals); }
}

/* ---------------------------------------------------------------------------
 * Tokenisation
 * --------------------------------------------------------------------------- */

/* Split s on any run of non-ASCII-alphanumeric characters (mirrors [^A-Za-z0-9]+).
 * Lowercased tokens appended to a growable string array. */
static void ca_split_alnum_lower(const char *s, char ***out, size_t *out_count) {
    char **arr = NULL;
    size_t count = 0, cap = 0;
    const char *p = s;
    while (p && *p) {
        while (*p && !isalnum((unsigned char)*p)) ++p;
        const char *start = p;
        while (*p && isalnum((unsigned char)*p)) ++p;
        if (p > start) {
            size_t len = (size_t)(p - start);
            char *tok = (char *)malloc(len + 1);
            if (!tok) continue;
            memcpy(tok, start, len);
            tok[len] = '\0';
            ca_lower_inplace(tok);
            if (count == cap) {
                size_t ncap = cap ? cap * 2 : 8;
                char **narr = (char **)realloc(arr, ncap * sizeof(char *));
                if (!narr) { free(tok); break; }
                arr = narr; cap = ncap;
            }
            arr[count++] = tok;
        }
    }
    *out = arr;
    *out_count = count;
}

/* Membership in a separator set for the extractor variants (which include
 * punctuation the alnum splitter would also break on, but the reference uses an
 * explicit set with specific inclusions). */
static bool ca_in_set(char c, const char *set) {
    return strchr(set, c) != NULL;
}

/* ===========================================================================
 * MemoryItem / MemoryHit
 * =========================================================================== */

void ca_memory_item_free(ca_memory_item_t *item) {
    if (!item) return;
    free(item->id);
    free(item->text);
    ca_free_kv(item->meta_keys, item->meta_values, item->meta_count);
    item->id = item->text = NULL;
    item->meta_keys = item->meta_values = NULL;
    item->meta_count = 0;
}

void ca_memory_hit_free(ca_memory_hit_t *hit) {
    if (!hit) return;
    ca_memory_item_free(&hit->item);
    hit->score = 0;
}

void ca_memory_hit_free_array(ca_memory_hit_t *hits, size_t count) {
    if (!hits) return;
    for (size_t i = 0; i < count; ++i) ca_memory_item_free(&hits[i].item);
    free(hits);
}

const char *ca_memory_item_get_meta(const ca_memory_item_t *item, const char *key) {
    if (!item || !key) return NULL;
    for (size_t i = 0; i < item->meta_count; ++i) {
        if (item->meta_keys[i] && strcmp(item->meta_keys[i], key) == 0) {
            return item->meta_values[i];
        }
    }
    return NULL;
}

/* ===========================================================================
 * Knowledge graph
 * =========================================================================== */

void ca_knowledge_node_free(ca_knowledge_node_t *node) {
    if (!node) return;
    free(node->id);
    free(node->kind);
    free(node->name);
    ca_free_kv(node->prop_keys, node->prop_values, node->prop_count);
    memset(node, 0, sizeof(*node));
}

void ca_knowledge_triple_free(ca_knowledge_triple_t *t) {
    if (!t) return;
    free(t->subject);
    free(t->predicate);
    free(t->object);
    free(t->source);
    memset(t, 0, sizeof(*t));
}

void ca_knowledge_triple_free_array(ca_knowledge_triple_t *triples, size_t count) {
    if (!triples) return;
    for (size_t i = 0; i < count; ++i) ca_knowledge_triple_free(&triples[i]);
    free(triples);
}

struct ca_knowledge_graph {
    ca_knowledge_node_t   *nodes;
    size_t                 node_count, node_cap;
    ca_knowledge_triple_t *triples;   /* keyed logically by (s,p,o) */
    size_t                 triple_count, triple_cap;
};

ca_knowledge_graph_t *ca_knowledge_graph_create(void) {
    return (ca_knowledge_graph_t *)calloc(1, sizeof(struct ca_knowledge_graph));
}

void ca_knowledge_graph_destroy(ca_knowledge_graph_t *kg) {
    if (!kg) return;
    for (size_t i = 0; i < kg->node_count; ++i) ca_knowledge_node_free(&kg->nodes[i]);
    free(kg->nodes);
    for (size_t i = 0; i < kg->triple_count; ++i) ca_knowledge_triple_free(&kg->triples[i]);
    free(kg->triples);
    free(kg);
}

/* Deep-copy the fields of one node from raw components into dst. */
static bool ca_node_fill(ca_knowledge_node_t *dst, const char *id, const char *kind,
                         const char *name, const char *const *pk, const char *const *pv,
                         size_t pc) {
    dst->id = ca_dup(id);
    dst->kind = ca_dup(kind);
    dst->name = ca_dup(name);
    if (!ca_dup_kv(pk, pv, pc, &dst->prop_keys, &dst->prop_values)) return false;
    dst->prop_count = pc;
    return true;
}

bool ca_knowledge_graph_upsert_node(ca_knowledge_graph_t *kg,
                                    const char *id, const char *kind, const char *name,
                                    const char *const *prop_keys,
                                    const char *const *prop_values,
                                    size_t prop_count) {
    if (!kg || ca_is_blank(id)) return false;
    /* Replace existing by id. */
    for (size_t i = 0; i < kg->node_count; ++i) {
        if (kg->nodes[i].id && strcmp(kg->nodes[i].id, id) == 0) {
            ca_knowledge_node_t tmp;
            memset(&tmp, 0, sizeof(tmp));
            if (!ca_node_fill(&tmp, id, kind, name, prop_keys, prop_values, prop_count)) {
                ca_knowledge_node_free(&tmp);
                return false;
            }
            ca_knowledge_node_free(&kg->nodes[i]);
            kg->nodes[i] = tmp;
            return true;
        }
    }
    if (kg->node_count == kg->node_cap) {
        size_t ncap = kg->node_cap ? kg->node_cap * 2 : 8;
        ca_knowledge_node_t *n = (ca_knowledge_node_t *)realloc(kg->nodes, ncap * sizeof(*n));
        if (!n) return false;
        kg->nodes = n; kg->node_cap = ncap;
    }
    ca_knowledge_node_t *slot = &kg->nodes[kg->node_count];
    memset(slot, 0, sizeof(*slot));
    if (!ca_node_fill(slot, id, kind, name, prop_keys, prop_values, prop_count)) {
        ca_knowledge_node_free(slot);
        return false;
    }
    kg->node_count++;
    return true;
}

bool ca_knowledge_graph_get_node(const ca_knowledge_graph_t *kg, const char *id,
                                 ca_knowledge_node_t *out) {
    if (!kg || !id || !out) return false;
    for (size_t i = 0; i < kg->node_count; ++i) {
        if (kg->nodes[i].id && strcmp(kg->nodes[i].id, id) == 0) {
            const ca_knowledge_node_t *src = &kg->nodes[i];
            memset(out, 0, sizeof(*out));
            return ca_node_fill(out, src->id, src->kind, src->name,
                                (const char *const *)src->prop_keys,
                                (const char *const *)src->prop_values, src->prop_count);
        }
    }
    return false;
}

bool ca_knowledge_graph_add_triple(ca_knowledge_graph_t *kg,
                                   const char *subject, const char *predicate,
                                   const char *object, const char *source,
                                   double confidence) {
    if (!kg) return false;
    if (ca_is_blank(subject) || ca_is_blank(predicate) || ca_is_blank(object)) return false;
    if (confidence < 0.0 || confidence > 1.0) return false;

    /* Key is (subject, predicate, object): replace an existing match. */
    for (size_t i = 0; i < kg->triple_count; ++i) {
        ca_knowledge_triple_t *t = &kg->triples[i];
        if (strcmp(t->subject, subject) == 0 && strcmp(t->predicate, predicate) == 0 &&
            strcmp(t->object, object) == 0) {
            free(t->source);
            t->source = ca_dup(source);
            t->confidence = confidence;
            t->recorded_at_ms = ca_now_ms();
            return true;
        }
    }
    if (kg->triple_count == kg->triple_cap) {
        size_t ncap = kg->triple_cap ? kg->triple_cap * 2 : 8;
        ca_knowledge_triple_t *n = (ca_knowledge_triple_t *)realloc(kg->triples, ncap * sizeof(*n));
        if (!n) return false;
        kg->triples = n; kg->triple_cap = ncap;
    }
    ca_knowledge_triple_t *slot = &kg->triples[kg->triple_count];
    memset(slot, 0, sizeof(*slot));
    slot->subject = ca_dup(subject);
    slot->predicate = ca_dup(predicate);
    slot->object = ca_dup(object);
    slot->source = ca_dup(source);
    slot->confidence = confidence;
    slot->recorded_at_ms = ca_now_ms();
    kg->triple_count++;
    return true;
}

/* Deep-copy one triple into dst. */
static void ca_triple_copy(ca_knowledge_triple_t *dst, const ca_knowledge_triple_t *src) {
    dst->subject = ca_dup(src->subject);
    dst->predicate = ca_dup(src->predicate);
    dst->object = ca_dup(src->object);
    dst->source = ca_dup(src->source);
    dst->confidence = src->confidence;
    dst->recorded_at_ms = src->recorded_at_ms;
}

ca_knowledge_triple_t *ca_knowledge_graph_all_triples(const ca_knowledge_graph_t *kg,
                                                      size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!kg || kg->triple_count == 0) return NULL;
    ca_knowledge_triple_t *arr =
        (ca_knowledge_triple_t *)calloc(kg->triple_count, sizeof(*arr));
    if (!arr) return NULL;
    for (size_t i = 0; i < kg->triple_count; ++i) ca_triple_copy(&arr[i], &kg->triples[i]);
    if (out_count) *out_count = kg->triple_count;
    return arr;
}

ca_knowledge_triple_t *ca_knowledge_graph_read_triples(const ca_knowledge_graph_t *kg,
                                                       const char *subject,
                                                       size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!kg || ca_is_blank(subject)) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < kg->triple_count; ++i) {
        if (strcmp(kg->triples[i].subject, subject) == 0) n++;
    }
    if (n == 0) return NULL;
    ca_knowledge_triple_t *arr = (ca_knowledge_triple_t *)calloc(n, sizeof(*arr));
    if (!arr) return NULL;
    size_t j = 0;
    for (size_t i = 0; i < kg->triple_count; ++i) {
        if (strcmp(kg->triples[i].subject, subject) == 0) ca_triple_copy(&arr[j++], &kg->triples[i]);
    }
    if (out_count) *out_count = n;
    return arr;
}

/* ===========================================================================
 * HippoRAG store — Personalised PageRank
 * =========================================================================== */

struct ca_hippo_store {
    ca_knowledge_graph_t *kg;  /* borrowed */
    int    walk_iterations;
    double damping;
};

ca_hippo_store_t *ca_hippo_store_create_tuned(ca_knowledge_graph_t *kg,
                                              int walk_iterations, double damping) {
    if (!kg) return NULL;
    ca_hippo_store_t *s = (ca_hippo_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->kg = kg;
    s->walk_iterations = walk_iterations;
    s->damping = damping;
    return s;
}

ca_hippo_store_t *ca_hippo_store_create(ca_knowledge_graph_t *kg) {
    return ca_hippo_store_create_tuned(kg, 32, 0.85);
}

void ca_hippo_store_destroy(ca_hippo_store_t *store) { free(store); }

const char *ca_hippo_store_backend_id(const ca_hippo_store_t *store) {
    (void)store;
    return "inmemory-hippo-ppr";
}

bool ca_hippo_store_index(ca_hippo_store_t *store, const ca_memory_item_t *item) {
    if (!store || !item || ca_is_blank(item->id)) return false;
    if (!ca_knowledge_graph_add_triple(store->kg, item->id, "memory_text",
                                       item->text ? item->text : "", item->id, 1.0)) {
        return false;
    }
    for (size_t i = 0; i < item->meta_count; ++i) {
        if (!ca_knowledge_graph_add_triple(store->kg, item->id, item->meta_keys[i],
                                           item->meta_values[i], item->id, 0.9)) {
            return false;
        }
    }
    return true;
}

/* --- internal rank-map (node id -> double), linear search --- */

typedef struct {
    char  **ids;       /* borrowed pointers into the node-name table below */
    double *vals;
    size_t  count;
} ca_rankmap_t;

/* String set of unique node ids (owns the strings). */
typedef struct {
    char  **ids;
    size_t  count, cap;
} ca_strset_t;

static int ca_strset_index(const ca_strset_t *set, const char *id) {
    for (size_t i = 0; i < set->count; ++i) {
        if (strcmp(set->ids[i], id) == 0) return (int)i;
    }
    return -1;
}

static void ca_strset_add(ca_strset_t *set, const char *id) {
    if (ca_strset_index(set, id) >= 0) return;
    if (set->count == set->cap) {
        size_t ncap = set->cap ? set->cap * 2 : 8;
        char **n = (char **)realloc(set->ids, ncap * sizeof(char *));
        if (!n) return;
        set->ids = n; set->cap = ncap;
    }
    set->ids[set->count++] = ca_dup(id);
}

static void ca_strset_free(ca_strset_t *set) {
    if (set->ids) { for (size_t i = 0; i < set->count; ++i) free(set->ids[i]); free(set->ids); }
    set->ids = NULL; set->count = set->cap = 0;
}

/* Outgoing edge (object index, confidence). */
typedef struct { int object_idx; double confidence; } ca_edge_t;
/* Adjacency for one node. */
typedef struct { ca_edge_t *edges; size_t count, cap; } ca_adj_t;

ca_memory_hit_t *ca_hippo_store_multi_hop_recall(ca_hippo_store_t *store,
                                                 const char *query, int top_k,
                                                 size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || ca_is_blank(query) || top_k <= 0) {
        if (out_count) *out_count = SIZE_MAX;
        return NULL;
    }

    size_t triple_count = 0;
    ca_knowledge_triple_t *triples = ca_knowledge_graph_all_triples(store->kg, &triple_count);
    if (triple_count == 0) {
        ca_knowledge_triple_free_array(triples, triple_count);
        return NULL; /* empty graph → empty result */
    }

    /* Node universe + adjacency (subject -> [(object, confidence)]). */
    ca_strset_t nodes = {0};
    for (size_t i = 0; i < triple_count; ++i) {
        ca_strset_add(&nodes, triples[i].subject);
        ca_strset_add(&nodes, triples[i].object);
    }
    size_t N = nodes.count;
    ca_adj_t *adj = (ca_adj_t *)calloc(N, sizeof(ca_adj_t));
    if (!adj) { ca_strset_free(&nodes); ca_knowledge_triple_free_array(triples, triple_count); return NULL; }
    for (size_t i = 0; i < triple_count; ++i) {
        int si = ca_strset_index(&nodes, triples[i].subject);
        int oi = ca_strset_index(&nodes, triples[i].object);
        if (si < 0 || oi < 0) continue;
        ca_adj_t *a = &adj[si];
        if (a->count == a->cap) {
            size_t ncap = a->cap ? a->cap * 2 : 4;
            ca_edge_t *e = (ca_edge_t *)realloc(a->edges, ncap * sizeof(ca_edge_t));
            if (!e) continue;
            a->edges = e; a->cap = ncap;
        }
        a->edges[a->count].object_idx = oi;
        a->edges[a->count].confidence = triples[i].confidence;
        a->count++;
    }

    /* Seed set: query terms that appear as nodes (case-insensitive). */
    char **qterms = NULL; size_t qn = 0;
    ca_split_alnum_lower(query, &qterms, &qn);
    int *is_seed = (int *)calloc(N, sizeof(int));
    size_t seed_count = 0;
    for (size_t i = 0; i < N; ++i) {
        char *lower = ca_dup(nodes.ids[i]);
        ca_lower_inplace(lower);
        for (size_t j = 0; j < qn; ++j) {
            if (strcmp(lower, qterms[j]) == 0) { is_seed[i] = 1; break; }
        }
        free(lower);
        if (is_seed[i]) seed_count++;
    }
    for (size_t j = 0; j < qn; ++j) free(qterms[j]);
    free(qterms);

    ca_memory_hit_t *result = NULL;

    /* Precision guarantee 1: no genuine association → return nothing. */
    if (seed_count == 0) {
        goto cleanup;
    }

    {
        double seed_mass = 1.0 / (double)seed_count;
        double *rank = (double *)calloc(N, sizeof(double));
        double *next = (double *)calloc(N, sizeof(double));
        if (!rank || !next) { free(rank); free(next); goto cleanup; }
        for (size_t i = 0; i < N; ++i) rank[i] = is_seed[i] ? seed_mass : 0.0;

        for (int it = 0; it < store->walk_iterations; ++it) {
            for (size_t i = 0; i < N; ++i) next[i] = 0.0;
            /* Random-jump (personalisation): mass returns to the seeds. */
            for (size_t i = 0; i < N; ++i) {
                if (is_seed[i]) next[i] += (1.0 - store->damping) * seed_mass;
            }
            /* Walk component. */
            for (size_t node = 0; node < N; ++node) {
                double mass = rank[node];
                if (mass <= 0.0) continue;
                ca_adj_t *a = &adj[node];
                if (a->count == 0) {
                    /* Dangling: redistribute via personalisation. */
                    for (size_t i = 0; i < N; ++i) {
                        if (is_seed[i]) next[i] += (store->damping * mass) / (double)seed_count;
                    }
                    continue;
                }
                double total_conf = 0.0;
                for (size_t e = 0; e < a->count; ++e) total_conf += a->edges[e].confidence;
                for (size_t e = 0; e < a->count; ++e) {
                    double weight = total_conf > 0.0
                        ? a->edges[e].confidence / total_conf
                        : 1.0 / (double)a->count;
                    next[a->edges[e].object_idx] += store->damping * mass * weight;
                }
            }
            double *tmp = rank; rank = next; next = tmp;
        }

        /* Precision guarantee 2: exclude seeds. Collect (idx, value) then sort. */
        typedef struct { int idx; double val; } scored_t;
        scored_t *scored = (scored_t *)calloc(N, sizeof(scored_t));
        size_t sn = 0;
        if (scored) {
            for (size_t i = 0; i < N; ++i) {
                if (rank[i] > 0.0 && !is_seed[i]) {
                    scored[sn].idx = (int)i;
                    scored[sn].val = rank[i];
                    sn++;
                }
            }
            /* Sort by value desc; ties broken by id asc for determinism. */
            for (size_t a = 0; a + 1 < sn; ++a) {
                for (size_t b = a + 1; b < sn; ++b) {
                    bool swap;
                    if (scored[b].val != scored[a].val) {
                        swap = scored[b].val > scored[a].val;
                    } else {
                        swap = strcmp(nodes.ids[scored[b].idx], nodes.ids[scored[a].idx]) < 0;
                    }
                    if (swap) { scored_t t = scored[a]; scored[a] = scored[b]; scored[b] = t; }
                }
            }
            size_t limit = (size_t)top_k < sn ? (size_t)top_k : sn;
            if (limit > 0) {
                result = (ca_memory_hit_t *)calloc(limit, sizeof(ca_memory_hit_t));
                if (result) {
                    for (size_t i = 0; i < limit; ++i) {
                        const char *key = nodes.ids[scored[i].idx];
                        ca_knowledge_node_t node;
                        bool have = ca_knowledge_graph_get_node(store->kg, key, &node);
                        const char *text = key;
                        if (have && node.name && node.name[0] != '\0') text = node.name;
                        result[i].item.id = ca_dup(key);
                        result[i].item.text = ca_dup(text);
                        if (have && node.prop_count > 0) {
                            ca_dup_kv((const char *const *)node.prop_keys,
                                      (const char *const *)node.prop_values, node.prop_count,
                                      &result[i].item.meta_keys, &result[i].item.meta_values);
                            result[i].item.meta_count = node.prop_count;
                        }
                        result[i].score = scored[i].val;
                        if (have) ca_knowledge_node_free(&node);
                    }
                    if (out_count) *out_count = limit;
                }
            }
            free(scored);
        }
        free(rank);
        free(next);
    }

cleanup:
    for (size_t i = 0; i < N; ++i) free(adj[i].edges);
    free(adj);
    free(is_seed);
    ca_strset_free(&nodes);
    ca_knowledge_triple_free_array(triples, triple_count);
    return result;
}

/* ===========================================================================
 * Episodic store
 * =========================================================================== */

void ca_episodic_entry_free(ca_episodic_entry_t *e) {
    if (!e) return;
    free(e->id);
    free(e->user_text);
    free(e->assistant_text);
    free(e->app_context);
    free(e->embedding);
    ca_free_kv(e->tag_keys, e->tag_values, e->tag_count);
    memset(e, 0, sizeof(*e));
}

const char *ca_episodic_entry_get_tag(const ca_episodic_entry_t *entry, const char *key) {
    if (!entry || !key) return NULL;
    for (size_t i = 0; i < entry->tag_count; ++i) {
        if (entry->tag_keys[i] && strcmp(entry->tag_keys[i], key) == 0) {
            return entry->tag_values[i];
        }
    }
    return NULL;
}

void ca_episodic_entry_free_array(ca_episodic_entry_t *entries, size_t count) {
    if (!entries) return;
    for (size_t i = 0; i < count; ++i) ca_episodic_entry_free(&entries[i]);
    free(entries);
}

static void ca_episodic_copy(ca_episodic_entry_t *dst, const ca_episodic_entry_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->id = ca_dup(src->id);
    dst->recorded_at_ms = src->recorded_at_ms;
    dst->user_text = ca_dup(src->user_text);
    dst->assistant_text = ca_dup(src->assistant_text);
    dst->app_context = ca_dup(src->app_context);
    if (src->embedding && src->embedding_len > 0) {
        dst->embedding = (float *)malloc(src->embedding_len * sizeof(float));
        if (dst->embedding) {
            memcpy(dst->embedding, src->embedding, src->embedding_len * sizeof(float));
            dst->embedding_len = src->embedding_len;
        }
    }
    if (src->tag_count > 0) {
        if (ca_dup_kv((const char *const *)src->tag_keys,
                      (const char *const *)src->tag_values, src->tag_count,
                      &dst->tag_keys, &dst->tag_values)) {
            dst->tag_count = src->tag_count;
        }
    }
}

struct ca_episodic_store {
    ca_episodic_entry_t *entries;
    size_t               count, cap;
    size_t               max_entries;
};

ca_episodic_store_t *ca_episodic_store_create(size_t max_entries) {
    if (max_entries == 0) return NULL;
    ca_episodic_store_t *s = (ca_episodic_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->max_entries = max_entries;
    return s;
}

void ca_episodic_store_destroy(ca_episodic_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_episodic_entry_free(&store->entries[i]);
    free(store->entries);
    free(store);
}

bool ca_episodic_store_add(ca_episodic_store_t *store, const ca_episodic_entry_t *entry) {
    if (!store || !entry) return false;
    if (store->count == store->cap) {
        size_t ncap = store->cap ? store->cap * 2 : 8;
        ca_episodic_entry_t *n = (ca_episodic_entry_t *)realloc(store->entries, ncap * sizeof(*n));
        if (!n) return false;
        store->entries = n; store->cap = ncap;
    }
    ca_episodic_copy(&store->entries[store->count], entry);
    store->count++;
    /* FIFO eviction of the oldest (front). */
    while (store->count > store->max_entries) {
        ca_episodic_entry_free(&store->entries[0]);
        memmove(&store->entries[0], &store->entries[1],
                (store->count - 1) * sizeof(ca_episodic_entry_t));
        store->count--;
    }
    return true;
}

size_t ca_episodic_store_count(const ca_episodic_store_t *store) {
    return store ? store->count : 0;
}

/* Stable insertion-sort of an index array by recency descending (newest first).
 * Stability preserves insertion order among equal timestamps (matches the
 * reference's stable sort). */
static void ca_sort_indices_recency_desc(const ca_episodic_store_t *store,
                                          size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = store->entries[key].recorded_at_ms;
        size_t j = i;
        while (j > 0 && store->entries[idx[j - 1]].recorded_at_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

static float ca_cosine_dot(const float *a, const float *b, size_t n) {
    float dot = 0.0f;
    for (size_t i = 0; i < n; ++i) dot += a[i] * b[i];
    return dot;
}

ca_episodic_entry_t *ca_episodic_store_search(const ca_episodic_store_t *store,
                                              const float *query_embedding,
                                              size_t query_len, int top_k,
                                              size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || top_k <= 0 || store->count == 0) return NULL;

    if (!query_embedding || query_len == 0) {
        /* Recency fallback. */
        size_t *idx = (size_t *)malloc(store->count * sizeof(size_t));
        if (!idx) return NULL;
        for (size_t i = 0; i < store->count; ++i) idx[i] = i;
        ca_sort_indices_recency_desc(store, idx, store->count);
        size_t limit = (size_t)top_k < store->count ? (size_t)top_k : store->count;
        ca_episodic_entry_t *out = (ca_episodic_entry_t *)calloc(limit, sizeof(*out));
        if (out) {
            for (size_t i = 0; i < limit; ++i) ca_episodic_copy(&out[i], &store->entries[idx[i]]);
            if (out_count) *out_count = limit;
        }
        free(idx);
        return out;
    }

    /* Cosine over dimension-matching entries; stable sort by score desc. */
    typedef struct { size_t idx; float score; } cand_t;
    cand_t *cands = (cand_t *)malloc(store->count * sizeof(cand_t));
    if (!cands) return NULL;
    size_t cn = 0;
    for (size_t i = 0; i < store->count; ++i) {
        const ca_episodic_entry_t *e = &store->entries[i];
        if (e->embedding && e->embedding_len == query_len) {
            cands[cn].idx = i;
            cands[cn].score = ca_cosine_dot(query_embedding, e->embedding, query_len);
            cn++;
        }
    }
    /* Stable insertion sort by score desc (preserves insertion order on ties). */
    for (size_t i = 1; i < cn; ++i) {
        cand_t key = cands[i];
        size_t j = i;
        while (j > 0 && cands[j - 1].score < key.score) { cands[j] = cands[j - 1]; j--; }
        cands[j] = key;
    }
    size_t limit = (size_t)top_k < cn ? (size_t)top_k : cn;
    ca_episodic_entry_t *out = NULL;
    if (limit > 0) {
        out = (ca_episodic_entry_t *)calloc(limit, sizeof(*out));
        if (out) {
            for (size_t i = 0; i < limit; ++i) ca_episodic_copy(&out[i], &store->entries[cands[i].idx]);
            if (out_count) *out_count = limit;
        }
    }
    free(cands);
    return out;
}

ca_episodic_entry_t *ca_episodic_store_get_recent(const ca_episodic_store_t *store,
                                                  int count, size_t *out_count) {
    return ca_episodic_store_search(store, NULL, 0, count, out_count);
}

size_t ca_episodic_store_prune_older_than(ca_episodic_store_t *store, int64_t cutoff_ms) {
    if (!store) return 0;
    size_t before = store->count;
    size_t w = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->entries[i].recorded_at_ms < cutoff_ms) {
            ca_episodic_entry_free(&store->entries[i]);
        } else {
            if (w != i) store->entries[w] = store->entries[i];
            w++;
        }
    }
    store->count = w;
    return before - w;
}

ca_episodic_entry_t *ca_episodic_store_search_adapter(void *user,
                                                      const float *query_embedding,
                                                      size_t query_len, int top_k,
                                                      size_t *out_count) {
    return ca_episodic_store_search((const ca_episodic_store_t *)user,
                                    query_embedding, query_len, top_k, out_count);
}

ca_memory_hit_t *ca_hippo_store_recall_adapter(void *user, const char *query,
                                               int top_k, size_t *out_count) {
    return ca_hippo_store_multi_hop_recall((ca_hippo_store_t *)user, query, top_k, out_count);
}

/* ===========================================================================
 * Fused recall — Reciprocal Rank Fusion
 * =========================================================================== */

struct ca_fused_recall {
    ca_episodic_search_fn episodic_fn;
    void                 *episodic_user;
    ca_hippo_recall_fn    graph_fn;
    void                 *graph_user;
    int                   candidate_pool_size;
    int                   rrf_k;
    double                graph_confidence_threshold;
};

ca_fused_recall_t *ca_fused_recall_create(ca_episodic_search_fn episodic_fn,
                                          void *episodic_user,
                                          ca_hippo_recall_fn graph_fn, void *graph_user,
                                          const ca_fused_recall_options_t *opts) {
    if (!episodic_fn) return NULL;
    ca_fused_recall_t *fr = (ca_fused_recall_t *)calloc(1, sizeof(*fr));
    if (!fr) return NULL;
    fr->episodic_fn = episodic_fn;
    fr->episodic_user = episodic_user;
    fr->graph_fn = graph_fn;
    fr->graph_user = graph_user;
    fr->candidate_pool_size = 20;
    fr->rrf_k = 60;
    fr->graph_confidence_threshold = 0.4;
    if (opts) {
        if (opts->candidate_pool_size != 0) fr->candidate_pool_size = opts->candidate_pool_size;
        if (opts->rrf_k != 0) fr->rrf_k = opts->rrf_k;
        if (opts->graph_confidence_threshold != 0.0)
            fr->graph_confidence_threshold = opts->graph_confidence_threshold;
    }
    return fr;
}

void ca_fused_recall_destroy(ca_fused_recall_t *fr) { free(fr); }

/* Lowercase + collapse internal whitespace so equivalent texts fuse to one key.
 * Returns a freshly allocated string (may be empty). */
static char *ca_normalise_key(const char *text) {
    if (!text) return ca_dup("");
    /* find first/last non-space */
    const char *start = text;
    while (*start && isspace((unsigned char)*start)) ++start;
    const char *end = text + strlen(text);
    while (end > start && isspace((unsigned char)*(end - 1))) --end;
    if (end == start) return ca_dup("");
    size_t len = (size_t)(end - start);
    char *out = (char *)malloc(len + 1);
    if (!out) return NULL;
    size_t w = 0;
    bool prev_space = false;
    for (const char *p = start; p < end; ++p) {
        if (isspace((unsigned char)*p)) {
            if (!prev_space) { out[w++] = ' '; prev_space = true; }
        } else {
            out[w++] = (char)tolower((unsigned char)*p);
            prev_space = false;
        }
    }
    out[w] = '\0';
    return out;
}

/* Adapt an episodic entry into a MemoryItem (owned), keyed by user text with
 * episodic provenance metadata. */
static void ca_adapt_episodic(ca_memory_item_t *out, const ca_episodic_entry_t *e) {
    memset(out, 0, sizeof(*out));
    out->id = ca_dup(e->id);
    out->text = ca_dup(e->user_text ? e->user_text : "");

    /* Build metadata: source=episodic, recordedAt, optionally assistantText/appContext. */
    const char *keys[4]; const char *vals[4];
    char recbuf[32];
    snprintf(recbuf, sizeof(recbuf), "%lld", (long long)e->recorded_at_ms);
    size_t m = 0;
    keys[m] = "source"; vals[m] = "episodic"; m++;
    keys[m] = "recordedAt"; vals[m] = recbuf; m++;
    if (e->assistant_text && e->assistant_text[0] != '\0') {
        keys[m] = "assistantText"; vals[m] = e->assistant_text; m++;
    }
    if (e->app_context && e->app_context[0] != '\0') {
        keys[m] = "appContext"; vals[m] = e->app_context; m++;
    }
    ca_dup_kv(keys, vals, m, &out->meta_keys, &out->meta_values);
    out->meta_count = m;
}

/* Reports whether a graph hit carries a confidence value below the threshold. A
 * hit with no confidence metadata is never below (gate no-op). */
static bool ca_is_below_confidence(const ca_memory_hit_t *hit, double threshold) {
    const char *raw = ca_memory_item_get_meta(&hit->item, "confidence");
    if (!raw) return false;
    char *endp = NULL;
    double c = strtod(raw, &endp);
    if (endp == raw) return false; /* unparseable → not below */
    return c < threshold;
}

/* Fusion accumulator entry. */
typedef struct {
    char            *key;    /* normalised text key (owned) */
    ca_memory_item_t item;   /* owned; the first item seen for this key */
    double           score;
    size_t           order;  /* insertion order for stable tie-break */
} ca_fused_entry_t;

ca_memory_hit_t *ca_fused_recall_recall(ca_fused_recall_t *fr, const char *query,
                                        const float *query_embedding, size_t query_len,
                                        int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!fr || top_k <= 0) {
        if (out_count) *out_count = SIZE_MAX;
        return NULL;
    }
    int pool = fr->candidate_pool_size;

    /* Fast path: episodic. */
    size_t ep_count = 0;
    ca_episodic_entry_t *ep = fr->episodic_fn(fr->episodic_user, query_embedding,
                                              query_len, pool, &ep_count);
    if (ep_count == SIZE_MAX) ep_count = 0; /* defensive; episodic never errors here */

    /* Slow path: graph, best-effort. Empty query cannot seed a walk → skip. */
    size_t g_count = 0;
    ca_memory_hit_t *gh = NULL;
    if (fr->graph_fn && !ca_is_blank(query)) {
        gh = fr->graph_fn(fr->graph_user, query, pool, &g_count);
        if (g_count == SIZE_MAX) { g_count = 0; gh = NULL; } /* error → degrade */
    }

    double k = (double)fr->rrf_k;
    ca_fused_entry_t *fused = NULL;
    size_t fused_count = 0, fused_cap = 0;

    /* accumulate helper (inline via macro-like loop below). */
    #define CA_FUSE_ACCUM(ITEM_PTR, ONE_BASED_RANK)                                  \
        do {                                                                         \
            char *fk = ca_normalise_key((ITEM_PTR)->text);                           \
            if (fk && fk[0] != '\0') {                                               \
                double contrib = 1.0 / (k + (double)(ONE_BASED_RANK));               \
                int found = -1;                                                      \
                for (size_t _i = 0; _i < fused_count; ++_i) {                        \
                    if (strcmp(fused[_i].key, fk) == 0) { found = (int)_i; break; }  \
                }                                                                    \
                if (found >= 0) {                                                    \
                    fused[found].score += contrib;                                   \
                    free(fk);                                                        \
                    ca_memory_item_free(ITEM_PTR);                                   \
                } else {                                                             \
                    if (fused_count == fused_cap) {                                  \
                        size_t _nc = fused_cap ? fused_cap * 2 : 8;                  \
                        ca_fused_entry_t *_n = (ca_fused_entry_t *)realloc(fused, _nc * sizeof(*_n)); \
                        if (_n) { fused = _n; fused_cap = _nc; }                     \
                    }                                                                \
                    if (fused_count < fused_cap) {                                   \
                        fused[fused_count].key = fk;                                 \
                        fused[fused_count].item = *(ITEM_PTR);                       \
                        fused[fused_count].score = contrib;                          \
                        fused[fused_count].order = fused_count;                      \
                        fused_count++;                                               \
                    } else { free(fk); ca_memory_item_free(ITEM_PTR); }             \
                }                                                                    \
            } else {                                                                 \
                free(fk);                                                            \
                ca_memory_item_free(ITEM_PTR);                                       \
            }                                                                        \
        } while (0)

    for (size_t i = 0; i < ep_count; ++i) {
        ca_memory_item_t item;
        ca_adapt_episodic(&item, &ep[i]);
        CA_FUSE_ACCUM(&item, i + 1);
    }
    for (size_t i = 0; i < g_count; ++i) {
        if (ca_is_below_confidence(&gh[i], fr->graph_confidence_threshold)) continue;
        /* Move the hit's item into the accumulator (transfer ownership). */
        ca_memory_item_t item = gh[i].item;
        memset(&gh[i].item, 0, sizeof(gh[i].item));
        CA_FUSE_ACCUM(&item, i + 1);
    }
    #undef CA_FUSE_ACCUM

    ca_episodic_entry_free_array(ep, ep_count);
    ca_memory_hit_free_array(gh, g_count); /* frees any items we did NOT take */

    /* Sort by score desc; equal scores keep insertion order. */
    for (size_t a = 0; a + 1 < fused_count; ++a) {
        for (size_t b = a + 1; b < fused_count; ++b) {
            bool swap;
            if (fused[b].score != fused[a].score) {
                swap = fused[b].score > fused[a].score;
            } else {
                swap = fused[b].order < fused[a].order;
            }
            if (swap) { ca_fused_entry_t t = fused[a]; fused[a] = fused[b]; fused[b] = t; }
        }
    }

    size_t limit = (size_t)top_k < fused_count ? (size_t)top_k : fused_count;
    ca_memory_hit_t *result = NULL;
    if (limit > 0) {
        result = (ca_memory_hit_t *)calloc(limit, sizeof(ca_memory_hit_t));
        if (result) {
            for (size_t i = 0; i < limit; ++i) {
                result[i].item = fused[i].item;   /* transfer ownership */
                memset(&fused[i].item, 0, sizeof(fused[i].item));
                result[i].score = fused[i].score;
            }
            if (out_count) *out_count = limit;
        }
    }
    /* Free any fused entries not moved into the result (beyond limit or on OOM). */
    for (size_t i = 0; i < fused_count; ++i) {
        free(fused[i].key);
        ca_memory_item_free(&fused[i].item);
    }
    free(fused);
    return result;
}

/* ===========================================================================
 * Heuristic knowledge-graph extractor
 * =========================================================================== */

static const char *const CA_KG_STOP[] = {
    "the","a","an","and","or","but","if","is","are","was","were","be","been","being",
    "to","of","in","on","at","for","with","from","by","as","into","about","over","under",
    "my","your","our","their","his","her","its","this","that","these","those",
    "i","you","he","she","it","we","they","me","him","them","us",
    "do","does","did","done","have","has","had","will","would","can","could","should",
    "shall","may","might","must","not","no","yes","so","than","then","there","here",
    "how","why","what","when","where","who","which","whom",
    "am","get","got","really","just","very","much","many","some","any","all",
};
static const size_t CA_KG_STOP_N = sizeof(CA_KG_STOP) / sizeof(CA_KG_STOP[0]);

/* Separator set for the extractor: includes apostrophe/hyphen/slash. */
static const char *CA_KG_SEP = " \t\n\r.,?!;:'\"()/-";

static bool ca_kg_is_stop(const char *w) {
    for (size_t i = 0; i < CA_KG_STOP_N; ++i) {
        if (strcmp(w, CA_KG_STOP[i]) == 0) return true;
    }
    return false;
}

/* Extract distinct content words (>=3 chars, not stop) from combined lowercased
 * text, in first-seen order. */
static void ca_kg_content_words(const char *combined, char ***out, size_t *out_count) {
    char **words = NULL; size_t count = 0, cap = 0;
    char *buf = ca_dup(combined ? combined : "");
    if (!buf) { *out = NULL; *out_count = 0; return; }
    ca_lower_inplace(buf);
    size_t len = strlen(buf);
    size_t i = 0;
    while (i < len) {
        while (i < len && ca_in_set(buf[i], CA_KG_SEP)) ++i;
        size_t start = i;
        while (i < len && !ca_in_set(buf[i], CA_KG_SEP)) ++i;
        if (i > start) {
            size_t wlen = i - start;
            if (wlen < 3) continue;
            char *w = (char *)malloc(wlen + 1);
            if (!w) continue;
            memcpy(w, buf + start, wlen);
            w[wlen] = '\0';
            if (ca_kg_is_stop(w)) { free(w); continue; }
            bool dup = false;
            for (size_t j = 0; j < count; ++j) { if (strcmp(words[j], w) == 0) { dup = true; break; } }
            if (dup) { free(w); continue; }
            if (count == cap) {
                size_t nc = cap ? cap * 2 : 8;
                char **n = (char **)realloc(words, nc * sizeof(char *));
                if (!n) { free(w); break; }
                words = n; cap = nc;
            }
            words[count++] = w;
        }
    }
    free(buf);
    *out = words;
    *out_count = count;
}

ca_knowledge_triple_t *ca_kg_extract_from_turn(const char *user_text,
                                               const char *assistant_text,
                                               const char *source_episode_id,
                                               size_t *out_count) {
    if (out_count) *out_count = 0;

    /* Memory id: source when present, else user text. */
    const char *memory = (!ca_is_blank(source_episode_id)) ? source_episode_id : user_text;
    if (ca_is_blank(memory)) return NULL;

    /* combined = user + " " + assistant */
    const char *u = user_text ? user_text : "";
    const char *a = assistant_text ? assistant_text : "";
    size_t clen = strlen(u) + 1 + strlen(a) + 1;
    char *combined = (char *)malloc(clen);
    if (!combined) return NULL;
    snprintf(combined, clen, "%s %s", u, a);

    char **words = NULL; size_t wn = 0;
    ca_kg_content_words(combined, &words, &wn);
    free(combined);

    if (wn == 0) { free(words); return NULL; }

    size_t total = wn * 2;
    ca_knowledge_triple_t *triples = (ca_knowledge_triple_t *)calloc(total, sizeof(*triples));
    if (!triples) { for (size_t i = 0; i < wn; ++i) free(words[i]); free(words); return NULL; }
    int64_t now = ca_now_ms();
    size_t t = 0;
    for (size_t i = 0; i < wn; ++i) {
        /* memory --mentions--> word */
        triples[t].subject = ca_dup(memory);
        triples[t].predicate = ca_dup("mentions");
        triples[t].object = ca_dup(words[i]);
        triples[t].source = ca_dup(source_episode_id);
        triples[t].confidence = 0.6;
        triples[t].recorded_at_ms = now;
        t++;
        /* word --seenin--> memory */
        triples[t].subject = ca_dup(words[i]);
        triples[t].predicate = ca_dup("seenin");
        triples[t].object = ca_dup(memory);
        triples[t].source = ca_dup(source_episode_id);
        triples[t].confidence = 0.6;
        triples[t].recorded_at_ms = now;
        t++;
    }
    for (size_t i = 0; i < wn; ++i) free(words[i]);
    free(words);
    if (out_count) *out_count = total;
    return triples;
}

ca_knowledge_triple_t *ca_kg_extractor_heuristic_adapter(void *user,
                                                         const char *user_text,
                                                         const char *assistant_text,
                                                         const char *source_episode_id,
                                                         size_t *out_count) {
    (void)user;
    return ca_kg_extract_from_turn(user_text, assistant_text, source_episode_id, out_count);
}
