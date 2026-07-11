/*
 * simulation.c — CircleAI.Simulation (C11 port).
 *
 * Immutable-ish knowledge graph (nodes/edges keyed by UUID id, last-write wins),
 * the episodic graph extractor, the deterministic diffusion engine, and the
 * anomaly -> ThreatPropagation scenario factory. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/simulation.h"
#include "circle_ai/memory_brain.h" /* ca_uuid_v4, CA_UUID_STR_LEN */
#include "board_common.h"
#include <stdio.h>
#include <inttypes.h>

/* ── kv helpers ─────────────────────────────────────────────────────────── */

static void skv_free(ca_sim_kv_t *kv, size_t n) {
    if (!kv) return;
    for (size_t i = 0; i < n; ++i) { free(kv[i].key); free(kv[i].value); }
    free(kv);
}
static bool skv_copy(ca_sim_kv_t **out, const ca_sim_kv_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_sim_kv_t *v = (ca_sim_kv_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { skv_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}
static bool skv_push(ca_sim_kv_t **v, size_t *n, size_t *cap, const char *k, const char *val) {
    if (*n == *cap) {
        size_t nc = *cap ? *cap * 2 : 8;
        void *nv = realloc(*v, nc * sizeof(ca_sim_kv_t));
        if (!nv) return false;
        *v = (ca_sim_kv_t *)nv; *cap = nc;
    }
    (*v)[*n].key = cab_strdup_empty(k);
    (*v)[*n].value = cab_strdup_empty(val);
    if (!(*v)[*n].key || !(*v)[*n].value) return false;
    (*n)++;
    return true;
}

/* ── GraphNode ──────────────────────────────────────────────────────────── */

void ca_graph_node_free(ca_graph_node_t *n) {
    if (!n) return;
    free(n->id); free(n->label); free(n->kind);
    skv_free(n->properties, n->property_count);
    memset(n, 0, sizeof(*n));
}
void ca_graph_node_free_array(ca_graph_node_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_graph_node_free(&arr[i]);
    free(arr);
}
static bool node_copy(ca_graph_node_t *dst, const ca_graph_node_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->extracted_at_ms = src->extracted_at_ms;
    dst->id = cab_strdup_empty(src->id);
    dst->label = cab_strdup_empty(src->label);
    dst->kind = cab_strdup_empty(src->kind);
    if (!dst->id || !dst->label || !dst->kind) { ca_graph_node_free(dst); return false; }
    if (!skv_copy(&dst->properties, src->properties, src->property_count)) {
        ca_graph_node_free(dst); return false;
    }
    dst->property_count = src->property_count;
    return true;
}
bool ca_graph_node_create(const char *label, const char *kind,
                          const ca_sim_kv_t *props, size_t prop_count,
                          int64_t now_ms, ca_graph_node_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    out->id = cab_strdup_empty(uuid);
    out->label = cab_strdup_empty(label ? label : "");
    out->kind = cab_strdup_empty(kind ? kind : "");
    out->extracted_at_ms = now_ms;
    if (!out->id || !out->label || !out->kind) { ca_graph_node_free(out); return false; }
    if (!skv_copy(&out->properties, props, prop_count)) { ca_graph_node_free(out); return false; }
    out->property_count = prop_count;
    return true;
}

/* ── GraphEdge ──────────────────────────────────────────────────────────── */

void ca_graph_edge_free(ca_graph_edge_t *e) {
    if (!e) return;
    free(e->id); free(e->source_id); free(e->target_id); free(e->relation);
    memset(e, 0, sizeof(*e));
}
void ca_graph_edge_free_array(ca_graph_edge_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_graph_edge_free(&arr[i]);
    free(arr);
}
static bool edge_copy(ca_graph_edge_t *dst, const ca_graph_edge_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->weight = src->weight;
    dst->created_at_ms = src->created_at_ms;
    dst->id = cab_strdup_empty(src->id);
    dst->source_id = cab_strdup_empty(src->source_id);
    dst->target_id = cab_strdup_empty(src->target_id);
    dst->relation = cab_strdup_empty(src->relation);
    if (!dst->id || !dst->source_id || !dst->target_id || !dst->relation) {
        ca_graph_edge_free(dst); return false;
    }
    return true;
}
bool ca_graph_edge_create(const char *source_id, const char *target_id,
                          const char *relation, float weight, int64_t now_ms,
                          ca_graph_edge_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    if (weight < 0.0f) weight = 0.0f;
    if (weight > 1.0f) weight = 1.0f;
    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    out->id = cab_strdup_empty(uuid);
    out->source_id = cab_strdup_empty(source_id ? source_id : "");
    out->target_id = cab_strdup_empty(target_id ? target_id : "");
    out->relation = cab_strdup_empty(relation ? relation : "");
    out->weight = weight;
    out->created_at_ms = now_ms;
    if (!out->id || !out->source_id || !out->target_id || !out->relation) {
        ca_graph_edge_free(out); return false;
    }
    return true;
}

/* ── KnowledgeGraph ─────────────────────────────────────────────────────── */

struct ca_knowledge_graph_sim {
    ca_graph_node_t *nodes; size_t node_count, node_cap;
    ca_graph_edge_t *edges; size_t edge_count, edge_cap;
};

ca_knowledge_graph_sim_t *ca_knowledge_graph_sim_create(void) {
    return (ca_knowledge_graph_sim_t *)calloc(1, sizeof(ca_knowledge_graph_sim_t));
}
void ca_knowledge_graph_sim_destroy(ca_knowledge_graph_sim_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->node_count; ++i) ca_graph_node_free(&g->nodes[i]);
    free(g->nodes);
    for (size_t i = 0; i < g->edge_count; ++i) ca_graph_edge_free(&g->edges[i]);
    free(g->edges);
    free(g);
}

int ca_knowledge_graph_sim_add_node(ca_knowledge_graph_sim_t *g,
                                    const ca_graph_node_t *node) {
    if (!g || !node || !node->id) return -1;
    for (size_t i = 0; i < g->node_count; ++i) {
        if (cab_ord_eq(g->nodes[i].id, node->id)) {
            ca_graph_node_t copy;
            if (!node_copy(&copy, node)) return -1;
            ca_graph_node_free(&g->nodes[i]);
            g->nodes[i] = copy;
            return 0;
        }
    }
    ca_graph_node_t copy;
    if (!node_copy(&copy, node)) return -1;
    if (g->node_count == g->node_cap) {
        size_t nc = g->node_cap ? g->node_cap * 2 : 8;
        void *n = realloc(g->nodes, nc * sizeof(*g->nodes));
        if (!n) { ca_graph_node_free(&copy); return -1; }
        g->nodes = (ca_graph_node_t *)n; g->node_cap = nc;
    }
    g->nodes[g->node_count++] = copy;
    return 0;
}
int ca_knowledge_graph_sim_add_edge(ca_knowledge_graph_sim_t *g,
                                    const ca_graph_edge_t *edge) {
    if (!g || !edge || !edge->id) return -1;
    for (size_t i = 0; i < g->edge_count; ++i) {
        if (cab_ord_eq(g->edges[i].id, edge->id)) {
            ca_graph_edge_t copy;
            if (!edge_copy(&copy, edge)) return -1;
            ca_graph_edge_free(&g->edges[i]);
            g->edges[i] = copy;
            return 0;
        }
    }
    ca_graph_edge_t copy;
    if (!edge_copy(&copy, edge)) return -1;
    if (g->edge_count == g->edge_cap) {
        size_t nc = g->edge_cap ? g->edge_cap * 2 : 8;
        void *n = realloc(g->edges, nc * sizeof(*g->edges));
        if (!n) { ca_graph_edge_free(&copy); return -1; }
        g->edges = (ca_graph_edge_t *)n; g->edge_cap = nc;
    }
    g->edges[g->edge_count++] = copy;
    return 0;
}
size_t ca_knowledge_graph_sim_node_count(const ca_knowledge_graph_sim_t *g) {
    return g ? g->node_count : 0;
}
size_t ca_knowledge_graph_sim_edge_count(const ca_knowledge_graph_sim_t *g) {
    return g ? g->edge_count : 0;
}

ca_graph_edge_t *ca_knowledge_graph_sim_edges_for(const ca_knowledge_graph_sim_t *g,
                                                  const char *node_id,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    if (!g || cab_is_ws(node_id)) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < g->edge_count; ++i)
        if (cab_ord_eq(g->edges[i].source_id, node_id) ||
            cab_ord_eq(g->edges[i].target_id, node_id)) n++;
    if (n == 0) { *out_count = 0; return NULL; }
    ca_graph_edge_t *out = (ca_graph_edge_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < g->edge_count; ++i) {
        if (!(cab_ord_eq(g->edges[i].source_id, node_id) ||
              cab_ord_eq(g->edges[i].target_id, node_id))) continue;
        if (!edge_copy(&out[k], &g->edges[i])) {
            ca_graph_edge_free_array(out, k);
            *out_count = (size_t)-1; return NULL;
        }
        k++;
    }
    *out_count = n;
    return out;
}

static const ca_graph_node_t *graph_find_node(const ca_knowledge_graph_sim_t *g,
                                              const char *id) {
    for (size_t i = 0; i < g->node_count; ++i)
        if (cab_ord_eq(g->nodes[i].id, id)) return &g->nodes[i];
    return NULL;
}

ca_graph_node_t *ca_knowledge_graph_sim_reachable_from(const ca_knowledge_graph_sim_t *g,
                                                       const char *start_id,
                                                       size_t *out_count) {
    if (!out_count) return NULL;
    if (!g || cab_is_ws(start_id)) { *out_count = (size_t)-1; return NULL; }

    /* BFS over node ids. */
    char **visited = NULL; size_t vis_n = 0, vis_cap = 0;
    char **queue = NULL; size_t q_head = 0, q_n = 0, q_cap = 0;
    ca_graph_node_t *result = NULL; size_t res_n = 0, res_cap = 0;
    bool ok = true;

    #define ENQUEUE(idstr) do { \
        if (q_n == q_cap) { size_t nc = q_cap ? q_cap * 2 : 8; char **nq = realloc(queue, nc * sizeof(char*)); if (!nq) { ok = false; } else { queue = nq; q_cap = nc; } } \
        if (ok) { queue[q_n] = cab_strdup_empty(idstr); if (!queue[q_n]) ok = false; else q_n++; } \
    } while (0)

    ENQUEUE(start_id);
    while (ok && q_head < q_n) {
        char *cur = queue[q_head++];
        /* visited check + add */
        bool seen = false;
        for (size_t i = 0; i < vis_n; ++i) if (cab_ord_eq(visited[i], cur)) { seen = true; break; }
        if (seen) continue;
        if (vis_n == vis_cap) { size_t nc = vis_cap ? vis_cap * 2 : 8; char **nv = realloc(visited, nc * sizeof(char*)); if (!nv) { ok = false; break; } visited = nv; vis_cap = nc; }
        visited[vis_n] = cab_strdup_empty(cur); if (!visited[vis_n]) { ok = false; break; } vis_n++;

        const ca_graph_node_t *node = graph_find_node(g, cur);
        if (node) {
            if (res_n == res_cap) { size_t nc = res_cap ? res_cap * 2 : 8; void *nr = realloc(result, nc * sizeof(ca_graph_node_t)); if (!nr) { ok = false; break; } result = (ca_graph_node_t *)nr; res_cap = nc; }
            if (!node_copy(&result[res_n], node)) { ok = false; break; }
            res_n++;
        }
        /* enqueue neighbours */
        for (size_t i = 0; ok && i < g->edge_count; ++i) {
            const char *next = NULL;
            if (cab_ord_eq(g->edges[i].source_id, cur)) next = g->edges[i].target_id;
            else if (cab_ord_eq(g->edges[i].target_id, cur)) next = g->edges[i].source_id;
            if (!next) continue;
            bool nvis = false;
            for (size_t j = 0; j < vis_n; ++j) if (cab_ord_eq(visited[j], next)) { nvis = true; break; }
            if (!nvis) ENQUEUE(next);
        }
    }
    #undef ENQUEUE

    cab_strv_free(visited, vis_n);
    cab_strv_free(queue, q_n);
    if (!ok) { ca_graph_node_free_array(result, res_n); *out_count = (size_t)-1; return NULL; }
    if (res_n == 0) { free(result); *out_count = 0; return NULL; }
    *out_count = res_n;
    return result;
}

int ca_knowledge_graph_sim_merge(ca_knowledge_graph_sim_t *g,
                                 const ca_knowledge_graph_sim_t *other) {
    if (!g || !other) return -1;
    for (size_t i = 0; i < other->node_count; ++i)
        if (ca_knowledge_graph_sim_add_node(g, &other->nodes[i]) != 0) return -1;
    for (size_t i = 0; i < other->edge_count; ++i)
        if (ca_knowledge_graph_sim_add_edge(g, &other->edges[i]) != 0) return -1;
    return 0;
}

/* ── Scenario / Result ──────────────────────────────────────────────────── */

void ca_simulation_scenario_free(ca_simulation_scenario_t *s) {
    if (!s) return;
    free(s->id); free(s->description);
    skv_free(s->parameters, s->parameter_count);
    memset(s, 0, sizeof(*s));
}
bool ca_simulation_scenario_create(ca_scenario_kind_t kind, const char *description,
                                   const ca_sim_kv_t *params, size_t param_count,
                                   int steps, int64_t now_ms,
                                   ca_simulation_scenario_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return false;
    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    out->id = cab_strdup_empty(uuid);
    out->kind = kind;
    out->description = cab_strdup_empty(description ? description : "");
    out->step_count = steps;
    out->created_at_ms = now_ms;
    if (!out->id || !out->description) { ca_simulation_scenario_free(out); return false; }
    if (!skv_copy(&out->parameters, params, param_count)) { ca_simulation_scenario_free(out); return false; }
    out->parameter_count = param_count;
    return true;
}

void ca_simulation_result_free(ca_simulation_result_t *r) {
    if (!r) return;
    free(r->scenario_id);
    cab_strv_free(r->findings, r->finding_count);
    cab_strv_free(r->recommendations, r->recommendation_count);
    memset(r, 0, sizeof(*r));
}

/* ── EpisodicGraphExtractor.Build ───────────────────────────────────────── */

/* Sort entry indices by recorded_at asc (stable). */
static void sort_by_time_asc(const ca_episodic_entry_t *e, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i]; int64_t kt = e[key].recorded_at_ms; size_t j = i;
        while (j > 0 && e[idx[j - 1]].recorded_at_ms > kt) { idx[j] = idx[j - 1]; j--; }
        idx[j] = key;
    }
}

ca_knowledge_graph_sim_t *ca_episodic_graph_extractor_build(
    const ca_episodic_entry_t *entries, size_t entry_count, int64_t now_ms) {
    ca_knowledge_graph_sim_t *g = ca_knowledge_graph_sim_create();
    if (!g) return NULL;
    if (entry_count == 0) return g;

    size_t *idx = (size_t *)malloc(entry_count * sizeof(size_t));
    if (!idx) { ca_knowledge_graph_sim_destroy(g); return NULL; }
    for (size_t i = 0; i < entry_count; ++i) idx[i] = i;
    sort_by_time_asc(entries, idx, entry_count);

    /* app + topic node registries (name -> node id, CI keyed) */
    typedef struct { char *name; char *id; } named_id_t;
    named_id_t *apps = NULL; size_t app_n = 0, app_cap = 0;
    named_id_t *topics = NULL; size_t topic_n = 0, topic_cap = 0;

    char *prev_id = NULL;
    int64_t prev_time = 0;
    bool has_prev = false;
    bool ok = true;

    for (size_t i = 0; ok && i < entry_count; ++i) {
        const ca_episodic_entry_t *e = &entries[idx[i]];
        const char *ut = e->user_text ? e->user_text : "";
        char label[64];
        size_t ll = strlen(ut); if (ll > 60) ll = 60;
        if (ll >= sizeof(label)) ll = sizeof(label) - 1;
        memcpy(label, ut, ll); label[ll] = '\0';

        ca_graph_node_t ev;
        ca_sim_kv_t prop = { (char *)"episode_id", (char *)(e->id ? e->id : "") };
        if (!ca_graph_node_create(label, "event", &prop, 1, now_ms, &ev)) { ok = false; break; }
        char *ev_id = cab_strdup_empty(ev.id);
        if (!ev_id) { ca_graph_node_free(&ev); ok = false; break; }
        ok = (ca_knowledge_graph_sim_add_node(g, &ev) == 0);
        ca_graph_node_free(&ev);
        if (!ok) { free(ev_id); break; }

        /* app context node + occurred_in edge */
        if (e->app_context && !cab_is_ws(e->app_context)) {
            char *app_id = NULL;
            for (size_t a = 0; a < app_n; ++a) if (cab_ci_eq(apps[a].name, e->app_context)) { app_id = apps[a].id; break; }
            if (!app_id) {
                ca_graph_node_t an;
                if (!ca_graph_node_create(e->app_context, "app", NULL, 0, now_ms, &an)) { ok = false; free(ev_id); break; }
                app_id = cab_strdup_empty(an.id);
                char *name = cab_strdup_empty(e->app_context);
                ok = app_id && name && (ca_knowledge_graph_sim_add_node(g, &an) == 0);
                ca_graph_node_free(&an);
                if (!ok) { free(app_id); free(name); free(ev_id); break; }
                if (app_n == app_cap) { size_t nc = app_cap ? app_cap * 2 : 4; void *na = realloc(apps, nc * sizeof(named_id_t)); if (!na) { free(app_id); free(name); free(ev_id); ok = false; break; } apps = (named_id_t *)na; app_cap = nc; }
                apps[app_n].name = name; apps[app_n].id = app_id; app_n++;
            }
            ca_graph_edge_t edge;
            if (!ca_graph_edge_create(ev_id, app_id, "occurred_in", 1.0f, now_ms, &edge)) { ok = false; free(ev_id); break; }
            ok = (ca_knowledge_graph_sim_add_edge(g, &edge) == 0);
            ca_graph_edge_free(&edge);
            if (!ok) { free(ev_id); break; }
        }

        /* tags -> topic nodes + tagged_with edges */
        for (size_t t = 0; ok && t < e->tag_count; ++t) {
            const char *tag = e->tag_keys[t];
            char *topic_id = NULL;
            for (size_t a = 0; a < topic_n; ++a) if (cab_ci_eq(topics[a].name, tag)) { topic_id = topics[a].id; break; }
            if (!topic_id) {
                ca_graph_node_t tn;
                if (!ca_graph_node_create(tag, "topic", NULL, 0, now_ms, &tn)) { ok = false; break; }
                topic_id = cab_strdup_empty(tn.id);
                char *name = cab_strdup_empty(tag);
                ok = topic_id && name && (ca_knowledge_graph_sim_add_node(g, &tn) == 0);
                ca_graph_node_free(&tn);
                if (!ok) { free(topic_id); free(name); break; }
                if (topic_n == topic_cap) { size_t nc = topic_cap ? topic_cap * 2 : 4; void *na = realloc(topics, nc * sizeof(named_id_t)); if (!na) { free(topic_id); free(name); ok = false; break; } topics = (named_id_t *)na; topic_cap = nc; }
                topics[topic_n].name = name; topics[topic_n].id = topic_id; topic_n++;
            }
            ca_graph_edge_t edge;
            if (!ca_graph_edge_create(ev_id, topic_id, "tagged_with", 1.0f, now_ms, &edge)) { ok = false; break; }
            ok = (ca_knowledge_graph_sim_add_edge(g, &edge) == 0);
            ca_graph_edge_free(&edge);
        }
        if (!ok) { free(ev_id); break; }

        /* followed_by edge from previous event if within 1 hour */
        if (has_prev && (e->recorded_at_ms - prev_time) <= 3600000LL) {
            ca_graph_edge_t edge;
            if (!ca_graph_edge_create(prev_id, ev_id, "followed_by", 0.5f, now_ms, &edge)) { ok = false; free(ev_id); break; }
            ok = (ca_knowledge_graph_sim_add_edge(g, &edge) == 0);
            ca_graph_edge_free(&edge);
            if (!ok) { free(ev_id); break; }
        }

        free(prev_id);
        prev_id = ev_id;
        prev_time = e->recorded_at_ms;
        has_prev = true;
    }

    free(prev_id);
    for (size_t a = 0; a < app_n; ++a) { free(apps[a].name); free(apps[a].id); }
    free(apps);
    for (size_t a = 0; a < topic_n; ++a) { free(topics[a].name); free(topics[a].id); }
    free(topics);
    free(idx);

    if (!ok) { ca_knowledge_graph_sim_destroy(g); return NULL; }
    return g;
}

/* ── LocalSimulationEngine.Run ──────────────────────────────────────────── */

static bool str_vec_push(char ***v, size_t *n, size_t *cap, const char *s) {
    if (*n == *cap) {
        size_t nc = *cap ? *cap * 2 : 4;
        char **nv = (char **)realloc(*v, nc * sizeof(char *));
        if (!nv) return false;
        *v = nv; *cap = nc;
    }
    (*v)[*n] = cab_strdup_empty(s);
    if (!(*v)[*n]) return false;
    (*n)++;
    return true;
}

bool ca_simulation_engine_run(const ca_simulation_scenario_t *scenario,
                              const ca_knowledge_graph_sim_t *graph,
                              int64_t completed_ms, ca_simulation_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!scenario || !graph || !out) return false;

    const float DECAY = 0.01f;
    const float HIGH = 0.7f;
    float health = 1.0f;

    /* collect high-impact node labels (distinct, first-seen order). */
    char **high = NULL; size_t high_n = 0, high_cap = 0;
    bool ok = true;

    for (int step = 0; step < scenario->step_count && health > 0.0f; ++step) {
        for (size_t i = 0; i < graph->edge_count; ++i) {
            health -= (1.0f - graph->edges[i].weight) * DECAY;
            if (graph->edges[i].weight >= HIGH) {
                const ca_graph_node_t *src = graph_find_node(graph, graph->edges[i].source_id);
                if (src) {
                    bool dup = false;
                    for (size_t h = 0; h < high_n; ++h) if (cab_ord_eq(high[h], src->label)) { dup = true; break; }
                    if (!dup) { if (!str_vec_push(&high, &high_n, &high_cap, src->label)) { ok = false; break; } }
                }
            }
        }
        if (!ok) break;
    }
    if (health < 0.0f) health = 0.0f;
    if (health > 1.0f) health = 1.0f;

    ca_simulation_outcome_t outcome;
    if (health >= 0.8f) outcome = CA_SIM_OUTCOME_HEALTHY;
    else if (health >= 0.5f) outcome = CA_SIM_OUTCOME_DEGRADED;
    else if (health >= 0.2f) outcome = CA_SIM_OUTCOME_CRITICAL;
    else outcome = CA_SIM_OUTCOME_UNKNOWN;

    /* findings */
    char **findings = NULL; size_t find_n = 0, find_cap = 0;
    if (ok) {
        if (high_n > 0) {
            for (size_t h = 0; ok && h < high_n; ++h) {
                char msg[256];
                snprintf(msg, sizeof(msg), "High-impact node detected: %s", high[h]);
                ok = str_vec_push(&findings, &find_n, &find_cap, msg);
            }
        } else {
            ok = str_vec_push(&findings, &find_n, &find_cap, "No high-impact nodes detected.");
        }
    }
    cab_strv_free(high, high_n);

    /* recommendations */
    char **recs = NULL; size_t rec_n = 0, rec_cap = 0;
    if (ok) {
        if (outcome == CA_SIM_OUTCOME_DEGRADED || outcome == CA_SIM_OUTCOME_CRITICAL) {
            ok = str_vec_push(&recs, &rec_n, &rec_cap, "Review high-weight edges before deployment.") &&
                 str_vec_push(&recs, &rec_n, &rec_cap, "Consider incremental rollout.");
        } else {
            ok = str_vec_push(&recs, &rec_n, &rec_cap, "Network health nominal — proceed with deployment.");
        }
    }

    if (!ok) {
        cab_strv_free(findings, find_n);
        cab_strv_free(recs, rec_n);
        return false;
    }

    out->scenario_id = cab_strdup_empty(scenario->id);
    if (!out->scenario_id) {
        cab_strv_free(findings, find_n); cab_strv_free(recs, rec_n);
        return false;
    }
    out->outcome = outcome;
    out->health_score = health;
    out->findings = find_n ? findings : (free(findings), NULL);
    out->finding_count = find_n;
    out->recommendations = rec_n ? recs : (free(recs), NULL);
    out->recommendation_count = rec_n;
    out->steps_run = scenario->step_count;
    out->completed_at_ms = completed_ms;
    return true;
}

bool ca_network_health_forecast(const ca_episodic_entry_t *history, size_t history_count,
                                const ca_simulation_scenario_t *scenario,
                                int64_t now_ms, ca_simulation_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!scenario || !out || (!history && history_count > 0)) return false;
    ca_knowledge_graph_sim_t *g = ca_episodic_graph_extractor_build(history, history_count, now_ms);
    if (!g) return false;
    bool ok = ca_simulation_engine_run(scenario, g, now_ms, out);
    ca_knowledge_graph_sim_destroy(g);
    return ok;
}

/* ── ThreatPropagationScenario.FromAnomalySignal ────────────────────────── */

static const char *threat_vector_name(ca_threat_vector_t v) {
    switch (v) {
        case CA_THREAT_MEMORY_ANOMALY:          return "MemoryAnomaly";
        case CA_THREAT_CONTROL_FLOW_DRIFT:      return "ControlFlowDrift";
        case CA_THREAT_PRIVILEGE_ESCALATION:    return "PrivilegeEscalation";
        case CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT: return "BiometricSpoofAttempt";
        case CA_THREAT_NETWORK_PIVOT:           return "NetworkPivot";
        case CA_THREAT_STATE_CORRUPTION:        return "StateCorruption";
        case CA_THREAT_AGENT_PATCH_REJECTED:    return "AgentPatchRejected";
        default:                                return "Unknown";
    }
}
static int step_count_for(ca_threat_vector_t v) {
    switch (v) {
        case CA_THREAT_NETWORK_PIVOT:           return 30;
        case CA_THREAT_CONTROL_FLOW_DRIFT:      return 25;
        case CA_THREAT_PRIVILEGE_ESCALATION:    return 25;
        case CA_THREAT_STATE_CORRUPTION:        return 20;
        case CA_THREAT_MEMORY_ANOMALY:          return 15;
        case CA_THREAT_AGENT_PATCH_REJECTED:    return 15;
        case CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT: return 12;
        default:                                return 10;
    }
}

bool ca_threat_propagation_from_anomaly(const ca_anomaly_signal_t *signal,
                                        int step_override, int64_t now_ms,
                                        ca_simulation_scenario_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!signal || !out) return false;

    const char *vname = threat_vector_name(signal->vector);
    char conf3[16], confpct[16];
    snprintf(conf3, sizeof(conf3), "%.3f", (double)signal->confidence);
    snprintf(confpct, sizeof(confpct), "%.0f%%", (double)signal->confidence * 100.0);
    char detected[32];
    snprintf(detected, sizeof(detected), "%" PRId64, signal->detected_at_unix_ms);

    /* parameters (the C anomaly schema has no free-form Evidence dict) */
    ca_sim_kv_t *params = NULL; size_t pn = 0, pcap = 0;
    bool ok = skv_push(&params, &pn, &pcap, "signal_id", signal->id) &&
              skv_push(&params, &pn, &pcap, "vector", vname) &&
              skv_push(&params, &pn, &pcap, "confidence", conf3) &&
              skv_push(&params, &pn, &pcap, "affected_module", signal->affected_module) &&
              skv_push(&params, &pn, &pcap, "detected_at", detected);
    if (!ok) { skv_free(params, pn); return false; }

    char desc[512];
    snprintf(desc, sizeof(desc), "threat-propagation: %s in %s (confidence %s)",
             vname, signal->affected_module, confpct);

    int steps = step_override >= 0 ? step_override : step_count_for(signal->vector);

    ok = ca_simulation_scenario_create(CA_SCENARIO_THREAT_PROPAGATION, desc,
                                       params, pn, steps, now_ms, out);
    skv_free(params, pn);
    return ok;
}
