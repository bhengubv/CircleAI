/*
 * test_inference_server_enterprise.c — Enterprise-tier inference primitives.
 *
 * Mirrors InMemoryInferenceServerEnterprise + NullImplementations: the
 * round-robin tenant router (node cycling + quota get/set + de-dup), the
 * in-memory batch scheduler (reserve/release/track), the even-split shard
 * planner (bucket + remainder distribution across nodes), the policy
 * cross-tier offload (top-tier / fits-locally / exceeds-ceiling), plus every
 * Null* backend's documented no-op behaviour.
 */

#include "circle_ai/inference_server_enterprise.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

/* ─────────────── round-robin tenant router ─────────────── */

static void test_round_robin_router(void) {
    ca_tenant_router_t *r = ca_round_robin_tenant_router_create();
    assert(r);
    assert(strcmp(ca_tenant_router_backend_id(r), "round-robin") == 0);

    ca_ent_tenant_context_t tenant = { "tenant-1", NULL };

    /* no nodes -> NULL, success */
    char *node = (char *)0x1;
    assert(ca_tenant_router_choose_node(r, &tenant, "m", &node));
    assert(node == NULL);

    assert(ca_tenant_router_register_node(r, "m", "node-a"));
    assert(ca_tenant_router_register_node(r, "m", "node-b"));
    /* de-dup: registering node-a again keeps 2 nodes -> cycle a,b,a,b */
    assert(ca_tenant_router_register_node(r, "m", "node-a"));

    const char *expect[] = { "node-a", "node-b", "node-a", "node-b" };
    for (int i = 0; i < 4; i++) {
        char *n = NULL;
        assert(ca_tenant_router_choose_node(r, &tenant, "m", &n));
        assert(n && strcmp(n, expect[i]) == 0);
        free(n);
    }

    /* a different model has its own (empty) node set */
    char *n2 = (char *)0x1;
    assert(ca_tenant_router_choose_node(r, &tenant, "other", &n2));
    assert(n2 == NULL);

    /* quotas */
    ca_tenant_quota_t q = { (char *)"tenant-1", 4, 2, (int64_t)1 << 30, 100000 };
    assert(ca_tenant_router_set_quota(r, &q));
    ca_tenant_quota_t got;
    assert(ca_tenant_router_get_quota(r, "tenant-1", &got));
    assert(strcmp(got.tenant_id, "tenant-1") == 0);
    assert(got.max_concurrent_requests == 4);
    assert(got.max_models_loaded == 2);
    assert(got.max_bytes_in_flight == (int64_t)1 << 30);
    assert(got.daily_token_budget == 100000);
    ca_tenant_quota_free(&got);

    /* unknown tenant -> miss */
    assert(!ca_tenant_router_get_quota(r, "tenant-x", &got));

    /* overwrite quota */
    ca_tenant_quota_t q2 = { (char *)"tenant-1", 8, 3, 0, 0 };
    assert(ca_tenant_router_set_quota(r, &q2));
    assert(ca_tenant_router_get_quota(r, "tenant-1", &got));
    assert(got.max_concurrent_requests == 8);
    ca_tenant_quota_free(&got);

    ca_tenant_router_destroy(r);
}

static void test_null_router(void) {
    ca_tenant_router_t *r = ca_null_tenant_router_create();
    assert(r);
    assert(strcmp(ca_tenant_router_backend_id(r), "null") == 0);
    ca_ent_tenant_context_t t = { "t", NULL };
    char *node = (char *)0x1;
    assert(ca_tenant_router_choose_node(r, &t, "m", &node));
    assert(node == NULL);
    /* null router keeps no quotas */
    ca_tenant_quota_t q = { (char *)"t", 1, 1, 1, 1 };
    assert(ca_tenant_router_set_quota(r, &q));
    ca_tenant_quota_t got;
    assert(!ca_tenant_router_get_quota(r, "t", &got));
    ca_tenant_router_destroy(r);
}

/* ─────────────── batch scheduler ─────────────── */

static void test_batch_scheduler(void) {
    ca_batch_scheduler_t *s = ca_in_memory_batch_scheduler_create();
    assert(s);
    assert(strcmp(ca_batch_scheduler_backend_id(s), "in-memory") == 0);
    assert(ca_batch_scheduler_reserved_count(s) == 0);

    ca_batch_slot_t slot;
    assert(ca_batch_scheduler_reserve(s, "m", 128, 5000, &slot));
    assert(slot.slot_id && strncmp(slot.slot_id, "slot-", 5) == 0);
    assert(strcmp(slot.model_id, "m") == 0);
    assert(slot.tokens == 128);
    assert(slot.deadline_unix_ms > 0);
    assert(ca_batch_scheduler_reserved_count(s) == 1);

    ca_batch_slot_t slot2;
    assert(ca_batch_scheduler_reserve(s, "m2", 64, 1000, &slot2));
    assert(strcmp(slot2.slot_id, slot.slot_id) != 0); /* unique ids */
    assert(ca_batch_scheduler_reserved_count(s) == 2);

    assert(ca_batch_scheduler_release(s, &slot));
    assert(ca_batch_scheduler_reserved_count(s) == 1);

    /* guards */
    ca_batch_slot_t bad;
    assert(!ca_batch_scheduler_reserve(s, "m", 0, 1000, &bad));
    assert(!ca_batch_scheduler_reserve(s, "m", 10, 0, &bad));
    assert(!ca_batch_scheduler_reserve(s, "", 10, 1000, &bad));

    ca_batch_slot_free(&slot);
    ca_batch_slot_free(&slot2);
    ca_batch_scheduler_destroy(s);
}

static void test_null_batch_scheduler(void) {
    ca_batch_scheduler_t *s = ca_null_batch_scheduler_create();
    assert(s);
    assert(strcmp(ca_batch_scheduler_backend_id(s), "null") == 0);
    ca_batch_slot_t slot;
    assert(ca_batch_scheduler_reserve(s, "m", 10, 1000, &slot));
    /* NullBatchScheduler returns an empty-guid slot and tracks nothing */
    assert(strcmp(slot.slot_id, "00000000-0000-0000-0000-000000000000") == 0);
    assert(ca_batch_scheduler_reserved_count(s) == 0);
    ca_batch_slot_free(&slot);
    ca_batch_scheduler_destroy(s);
}

/* ─────────────── shard planner ─────────────── */

static const char *g_nodes[] = { "node-0", "node-1", "node-2" };
static const char *const *nodes_for(void *user, const char *model_id, size_t *count) {
    (void)user; (void)model_id;
    *count = 3;
    return g_nodes;
}
static const char *const *nodes_none(void *user, const char *model_id, size_t *count) {
    (void)user; (void)model_id;
    *count = 0;
    return NULL;
}

static void test_shard_planner(void) {
    ca_model_shard_planner_t *p = ca_even_split_model_shard_planner_create(nodes_for, NULL);
    assert(p);
    assert(strcmp(ca_model_shard_planner_backend_id(p), "even-split") == 0);

    /* 100 bytes / 3 nodes: bucket 33, rem 1 -> sizes 34,33,33 */
    ca_shard_descriptor_t *shards = NULL; size_t n = 0;
    assert(ca_model_shard_planner_plan(p, "m", 100, &shards, &n));
    assert(n == 3);
    assert(shards[0].range_start == 0  && shards[0].range_end == 34);
    assert(shards[1].range_start == 34 && shards[1].range_end == 67);
    assert(shards[2].range_start == 67 && shards[2].range_end == 100);
    assert(strcmp(shards[0].shard_id, "shard-m-0") == 0);
    assert(strcmp(shards[0].node_id, "node-0") == 0);
    assert(strcmp(shards[2].node_id, "node-2") == 0);
    ca_shard_descriptors_free(shards, n);

    /* guards */
    assert(!ca_model_shard_planner_plan(p, "m", 0, &shards, &n));
    assert(!ca_model_shard_planner_plan(p, "", 100, &shards, &n));
    ca_model_shard_planner_destroy(p);

    /* no nodes -> empty, success */
    ca_model_shard_planner_t *p2 = ca_even_split_model_shard_planner_create(nodes_none, NULL);
    ca_shard_descriptor_t *s2 = (ca_shard_descriptor_t *)0x1; size_t n2 = 99;
    assert(ca_model_shard_planner_plan(p2, "m", 100, &s2, &n2));
    assert(s2 == NULL && n2 == 0);
    ca_model_shard_planner_destroy(p2);
}

static void test_null_shard_planner(void) {
    ca_model_shard_planner_t *p = ca_null_model_shard_planner_create();
    assert(p);
    assert(strcmp(ca_model_shard_planner_backend_id(p), "null") == 0);
    ca_shard_descriptor_t *s = (ca_shard_descriptor_t *)0x1; size_t n = 5;
    assert(ca_model_shard_planner_plan(p, "m", 100, &s, &n));
    assert(s == NULL && n == 0);
    ca_model_shard_planner_destroy(p);
}

/* ─────────────── cross-tier offload ─────────────── */

static void test_cross_tier_offload(void) {
    ca_cross_tier_offload_t *o = ca_policy_cross_tier_offload_create(2048, "farm-node-7");
    assert(o);
    assert(strcmp(ca_cross_tier_offload_backend_id(o), "policy") == 0);

    ca_offload_decision_t d;

    /* top-tier caller never offloads */
    assert(ca_cross_tier_offload_should_offload(o, "m", 9999, CA_SERVER_TIER_SERVER_FARM, &d));
    assert(!d.should_offload);
    assert(d.target_node_id == NULL);
    assert(strstr(d.reason, "top-tier") != NULL);
    ca_offload_decision_free(&d);

    /* fits locally */
    assert(ca_cross_tier_offload_should_offload(o, "m", 2048, CA_SERVER_TIER_SINGLE_NODE, &d));
    assert(!d.should_offload);
    assert(strstr(d.reason, "locally") != NULL);
    ca_offload_decision_free(&d);

    /* exceeds ceiling -> offload to farm node */
    assert(ca_cross_tier_offload_should_offload(o, "m", 4096, CA_SERVER_TIER_SINGLE_NODE, &d));
    assert(d.should_offload);
    assert(d.target_node_id && strcmp(d.target_node_id, "farm-node-7") == 0);
    assert(strstr(d.reason, "exceeds local ceiling") != NULL);
    ca_offload_decision_free(&d);

    /* guards */
    assert(!ca_cross_tier_offload_should_offload(o, "", 10, CA_SERVER_TIER_SERVER, &d));
    assert(!ca_cross_tier_offload_should_offload(o, "m", -1, CA_SERVER_TIER_SERVER, &d));

    ca_cross_tier_offload_destroy(o);

    /* constructor guard */
    assert(ca_policy_cross_tier_offload_create(0, NULL) == NULL);
}

static void test_null_offload(void) {
    ca_cross_tier_offload_t *o = ca_null_cross_tier_offload_create();
    assert(o);
    assert(strcmp(ca_cross_tier_offload_backend_id(o), "null") == 0);
    ca_offload_decision_t d;
    assert(ca_cross_tier_offload_should_offload(o, "m", 100000, CA_SERVER_TIER_SINGLE_NODE, &d));
    assert(!d.should_offload);
    assert(d.target_node_id == NULL);
    assert(strstr(d.reason, "no cross-tier offload configured") != NULL);
    ca_offload_decision_free(&d);
    ca_cross_tier_offload_destroy(o);
}

int main(void) {
    test_round_robin_router();
    test_null_router();
    test_batch_scheduler();
    test_null_batch_scheduler();
    test_shard_planner();
    test_null_shard_planner();
    test_cross_tier_offload();
    test_null_offload();
    printf("test_inference_server_enterprise: all passed\n");
    return 0;
}
