/*
 * test_games.c — CircleAI.Games (C11 port) verification against Contracts.cs +
 * InMemoryGames.cs + NullImplementations.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* tick handler counts + last frame */
typedef struct { int calls; int last_frame; int64_t last_elapsed; } tick_acc_t;
static void on_tick(void *ctx, const ca_games_tick_t *t) {
    tick_acc_t *a = (tick_acc_t *)ctx;
    a->calls++;
    a->last_frame = t->frame;
    a->last_elapsed = t->elapsed_ticks;
}

/* input handler records last action */
typedef struct { int calls; char action[64]; int keys; } input_acc_t;
static void on_input(void *ctx, const ca_games_input_event_t *e) {
    input_acc_t *a = (input_acc_t *)ctx;
    a->calls++;
    strncpy(a->action, e->action, sizeof(a->action) - 1);
    a->keys = e->has_payload ? (int)e->key_count : -1;
}

static void test_loop(void) {
    ca_games_loop_t *loop = ca_games_loop_create();
    assert(loop && strcmp(ca_games_loop_backend_id(loop), "timer") == 0);

    assert(ca_games_loop_start(loop, 0.0, 0) == -1);     /* fps<=0 */
    assert(ca_games_loop_start(loop, 60.0, 1000) == 0);  /* 60fps -> 16ms */
    assert(ca_games_loop_frame_period_ms(loop) == 16);
    assert(ca_games_loop_running(loop));
    assert(ca_games_loop_start(loop, 30.0, 0) == -2);    /* already started */

    tick_acc_t acc; memset(&acc, 0, sizeof(acc));
    ca_games_loop_sub_t *sub = ca_games_loop_subscribe(loop, on_tick, &acc);
    assert(sub);

    /* Two ticks at 1016ms, 1032ms -> frames 1,2; elapsed 16ms, 32ms in ticks. */
    assert(ca_games_loop_tick(loop, 1016) == 1);
    assert(ca_games_loop_tick(loop, 1032) == 1);
    assert(acc.calls == 2 && acc.last_frame == 2);
    assert(acc.last_elapsed == 32LL * 10000LL);

    /* Unsubscribe -> handler no longer fires (acc frozen), though the loop's
     * internal frame counter still advances (tick returns 0 subscribers). */
    ca_games_loop_unsubscribe(loop, sub);
    assert(ca_games_loop_tick(loop, 1048) == 0);
    assert(acc.calls == 2 && acc.last_frame == 2); /* handler didn't run */

    /* Stop -> tick no-ops. */
    assert(ca_games_loop_stop(loop) == 0);
    assert(!ca_games_loop_running(loop));
    assert(ca_games_loop_tick(loop, 2000) == 0);

    ca_games_loop_destroy(loop);

    /* Null loop. */
    ca_games_loop_t *nl = ca_games_null_loop_create();
    assert(strcmp(ca_games_loop_backend_id(nl), "null") == 0);
    assert(ca_games_loop_start(nl, 60.0, 0) == 0);
    ca_games_loop_sub_t *ns = ca_games_loop_subscribe(nl, on_tick, &acc);
    assert(ns);
    assert(ca_games_loop_tick(nl, 100) == 0);
    ca_games_loop_unsubscribe(nl, ns);
    ca_games_loop_destroy(nl);
    printf("  loop: ok\n");
}

static void test_input(void) {
    ca_games_input_map_t *map = ca_games_input_map_create();
    assert(map && strcmp(ca_games_input_map_backend_id(map), "in-memory") == 0);

    input_acc_t acc; memset(&acc, 0, sizeof(acc));
    ca_games_input_sub_t *sub = ca_games_input_map_subscribe(map, on_input, &acc);
    assert(sub);

    char *keys[] = { (char *)"dir" };
    char *vals[] = { (char *)"up" };
    ca_games_input_event_t ev; memset(&ev, 0, sizeof(ev));
    ev.action = (char *)"jump"; ev.has_payload = true;
    ev.payload_keys = keys; ev.payload_values = vals; ev.key_count = 1;
    assert(ca_games_input_map_raise(map, &ev) == 1);
    assert(acc.calls == 1 && strcmp(acc.action, "jump") == 0 && acc.keys == 1);

    /* null-payload event. */
    ca_games_input_event_t ev2; memset(&ev2, 0, sizeof(ev2));
    ev2.action = (char *)"fire"; ev2.has_payload = false;
    assert(ca_games_input_map_raise(map, &ev2) == 1);
    assert(strcmp(acc.action, "fire") == 0 && acc.keys == -1);

    ca_games_input_map_unsubscribe(map, sub);
    assert(ca_games_input_map_raise(map, &ev2) == 0);
    ca_games_input_map_destroy(map);

    /* Null input map. */
    ca_games_input_map_t *nm = ca_games_null_input_map_create();
    assert(strcmp(ca_games_input_map_backend_id(nm), "null") == 0);
    assert(ca_games_input_map_raise(nm, &ev2) == 0);
    ca_games_input_map_destroy(nm);
    printf("  input: ok\n");
}

static void test_scene(void) {
    ca_games_scene_graph_t *g = ca_games_scene_graph_create();
    assert(g && strcmp(ca_games_scene_graph_backend_id(g), "in-memory") == 0);

    ca_games_scene_node_t n1; memset(&n1, 0, sizeof(n1));
    n1.node_id = (char *)"n1"; n1.kind = (char *)"sprite"; n1.x = 1; n1.y = 2; n1.z = 3;
    ca_games_scene_node_t n2; memset(&n2, 0, sizeof(n2));
    n2.node_id = (char *)"n2"; n2.kind = (char *)"light";
    ca_games_scene_node_t bad; memset(&bad, 0, sizeof(bad));
    bad.node_id = (char *)"  "; bad.kind = (char *)"x";

    assert(ca_games_scene_graph_add(g, &bad) == -1); /* blank NodeId */
    assert(ca_games_scene_graph_add(g, &n1) == 0);
    assert(ca_games_scene_graph_add(g, &n2) == 0);

    size_t n = 0;
    ca_games_scene_node_t *snap = ca_games_scene_graph_snapshot(g, &n);
    assert(n == 2 && strcmp(snap[0].node_id, "n1") == 0 && snap[0].x == 1.0);
    ca_games_scene_node_free_array(snap, n);

    /* Remove n1. */
    assert(ca_games_scene_graph_remove(g, "n1") == 0);
    snap = ca_games_scene_graph_snapshot(g, &n);
    assert(n == 1 && strcmp(snap[0].node_id, "n2") == 0);
    ca_games_scene_node_free_array(snap, n);

    ca_games_scene_graph_destroy(g);

    /* Null graph. */
    ca_games_scene_graph_t *ng = ca_games_null_scene_graph_create();
    assert(strcmp(ca_games_scene_graph_backend_id(ng), "null") == 0);
    assert(ca_games_scene_graph_add(ng, &n1) == 0);
    snap = ca_games_scene_graph_snapshot(ng, &n);
    assert(snap == NULL && n == 0);
    ca_games_scene_graph_destroy(ng);
    printf("  scene: ok\n");
}

int main(void) {
    test_loop();
    test_input();
    test_scene();
    printf("test_games: all assertions passed\n");
    return 0;
}
