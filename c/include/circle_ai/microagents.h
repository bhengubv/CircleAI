#ifndef CIRCLE_AI_MICROAGENTS_H
#define CIRCLE_AI_MICROAGENTS_H

/*
 * microagents.h — CircleAI.MicroAgents (C11 port of Contracts.cs +
 * InMemoryMicroAgents.cs + MicroAgentHelpers.cs + NullImplementations.cs).
 *
 *   Records : MicroAgentDescriptor(AgentId, Description,
 *                                  IReadOnlyList<string> Capabilities);
 *             MicroAgentResponse(AgentId, Output,
 *                                IReadOnlyDictionary<string,string>? Metadata);
 *             MicroAgentInvocation(AgentId, Input, ResponseText,
 *                                  DateTimeOffset AtUtc).
 *   Agent   : IMicroAgent (vtable) — AgentId / BackendId / Descriptor /
 *               InvokeAsync(input) -> response. FuncMicroAgent wraps a delegate
 *               (BackendId "func"). NullMicroAgent (BackendId "null", empty
 *               output).
 *   Host    : IMicroAgentHost -> InMemoryMicroAgentHost — Register(agent) (keyed
 *               by AgentId), List() -> descriptors, InvokeAsync(agentId, input)
 *               -> response? (null on miss). BackendId "in-memory".
 *   Search  : MicroAgentSearch.ByCapability(all, capability) — agents whose
 *               descriptor advertises the tag (case-insensitive), ordered by
 *               AgentId asc. Search(all, query, topK) — AgentId/Description/
 *               Capabilities case-insensitive substring, Take(topK).
 *   Log     : MicroAgentInvocationLog — Append(i), ForAgent(agentId, limit)
 *               newest-first, TotalInvocations.
 *
 * IMicroAgent is an injected vtable so a host can supply any agent; Func + Null
 * are the built-ins. Metadata nullable via has_metadata.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* MicroAgentDescriptor(AgentId, Description, Capabilities). */
typedef struct {
    char  *agent_id;         /* owned, non-null */
    char  *description;      /* owned, non-null */
    char **capabilities;     /* owned array (capability_count) */
    size_t capability_count;
} ca_ma_descriptor_t;

void ca_ma_descriptor_free(ca_ma_descriptor_t *d);
void ca_ma_descriptor_free_array(ca_ma_descriptor_t *arr, size_t count);

/* MicroAgentResponse(AgentId, Output, IReadOnlyDictionary? Metadata). */
typedef struct {
    char  *agent_id;      /* owned, non-null */
    char  *output;        /* owned, non-null */
    bool   has_metadata;  /* false == C# null Metadata */
    char **meta_keys;     /* owned array (meta_count) */
    char **meta_values;   /* owned array (meta_count) */
    size_t meta_count;
} ca_ma_response_t;

void ca_ma_response_free(ca_ma_response_t *r);

/* MicroAgentInvocation(AgentId, Input, ResponseText, AtUtc). */
typedef struct {
    char   *agent_id;      /* owned, non-null */
    char   *input;         /* owned, non-null */
    char   *response_text; /* owned, non-null */
    int64_t at_utc_ms;
} ca_ma_invocation_t;

void ca_ma_invocation_free(ca_ma_invocation_t *i);
void ca_ma_invocation_free_array(ca_ma_invocation_t *arr, size_t count);

/* ── IMicroAgent (injected vtable) ──────────────────────────────────────── */

/* Invoke(input) -> fill *out (owned; free with ca_ma_response_free). 0 / -1. */
typedef int (*ca_ma_invoke_fn)(void *ctx, const char *input,
                               ca_ma_response_t *out);

typedef struct {
    const char        *agent_id;    /* borrowed, stable for the agent's life */
    const char        *backend_id;  /* borrowed */
    ca_ma_descriptor_t descriptor;  /* owned by the agent */
    ca_ma_invoke_fn    invoke;
    void              *ctx;
} ca_ma_agent_t;

/* FuncMicroAgent: wrap `invoke` into an agent (BackendId "func"). capabilities
 * may be NULL (empty). description NULL -> "". Fills *out (own the descriptor).
 * 0 on success, -1 on bad args (empty agent_id / null invoke) or OOM. Free the
 * agent with ca_ma_agent_free. */
int ca_ma_func_agent(const char *agent_id, const char *description,
                     char *const *capabilities, size_t capability_count,
                     ca_ma_invoke_fn invoke, void *ctx, ca_ma_agent_t *out);

/* Frees the agent's owned descriptor. Does not free ctx. */
void ca_ma_agent_free(ca_ma_agent_t *a);

/* NullMicroAgent: AgentId "null", BackendId "null", empty-capabilities
 * descriptor "No-op micro agent"; Invoke -> {AgentId, ""}. Free with
 * ca_ma_agent_free. Returns 0 (always succeeds) / -1 on OOM. */
int ca_ma_null_agent(ca_ma_agent_t *out);

/* Invoke an agent through its vtable. false on bad args/failure. */
bool ca_ma_agent_invoke(const ca_ma_agent_t *a, const char *input,
                        ca_ma_response_t *out);

/* ── IMicroAgentHost -> InMemoryMicroAgentHost ──────────────────────────── */

typedef struct ca_ma_host ca_ma_host_t;

ca_ma_host_t *ca_ma_host_create(void); /* NULL on OOM */
void ca_ma_host_destroy(ca_ma_host_t *h);
const char *ca_ma_host_backend_id(const ca_ma_host_t *h); /* "in-memory" */

/* Register(agent) — keyed by AgentId (replace). The host borrows the agent
 * pointer (it must outlive the host / until re-registered). 0 / -1 on bad
 * args. */
int ca_ma_host_register(ca_ma_host_t *h, const ca_ma_agent_t *agent);

/* List() -> fresh owned descriptor array (*out_count) in registration order.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_ma_descriptor_t *ca_ma_host_list(const ca_ma_host_t *h, size_t *out_count);

/* InvokeAsync(agentId, input) -> fill *out, true; false on miss/bad args. */
bool ca_ma_host_invoke(const ca_ma_host_t *h, const char *agent_id,
                       const char *input, ca_ma_response_t *out);

/* ── MicroAgentSearch ───────────────────────────────────────────────────── */

/* ByCapability(all, capability): descriptors advertising the tag (case-
 * insensitive), ordered by AgentId asc. NULL + 0 empty; NULL + SIZE_MAX on bad
 * args (null all / empty capability). */
ca_ma_descriptor_t *ca_ma_search_by_capability(const ca_ma_descriptor_t *all,
                                               size_t all_count,
                                               const char *capability,
                                               size_t *out_count);
/* Search(all, query, topK): AgentId/Description/Capabilities case-insensitive
 * substring, Take(topK) (input order). NULL + 0 empty; NULL + SIZE_MAX on bad
 * args (null all / null query / topK <= 0). */
ca_ma_descriptor_t *ca_ma_search(const ca_ma_descriptor_t *all, size_t all_count,
                                 const char *query, int top_k,
                                 size_t *out_count);

/* ── MicroAgentInvocationLog ────────────────────────────────────────────── */

typedef struct ca_ma_log ca_ma_log_t;

ca_ma_log_t *ca_ma_log_create(void); /* NULL on OOM */
void ca_ma_log_destroy(ca_ma_log_t *l);

/* Append(i). 0 / -1 on bad args/OOM. */
int ca_ma_log_append(ca_ma_log_t *l, const ca_ma_invocation_t *inv);
/* ForAgent(agentId, limit) newest-first by AtUtc. NULL + 0 empty; NULL +
 * SIZE_MAX on error (null log / limit <= 0). */
ca_ma_invocation_t *ca_ma_log_for_agent(const ca_ma_log_t *l,
                                        const char *agent_id, int limit,
                                        size_t *out_count);
/* TotalInvocations. */
size_t ca_ma_log_total(const ca_ma_log_t *l);

const char *ca_ma_null_agent_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MICROAGENTS_H */
