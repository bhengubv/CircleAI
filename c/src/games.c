/*
 * games.c — CircleAI.Games (C11 port of Contracts.cs + InMemoryGames.cs +
 * NullImplementations.cs).
 *
 * TimerGameLoop is modelled as an explicit-advance loop (ca_games_loop_tick);
 * InMemoryInputMap fans out raised events; InMemorySceneGraph is a NodeId-keyed
 * set. Fan-out snapshots the subscriber list first so a handler may unsubscribe
 * mid-dispatch. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/games.h"
#include "board_common.h"

/* ── records ────────────────────────────────────────────────────────────── */

void ca_games_input_event_free(ca_games_input_event_t *e) {
    if (!e) return;
    free(e->action);
    cab_strv_free(e->payload_keys, e->key_count);
    cab_strv_free(e->payload_values, e->key_count);
    e->action = NULL;
    e->payload_keys = e->payload_values = NULL;
    e->key_count = 0;
    e->has_payload = false;
}

/* InputEvent is passed to subscribers borrowed (mirrors InMemoryInputMap, which
 * hands the raised ev straight to each handler — no copy), so no deep-copy helper
 * is needed here; ca_games_input_event_free frees a caller-owned event. */

void ca_games_scene_node_free(ca_games_scene_node_t *n) {
    if (!n) return;
    free(n->node_id);
    free(n->kind);
    n->node_id = n->kind = NULL;
}
void ca_games_scene_node_free_array(ca_games_scene_node_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_games_scene_node_free(&arr[i]);
    free(arr);
}

static bool scene_node_copy(ca_games_scene_node_t *dst,
                            const ca_games_scene_node_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->node_id = cab_strdup_empty(src->node_id);
    dst->kind    = cab_strdup_empty(src->kind);
    dst->x = src->x; dst->y = src->y; dst->z = src->z;
    if (!dst->node_id || !dst->kind) {
        ca_games_scene_node_free(dst);
        return false;
    }
    return true;
}

/* ── IGameLoop ──────────────────────────────────────────────────────────── */

typedef struct {
    ca_games_tick_handler_fn fn;
    void                    *ctx;
    bool                     live;
} loop_sub_slot_t;

struct ca_games_loop {
    bool             is_null;
    const char      *backend_id;
    bool             running;
    int              frame;
    int64_t          start_ms;
    int              frame_period_ms;
    loop_sub_slot_t *subs;
    size_t           sub_count, sub_cap;
};
struct ca_games_loop_sub { ca_games_loop_t *owner; size_t slot; };

static ca_games_loop_t *loop_new(bool is_null, const char *backend_id) {
    ca_games_loop_t *l = (ca_games_loop_t *)calloc(1, sizeof(*l));
    if (!l) return NULL;
    l->is_null = is_null;
    l->backend_id = backend_id;
    return l;
}
ca_games_loop_t *ca_games_loop_create(void) { return loop_new(false, "timer"); }
ca_games_loop_t *ca_games_null_loop_create(void) { return loop_new(true, "null"); }

void ca_games_loop_destroy(ca_games_loop_t *loop) {
    if (!loop) return;
    free(loop->subs);
    free(loop);
}

const char *ca_games_loop_backend_id(const ca_games_loop_t *loop) {
    return loop ? loop->backend_id : NULL;
}

int ca_games_loop_start(ca_games_loop_t *loop, double target_fps, int64_t now_ms) {
    if (!loop) return -1;
    if (loop->is_null) return 0;
    if (target_fps <= 0) return -1;
    if (loop->running) return -2; /* already started */
    int ms = (int)(1000.0 / target_fps);
    if (ms < 1) ms = 1;
    loop->frame_period_ms = ms;
    loop->start_ms = now_ms;
    loop->frame = 0;
    loop->running = true;
    return 0;
}

int ca_games_loop_stop(ca_games_loop_t *loop) {
    if (!loop) return -1;
    loop->running = false;
    return 0;
}

bool ca_games_loop_running(const ca_games_loop_t *loop) {
    return loop && loop->running;
}
int ca_games_loop_frame_period_ms(const ca_games_loop_t *loop) {
    return loop ? loop->frame_period_ms : 0;
}

int ca_games_loop_tick(ca_games_loop_t *loop, int64_t now_ms) {
    if (!loop || loop->is_null || !loop->running) return 0;
    int frame = ++loop->frame;
    ca_games_tick_t tick;
    tick.frame = frame;
    tick.elapsed_ticks = (now_ms - loop->start_ms) * 10000LL; /* ms -> 100ns ticks */

    /* Snapshot live handlers, then invoke outside the snapshot so a handler may
     * unsubscribe safely (mirrors _subs.ToArray() before the foreach). */
    size_t cnt = 0;
    for (size_t i = 0; i < loop->sub_count; ++i)
        if (loop->subs[i].live) cnt++;
    if (cnt == 0) return 0;
    loop_sub_slot_t *snap = (loop_sub_slot_t *)malloc(cnt * sizeof(*snap));
    if (!snap) return 0;
    size_t k = 0;
    for (size_t i = 0; i < loop->sub_count; ++i)
        if (loop->subs[i].live) snap[k++] = loop->subs[i];
    for (size_t i = 0; i < cnt; ++i) snap[i].fn(snap[i].ctx, &tick);
    free(snap);
    return (int)cnt;
}

ca_games_loop_sub_t *ca_games_loop_subscribe(ca_games_loop_t *loop,
                                             ca_games_tick_handler_fn handler,
                                             void *ctx) {
    if (!loop || !handler) return NULL;
    ca_games_loop_sub_t *tok =
        (ca_games_loop_sub_t *)calloc(1, sizeof(*tok));
    if (!tok) return NULL;
    if (loop->is_null) { tok->owner = loop; tok->slot = (size_t)-1; return tok; }
    if (loop->sub_count == loop->sub_cap) {
        size_t nc = loop->sub_cap ? loop->sub_cap * 2 : 4;
        void *n = realloc(loop->subs, nc * sizeof(*loop->subs));
        if (!n) { free(tok); return NULL; }
        loop->subs = (loop_sub_slot_t *)n;
        loop->sub_cap = nc;
    }
    size_t slot = loop->sub_count++;
    loop->subs[slot].fn = handler;
    loop->subs[slot].ctx = ctx;
    loop->subs[slot].live = true;
    tok->owner = loop;
    tok->slot = slot;
    return tok;
}

void ca_games_loop_unsubscribe(ca_games_loop_t *loop, ca_games_loop_sub_t *sub) {
    if (!sub) return;
    if (loop && !loop->is_null && sub->slot != (size_t)-1 &&
        sub->slot < loop->sub_count)
        loop->subs[sub->slot].live = false;
    free(sub);
}

/* ── IInputMap ──────────────────────────────────────────────────────────── */

typedef struct {
    ca_games_input_handler_fn fn;
    void                     *ctx;
    bool                      live;
} input_sub_slot_t;

struct ca_games_input_map {
    bool              is_null;
    const char       *backend_id;
    input_sub_slot_t *subs;
    size_t            sub_count, sub_cap;
};
struct ca_games_input_sub { ca_games_input_map_t *owner; size_t slot; };

static ca_games_input_map_t *input_new(bool is_null, const char *backend_id) {
    ca_games_input_map_t *m = (ca_games_input_map_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->is_null = is_null;
    m->backend_id = backend_id;
    return m;
}
ca_games_input_map_t *ca_games_input_map_create(void) {
    return input_new(false, "in-memory");
}
ca_games_input_map_t *ca_games_null_input_map_create(void) {
    return input_new(true, "null");
}
void ca_games_input_map_destroy(ca_games_input_map_t *map) {
    if (!map) return;
    free(map->subs);
    free(map);
}
const char *ca_games_input_map_backend_id(const ca_games_input_map_t *map) {
    return map ? map->backend_id : NULL;
}

int ca_games_input_map_raise(ca_games_input_map_t *map,
                             const ca_games_input_event_t *ev) {
    if (!map || !ev || !ev->action) return -1;
    if (map->is_null) return 0;
    size_t cnt = 0;
    for (size_t i = 0; i < map->sub_count; ++i)
        if (map->subs[i].live) cnt++;
    if (cnt == 0) return 0;
    input_sub_slot_t *snap = (input_sub_slot_t *)malloc(cnt * sizeof(*snap));
    if (!snap) return 0;
    size_t k = 0;
    for (size_t i = 0; i < map->sub_count; ++i)
        if (map->subs[i].live) snap[k++] = map->subs[i];
    for (size_t i = 0; i < cnt; ++i) snap[i].fn(snap[i].ctx, ev);
    free(snap);
    return (int)cnt;
}

ca_games_input_sub_t *ca_games_input_map_subscribe(
    ca_games_input_map_t *map, ca_games_input_handler_fn handler, void *ctx) {
    if (!map || !handler) return NULL;
    ca_games_input_sub_t *tok = (ca_games_input_sub_t *)calloc(1, sizeof(*tok));
    if (!tok) return NULL;
    if (map->is_null) { tok->owner = map; tok->slot = (size_t)-1; return tok; }
    if (map->sub_count == map->sub_cap) {
        size_t nc = map->sub_cap ? map->sub_cap * 2 : 4;
        void *n = realloc(map->subs, nc * sizeof(*map->subs));
        if (!n) { free(tok); return NULL; }
        map->subs = (input_sub_slot_t *)n;
        map->sub_cap = nc;
    }
    size_t slot = map->sub_count++;
    map->subs[slot].fn = handler;
    map->subs[slot].ctx = ctx;
    map->subs[slot].live = true;
    tok->owner = map;
    tok->slot = slot;
    return tok;
}

void ca_games_input_map_unsubscribe(ca_games_input_map_t *map,
                                    ca_games_input_sub_t *sub) {
    if (!sub) return;
    if (map && !map->is_null && sub->slot != (size_t)-1 &&
        sub->slot < map->sub_count)
        map->subs[sub->slot].live = false;
    free(sub);
}

/* ── ISceneGraph ────────────────────────────────────────────────────────── */

struct ca_games_scene_graph {
    bool                   is_null;
    const char            *backend_id;
    ca_games_scene_node_t *nodes;
    size_t                 count, cap;
};

static ca_games_scene_graph_t *graph_new(bool is_null, const char *backend_id) {
    ca_games_scene_graph_t *g = (ca_games_scene_graph_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->is_null = is_null;
    g->backend_id = backend_id;
    return g;
}
ca_games_scene_graph_t *ca_games_scene_graph_create(void) {
    return graph_new(false, "in-memory");
}
ca_games_scene_graph_t *ca_games_null_scene_graph_create(void) {
    return graph_new(true, "null");
}
void ca_games_scene_graph_destroy(ca_games_scene_graph_t *g) {
    if (!g) return;
    for (size_t i = 0; i < g->count; ++i) ca_games_scene_node_free(&g->nodes[i]);
    free(g->nodes);
    free(g);
}
const char *ca_games_scene_graph_backend_id(const ca_games_scene_graph_t *g) {
    return g ? g->backend_id : NULL;
}

int ca_games_scene_graph_add(ca_games_scene_graph_t *g,
                             const ca_games_scene_node_t *node) {
    if (!g || !node) return -1;
    if (cab_is_ws(node->node_id)) return -1; /* NodeId required */
    if (g->is_null) return 0;
    for (size_t i = 0; i < g->count; ++i) {
        if (cab_ord_eq(g->nodes[i].node_id, node->node_id)) {
            ca_games_scene_node_t copy;
            if (!scene_node_copy(&copy, node)) return -1;
            ca_games_scene_node_free(&g->nodes[i]);
            g->nodes[i] = copy;
            return 0;
        }
    }
    ca_games_scene_node_t copy;
    if (!scene_node_copy(&copy, node)) return -1;
    if (g->count == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 4;
        void *n = realloc(g->nodes, nc * sizeof(*g->nodes));
        if (!n) { ca_games_scene_node_free(&copy); return -1; }
        g->nodes = (ca_games_scene_node_t *)n;
        g->cap = nc;
    }
    g->nodes[g->count++] = copy;
    return 0;
}

int ca_games_scene_graph_remove(ca_games_scene_graph_t *g, const char *node_id) {
    if (!g || cab_is_ws(node_id)) return -1;
    if (g->is_null) return 0;
    for (size_t i = 0; i < g->count; ++i) {
        if (cab_ord_eq(g->nodes[i].node_id, node_id)) {
            ca_games_scene_node_free(&g->nodes[i]);
            /* Preserve insertion order of the survivors. */
            for (size_t j = i + 1; j < g->count; ++j) g->nodes[j - 1] = g->nodes[j];
            g->count--;
            return 0;
        }
    }
    return 0;
}

ca_games_scene_node_t *ca_games_scene_graph_snapshot(
    const ca_games_scene_graph_t *g, size_t *out_count) {
    if (!out_count) return NULL;
    if (!g) { *out_count = (size_t)-1; return NULL; }
    if (g->count == 0) { *out_count = 0; return NULL; }
    ca_games_scene_node_t *out =
        (ca_games_scene_node_t *)calloc(g->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < g->count; ++i) {
        if (!scene_node_copy(&out[i], &g->nodes[i])) {
            ca_games_scene_node_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = g->count;
    return out;
}
