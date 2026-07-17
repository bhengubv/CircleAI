/*
 * test_neuron.c — the Neuron (CircleAI.Hosting.Neuron), C11.
 *
 * Mirrors the C# CircleAI.Tests Neuron suite: the concierge decision table +
 * gate, the two-slot admission gate + eviction, the NeuronNode facade over a
 * brain, and NullChatRuntime.
 */

#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "circle_ai/circle_ai.h"

/* ── test doubles ─────────────────────────────────────────────────────────── */

static int64_t ram_1m(void *u) {
    (void)u;
    return 1000000;
}

static int g_builds = 0;
static int g_token; /* a non-NULL handle */

static void *build_ok(const char *id, void *u) {
    (void)id;
    (void)u;
    g_builds++;
    return &g_token;
}

static void *build_null(const char *id, void *u) {
    (void)id;
    (void)u;
    return NULL;
}

static bool gate_deny(const ca_route_decision_t *d, void *u) {
    (void)d;
    (void)u;
    return false;
}

/* A fake brain for the NeuronNode. */
typedef struct {
    bool        ready;
    const char *model_id;
    const char *reply;
} fake_brain_t;

static bool fb_is_ready(void *impl) { return ((fake_brain_t *)impl)->ready; }
static const char *fb_model_id(void *impl) { return ((fake_brain_t *)impl)->model_id; }
static void fb_stream(void *impl, const ca_chat_turn_t *turns, size_t n,
                      ca_chunk_callback cb, void *ud) {
    (void)turns;
    (void)n;
    if (cb) cb(((fake_brain_t *)impl)->reply, ud);
}
static bool fb_save(void *impl, const char *path) { (void)impl; (void)path; return true; }
static bool fb_load(void *impl, const char *path) { (void)impl; (void)path; return true; }

static char g_out[256];
static void collect(const char *chunk, void *ud) {
    (void)ud;
    strncat(g_out, chunk, sizeof g_out - strlen(g_out) - 1);
}

/* ── tests ────────────────────────────────────────────────────────────────── */

static void test_router(void) {
    ca_neuron_router_t r;
    ca_neuron_router_init(&r);

    /* plain → generalist(DEFAULT) */
    ca_route_context_t plain = { "what's the weather today?", false, -1 };
    ca_route_decision_t d = ca_neuron_route(&r, &plain);
    assert(d.organ == CA_ORGAN_GENERALIST);
    assert(d.capability == CA_CHAT_CAP_DEFAULT);

    /* image → specialist(VISION) */
    ca_route_context_t vis = { "what is this?", true, -1 };
    d = ca_neuron_route(&r, &vis);
    assert(d.organ == CA_ORGAN_SPECIALIST);
    assert(d.capability == CA_CHAT_CAP_VISION);

    /* reasoning cue → specialist(REASONING) */
    ca_route_context_t rea = { "please debug this stack trace", false, -1 };
    d = ca_neuron_route(&r, &rea);
    assert(d.organ == CA_ORGAN_SPECIALIST);
    assert(d.capability == CA_CHAT_CAP_REASONING);

    /* long prompt → specialist(LONG_CTX) */
    ca_neuron_router_t rl;
    ca_neuron_router_init(&rl);
    rl.long_context_chars = 50;
    char big[61];
    memset(big, 'x', 60);
    big[60] = '\0';
    ca_route_context_t lng = { big, false, -1 };
    d = ca_neuron_route(&rl, &lng);
    assert(d.organ == CA_ORGAN_SPECIALIST);
    assert(d.capability == CA_CHAT_CAP_LONG_CTX);

    /* gate veto demotes a specialist back to the generalist */
    ca_neuron_router_t rg;
    ca_neuron_router_init(&rg);
    rg.gate = gate_deny;
    ca_route_context_t solve = { "solve this equation", false, -1 };
    d = ca_neuron_route(&rg, &solve);
    assert(d.organ == CA_ORGAN_GENERALIST);
}

static void test_slot_manager(void) {
    ca_resident_slot_manager_t m;
    void *out = NULL;

    /* admits within budget */
    ca_slot_manager_init(&m, 1000, ram_1m, NULL);
    assert(ca_slot_ensure_specialist(&m, "spec", 5000, build_ok, NULL, &out) == CA_SLOT_ADMITTED);
    assert(out == &g_token);
    assert(ca_slot_resident_model_id(&m) && strcmp(ca_slot_resident_model_id(&m), "spec") == 0);

    /* denies over budget */
    ca_slot_manager_init(&m, 900000, ram_1m, NULL);
    assert(ca_slot_ensure_specialist(&m, "spec", 500000, build_ok, NULL, &out) == CA_SLOT_INSUFFICIENT_RAM);
    assert(ca_slot_resident_model_id(&m) == NULL);

    /* already resident → build once */
    ca_slot_manager_init(&m, 0, ram_1m, NULL);
    g_builds = 0;
    assert(ca_slot_ensure_specialist(&m, "spec", 1, build_ok, NULL, NULL) == CA_SLOT_ADMITTED);
    assert(ca_slot_ensure_specialist(&m, "spec", 1, build_ok, NULL, NULL) == CA_SLOT_ALREADY_RESIDENT);
    assert(g_builds == 1);

    /* a different pick evicts the incumbent */
    ca_slot_manager_init(&m, 0, ram_1m, NULL);
    ca_slot_ensure_specialist(&m, "A", 1, build_ok, NULL, NULL);
    ca_slot_ensure_specialist(&m, "B", 1, build_ok, NULL, NULL);
    assert(strcmp(ca_slot_resident_model_id(&m), "B") == 0);

    /* build failure leaves the slot empty */
    ca_slot_manager_init(&m, 0, ram_1m, NULL);
    assert(ca_slot_ensure_specialist(&m, "spec", 1, build_null, NULL, NULL) == CA_SLOT_BUILD_FAILED);
    assert(ca_slot_resident_model_id(&m) == NULL);

    /* evict clears the slot */
    ca_slot_manager_init(&m, 0, ram_1m, NULL);
    ca_slot_ensure_specialist(&m, "spec", 1, build_ok, NULL, NULL);
    ca_slot_evict(&m);
    assert(ca_slot_resident_model_id(&m) == NULL);
}

static void test_neuron_node(void) {
    fake_brain_t fb = { false, "qwen-x", "hello" };
    ca_neuron_brain_t brain = {
        fb_is_ready, fb_model_id, fb_stream, fb_save, fb_load, &fb
    };
    ca_neuron_node_t node;
    ca_neuron_node_init(&node, brain, NULL);

    assert(strcmp(ca_neuron_node_id(&node), "circleai-neuron") == 0);
    assert(!ca_neuron_node_is_ready(&node));
    assert(strstr(ca_neuron_node_status(&node), "loading") != NULL);

    fb.ready = true;
    assert(ca_neuron_node_is_ready(&node));
    assert(strcmp(ca_neuron_node_status(&node), "ready") == 0);
    assert(strstr(ca_neuron_node_engine_label(&node), "qwen-x") != NULL);

    g_out[0] = '\0';
    ca_chat_turn_t turns[1] = { { "user", "hi" } };
    ca_neuron_node_stream(&node, turns, 1, collect, NULL);
    assert(strcmp(g_out, "hello") == 0);

    assert(ca_neuron_node_snapshot_path(&node) != NULL);
    assert(ca_neuron_node_save_session(&node, "/tmp/x") == true);
    assert(ca_neuron_node_load_session(&node, "/tmp/x") == true);
}

static void test_null_runtime(void) {
    assert(ca_null_chat_runtime_is_ready() == false);
    g_out[0] = '\0';
    ca_chat_turn_t turns[1] = { { "user", "hi" } };
    ca_null_chat_runtime_stream(turns, 1, collect, NULL);
    assert(strstr(g_out, "No chat engine") != NULL);
}

int main(void) {
    test_router();
    test_slot_manager();
    test_neuron_node();
    test_null_runtime();
    printf("test_neuron: all assertions passed\n");
    return 0;
}
