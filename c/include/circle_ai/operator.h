#ifndef CIRCLE_AI_OPERATOR_H
#define CIRCLE_AI_OPERATOR_H

/*
 * operator.h — CircleAI.Operator (C11 port of Contracts.cs + InMemoryOperator.cs
 * + NullImplementations.cs). Kubernetes-operator / kagent pattern.
 *
 *   Enum    : ModelLifecyclePhase { Pending, Downloading, Loading, Ready,
 *                                   Brownout, Unloading, Failed }.
 *   Records : ModelDeployment(ModelId, Namespace, int Replicas, TargetTierLabel);
 *             ModelStatus(ModelId, Namespace, ModelLifecyclePhase Phase,
 *                         int ReadyReplicas, string? LastError).
 *   Board   : IModelOperator + IDeploymentObserver -> InMemoryModelOperator.
 *               ApplyAsync drives the lifecycle machine Pending -> Downloading ->
 *               Loading -> Ready(Replicas), notifying every observer on each
 *               transition (snapshot list first). Keyed by "{ns}/{id}".
 *               DeleteAsync removes; GetStatusAsync -> status?; Subscribe(handler)
 *               -> token. Validates ModelId/Namespace non-empty, Replicas >= 0.
 *               BackendId "in-memory".
 *             Null* : NullModelOperator / NullDeploymentObserver, BackendId
 *               "null" (Apply/Delete no-op, GetStatus -> miss, Subscribe -> noop).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * LastError via has_*. Observers fan out synchronously; a handler may unsubscribe
 * safely (list snapshotted). Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    CA_OP_PHASE_PENDING     = 0,
    CA_OP_PHASE_DOWNLOADING = 1,
    CA_OP_PHASE_LOADING     = 2,
    CA_OP_PHASE_READY       = 3,
    CA_OP_PHASE_BROWNOUT    = 4,
    CA_OP_PHASE_UNLOADING   = 5,
    CA_OP_PHASE_FAILED      = 6
} ca_op_lifecycle_phase_t;

/* ModelDeployment(ModelId, Namespace, int Replicas, TargetTierLabel). */
typedef struct {
    char *model_id;          /* owned, non-null */
    char *ns;                /* owned, non-null ("Namespace") */
    int   replicas;
    char *target_tier_label; /* owned, non-null */
} ca_op_deployment_t;

/* ModelStatus(ModelId, Namespace, Phase, int ReadyReplicas, string? LastError). */
typedef struct {
    char                   *model_id;       /* owned, non-null */
    char                   *ns;             /* owned, non-null */
    ca_op_lifecycle_phase_t phase;
    int                     ready_replicas;
    bool                    has_last_error; /* false == C# null LastError */
    char                   *last_error;     /* owned, valid only when has_* */
} ca_op_status_t;

void ca_op_status_free(ca_op_status_t *s);

/* Deployment observer. Receives a borrowed status (valid for the call only). */
typedef void (*ca_op_observer_fn)(void *ctx, const ca_op_status_t *status);

typedef struct ca_op_operator ca_op_operator_t;
typedef struct ca_op_observer_token ca_op_observer_token_t;

ca_op_operator_t *ca_op_operator_create(void); /* NULL on OOM */
void ca_op_operator_destroy(ca_op_operator_t *o);
const char *ca_op_operator_backend_id(const ca_op_operator_t *o); /* "in-memory" */

/* ApplyAsync(deployment): drives Pending -> Downloading -> Loading ->
 * Ready(Replicas), notifying observers on each transition. Returns 0 on success,
 * -1 on bad args (null / empty ModelId or Namespace / Replicas < 0) or OOM. */
int ca_op_operator_apply(ca_op_operator_t *o, const ca_op_deployment_t *deployment);

/* DeleteAsync(modelId, namespace): removes the status. 0 on success, -1 on bad
 * args (null / empty). */
int ca_op_operator_delete(ca_op_operator_t *o, const char *model_id,
                          const char *ns);

/* GetStatusAsync(modelId, namespace) -> fresh owned copy into *out, true; false
 * on miss or bad args. */
bool ca_op_operator_get_status(const ca_op_operator_t *o, const char *model_id,
                               const char *ns, ca_op_status_t *out);

/* Subscribe(handler) -> owned token (dispose to unsubscribe). handler required.
 * NULL on bad args/OOM. */
ca_op_observer_token_t *ca_op_operator_subscribe(ca_op_operator_t *o,
                                                 ca_op_observer_fn handler,
                                                 void *ctx);
/* Dispose the subscription. */
void ca_op_operator_unsubscribe(ca_op_operator_t *o,
                                ca_op_observer_token_t *token);

/* Drain the next buffered status delivered to a token's cursor into *out
 * (freshly owned; free with ca_op_status_free). true if produced, false when
 * empty. Lets a test read transitions a handler-less subscriber received. */
bool ca_op_observer_token_next(ca_op_observer_token_t *token, ca_op_status_t *out);
size_t ca_op_observer_token_pending(const ca_op_observer_token_t *token);

/* ── Null backends ──────────────────────────────────────────────────────── */

const char *ca_op_null_operator_backend_id(void); /* "null" */
/* Apply -> 0 (no-op, validates nothing beyond null). Delete -> 0. */
int  ca_op_null_operator_apply(const ca_op_deployment_t *deployment);
int  ca_op_null_operator_delete(const char *model_id, const char *ns);
/* GetStatus -> always miss. */
bool ca_op_null_operator_get_status(const char *model_id, const char *ns,
                                    ca_op_status_t *out);
const char *ca_op_null_observer_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_OPERATOR_H */
