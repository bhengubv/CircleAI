/*
 * orchestration.c — CircleAI.Orchestration (C11 port).
 *
 * LocalAgentDispatcher: a small per-role handler table. Dispatch routes to the
 * registered handler; a missing handler yields a Blocked SwarmResult with the
 * actionable message. RunQualityGate classifies "[CRITICAL]"/"[HIGH]"-prefixed
 * issues (case-insensitive) as blockers, the rest as warnings.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/orchestration.h"
#include "board_common.h"

#include <stdio.h>

/* ── record free ────────────────────────────────────────────────────────── */

void ca_orch_task_free(ca_orch_task_t *t) {
    if (!t) return;
    free(t->id);
    free(t->description);
    cab_strv_free(t->input_keys, t->input_count);
    cab_strv_free(t->input_values, t->input_count);
    t->id = t->description = NULL;
    t->input_keys = t->input_values = NULL;
    t->input_count = 0;
}

void ca_orch_swarm_result_free(ca_orch_swarm_result_t *r) {
    if (!r) return;
    free(r->task_id);
    free(r->output);
    cab_strv_free(r->issues, r->issue_count);
    r->task_id = r->output = NULL;
    r->issues = NULL;
    r->issue_count = 0;
}

void ca_orch_quality_gate_free(ca_orch_quality_gate_t *g) {
    if (!g) return;
    cab_strv_free(g->blockers, g->blocker_count);
    cab_strv_free(g->warnings, g->warning_count);
    g->blockers = g->warnings = NULL;
    g->blocker_count = g->warning_count = 0;
}

/* ── AgentSwarmConfig ───────────────────────────────────────────────────── */

#define MINUTES_5_MS (5LL * 60 * 1000)

ca_orch_swarm_config_t ca_orch_swarm_config_default(void) {
    ca_orch_swarm_config_t c;
    c.max_concurrency = 4;
    c.task_timeout_ms = MINUTES_5_MS;
    c.require_review_pass_before_deploy = true;
    c.require_security_pass_before_deploy = true;
    return c;
}

/* DeviceTierDefaults.MaxConcurrency mapped onto the C device tiers. The C#
 * DeviceTier scheme is {Wearable, Phone, Tablet, Desktop, Workstation}; the C
 * ca_device_tier_t adds Laptop + Embedded. Laptop maps to Desktop's slot (8),
 * Embedded to the default (2), matching the numeric intent. */
static int max_concurrency_for(ca_device_tier_t tier, int cpu_cores) {
    switch (tier) {
        case CA_TIER_WEARABLE:    return 1;
        case CA_TIER_PHONE:       return 2;
        case CA_TIER_TABLET:      return 4;
        case CA_TIER_LAPTOP:      return 8;  /* ~ DeviceTier.Desktop */
        case CA_TIER_WORKSTATION: {
            int v = cpu_cores - 2;
            if (v < 1) v = 1;
            if (v > 16) v = 16;
            return v;
        }
        case CA_TIER_EMBEDDED:    return 2;  /* default branch */
    }
    return 2;
}

ca_orch_swarm_config_t ca_orch_swarm_config_for_device(ca_device_tier_t tier,
                                                       int cpu_cores) {
    ca_orch_swarm_config_t c = ca_orch_swarm_config_default();
    c.max_concurrency = max_concurrency_for(tier, cpu_cores);
    return c;
}

/* ── LocalAgentDispatcher ───────────────────────────────────────────────── */

typedef struct {
    bool               set;
    ca_orch_handler_fn handler;
    void              *ctx;
} handler_slot_t;

struct ca_orch_dispatcher {
    handler_slot_t handlers[4]; /* indexed by ca_orch_role_t */
    bool           disposed;
};

ca_orch_dispatcher_t *ca_orch_dispatcher_create(void) {
    return (ca_orch_dispatcher_t *)calloc(1, sizeof(ca_orch_dispatcher_t));
}
void ca_orch_dispatcher_destroy(ca_orch_dispatcher_t *d) {
    free(d);
}

int ca_orch_dispatcher_register(ca_orch_dispatcher_t *d, ca_orch_role_t role,
                                ca_orch_handler_fn handler, void *ctx) {
    if (!d || !handler) return -1;
    if ((int)role < 0 || (int)role > 3) return -1;
    d->handlers[role].set     = true;
    d->handlers[role].handler = handler;
    d->handlers[role].ctx     = ctx;
    return 0;
}

static const char *role_name(ca_orch_role_t r) {
    switch (r) {
        case CA_ORCH_ROLE_ENGINEERING: return "Engineering";
        case CA_ORCH_ROLE_OPERATIONS:  return "Operations";
        case CA_ORCH_ROLE_REVIEW:      return "Review";
        case CA_ORCH_ROLE_SECURITY:    return "Security";
    }
    return "Engineering";
}

/* Build the Blocked-no-handler SwarmResult. Returns -1 on OOM. */
static int blocked_result(ca_orch_swarm_result_t *out, const ca_orch_task_t *t) {
    memset(out, 0, sizeof(*out));
    out->role   = t->role;
    out->status = CA_ORCH_STATUS_BLOCKED;
    out->completed_at_ms = t->created_at_ms; /* deterministic: no UtcNow here */
    out->task_id = cab_strdup_empty(t->id);
    if (!out->task_id) return -1;

    char msg[256];
    snprintf(msg, sizeof(msg), "No handler registered for role %s.",
             role_name(t->role));
    out->output = cab_strdup(msg);
    if (!out->output) { ca_orch_swarm_result_free(out); return -1; }

    out->issues = (char **)calloc(1, sizeof(char *));
    if (!out->issues) { ca_orch_swarm_result_free(out); return -1; }
    char issue[256];
    snprintf(issue, sizeof(issue),
             "Register a handler for AgentRole.%s before dispatching.",
             role_name(t->role));
    out->issues[0] = cab_strdup(issue);
    if (!out->issues[0]) { ca_orch_swarm_result_free(out); return -1; }
    out->issue_count = 1;
    return 0;
}

int ca_orch_dispatcher_dispatch(ca_orch_dispatcher_t *d,
                                const ca_orch_task_t *task,
                                ca_orch_swarm_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!d || !task || !out) return -1;
    if (d->disposed) return -1; /* ObjectDisposedException */
    if ((int)task->role < 0 || (int)task->role > 3) return -1;

    handler_slot_t *slot = &d->handlers[task->role];
    if (slot->set)
        return slot->handler(slot->ctx, task, out);
    return blocked_result(out, task);
}

/* Case-insensitive StartsWith. */
static bool starts_with_ci(const char *s, const char *prefix) {
    if (!s || !prefix) return false;
    size_t pl = strlen(prefix);
    for (size_t i = 0; i < pl; ++i) {
        if (s[i] == '\0') return false;
        if (tolower((unsigned char)s[i]) != tolower((unsigned char)prefix[i]))
            return false;
    }
    return true;
}

int ca_orch_dispatcher_run_quality_gate(const ca_orch_swarm_result_t *result,
                                        ca_orch_quality_gate_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!result || !out) return -1;

    /* Two passes: blockers first (in order), then warnings (in order). */
    if (result->issue_count > 0) {
        out->blockers = (char **)calloc(result->issue_count, sizeof(char *));
        out->warnings = (char **)calloc(result->issue_count, sizeof(char *));
        if (!out->blockers || !out->warnings) {
            ca_orch_quality_gate_free(out);
            return -1;
        }
    }
    for (size_t i = 0; i < result->issue_count; ++i) {
        const char *iss = result->issues[i];
        bool is_blocker = starts_with_ci(iss, "[CRITICAL]") ||
                          starts_with_ci(iss, "[HIGH]");
        char **dst = is_blocker ? out->blockers : out->warnings;
        size_t *cnt = is_blocker ? &out->blocker_count : &out->warning_count;
        dst[*cnt] = cab_strdup_empty(iss);
        if (!dst[*cnt]) { ca_orch_quality_gate_free(out); return -1; }
        (*cnt)++;
    }
    out->passed = (out->blocker_count == 0);
    return 0;
}

void ca_orch_dispatcher_dispose(ca_orch_dispatcher_t *d) {
    if (d) d->disposed = true;
}
