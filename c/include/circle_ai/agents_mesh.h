#ifndef CIRCLE_AI_AGENTS_MESH_H
#define CIRCLE_AI_AGENTS_MESH_H

/*
 * agents_mesh.h - CircleAI.MicroAgents, CircleAI.Orchestration, CircleAI.Mesh
 * and CircleAI.Pipelines (C11).
 *
 * Four modules about work that is split up: into small named agents, into a
 * swarm with roles, onto somebody else's device, and through a source-to-sink
 * pipeline.
 *
 * A MICRO-AGENT IS SMALL ENOUGH TO DESCRIBE IN A SENTENCE. That is the whole
 * discipline. The moment one needs a paragraph it has become a service, and the
 * host should be running it as one - with its own lifecycle, its own failure
 * handling, and its own place in a log.
 *
 * OFFLOADING SENDS A PROMPT TO SOMEBODY ELSE'S HARDWARE. Everything in the mesh
 * half is written to refuse by default and to keep saying WHOSE device answered,
 * because "it was faster on the other phone" is not a reason somebody consented
 * to their conversation leaving this one.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- micro-agents --------------------------------------------------------- */

typedef struct {
    char *agent_id;
    char *name;
    /* One sentence. If it takes more, it is not a micro-agent. */
    char *description;
    char **capabilities;
    size_t capability_count;
} ca_micro_agent_descriptor_t;

void ca_micro_agent_descriptor_free(ca_micro_agent_descriptor_t *descriptor);

typedef struct {
    bool handled;
    char *output;
    /* Populated whether it handled the request or not. An agent that declines
     * should say why - otherwise a search across twenty agents that all decline
     * reports nothing at all, and nobody can tell whether that is because none
     * matched or because one crashed. */
    char *note;
    int64_t duration_ms;
} ca_micro_agent_response_t;

void ca_micro_agent_response_free(ca_micro_agent_response_t *response);

typedef struct ca_micro_agent {
    void *state;
    const ca_micro_agent_descriptor_t *(*describe)(void *state);
    /* `handled` false means "not mine", which is NOT a failure. Conflating the
     * two makes a router treat a polite decline as an outage. */
    ca_micro_agent_response_t *(*invoke)(void *state, const char *request);
    void (*free_fn)(void *state);
} ca_micro_agent_t;

void ca_micro_agent_free(ca_micro_agent_t *agent);

/* Declines everything, politely. The default. */
ca_micro_agent_t *ca_null_micro_agent_new(void);

/* One function, wrapped as an agent. What most micro-agents actually are, and
 * making that the easy path is what keeps them small. */
ca_micro_agent_t *ca_func_micro_agent_new(
    const ca_micro_agent_descriptor_t *descriptor,
    ca_micro_agent_response_t *(*fn)(void *fn_state, const char *request),
    void *fn_state);

typedef struct {
    char *agent_id;
    char *request;
    char *output;
    bool handled;
    int64_t at_unix;
    int64_t duration_ms;
} ca_micro_agent_invocation_t;

void ca_micro_agent_invocation_free(ca_micro_agent_invocation_t *invocation);

typedef struct ca_micro_agent_invocation_log ca_micro_agent_invocation_log_t;

/* Every invocation, including the declines. The declines are the useful half:
 * a request nothing handled is the signal that an agent is missing, and it is
 * invisible if only successes are recorded. */
ca_micro_agent_invocation_log_t *ca_micro_agent_invocation_log_new(size_t capacity);
void ca_micro_agent_invocation_log_free(ca_micro_agent_invocation_log_t *log);

void ca_micro_agent_invocation_log_record(ca_micro_agent_invocation_log_t *log,
                                          const ca_micro_agent_invocation_t *invocation);

size_t ca_micro_agent_invocation_log_count(const ca_micro_agent_invocation_log_t *log);
size_t ca_micro_agent_invocation_log_unhandled_count(
    const ca_micro_agent_invocation_log_t *log);

typedef struct ca_micro_agent_host {
    void *state;
    bool (*add)(void *state, ca_micro_agent_t *agent);
    ca_micro_agent_descriptor_t *(*list)(void *state, size_t *out_count);
    /* Asks agents until one handles it. Returns the response either way. */
    ca_micro_agent_response_t *(*dispatch)(void *state, const char *request);
    void (*free_fn)(void *state);
} ca_micro_agent_host_t;

void ca_micro_agent_host_free(ca_micro_agent_host_t *host);
ca_micro_agent_host_t *ca_micro_agent_host_new(void);

/* Finds agents by capability and by description text. Description matching
 * exists because capability strings are written by whoever added the agent, and
 * nobody remembers the exact vocabulary six months later. */
ca_micro_agent_descriptor_t *ca_micro_agent_search(const ca_micro_agent_host_t *host,
                                                   const char *query,
                                                   size_t *out_count);

/* -- orchestration -------------------------------------------------------- */

typedef enum {
    CA_AGENT_ROLE_PLANNER = 0,
    CA_AGENT_ROLE_RESEARCHER,
    CA_AGENT_ROLE_EXECUTOR,
    CA_AGENT_ROLE_REVIEWER,
    /* Reconciles disagreement between the others. Named as a role rather than
     * left to the planner, because a planner grading its own plan is how a
     * swarm agrees with itself all the way into a wall. */
    CA_AGENT_ROLE_ARBITER
} ca_agent_role_t;

const char *ca_agent_role_name(ca_agent_role_t role);

typedef enum {
    CA_AGENT_PRIORITY_LOW = 0,
    CA_AGENT_PRIORITY_NORMAL,
    CA_AGENT_PRIORITY_HIGH,
    /* Reserved for incidents. A priority anything can request is not a
     * priority, so this one is set by the incident trigger and not by a task. */
    CA_AGENT_PRIORITY_CRITICAL
} ca_agent_priority_t;

const char *ca_agent_priority_name(ca_agent_priority_t priority);

typedef enum {
    CA_AGENT_STATUS_IDLE = 0,
    CA_AGENT_STATUS_WORKING,
    CA_AGENT_STATUS_BLOCKED,
    CA_AGENT_STATUS_DONE,
    CA_AGENT_STATUS_FAILED
} ca_agent_status_t;

const char *ca_agent_status_name(ca_agent_status_t status);

typedef struct {
    char *task_id;
    char *description;
    ca_agent_role_t role;
    ca_agent_priority_t priority;
    ca_agent_status_t status;
    char **depends_on;
    size_t dependency_count;
    char *assigned_agent_id;
    char *output;
} ca_agent_task_t;

void ca_agent_task_free(ca_agent_task_t *task);

typedef struct {
    size_t max_concurrent;
    int max_rounds;
    int64_t task_timeout_seconds;
    /* Stop the whole swarm when this many tasks fail. A swarm that keeps going
     * through repeated failure is a swarm burning tokens on a premise that has
     * already turned out to be wrong. */
    int abort_after_failures;
} ca_agent_swarm_config_t;

ca_agent_swarm_config_t ca_agent_swarm_config_default(void);

typedef struct ca_agent_dispatcher {
    void *state;
    bool (*submit)(void *state, const ca_agent_task_t *task);
    /* Next runnable task - dependencies satisfied, highest priority first.
     * NULL when nothing is runnable, which is different from nothing being
     * left: a swarm where every remaining task is blocked has deadlocked, and
     * the caller must be able to tell. */
    ca_agent_task_t *(*next)(void *state);
    bool (*complete)(void *state, const char *task_id, const char *output);
    bool (*fail)(void *state, const char *task_id, const char *error);
    size_t (*pending_count)(void *state);
    void (*free_fn)(void *state);
} ca_agent_dispatcher_t;

void ca_agent_dispatcher_free(ca_agent_dispatcher_t *dispatcher);

/* Everything on this device. The default, and on a phone usually the only
 * one. */
ca_agent_dispatcher_t *ca_local_agent_dispatcher_new(
    const ca_agent_swarm_config_t *config);

typedef struct ca_loki_orchestrator ca_loki_orchestrator_t;

/*
 * Runs a swarm to completion.
 *
 * REFUSES A CYCLIC DEPENDENCY GRAPH UP FRONT rather than discovering it as a
 * deadlock. The check is cheap, and the alternative is a run that sits at zero
 * runnable tasks with no explanation while the timeout burns down.
 */
ca_loki_orchestrator_t *ca_loki_orchestrator_new(ca_agent_dispatcher_t *dispatcher);
void ca_loki_orchestrator_free(ca_loki_orchestrator_t *orchestrator);

bool ca_loki_orchestrator_run(ca_loki_orchestrator_t *orchestrator, char **out_error);

typedef struct {
    char *incident_id;
    char *summary;
    ca_agent_priority_t priority;
    int64_t raised_unix;
    char *source;
} ca_incident_trigger_t;

void ca_incident_trigger_free(ca_incident_trigger_t *trigger);

typedef struct ca_security_orchestration_bridge ca_security_orchestration_bridge_t;

/* Turns a security observation into tasks. AWARENESS-DRIVEN, NOT
 * ENFORCEMENT-DRIVEN: it schedules work for somebody to look at, and nothing
 * it produces blocks, quarantines or deletes on its own. */
ca_security_orchestration_bridge_t *ca_security_orchestration_bridge_new(
    ca_agent_dispatcher_t *dispatcher);

void ca_security_orchestration_bridge_free(ca_security_orchestration_bridge_t *bridge);

bool ca_security_orchestration_bridge_raise(ca_security_orchestration_bridge_t *bridge,
                                            const ca_incident_trigger_t *trigger);

/* -- mesh offload --------------------------------------------------------- */

typedef struct {
    char *peer_id;
    char *display_name;
    /* Whether this peer has been added by BOTH devices. Offloading to a peer
     * that has not added us back is sending a prompt to a stranger. */
    bool mutually_added;
} ca_offload_served_by_t;

void ca_offload_served_by_free(ca_offload_served_by_t *served_by);

typedef struct {
    char *turn_id;
    char *prompt;
    char *response;
    /* NULL means it ran HERE. Always carried through to the caller, so a UI can
     * say which device answered - the one fact that makes offloading something
     * somebody agreed to rather than something that happened to them. */
    ca_offload_served_by_t *served_by;
    int64_t duration_ms;
} ca_offload_turn_t;

void ca_offload_turn_free(ca_offload_turn_t *turn);

typedef struct ca_local_inference_fallback {
    void *state;
    /* Runs it here instead. Caller frees. */
    char *(*run)(void *state, const char *prompt);
    bool (*is_available)(void *state);
    void (*free_fn)(void *state);
} ca_local_inference_fallback_t;

void ca_local_inference_fallback_free(ca_local_inference_fallback_t *fallback);

/* Reports unavailable and runs nothing. The default: a router with no local
 * fallback must know it has none, or it will route to the mesh because it
 * believes there is a safety net. */
ca_local_inference_fallback_t *ca_null_local_inference_fallback_new(void);

typedef struct ca_mesh_offload_client {
    void *state;
    ca_offload_turn_t *(*send)(void *state, const char *peer_id, const char *prompt);
    void (*free_fn)(void *state);
} ca_mesh_offload_client_t;

void ca_mesh_offload_client_free(ca_mesh_offload_client_t *client);

ca_mesh_offload_client_t *ca_mesh_offload_client_new(void *transport);

typedef struct ca_offload_router {
    void *state;
    /* Decides and executes. Falls back locally whenever the decision is no,
     * which is most of the time. */
    ca_offload_turn_t *(*route)(void *state, const char *prompt);
    void (*free_fn)(void *state);
} ca_offload_router_t;

void ca_offload_router_free(ca_offload_router_t *router);

/*
 * Routes to a peer only when ALL of it holds: the peer is mutually added, the
 * link is already up, the local device genuinely cannot do the work, and the
 * person has consented to offload for this kind of request.
 *
 * Latency alone is never sufficient. "It would be faster over there" is the
 * argument that ends with somebody's conversation on a device they do not own.
 */
ca_offload_router_t *ca_mesh_offload_router_new(ca_mesh_offload_client_t *client,
                                                ca_local_inference_fallback_t *fallback);

typedef struct {
    char *device_id;
    char **capabilities;
    size_t capability_count;
    int64_t ram_bytes;
    double load_average;
    int64_t at_unix;
} ca_mesh_advertisement_beacon_t;

void ca_mesh_advertisement_beacon_free(ca_mesh_advertisement_beacon_t *beacon);

typedef struct ca_aether_mesh_capability_broadcaster ca_aether_mesh_capability_broadcaster_t;

/*
 * Tells nearby devices what this one can do.
 *
 * CAPABILITIES ONLY - never what it is doing, never who owns it, never what was
 * asked. A beacon that carried activity would make a mesh of phones into a
 * mesh of people broadcasting their behaviour to the room.
 */
ca_aether_mesh_capability_broadcaster_t *ca_aether_mesh_capability_broadcaster_new(
    const char *device_id, void *transport);

void ca_aether_mesh_capability_broadcaster_free(
    ca_aether_mesh_capability_broadcaster_t *broadcaster);

bool ca_aether_mesh_capability_broadcaster_advertise(
    ca_aether_mesh_capability_broadcaster_t *broadcaster,
    const ca_mesh_advertisement_beacon_t *beacon);

/* -- pipelines ------------------------------------------------------------ */

typedef struct {
    char *record_id;
    char *payload_json;
    int64_t at_unix;
    char **tag_keys;
    char **tag_values;
    size_t tag_count;
} ca_pipeline_record_t;

void ca_pipeline_record_free(ca_pipeline_record_t *record);

typedef struct ca_pipeline_source {
    void *state;
    /* Next batch, or NULL when exhausted. An EMPTY batch means "nothing right
     * now, ask again"; NULL means "there will never be more". A source that
     * cannot distinguish them turns a slow feed into a finished one. */
    ca_pipeline_record_t *(*read)(void *state, size_t max, size_t *out_count);
    void (*free_fn)(void *state);
} ca_pipeline_source_t;

void ca_pipeline_source_free(ca_pipeline_source_t *source);

ca_pipeline_source_t *ca_pipeline_source_new(void);
ca_pipeline_source_t *ca_null_pipeline_source_new(void);

typedef struct ca_pipeline_sink {
    void *state;
    bool (*write)(void *state, const ca_pipeline_record_t *records, size_t count);
    bool (*flush)(void *state);
    void (*free_fn)(void *state);
} ca_pipeline_sink_t;

void ca_pipeline_sink_free(ca_pipeline_sink_t *sink);

ca_pipeline_sink_t *ca_pipeline_sink_new(void);
ca_pipeline_sink_t *ca_null_pipeline_sink_new(void);

typedef struct ca_pipeline_executor {
    void *state;
    /* Pumps source to sink until the source is exhausted. Returns records
     * moved; negative on error with *out_error set. */
    int64_t (*run)(void *state, ca_pipeline_source_t *source,
                   ca_pipeline_sink_t *sink, char **out_error);
    void (*free_fn)(void *state);
} ca_pipeline_executor_t;

void ca_pipeline_executor_free(ca_pipeline_executor_t *executor);

/* Flushes the sink before reporting success. A run that reports a count and
 * then loses the tail on an unflushed buffer is worse than one that fails: the
 * number was already believed. */
ca_pipeline_executor_t *ca_pipeline_executor_new(size_t batch_size);
ca_pipeline_executor_t *ca_null_pipeline_executor_new(void);

typedef struct {
    char **column_names;
    size_t column_count;
    /* Row-major, column_count entries per row. */
    char **cells;
    size_t row_count;
    char *error;
} ca_database_query_result_t;

void ca_database_query_result_free(ca_database_query_result_t *result);

typedef struct ca_database_query_tool {
    void *state;
    /* PARAMETERISED, always. `sql` carries placeholders and the values come
     * separately - a tool that took a finished string would let a model build
     * the query, which is injection with extra steps. */
    ca_database_query_result_t *(*query)(void *state, const char *sql,
                                         const char **params, size_t param_count);
    void (*free_fn)(void *state);
} ca_database_query_tool_t;

void ca_database_query_tool_free(ca_database_query_tool_t *tool);

ca_database_query_tool_t *ca_database_query_tool_new(void);

/* Answers nothing. The default, because a database tool wired by accident is a
 * model with read access to whatever the process could reach. */
ca_database_query_tool_t *ca_null_database_query_tool_new(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AGENTS_MESH_H */
