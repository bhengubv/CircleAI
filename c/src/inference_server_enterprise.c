/*
 * inference_server_enterprise.c — CircleAI.Inference.Server.Enterprise (C11).
 * See inference_server_enterprise.h. Faithful port of the in-memory + null
 * enterprise primitives. Pure C11 + libc. No threads.
 */

#include "circle_ai/inference_server_enterprise.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <time.h>

/* ─────────────────────── helpers ─────────────────────── */

static char *xstrdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool is_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++)
        if (!isspace(*p)) return false;
    return true;
}

static int64_t now_unix_ms(void) { return (int64_t)time(NULL) * 1000; }

/* ─────────────────────── DTO frees ─────────────────────── */

void ca_tenant_quota_free(ca_tenant_quota_t *q) {
    if (!q) return;
    free(q->tenant_id);
    q->tenant_id = NULL;
}

void ca_batch_slot_free(ca_batch_slot_t *s) {
    if (!s) return;
    free(s->slot_id);
    free(s->model_id);
    s->slot_id = s->model_id = NULL;
}

void ca_shard_descriptors_free(ca_shard_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; i++) { free(arr[i].shard_id); free(arr[i].node_id); }
    free(arr);
}

void ca_offload_decision_free(ca_offload_decision_t *d) {
    if (!d) return;
    free(d->target_node_id);
    free(d->reason);
    d->target_node_id = d->reason = NULL;
}

/* ===========================================================================
 * ITenantRouter
 * =========================================================================== */

/* nodes for one model + its round-robin cursor. */
typedef struct { char *model_id; char **nodes; size_t node_count, node_cap; size_t rr; } router_model;
typedef struct { char *tenant_id; ca_tenant_quota_t quota; } router_quota;

struct ca_tenant_router {
    bool          is_null;
    const char   *backend_id;   /* static string */
    router_model *models; size_t model_count, model_cap;
    router_quota *quotas; size_t quota_count, quota_cap;
};

ca_tenant_router_t *ca_round_robin_tenant_router_create(void) {
    ca_tenant_router_t *r = (ca_tenant_router_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->is_null = false;
    r->backend_id = "round-robin";
    return r;
}

ca_tenant_router_t *ca_null_tenant_router_create(void) {
    ca_tenant_router_t *r = (ca_tenant_router_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->is_null = true;
    r->backend_id = "null";
    return r;
}

void ca_tenant_router_destroy(ca_tenant_router_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->model_count; i++) {
        free(r->models[i].model_id);
        for (size_t j = 0; j < r->models[i].node_count; j++) free(r->models[i].nodes[j]);
        free(r->models[i].nodes);
    }
    free(r->models);
    for (size_t i = 0; i < r->quota_count; i++) {
        free(r->quotas[i].tenant_id);
        ca_tenant_quota_free(&r->quotas[i].quota);
    }
    free(r->quotas);
    free(r);
}

const char *ca_tenant_router_backend_id(const ca_tenant_router_t *r) {
    return r ? r->backend_id : NULL;
}

static router_model *router_find_model(ca_tenant_router_t *r, const char *model_id) {
    for (size_t i = 0; i < r->model_count; i++)
        if (strcmp(r->models[i].model_id, model_id) == 0) return &r->models[i];
    return NULL;
}

bool ca_tenant_router_register_node(ca_tenant_router_t *r, const char *model_id,
                                    const char *node_id) {
    if (!r || is_blank(model_id) || is_blank(node_id)) return false;
    if (r->is_null) return true; /* null router ignores registration */

    router_model *m = router_find_model(r, model_id);
    if (!m) {
        if (r->model_count == r->model_cap) {
            size_t nc = r->model_cap ? r->model_cap * 2 : 4;
            router_model *n = (router_model *)realloc(r->models, nc * sizeof(*n));
            if (!n) return false;
            r->models = n; r->model_cap = nc;
        }
        m = &r->models[r->model_count];
        memset(m, 0, sizeof(*m));
        m->model_id = xstrdup(model_id);
        if (!m->model_id) return false;
        r->model_count++;
    }
    /* de-dup (mirrors "if (!list.Contains(nodeId))") */
    for (size_t j = 0; j < m->node_count; j++)
        if (strcmp(m->nodes[j], node_id) == 0) return true;
    if (m->node_count == m->node_cap) {
        size_t nc = m->node_cap ? m->node_cap * 2 : 4;
        char **n = (char **)realloc(m->nodes, nc * sizeof(char *));
        if (!n) return false;
        m->nodes = n; m->node_cap = nc;
    }
    m->nodes[m->node_count] = xstrdup(node_id);
    if (!m->nodes[m->node_count]) return false;
    m->node_count++;
    return true;
}

bool ca_tenant_router_choose_node(ca_tenant_router_t *r, const ca_ent_tenant_context_t *tenant,
                                  const char *model_id, char **out_node) {
    if (!r || !tenant || is_blank(model_id) || !out_node) return false;
    *out_node = NULL;
    if (r->is_null) return true; /* always null */

    router_model *m = router_find_model(r, model_id);
    if (!m || m->node_count == 0) return true; /* no node -> NULL, success */
    char *pick = m->nodes[m->rr % m->node_count];
    m->rr++;
    *out_node = xstrdup(pick);
    return *out_node != NULL;
}

static router_quota *router_find_quota(ca_tenant_router_t *r, const char *tenant_id) {
    for (size_t i = 0; i < r->quota_count; i++)
        if (strcmp(r->quotas[i].tenant_id, tenant_id) == 0) return &r->quotas[i];
    return NULL;
}

bool ca_tenant_router_set_quota(ca_tenant_router_t *r, const ca_tenant_quota_t *quota) {
    if (!r || !quota || is_blank(quota->tenant_id)) return false;
    if (r->is_null) return true; /* null router accepts + drops */

    router_quota *q = router_find_quota(r, quota->tenant_id);
    if (!q) {
        if (r->quota_count == r->quota_cap) {
            size_t nc = r->quota_cap ? r->quota_cap * 2 : 4;
            router_quota *n = (router_quota *)realloc(r->quotas, nc * sizeof(*n));
            if (!n) return false;
            r->quotas = n; r->quota_cap = nc;
        }
        q = &r->quotas[r->quota_count];
        memset(q, 0, sizeof(*q));
        q->tenant_id = xstrdup(quota->tenant_id);
        if (!q->tenant_id) return false;
        r->quota_count++;
    } else {
        ca_tenant_quota_free(&q->quota);
    }
    q->quota.tenant_id = xstrdup(quota->tenant_id);
    q->quota.max_concurrent_requests = quota->max_concurrent_requests;
    q->quota.max_models_loaded = quota->max_models_loaded;
    q->quota.max_bytes_in_flight = quota->max_bytes_in_flight;
    q->quota.daily_token_budget = quota->daily_token_budget;
    return q->quota.tenant_id != NULL;
}

bool ca_tenant_router_get_quota(ca_tenant_router_t *r, const char *tenant_id,
                                ca_tenant_quota_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || is_blank(tenant_id) || !out) return false;
    if (r->is_null) return false; /* null router has no quotas */
    router_quota *q = router_find_quota(r, tenant_id);
    if (!q) return false;
    out->tenant_id = xstrdup(q->quota.tenant_id);
    out->max_concurrent_requests = q->quota.max_concurrent_requests;
    out->max_models_loaded = q->quota.max_models_loaded;
    out->max_bytes_in_flight = q->quota.max_bytes_in_flight;
    out->daily_token_budget = q->quota.daily_token_budget;
    return true;
}

/* ===========================================================================
 * IBatchScheduler
 * =========================================================================== */

struct ca_batch_scheduler {
    bool           is_null;
    const char    *backend_id;
    ca_batch_slot_t *slots; size_t count, cap;
    int64_t        seq;
};

ca_batch_scheduler_t *ca_in_memory_batch_scheduler_create(void) {
    ca_batch_scheduler_t *s = (ca_batch_scheduler_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->is_null = false;
    s->backend_id = "in-memory";
    return s;
}

ca_batch_scheduler_t *ca_null_batch_scheduler_create(void) {
    ca_batch_scheduler_t *s = (ca_batch_scheduler_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->is_null = true;
    s->backend_id = "null";
    return s;
}

void ca_batch_scheduler_destroy(ca_batch_scheduler_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; i++) ca_batch_slot_free(&s->slots[i]);
    free(s->slots);
    free(s);
}

const char *ca_batch_scheduler_backend_id(const ca_batch_scheduler_t *s) {
    return s ? s->backend_id : NULL;
}

bool ca_batch_scheduler_reserve(ca_batch_scheduler_t *s, const char *model_id,
                                int estimated_tokens, int64_t max_wait_ms,
                                ca_batch_slot_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || is_blank(model_id) || estimated_tokens <= 0 || max_wait_ms <= 0 || !out)
        return false;

    if (s->is_null) {
        /* Null scheduler: empty-guid slot, no tracking (mirrors NullBatchScheduler). */
        out->slot_id = xstrdup("00000000-0000-0000-0000-000000000000");
        out->model_id = xstrdup(model_id);
        out->tokens = estimated_tokens;
        out->deadline_unix_ms = now_unix_ms() + max_wait_ms;
        return out->slot_id && out->model_id;
    }

    char sid[32];
    snprintf(sid, sizeof(sid), "slot-%lld", (long long)(++s->seq));

    /* track it */
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        ca_batch_slot_t *n = (ca_batch_slot_t *)realloc(s->slots, nc * sizeof(*n));
        if (!n) return false;
        s->slots = n; s->cap = nc;
    }
    ca_batch_slot_t *row = &s->slots[s->count];
    row->slot_id = xstrdup(sid);
    row->model_id = xstrdup(model_id);
    row->tokens = estimated_tokens;
    row->deadline_unix_ms = now_unix_ms() + max_wait_ms;
    if (!row->slot_id || !row->model_id) { ca_batch_slot_free(row); return false; }
    s->count++;

    out->slot_id = xstrdup(row->slot_id);
    out->model_id = xstrdup(row->model_id);
    out->tokens = row->tokens;
    out->deadline_unix_ms = row->deadline_unix_ms;
    return out->slot_id && out->model_id;
}

bool ca_batch_scheduler_release(ca_batch_scheduler_t *s, const ca_batch_slot_t *slot) {
    if (!s || !slot || !slot->slot_id) return false;
    if (s->is_null) return true;
    for (size_t i = 0; i < s->count; i++) {
        if (s->slots[i].slot_id && strcmp(s->slots[i].slot_id, slot->slot_id) == 0) {
            ca_batch_slot_free(&s->slots[i]);
            s->slots[i] = s->slots[s->count - 1];
            s->count--;
            return true;
        }
    }
    return true; /* TryRemove semantics — releasing an unknown slot is not an error */
}

int ca_batch_scheduler_reserved_count(const ca_batch_scheduler_t *s) {
    return s ? (int)s->count : 0;
}

/* ===========================================================================
 * IModelShardPlanner
 * =========================================================================== */

struct ca_model_shard_planner {
    bool            is_null;
    const char     *backend_id;
    ca_nodes_for_fn nodes_for;
    void           *user;
};

ca_model_shard_planner_t *ca_even_split_model_shard_planner_create(
    ca_nodes_for_fn nodes_for, void *user) {
    if (!nodes_for) return NULL;
    ca_model_shard_planner_t *p = (ca_model_shard_planner_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->is_null = false;
    p->backend_id = "even-split";
    p->nodes_for = nodes_for;
    p->user = user;
    return p;
}

ca_model_shard_planner_t *ca_null_model_shard_planner_create(void) {
    ca_model_shard_planner_t *p = (ca_model_shard_planner_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->is_null = true;
    p->backend_id = "null";
    return p;
}

void ca_model_shard_planner_destroy(ca_model_shard_planner_t *p) { free(p); }

const char *ca_model_shard_planner_backend_id(const ca_model_shard_planner_t *p) {
    return p ? p->backend_id : NULL;
}

bool ca_model_shard_planner_plan(ca_model_shard_planner_t *p, const char *model_id,
                                 int param_bytes, ca_shard_descriptor_t **out_arr,
                                 size_t *out_count) {
    if (out_arr) *out_arr = NULL;
    if (out_count) *out_count = 0;
    if (!p || is_blank(model_id) || param_bytes <= 0 || !out_arr || !out_count) return false;

    if (p->is_null) return true; /* null planner -> empty */

    size_t node_count = 0;
    const char *const *nodes = p->nodes_for(p->user, model_id, &node_count);
    if (!nodes || node_count == 0) return true; /* empty */

    ca_shard_descriptor_t *arr =
        (ca_shard_descriptor_t *)calloc(node_count, sizeof(*arr));
    if (!arr) return false;

    int bucket = param_bytes / (int)node_count;
    int rem = param_bytes % (int)node_count;
    int cursor = 0;
    for (size_t i = 0; i < node_count; i++) {
        int size = bucket + ((int)i < rem ? 1 : 0);
        char sid[128];
        snprintf(sid, sizeof(sid), "shard-%s-%zu", model_id, i);
        arr[i].shard_id = xstrdup(sid);
        arr[i].range_start = cursor;
        arr[i].range_end = cursor + size;
        arr[i].node_id = xstrdup(nodes[i] ? nodes[i] : "");
        cursor += size;
        if (!arr[i].shard_id || !arr[i].node_id) {
            ca_shard_descriptors_free(arr, node_count);
            return false;
        }
    }
    *out_arr = arr;
    *out_count = node_count;
    return true;
}

/* ===========================================================================
 * ICrossTierOffload
 * =========================================================================== */

struct ca_cross_tier_offload {
    bool        is_null;
    const char *backend_id;
    int         local_prompt_ceiling;
    char       *farm_target_node;  /* owned; may be NULL */
};

ca_cross_tier_offload_t *ca_policy_cross_tier_offload_create(int local_prompt_ceiling,
                                                             const char *farm_target_node) {
    if (local_prompt_ceiling <= 0) return NULL;
    ca_cross_tier_offload_t *o = (ca_cross_tier_offload_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->is_null = false;
    o->backend_id = "policy";
    o->local_prompt_ceiling = local_prompt_ceiling;
    if (farm_target_node) {
        o->farm_target_node = xstrdup(farm_target_node);
        if (!o->farm_target_node) { free(o); return NULL; }
    }
    return o;
}

ca_cross_tier_offload_t *ca_null_cross_tier_offload_create(void) {
    ca_cross_tier_offload_t *o = (ca_cross_tier_offload_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->is_null = true;
    o->backend_id = "null";
    return o;
}

void ca_cross_tier_offload_destroy(ca_cross_tier_offload_t *o) {
    if (!o) return;
    free(o->farm_target_node);
    free(o);
}

const char *ca_cross_tier_offload_backend_id(const ca_cross_tier_offload_t *o) {
    return o ? o->backend_id : NULL;
}

bool ca_cross_tier_offload_should_offload(ca_cross_tier_offload_t *o, const char *model_id,
                                          int prompt_tokens, ca_server_tier_t caller_tier,
                                          ca_offload_decision_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!o || is_blank(model_id) || prompt_tokens < 0 || !out) return false;

    if (o->is_null) {
        out->should_offload = false;
        out->target_node_id = NULL;
        out->reason = xstrdup("Local execution; no cross-tier offload configured.");
        return out->reason != NULL;
    }

    if (caller_tier == CA_SERVER_TIER_SERVER_FARM) {
        out->should_offload = false;
        out->target_node_id = NULL;
        out->reason = xstrdup("Caller is already top-tier");
        return out->reason != NULL;
    }
    if (prompt_tokens <= o->local_prompt_ceiling) {
        out->should_offload = false;
        out->target_node_id = NULL;
        out->reason = xstrdup("Prompt fits locally");
        return out->reason != NULL;
    }
    out->should_offload = true;
    out->target_node_id = o->farm_target_node ? xstrdup(o->farm_target_node) : NULL;
    char buf[128];
    snprintf(buf, sizeof(buf), "Prompt exceeds local ceiling (%d tokens)", o->local_prompt_ceiling);
    out->reason = xstrdup(buf);
    return out->reason != NULL;
}
