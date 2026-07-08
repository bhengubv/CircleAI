#ifndef CIRCLE_AI_INFERENCE_SERVER_ENTERPRISE_H
#define CIRCLE_AI_INFERENCE_SERVER_ENTERPRISE_H

/*
 * inference_server_enterprise.h — CircleAI.Inference.Server.Enterprise (C11).
 *
 * Multi-tenant routing + batch scheduling + model sharding + cross-tier offload.
 * Faithful port of Contracts.cs + InMemoryInferenceServerEnterprise.cs +
 * NullImplementations.cs.
 *
 *   - ServerTier
 *   - ITenantRouter        : RoundRobinTenantRouter        + NullTenantRouter
 *   - IBatchScheduler      : InMemoryBatchScheduler        + NullBatchScheduler
 *   - IModelShardPlanner   : EvenSplitModelShardPlanner    + NullModelShardPlanner
 *   - ICrossTierOffload    : PolicyCrossTierOffload        + NullCrossTierOffload
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * *_free, deep-copied returns, errors via NULL / false. No pthreads.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ServerTier. */
typedef enum {
    CA_SERVER_TIER_SINGLE_NODE = 0,
    CA_SERVER_TIER_SERVER      = 1,
    CA_SERVER_TIER_SERVER_FARM = 2
} ca_server_tier_t;

/* TenantContext (Tags omitted from the C struct — unused by the impls). */
typedef struct {
    const char *tenant_id;         /* borrowed */
    const char *parent_tenant_id;  /* borrowed; may be NULL */
} ca_ent_tenant_context_t;

/* TenantQuota. */
typedef struct {
    char   *tenant_id;               /* owned */
    int     max_concurrent_requests;
    int     max_models_loaded;
    int64_t max_bytes_in_flight;
    int     daily_token_budget;
} ca_tenant_quota_t;

void ca_tenant_quota_free(ca_tenant_quota_t *q);

/* BatchSlot. */
typedef struct {
    char   *slot_id;         /* owned */
    char   *model_id;        /* owned */
    int     tokens;
    int64_t deadline_unix_ms;
} ca_batch_slot_t;

void ca_batch_slot_free(ca_batch_slot_t *s);

/* ShardDescriptor. */
typedef struct {
    char *shard_id;   /* owned */
    int   range_start;
    int   range_end;
    char *node_id;    /* owned */
} ca_shard_descriptor_t;

void ca_shard_descriptors_free(ca_shard_descriptor_t *arr, size_t count);

/* OffloadDecision. */
typedef struct {
    bool  should_offload;
    char *target_node_id;  /* owned; may be NULL */
    char *reason;          /* owned */
} ca_offload_decision_t;

void ca_offload_decision_free(ca_offload_decision_t *d);

/* ===========================================================================
 * ITenantRouter — RoundRobinTenantRouter
 * =========================================================================== */

typedef struct ca_tenant_router ca_tenant_router_t;

/* Create the round-robin router (BackendId "round-robin"). NULL on OOM. */
ca_tenant_router_t *ca_round_robin_tenant_router_create(void);
/* Create the null router (BackendId "null"; ChooseNode always NULL). */
ca_tenant_router_t *ca_null_tenant_router_create(void);
void ca_tenant_router_destroy(ca_tenant_router_t *r);

const char *ca_tenant_router_backend_id(const ca_tenant_router_t *r);

/* Register a node for a model (round-robin only; no-op on null). Returns false
 * on invalid args / OOM. */
bool ca_tenant_router_register_node(ca_tenant_router_t *r, const char *model_id,
                                    const char *node_id);
/*
 * Choose a node for (tenant, model). On success *out_node is a freshly-allocated
 * node id (caller frees) and returns true. When no node is registered *out_node
 * is NULL and returns true. Returns false on invalid args.
 */
bool ca_tenant_router_choose_node(ca_tenant_router_t *r, const ca_ent_tenant_context_t *tenant,
                                  const char *model_id, char **out_node);
/* Set a quota (deep-copies). Returns false on invalid args / OOM. */
bool ca_tenant_router_set_quota(ca_tenant_router_t *r, const ca_tenant_quota_t *quota);
/*
 * Get a tenant's quota. On a hit *out is filled (caller frees via
 * ca_tenant_quota_free) and returns true; on a miss returns false (out zeroed).
 */
bool ca_tenant_router_get_quota(ca_tenant_router_t *r, const char *tenant_id,
                                ca_tenant_quota_t *out);

/* ===========================================================================
 * IBatchScheduler — InMemoryBatchScheduler
 * =========================================================================== */

typedef struct ca_batch_scheduler ca_batch_scheduler_t;

ca_batch_scheduler_t *ca_in_memory_batch_scheduler_create(void);
ca_batch_scheduler_t *ca_null_batch_scheduler_create(void);
void ca_batch_scheduler_destroy(ca_batch_scheduler_t *s);

const char *ca_batch_scheduler_backend_id(const ca_batch_scheduler_t *s);

/*
 * Reserve a slot. estimated_tokens > 0, max_wait_ms > 0. *out is filled (caller
 * frees via ca_batch_slot_free). Returns false on invalid args / OOM.
 */
bool ca_batch_scheduler_reserve(ca_batch_scheduler_t *s, const char *model_id,
                                int estimated_tokens, int64_t max_wait_ms,
                                ca_batch_slot_t *out);
/* Release a previously-reserved slot (matched by slot_id). */
bool ca_batch_scheduler_release(ca_batch_scheduler_t *s, const ca_batch_slot_t *slot);
/* Number of currently-reserved slots (diagnostics). */
int  ca_batch_scheduler_reserved_count(const ca_batch_scheduler_t *s);

/* ===========================================================================
 * IModelShardPlanner — EvenSplitModelShardPlanner
 * =========================================================================== */

typedef struct ca_model_shard_planner ca_model_shard_planner_t;

/*
 * Even-split planner. nodes_for(user, model_id, &count) returns a borrowed array
 * of node-id strings for the model (or NULL/0 for none). The planner copies the
 * ids it needs. Returns NULL when nodes_for is NULL.
 */
typedef const char *const *(*ca_nodes_for_fn)(void *user, const char *model_id, size_t *count);

ca_model_shard_planner_t *ca_even_split_model_shard_planner_create(
    ca_nodes_for_fn nodes_for, void *user);
ca_model_shard_planner_t *ca_null_model_shard_planner_create(void);
void ca_model_shard_planner_destroy(ca_model_shard_planner_t *p);

const char *ca_model_shard_planner_backend_id(const ca_model_shard_planner_t *p);

/*
 * Plan shards for (model_id, param_bytes > 0). On success *out_arr is a
 * freshly-allocated array of *out_count descriptors (caller frees via
 * ca_shard_descriptors_free). When no nodes exist, *out_arr is NULL and
 * *out_count is 0 with a true return. Returns false on invalid args.
 */
bool ca_model_shard_planner_plan(ca_model_shard_planner_t *p, const char *model_id,
                                 int param_bytes, ca_shard_descriptor_t **out_arr,
                                 size_t *out_count);

/* ===========================================================================
 * ICrossTierOffload — PolicyCrossTierOffload
 * =========================================================================== */

typedef struct ca_cross_tier_offload ca_cross_tier_offload_t;

/* Policy offload. local_prompt_ceiling > 0; farm_target_node may be NULL. */
ca_cross_tier_offload_t *ca_policy_cross_tier_offload_create(int local_prompt_ceiling,
                                                             const char *farm_target_node);
ca_cross_tier_offload_t *ca_null_cross_tier_offload_create(void);
void ca_cross_tier_offload_destroy(ca_cross_tier_offload_t *o);

const char *ca_cross_tier_offload_backend_id(const ca_cross_tier_offload_t *o);

/*
 * Decide whether to offload (model_id, prompt_tokens >= 0, caller_tier). *out is
 * filled (caller frees via ca_offload_decision_free). Returns false on invalid
 * args. Policy: ServerFarm caller => no; prompt <= ceiling => no; else yes with
 * the farm target node.
 */
bool ca_cross_tier_offload_should_offload(ca_cross_tier_offload_t *o, const char *model_id,
                                          int prompt_tokens, ca_server_tier_t caller_tier,
                                          ca_offload_decision_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INFERENCE_SERVER_ENTERPRISE_H */
