#ifndef CIRCLE_AI_GAMES_H
#define CIRCLE_AI_GAMES_H

/*
 * games.h — CircleAI.Games (C11 port of Contracts.cs + InMemoryGames.cs +
 * NullImplementations.cs).
 *
 *   Records : GameTick(int Frame, TimeSpan Elapsed);
 *             InputEvent(Action, IReadOnlyDictionary<string,string>? Payload);
 *             SceneNode(NodeId, Kind, double X, double Y, double Z).
 *   Contracts:
 *     IGameLoop   -> TimerGameLoop + NullGameLoop.
 *                    BackendId; Start(targetFps=60) / Stop; Subscribe(handler)
 *                    -> disposable token. Ticks fan out to subscribers.
 *     IInputMap   -> InMemoryInputMap + NullInputMap. BackendId; Raise(ev) fans
 *                    out; Subscribe(handler) -> token.
 *     ISceneGraph -> InMemorySceneGraph + NullSceneGraph. BackendId; Add(node)
 *                    (NodeId keyed, blank rejected); Remove(nodeId); Snapshot().
 *
 * The C# TimerGameLoop drives OnTick from a System.Threading.Timer. To stay
 * deterministic + single-threaded, the port drives ticks explicitly: Start records
 * the frame-period + a monotonic base (caller-supplied ms) and toggles running;
 * ca_games_loop_tick(now_ms) increments the frame and fans out a GameTick with
 * Elapsed = now - start (as the C# tick handler computes DateTime.UtcNow - _start),
 * only while running. Handler exceptions in C# are swallowed and logged; here the
 * handler is a plain callback. Subscriber lists are snapshotted before dispatch so
 * a handler may unsubscribe mid-fan-out. TimeSpan carried as .NET ticks (100ns).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* GameTick(int Frame, TimeSpan Elapsed). */
typedef struct {
    int     frame;
    int64_t elapsed_ticks; /* TimeSpan ticks (100ns) */
} ca_games_tick_t;

/* InputEvent(Action, Payload?). Payload is an optional string->string map carried
 * as parallel owned arrays; has_payload==false is the C# null Payload (note: an
 * empty-but-present map still has has_payload==true, key_count==0). */
typedef struct {
    char   *action;        /* owned, non-null */
    bool    has_payload;   /* false == C# null Payload */
    char  **payload_keys;  /* owned array of owned strings (len key_count) */
    char  **payload_values;/* owned array of owned strings (len key_count) */
    size_t  key_count;
} ca_games_input_event_t;

void ca_games_input_event_free(ca_games_input_event_t *e);

/* SceneNode(NodeId, Kind, double X, double Y, double Z). */
typedef struct {
    char   *node_id;   /* owned, non-null */
    char   *kind;      /* owned, non-null */
    double  x, y, z;
} ca_games_scene_node_t;

void ca_games_scene_node_free(ca_games_scene_node_t *n);
void ca_games_scene_node_free_array(ca_games_scene_node_t *arr, size_t count);

/* ── IGameLoop ──────────────────────────────────────────────────────────── */

typedef struct ca_games_loop ca_games_loop_t;
typedef struct ca_games_loop_sub ca_games_loop_sub_t;

typedef void (*ca_games_tick_handler_fn)(void *ctx, const ca_games_tick_t *tick);

/* TimerGameLoop() (BackendId "timer"). NULL on OOM. */
ca_games_loop_t *ca_games_loop_create(void);
/* NullGameLoop (BackendId "null"; Start/Stop no-op; Subscribe inert token; tick
 * fans out to nobody). NULL on OOM. */
ca_games_loop_t *ca_games_null_loop_create(void);
void ca_games_loop_destroy(ca_games_loop_t *loop);

const char *ca_games_loop_backend_id(const ca_games_loop_t *loop);

/* StartAsync(targetFps, now_ms). targetFps must be > 0 (else -1). Starting an
 * already-started loop returns -2 (C# InvalidOperationException). now_ms is the
 * monotonic base for Elapsed. On the Null loop this is a no-op returning 0. */
int ca_games_loop_start(ca_games_loop_t *loop, double target_fps, int64_t now_ms);

/* StopAsync() — stops the loop (idempotent). 0. */
int ca_games_loop_stop(ca_games_loop_t *loop);

/* Whether the loop is currently running. */
bool ca_games_loop_running(const ca_games_loop_t *loop);

/* The frame-period the loop was started with, in ms: max(1, (int)(1000/fps)).
 * 0 when not started. */
int ca_games_loop_frame_period_ms(const ca_games_loop_t *loop);

/* Drive one tick at now_ms: increments the frame and fans out a GameTick with
 * Elapsed = now_ms - start, but only while running. Returns the number of
 * subscribers notified, or 0 when stopped / on the Null loop. Mirrors the C#
 * Timer's OnTick (which snapshots subscribers, then invokes each). */
int ca_games_loop_tick(ca_games_loop_t *loop, int64_t now_ms);

/* Subscribe(handler) -> owned token (dispose to unsubscribe). handler required.
 * NULL on bad args/OOM. On the Null loop returns a live-but-inert token. */
ca_games_loop_sub_t *ca_games_loop_subscribe(ca_games_loop_t *loop,
                                             ca_games_tick_handler_fn handler,
                                             void *ctx);
void ca_games_loop_unsubscribe(ca_games_loop_t *loop, ca_games_loop_sub_t *sub);

/* ── IInputMap ──────────────────────────────────────────────────────────── */

typedef struct ca_games_input_map ca_games_input_map_t;
typedef struct ca_games_input_sub ca_games_input_sub_t;

typedef void (*ca_games_input_handler_fn)(void *ctx,
                                          const ca_games_input_event_t *ev);

/* InMemoryInputMap() (BackendId "in-memory"). NULL on OOM. */
ca_games_input_map_t *ca_games_input_map_create(void);
/* NullInputMap (BackendId "null"; Subscribe inert; Raise fans out to nobody). */
ca_games_input_map_t *ca_games_null_input_map_create(void);
void ca_games_input_map_destroy(ca_games_input_map_t *map);

const char *ca_games_input_map_backend_id(const ca_games_input_map_t *map);

/* Raise(ev) — fans out a borrowed InputEvent to every live subscriber, snapshotting
 * the list first. ev required (its Action non-null). Returns subscriber count
 * notified, or -1 on bad args. No-op (0) on the Null map. */
int ca_games_input_map_raise(ca_games_input_map_t *map,
                             const ca_games_input_event_t *ev);

/* Subscribe(handler) -> owned token. handler required. NULL on bad args/OOM. */
ca_games_input_sub_t *ca_games_input_map_subscribe(
    ca_games_input_map_t *map, ca_games_input_handler_fn handler, void *ctx);
void ca_games_input_map_unsubscribe(ca_games_input_map_t *map,
                                    ca_games_input_sub_t *sub);

/* ── ISceneGraph ────────────────────────────────────────────────────────── */

typedef struct ca_games_scene_graph ca_games_scene_graph_t;

/* InMemorySceneGraph() (BackendId "in-memory"). NULL on OOM. */
ca_games_scene_graph_t *ca_games_scene_graph_create(void);
/* NullSceneGraph (BackendId "null"; Add/Remove no-op; Snapshot empty). */
ca_games_scene_graph_t *ca_games_null_scene_graph_create(void);
void ca_games_scene_graph_destroy(ca_games_scene_graph_t *g);

const char *ca_games_scene_graph_backend_id(const ca_games_scene_graph_t *g);

/* AddAsync(node) — NodeId keyed set; NodeId must be non-null / non-whitespace
 * (else -1). 0 on success, -1 on bad args / OOM. No-op (0) on the Null graph. */
int ca_games_scene_graph_add(ca_games_scene_graph_t *g,
                             const ca_games_scene_node_t *node);

/* RemoveAsync(nodeId) — removes it if present. nodeId must be non-null /
 * non-whitespace (else -1). 0 on success. No-op (0) on the Null graph. */
int ca_games_scene_graph_remove(ca_games_scene_graph_t *g, const char *node_id);

/* SnapshotAsync() -> fresh owned array (insertion order). NULL + 0 empty;
 * NULL + SIZE_MAX on error. Always empty on the Null graph. */
ca_games_scene_node_t *ca_games_scene_graph_snapshot(
    const ca_games_scene_graph_t *g, size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_GAMES_H */
