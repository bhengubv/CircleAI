/*
 * test_mesh_capability.c — CircleAI.AetherNet mesh capability discovery
 * (mesh_capability.h).
 *
 * Verifies:
 *   Advertisement    : deep copy, optional latency hint
 *   Registry         : upsert (insert + replace latest-per-peer), remove
 *                      idempotency, count
 *   List             : full snapshot; stale filter (advertised_at >= cutoff)
 *   Find             : model filter (OrdinalIgnoreCase), min free-KV, stale
 *                      filter, sort by free-KV DESCENDING (stable for ties)
 *   Broadcasters     : NullMeshCapabilityBroadcaster no-op; capturing
 *                      broadcaster records last advertisement + count
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

/* Mutable test clock. */
static int64_t g_now = 100000;
static int64_t test_now(void *user) { (void)user; return g_now; }

static ca_mesh_capability_advertisement_t *ad(const char *peer,
                                              const char *model, int freekv,
                                              int64_t at) {
    return ca_mesh_capability_advertisement_create(
        peer, model, freekv, CA_TIER_PHONE, 2048, at, false, 0);
}

/* ---------------------------------------------------------------------------
 * Advertisement copy
 * --------------------------------------------------------------------------- */
static void test_advertisement(void) {
    ca_mesh_capability_advertisement_t *a =
        ca_mesh_capability_advertisement_create("peer1", "Qwen3-1.7B-MNN", 512,
                                                CA_TIER_PHONE, 4096, 500, true,
                                                42);
    assert(a);
    assert(strcmp(a->peer_id, "peer1") == 0);
    assert(strcmp(a->model_id, "Qwen3-1.7B-MNN") == 0);
    assert(a->free_kv_tokens == 512 && a->context_window_tokens == 4096);
    assert(a->has_latency_hint && a->latency_hint_ms == 42);

    ca_mesh_capability_advertisement_t *c =
        ca_mesh_capability_advertisement_copy(a);
    assert(c && c->peer_id != a->peer_id);
    assert(strcmp(c->model_id, a->model_id) == 0);
    assert(c->has_latency_hint && c->latency_hint_ms == 42);
    ca_mesh_capability_advertisement_destroy(a);
    ca_mesh_capability_advertisement_destroy(c);
}

/* ---------------------------------------------------------------------------
 * Upsert / replace / remove
 * --------------------------------------------------------------------------- */
static void test_upsert_remove(void) {
    ca_mesh_capability_registry_t *reg =
        ca_mesh_capability_registry_create(test_now, NULL);
    assert(reg && ca_mesh_capability_registry_count(reg) == 0);

    ca_mesh_capability_advertisement_t *a1 = ad("p1", "modelA", 100, 100000);
    assert(ca_mesh_capability_registry_upsert(reg, a1) == 0);
    ca_mesh_capability_advertisement_destroy(a1); /* registry deep-copied */
    assert(ca_mesh_capability_registry_count(reg) == 1);

    /* Replace same peer -> still 1, new value wins. */
    ca_mesh_capability_advertisement_t *a1b = ad("p1", "modelA", 250, 100050);
    assert(ca_mesh_capability_registry_upsert(reg, a1b) == 0);
    ca_mesh_capability_advertisement_destroy(a1b);
    assert(ca_mesh_capability_registry_count(reg) == 1);

    ca_mesh_capability_advertisement_t **list = NULL;
    size_t n = ca_mesh_capability_registry_list(reg, false, 0, &list);
    assert(n == 1 && list[0]->free_kv_tokens == 250);
    ca_mesh_capability_advertisement_list_free(list, n);

    /* Bad args: NULL ad, whitespace peer id. */
    assert(ca_mesh_capability_registry_upsert(reg, NULL) == -1);
    ca_mesh_capability_advertisement_t *bad = ad("   ", "m", 1, 1);
    assert(ca_mesh_capability_registry_upsert(reg, bad) == -1);
    ca_mesh_capability_advertisement_destroy(bad);

    /* Remove idempotency. */
    assert(ca_mesh_capability_registry_remove(reg, "p1"));
    assert(!ca_mesh_capability_registry_remove(reg, "p1")); /* already gone */
    assert(!ca_mesh_capability_registry_remove(reg, "  "));  /* whitespace */
    assert(ca_mesh_capability_registry_count(reg) == 0);

    ca_mesh_capability_registry_destroy(reg);
}

/* ---------------------------------------------------------------------------
 * List with staleness filter
 * --------------------------------------------------------------------------- */
static void test_list_stale(void) {
    ca_mesh_capability_registry_t *reg =
        ca_mesh_capability_registry_create(test_now, NULL);
    g_now = 100000;

    ca_mesh_capability_advertisement_t *fresh = ad("fresh", "m", 10, 99000);
    ca_mesh_capability_advertisement_t *old = ad("old", "m", 10, 20000);
    ca_mesh_capability_registry_upsert(reg, fresh);
    ca_mesh_capability_registry_upsert(reg, old);
    ca_mesh_capability_advertisement_destroy(fresh);
    ca_mesh_capability_advertisement_destroy(old);

    /* No stale filter -> both. */
    ca_mesh_capability_advertisement_t **list = NULL;
    size_t n = ca_mesh_capability_registry_list(reg, false, 0, &list);
    assert(n == 2);
    ca_mesh_capability_advertisement_list_free(list, n);

    /* staleAfter = 5000ms -> cutoff = 95000; only 'fresh' (99000 >= 95000). */
    n = ca_mesh_capability_registry_list(reg, true, 5000, &list);
    assert(n == 1 && strcmp(list[0]->peer_id, "fresh") == 0);
    ca_mesh_capability_advertisement_list_free(list, n);

    ca_mesh_capability_registry_destroy(reg);
}

/* ---------------------------------------------------------------------------
 * Find: model filter + min free-KV + descending stable sort
 * --------------------------------------------------------------------------- */
static void test_find(void) {
    ca_mesh_capability_registry_t *reg =
        ca_mesh_capability_registry_create(test_now, NULL);
    g_now = 100000;

    /* Three peers on modelX with differing budgets; one on modelY; one stale. */
    ca_mesh_capability_advertisement_t *x_hi = ad("x_hi", "modelX", 900, 99000);
    ca_mesh_capability_advertisement_t *x_lo = ad("x_lo", "modelX", 100, 99000);
    ca_mesh_capability_advertisement_t *x_mid = ad("x_mid", "MODELX", 500, 99000);
    ca_mesh_capability_advertisement_t *y = ad("y", "modelY", 999, 99000);
    ca_mesh_capability_advertisement_t *x_stale = ad("x_stale", "modelX", 800, 10000);
    ca_mesh_capability_registry_upsert(reg, x_hi);
    ca_mesh_capability_registry_upsert(reg, x_lo);
    ca_mesh_capability_registry_upsert(reg, x_mid);
    ca_mesh_capability_registry_upsert(reg, y);
    ca_mesh_capability_registry_upsert(reg, x_stale);
    ca_mesh_capability_advertisement_destroy(x_hi);
    ca_mesh_capability_advertisement_destroy(x_lo);
    ca_mesh_capability_advertisement_destroy(x_mid);
    ca_mesh_capability_advertisement_destroy(y);
    ca_mesh_capability_advertisement_destroy(x_stale);

    /* Find modelX, no min, no stale: 4 peers (case-insensitive match), sorted
     * by free-KV DESC: x_hi(900), x_stale(800), x_mid(500), x_lo(100). */
    ca_mesh_capability_advertisement_t **r = NULL;
    size_t n = ca_mesh_capability_registry_find(reg, "modelX", 0, false, 0, &r);
    assert(n == 4);
    assert(r[0]->free_kv_tokens == 900 && strcmp(r[0]->peer_id, "x_hi") == 0);
    assert(r[1]->free_kv_tokens == 800 && strcmp(r[1]->peer_id, "x_stale") == 0);
    assert(r[2]->free_kv_tokens == 500 && strcmp(r[2]->peer_id, "x_mid") == 0);
    assert(r[3]->free_kv_tokens == 100 && strcmp(r[3]->peer_id, "x_lo") == 0);
    ca_mesh_capability_advertisement_list_free(r, n);

    /* min free-KV = 500 -> only 900,800,500. */
    n = ca_mesh_capability_registry_find(reg, "modelX", 500, false, 0, &r);
    assert(n == 3 && r[0]->free_kv_tokens == 900 && r[2]->free_kv_tokens == 500);
    ca_mesh_capability_advertisement_list_free(r, n);

    /* stale filter cutoff 95000 drops x_stale(10000) -> 900,500,100. */
    n = ca_mesh_capability_registry_find(reg, "modelX", 0, true, 5000, &r);
    assert(n == 3);
    assert(strcmp(r[0]->peer_id, "x_hi") == 0);
    assert(strcmp(r[1]->peer_id, "x_mid") == 0);
    assert(strcmp(r[2]->peer_id, "x_lo") == 0);
    ca_mesh_capability_advertisement_list_free(r, n);

    /* No match -> 0, NULL. */
    n = ca_mesh_capability_registry_find(reg, "nope", 0, false, 0, &r);
    assert(n == 0 && r == NULL);

    /* Bad args -> SIZE_MAX. */
    assert(ca_mesh_capability_registry_find(reg, "  ", 0, false, 0, &r) ==
           (size_t)-1);
    assert(ca_mesh_capability_registry_find(NULL, "modelX", 0, false, 0, &r) ==
           (size_t)-1);

    ca_mesh_capability_registry_destroy(reg);
}

/* Stable tie-break: equal budgets preserve insertion order. */
static void test_find_stable_ties(void) {
    ca_mesh_capability_registry_t *reg =
        ca_mesh_capability_registry_create(test_now, NULL);
    /* Insert in a known order; all same budget. */
    const char *ids[] = { "a", "b", "c", "d" };
    for (int i = 0; i < 4; i++) {
        ca_mesh_capability_advertisement_t *a = ad(ids[i], "m", 300, 99000);
        ca_mesh_capability_registry_upsert(reg, a);
        ca_mesh_capability_advertisement_destroy(a);
    }
    ca_mesh_capability_advertisement_t **r = NULL;
    size_t n = ca_mesh_capability_registry_find(reg, "m", 0, false, 0, &r);
    assert(n == 4);
    assert(strcmp(r[0]->peer_id, "a") == 0);
    assert(strcmp(r[1]->peer_id, "b") == 0);
    assert(strcmp(r[2]->peer_id, "c") == 0);
    assert(strcmp(r[3]->peer_id, "d") == 0);
    ca_mesh_capability_advertisement_list_free(r, n);
    ca_mesh_capability_registry_destroy(reg);
}

/* ---------------------------------------------------------------------------
 * Broadcasters
 * --------------------------------------------------------------------------- */
static void test_broadcasters(void) {
    ca_mesh_capability_advertisement_t *a = ad("me", "modelZ", 700, 500);

    /* Null broadcaster: no-op, always succeeds. */
    ca_mesh_capability_broadcaster_t nullb = ca_null_mesh_capability_broadcaster();
    assert(nullb.broadcast(nullb.self, a) == 0);

    /* Capturing broadcaster: records last + count. */
    ca_capturing_broadcaster_t *cap = ca_capturing_broadcaster_create();
    assert(cap && ca_capturing_broadcaster_count(cap) == 0);
    assert(ca_capturing_broadcaster_last(cap) == NULL);

    ca_mesh_capability_broadcaster_t cb =
        ca_capturing_broadcaster_as_broadcaster(cap);
    assert(cb.broadcast(cb.self, a) == 0);
    assert(ca_capturing_broadcaster_count(cap) == 1);
    const ca_mesh_capability_advertisement_t *last =
        ca_capturing_broadcaster_last(cap);
    assert(last && strcmp(last->model_id, "modelZ") == 0 &&
           last->free_kv_tokens == 700);

    /* Second broadcast replaces last, bumps count. */
    ca_mesh_capability_advertisement_t *a2 = ad("me", "modelZ", 650, 600);
    assert(cb.broadcast(cb.self, a2) == 0);
    assert(ca_capturing_broadcaster_count(cap) == 2);
    assert(ca_capturing_broadcaster_last(cap)->free_kv_tokens == 650);
    ca_mesh_capability_advertisement_destroy(a2);

    ca_capturing_broadcaster_destroy(cap);
    ca_mesh_capability_advertisement_destroy(a);
}

int main(void) {
    test_advertisement();
    test_upsert_remove();
    test_list_stale();
    test_find();
    test_find_stable_ties();
    test_broadcasters();
    return 0;
}
