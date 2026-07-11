#ifndef CIRCLE_AI_WORKFLOWS_H
#define CIRCLE_AI_WORKFLOWS_H

/*
 * workflows.h — CircleAI.Workflows (C11 port of Contracts.cs +
 * NullImplementations.cs + PacaConversations.cs).
 *
 *   Enums   : WorkflowPhase { Pending, Running, Suspended, Completed, Failed };
 *             ConversationState { Queued, Running, Finished, Failed, Stopped }.
 *   Records : WorkflowDefinition(DefinitionId, Name, Version, Description);
 *             WorkflowExecution(RunId, DefinitionId, WorkflowPhase Phase,
 *                               StartUtc, string? FailureReason);
 *             CheckpointPayload(RunId, StepId, ReadOnlyMemory<byte> StateBlob);
 *             AgentConversation(Id, ProjectId, AgentMemberId,
 *                               string? HumanMemberId, OpeningPrompt,
 *                               ConversationState State, QueuedAtUtc,
 *                               StartedAtUtc?, FinishedAtUtc?, ResultJson?,
 *                               FailureReason?);
 *             ConversationStep(ConversationId, int Order, Speaker, ContentJson,
 *                              At);
 *             ConversationPermissions(bool AllowCloneRepos, bool AllowCreatePr).
 *   Def store: IWorkflowDefinitionStore -> in-memory (Upsert/Get by
 *                DefinitionId) + Null (Upsert no-op, Get null).
 *   Runner   : IWorkflowRunner (vtable; the real durable runner is host-supplied)
 *                + Null runner (Start -> failed {Guid.Empty, "NullWorkflowRunner"},
 *                Get null, Cancel no-op).
 *   State    : IWorkflowState -> in-memory (Checkpoint by (RunId,StepId), Load)
 *                + Null (Checkpoint no-op, Load null).
 *   Conv.    : IConversationExecutor (vtable; host-supplied OpenHands/Docker
 *                runner) + PacaConversationRuntime — Queue(id,...), Get(id),
 *                Steps(id), Run(id, permissions) drives Queued -> Running ->
 *                Finished/Failed/Stopped (executor emits steps via a callback).
 *
 * The runner/executor are injected because their "real" behaviour drives
 * arbitrary host work; the definition + state stores have unambiguous
 * dictionary semantics and ship as real in-memory impls (plus the Null defaults).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL. Nullable via has_*.
 * *At as int64 Unix ms UTC. StateBlob is an owned byte copy. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_WF_PHASE_PENDING   = 0,
    CA_WF_PHASE_RUNNING   = 1,
    CA_WF_PHASE_SUSPENDED = 2,
    CA_WF_PHASE_COMPLETED = 3,
    CA_WF_PHASE_FAILED    = 4
} ca_wf_phase_t;

typedef enum {
    CA_WF_CONV_QUEUED   = 0,
    CA_WF_CONV_RUNNING  = 1,
    CA_WF_CONV_FINISHED = 2,
    CA_WF_CONV_FAILED   = 3,
    CA_WF_CONV_STOPPED  = 4
} ca_wf_conv_state_t;

/* WorkflowDefinition(DefinitionId, Name, Version, Description). */
typedef struct {
    char *definition_id; /* owned, non-null */
    char *name;          /* owned, non-null */
    char *version;       /* owned, non-null */
    char *description;   /* owned, non-null */
} ca_wf_definition_t;

void ca_wf_definition_free(ca_wf_definition_t *d);

/* WorkflowExecution(RunId, DefinitionId, Phase, StartUtc, FailureReason?). */
typedef struct {
    char         *run_id;             /* owned, non-null */
    char         *definition_id;      /* owned, non-null */
    ca_wf_phase_t phase;
    int64_t       start_utc_ms;
    bool          has_failure_reason; /* false == C# null FailureReason */
    char         *failure_reason;     /* owned, valid only when has_* */
} ca_wf_execution_t;

void ca_wf_execution_free(ca_wf_execution_t *e);

/* CheckpointPayload(RunId, StepId, ReadOnlyMemory<byte> StateBlob). */
typedef struct {
    char    *run_id;   /* owned, non-null */
    char    *step_id;  /* owned, non-null */
    uint8_t *state_blob;/* owned (may be NULL when len 0) */
    size_t   state_blob_len;
} ca_wf_checkpoint_t;

void ca_wf_checkpoint_free(ca_wf_checkpoint_t *c);

/* ── IWorkflowDefinitionStore ───────────────────────────────────────────── */

typedef struct ca_wf_def_store ca_wf_def_store_t;

ca_wf_def_store_t *ca_wf_def_store_create(void); /* NULL on OOM */
void ca_wf_def_store_destroy(ca_wf_def_store_t *s);
const char *ca_wf_def_store_backend_id(const ca_wf_def_store_t *s); /* "in-memory" */

/* Upsert(d) — DefinitionId keyed (replace). 0 / -1. */
int ca_wf_def_store_upsert(ca_wf_def_store_t *s, const ca_wf_definition_t *d);
/* Get(id) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_wf_def_store_get(const ca_wf_def_store_t *s, const char *id,
                         ca_wf_definition_t *out);

const char *ca_wf_null_def_store_backend_id(void); /* "null" */

/* ── IWorkflowRunner (injected vtable) + Null ───────────────────────────── */

/* Start(definitionId) -> fill *out. 0 / -1. */
typedef int (*ca_wf_runner_start_fn)(void *ctx, const char *definition_id,
                                     ca_wf_execution_t *out);
/* Get(runId) -> fill *out, true; false on miss. */
typedef bool (*ca_wf_runner_get_fn)(void *ctx, const char *run_id,
                                    ca_wf_execution_t *out);
/* Cancel(runId) -> 0/-1. */
typedef int (*ca_wf_runner_cancel_fn)(void *ctx, const char *run_id);

typedef struct {
    const char            *backend_id; /* borrowed */
    ca_wf_runner_start_fn  start;
    ca_wf_runner_get_fn    get;
    ca_wf_runner_cancel_fn cancel;
    void                  *ctx;
} ca_wf_runner_t;

int  ca_wf_runner_start(const ca_wf_runner_t *r, const char *definition_id,
                        ca_wf_execution_t *out);
bool ca_wf_runner_get(const ca_wf_runner_t *r, const char *run_id,
                      ca_wf_execution_t *out);
int  ca_wf_runner_cancel(const ca_wf_runner_t *r, const char *run_id);

const char *ca_wf_null_runner_backend_id(void); /* "null" */
/* Null runner Start -> failed {RunId Guid.Empty, Phase Failed,
 * FailureReason "NullWorkflowRunner"}. 0 / -1. */
int ca_wf_null_runner_start(const char *definition_id, ca_wf_execution_t *out);

/* ── IWorkflowState ─────────────────────────────────────────────────────── */

typedef struct ca_wf_state_store ca_wf_state_store_t;

ca_wf_state_store_t *ca_wf_state_store_create(void); /* NULL on OOM */
void ca_wf_state_store_destroy(ca_wf_state_store_t *s);
const char *ca_wf_state_store_backend_id(const ca_wf_state_store_t *s); /* "in-memory" */

/* Checkpoint(payload) — keyed by (RunId, StepId) (replace). 0 / -1. */
int ca_wf_state_store_checkpoint(ca_wf_state_store_t *s,
                                 const ca_wf_checkpoint_t *payload);
/* Load(runId, stepId) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_wf_state_store_load(const ca_wf_state_store_t *s, const char *run_id,
                            const char *step_id, ca_wf_checkpoint_t *out);

const char *ca_wf_null_state_backend_id(void); /* "null" */

/* ── Conversations ──────────────────────────────────────────────────────── */

/* AgentConversation(...). Optional strings via has_* gates. */
typedef struct {
    char              *id;                /* owned, non-null */
    char              *project_id;        /* owned, non-null */
    char              *agent_member_id;   /* owned, non-null */
    bool               has_human_member;  /* false == C# null HumanMemberId */
    char              *human_member_id;   /* owned, valid only when has_* */
    char              *opening_prompt;    /* owned, non-null */
    ca_wf_conv_state_t state;
    int64_t            queued_at_ms;
    bool               has_started_at;    /* StartedAtUtc? */
    int64_t            started_at_ms;
    bool               has_finished_at;   /* FinishedAtUtc? */
    int64_t            finished_at_ms;
    bool               has_result_json;   /* ResultJson? */
    char              *result_json;       /* owned, valid only when has_* */
    bool               has_failure_reason;/* FailureReason? */
    char              *failure_reason;    /* owned, valid only when has_* */
} ca_wf_conversation_t;

void ca_wf_conversation_free(ca_wf_conversation_t *c);

/* ConversationStep(ConversationId, int Order, Speaker, ContentJson, At). */
typedef struct {
    char   *conversation_id; /* owned, non-null */
    int     order;
    char   *speaker;         /* owned, non-null ("user"/"agent"/"tool") */
    char   *content_json;    /* owned, non-null */
    int64_t at_ms;
} ca_wf_conversation_step_t;

void ca_wf_conversation_step_free(ca_wf_conversation_step_t *s);
void ca_wf_conversation_step_free_array(ca_wf_conversation_step_t *arr,
                                        size_t count);

/* ConversationPermissions(bool AllowCloneRepos, bool AllowCreatePr). */
typedef struct {
    bool allow_clone_repos;
    bool allow_create_pr;
} ca_wf_conversation_permissions_t;

/* IConversationExecutor (injected vtable). Run the conversation; emit steps via
 * `on_step` (each borrowed for the call, and copied into the runtime). Return 0
 * on success, -1 to fail the conversation (message copied into fail_msg, at
 * most fail_msg_cap-1 chars); return 1 to signal the run was stopped. */
typedef void (*ca_wf_step_sink_fn)(void *sink_ctx,
                                    const ca_wf_conversation_step_t *step);
typedef int (*ca_wf_executor_run_fn)(void *ctx,
                                     const ca_wf_conversation_t *conversation,
                                     ca_wf_conversation_permissions_t permissions,
                                     ca_wf_step_sink_fn on_step, void *sink_ctx,
                                     char *fail_msg, size_t fail_msg_cap);

typedef struct {
    ca_wf_executor_run_fn run;
    void                 *ctx;
} ca_wf_conversation_executor_t;

/* PacaConversationRuntime(executor). */
typedef struct ca_wf_conversation_runtime ca_wf_conversation_runtime_t;

ca_wf_conversation_runtime_t *ca_wf_conversation_runtime_create(
    const ca_wf_conversation_executor_t *executor); /* NULL on bad args/OOM */
void ca_wf_conversation_runtime_destroy(ca_wf_conversation_runtime_t *rt);

/* Queue(id, projectId, agentMemberId, openingPrompt, humanMemberId?) -> fill
 * *out with the queued conversation (owned). now_ms is the QueuedAtUtc clock.
 * 0 on success, -1 on bad args / duplicate id / OOM. */
int ca_wf_conversation_runtime_queue(ca_wf_conversation_runtime_t *rt,
                                     const char *id, const char *project_id,
                                     const char *agent_member_id,
                                     const char *opening_prompt,
                                     const char *human_member_id, int64_t now_ms,
                                     ca_wf_conversation_t *out);
/* Get(id) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_wf_conversation_runtime_get(const ca_wf_conversation_runtime_t *rt,
                                    const char *id, ca_wf_conversation_t *out);
/* Steps(id) -> fresh owned array (*out_count) in emit order. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_wf_conversation_step_t *ca_wf_conversation_runtime_steps(
    const ca_wf_conversation_runtime_t *rt, const char *id, size_t *out_count);
/* Run(id, permissions, now_ms): drives Queued -> Running, invokes the executor
 * (which emits steps), then Finished / Failed / Stopped by its return. 0 on
 * success (whatever the final state), -1 on bad args / not-Queued / OOM. */
int ca_wf_conversation_runtime_run(ca_wf_conversation_runtime_t *rt,
                                   const char *id,
                                   ca_wf_conversation_permissions_t permissions,
                                   int64_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WORKFLOWS_H */
