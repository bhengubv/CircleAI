#ifndef CIRCLE_AI_ORCHESTRATION_H
#define CIRCLE_AI_ORCHESTRATION_H

/*
 * orchestration.h — CircleAI.Orchestration (C11 port of AgentRole.cs /
 * AgentTask.cs / AgentSwarmConfig.cs / IAgentDispatcher.cs / SwarmResult.cs /
 * QualityGateResult.cs / LocalAgentDispatcher.cs).
 *
 *   Enums   : AgentRole { Engineering=0, Operations, Review, Security };
 *             AgentPriority { Critical=0, High=1, Normal=2, Low=3 };
 *             AgentStatus { Pending=0, Running, Passed, Failed, Blocked }.
 *   Records : AgentTask(Guid Id, AgentRole Role, string Description,
 *                       AgentPriority Priority,
 *                       IReadOnlyDictionary<string,string> Inputs,
 *                       DateTimeOffset CreatedAt);
 *             AgentSwarmConfig(int MaxConcurrency, TimeSpan TaskTimeout,
 *                              bool RequireReviewPassBeforeDeploy,
 *                              bool RequireSecurityPassBeforeDeploy)
 *               + Default + ForDevice(tier, cpuCores);
 *             SwarmResult(Guid TaskId, AgentRole Role, AgentStatus Status,
 *                         string Output, IReadOnlyList<string> Issues,
 *                         DateTimeOffset CompletedAt);
 *             QualityGateResult(bool Passed, IReadOnlyList<string> Blockers,
 *                               IReadOnlyList<string> Warnings).
 *   Dispatch: IAgentDispatcher -> LocalAgentDispatcher — RegisterHandler(role,fn);
 *               DispatchAsync(task) routes to the handler (or returns a Blocked
 *               SwarmResult with an actionable message when none is registered);
 *               RunQualityGateAsync(result) classifies issues prefixed
 *               "[CRITICAL]"/"[HIGH]" (case-insensitive) as blockers, the rest
 *               as warnings, Passed when no blockers. Disposal blocks further
 *               dispatch.
 *
 * Guid Id/TaskId are caller-supplied 32-hex/36-char strings (the C# stamps
 * Guid.NewGuid(); the port takes an explicit id + created_at for determinism,
 * as the other ports do). Inputs is a parallel key/value string array.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL. TaskTimeout as int64 ms.
 * Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "device.h" /* ca_device_tier_t for ForDevice */

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_ORCH_ROLE_ENGINEERING = 0,
    CA_ORCH_ROLE_OPERATIONS  = 1,
    CA_ORCH_ROLE_REVIEW      = 2,
    CA_ORCH_ROLE_SECURITY    = 3
} ca_orch_role_t;

typedef enum {
    CA_ORCH_PRIORITY_CRITICAL = 0,
    CA_ORCH_PRIORITY_HIGH     = 1,
    CA_ORCH_PRIORITY_NORMAL   = 2,
    CA_ORCH_PRIORITY_LOW      = 3
} ca_orch_priority_t;

typedef enum {
    CA_ORCH_STATUS_PENDING = 0,
    CA_ORCH_STATUS_RUNNING = 1,
    CA_ORCH_STATUS_PASSED  = 2,
    CA_ORCH_STATUS_FAILED  = 3,
    CA_ORCH_STATUS_BLOCKED = 4
} ca_orch_status_t;

/* AgentTask(Id, Role, Description, Priority, Inputs, CreatedAt). Inputs is a
 * parallel key/value array of length input_count. */
typedef struct {
    char              *id;          /* owned, non-null (Guid string) */
    ca_orch_role_t     role;
    char              *description; /* owned, non-null */
    ca_orch_priority_t priority;
    char             **input_keys;  /* owned array (input_count) */
    char             **input_values;/* owned array (input_count) */
    size_t             input_count;
    int64_t            created_at_ms;
} ca_orch_task_t;

void ca_orch_task_free(ca_orch_task_t *t);

/* AgentSwarmConfig(MaxConcurrency, TaskTimeout, RequireReviewPassBeforeDeploy,
 * RequireSecurityPassBeforeDeploy). */
typedef struct {
    int     max_concurrency;
    int64_t task_timeout_ms;
    bool    require_review_pass_before_deploy;
    bool    require_security_pass_before_deploy;
} ca_orch_swarm_config_t;

/* Default: (4, 5 min, true, true). */
ca_orch_swarm_config_t ca_orch_swarm_config_default(void);
/* ForDevice(tier, cpuCores): MaxConcurrency sized per DeviceTierDefaults, rest
 * as Default. Mirrors AgentSwarmConfig.ForDevice(DeviceProbe). */
ca_orch_swarm_config_t ca_orch_swarm_config_for_device(ca_device_tier_t tier,
                                                       int cpu_cores);

/* SwarmResult(TaskId, Role, Status, Output, Issues, CompletedAt). */
typedef struct {
    char            *task_id;     /* owned, non-null (Guid string) */
    ca_orch_role_t   role;
    ca_orch_status_t status;
    char            *output;      /* owned, non-null */
    char           **issues;      /* owned array (issue_count) */
    size_t           issue_count;
    int64_t          completed_at_ms;
} ca_orch_swarm_result_t;

void ca_orch_swarm_result_free(ca_orch_swarm_result_t *r);

/* QualityGateResult(Passed, Blockers, Warnings). */
typedef struct {
    bool    passed;
    char  **blockers;      /* owned array (blocker_count) */
    size_t  blocker_count;
    char  **warnings;      /* owned array (warning_count) */
    size_t  warning_count;
} ca_orch_quality_gate_t;

void ca_orch_quality_gate_free(ca_orch_quality_gate_t *g);

/* Handler for a role: given a borrowed task, fill *out (owned; free with
 * ca_orch_swarm_result_free). Return 0 on success, -1 on failure. */
typedef int (*ca_orch_handler_fn)(void *ctx, const ca_orch_task_t *task,
                                  ca_orch_swarm_result_t *out);

typedef struct ca_orch_dispatcher ca_orch_dispatcher_t;

ca_orch_dispatcher_t *ca_orch_dispatcher_create(void); /* NULL on OOM */
void ca_orch_dispatcher_destroy(ca_orch_dispatcher_t *d);

/* RegisterHandler(role, handler): replaces any prior handler for role. 0 / -1
 * on bad args/OOM. */
int ca_orch_dispatcher_register(ca_orch_dispatcher_t *d, ca_orch_role_t role,
                                ca_orch_handler_fn handler, void *ctx);

/* DispatchAsync(task) -> fill *out. When a handler exists it runs; otherwise a
 * Blocked result is returned with the actionable message. Returns 0 on success,
 * -1 on bad args / disposed / handler failure / OOM. */
int ca_orch_dispatcher_dispatch(ca_orch_dispatcher_t *d,
                                const ca_orch_task_t *task,
                                ca_orch_swarm_result_t *out);

/* RunQualityGateAsync(result) -> fill *out. 0 / -1 on bad args/OOM. */
int ca_orch_dispatcher_run_quality_gate(const ca_orch_swarm_result_t *result,
                                        ca_orch_quality_gate_t *out);

/* Dispose(): after this, dispatch returns -1 (ObjectDisposedException). */
void ca_orch_dispatcher_dispose(ca_orch_dispatcher_t *d);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_ORCHESTRATION_H */
