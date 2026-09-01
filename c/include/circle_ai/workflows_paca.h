#ifndef CIRCLE_AI_WORKFLOWS_PACA_H
#define CIRCLE_AI_WORKFLOWS_PACA_H

/*
 * workflows_paca.h - CircleAI.Workflows (C11): work that outlives the process
 * that started it, and the project where people and agents work side by side.
 *
 * Two halves.
 *
 * A WORKFLOW IS A THING THAT CAN BE RESUMED. That is the entire reason it is
 * not a function call: it survives the app being killed, the phone running out
 * of battery, and the network going away mid-step. Everything about the shape
 * here - the definition separate from the execution, the checkpoint carrying an
 * opaque blob, the suspended phase - follows from that one requirement.
 *
 * PACA IS A PROJECT WHERE SOME MEMBERS ARE NOT PEOPLE. An agent is a member with
 * a git identity, limits, triggers and a prompt - not a bot bolted onto the
 * side. It appears in the member list, its activity shows up in the same
 * realtime feed as everybody else's, and it is bounded by the same permissions.
 * Making an agent a first-class member is what stops "the AI did it" being an
 * answer nobody can audit.
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

/* -- workflows ------------------------------------------------------------ */

typedef enum {
    CA_WORKFLOW_PHASE_PENDING = 0,
    CA_WORKFLOW_PHASE_RUNNING,
    /* Waiting on something outside itself - a person, a timer, a device that is
     * not here. Distinct from RUNNING because a suspended run must not hold a
     * worker, and distinct from FAILED because it is going to continue. */
    CA_WORKFLOW_PHASE_SUSPENDED,
    CA_WORKFLOW_PHASE_COMPLETED,
    CA_WORKFLOW_PHASE_FAILED
} ca_workflow_phase_t;

const char *ca_workflow_phase_name(ca_workflow_phase_t phase);

typedef struct {
    char *definition_id;
    char *name;
    char *version;
    char *description;
} ca_workflow_definition_t;

void ca_workflow_definition_free(ca_workflow_definition_t *definition);

typedef struct {
    char *run_id;
    char *definition_id;
    /* The version the run STARTED on. Pinned, because a definition that
     * changes under a suspended run resumes into a step that no longer exists,
     * and the failure surfaces days later with no obvious cause. */
    char *definition_version;
    ca_workflow_phase_t phase;
    char *current_step_id;
    int64_t started_unix;
    int64_t updated_unix;
    char *error;
} ca_workflow_execution_t;

void ca_workflow_execution_free(ca_workflow_execution_t *execution);

typedef struct {
    char *run_id;
    char *step_id;
    /* OPAQUE to everything here. A checkpoint the runner can read is a
     * checkpoint the runner will eventually depend on the shape of, and then a
     * step cannot change its own state without a migration. */
    uint8_t *state_blob;
    size_t state_len;
} ca_checkpoint_payload_t;

void ca_checkpoint_payload_free(ca_checkpoint_payload_t *payload);

typedef struct ca_workflow_definition_store {
    void *state;
    bool (*put)(void *state, const ca_workflow_definition_t *definition);
    /* Borrowed; NULL when absent. Version NULL means latest. */
    const ca_workflow_definition_t *(*get)(void *state, const char *definition_id,
                                           const char *version);
    ca_workflow_definition_t *(*list)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_workflow_definition_store_t;

void ca_workflow_definition_store_free(ca_workflow_definition_store_t *store);

/* Stores nothing, lists nothing. The default, so a host that has not wired
 * persistence gets a workflow engine that cannot resume rather than one that
 * appears to and silently loses every run. */
ca_workflow_definition_store_t *ca_null_workflow_definition_store_new(void);

typedef struct ca_workflow_state {
    void *state;
    bool (*checkpoint)(void *state, const ca_checkpoint_payload_t *payload);
    /* Caller frees. NULL when there is nothing to resume from. */
    ca_checkpoint_payload_t *(*load)(void *state, const char *run_id);
    bool (*set_phase)(void *state, const char *run_id, ca_workflow_phase_t phase,
                      const char *error);
    void (*free_fn)(void *state);
} ca_workflow_state_t;

void ca_workflow_state_free(ca_workflow_state_t *state);
ca_workflow_state_t *ca_null_workflow_state_new(void);

typedef struct ca_workflow_runner {
    void *state;
    /* Caller frees the run id. */
    char *(*start)(void *state, const char *definition_id, const char *input_json);
    /* Picks a suspended run back up. Returns false when the run does not exist
     * or its definition version is gone - never "starts it over", which would
     * repeat side effects that already happened. */
    bool (*resume)(void *state, const char *run_id);
    ca_workflow_execution_t *(*status)(void *state, const char *run_id);
    void (*free_fn)(void *state);
} ca_workflow_runner_t;

void ca_workflow_runner_free(ca_workflow_runner_t *runner);
ca_workflow_runner_t *ca_null_workflow_runner_new(void);

/* -- paca: members, human and not ----------------------------------------- */

typedef enum {
    CA_MEMBER_KIND_HUMAN = 0,
    CA_MEMBER_KIND_AGENT
} ca_member_kind_t;

const char *ca_member_kind_name(ca_member_kind_t kind);

typedef struct {
    char *member_id;
    char *display_name;
    ca_member_kind_t kind;
    char *role;
    int64_t joined_unix;
} ca_project_member_t;

void ca_project_member_free(ca_project_member_t *member);

typedef struct {
    char *model_id;
    double temperature;
    int max_tokens;
    char *fallback_model_id;
} ca_agent_llm_config_t;

void ca_agent_llm_config_free(ca_agent_llm_config_t *config);

typedef struct {
    char *system;
    /* Prepended to every task. Separate from `system` so that a project can
     * change how its agents behave without editing each agent, and so the diff
     * of a behaviour change is one line rather than five prompts. */
    char *project_preamble;
    char *task_suffix;
} ca_agent_system_prompts_t;

void ca_agent_system_prompts_free(ca_agent_system_prompts_t *prompts);

typedef struct {
    bool can_read_repository;
    bool can_write_repository;
    bool can_open_pull_request;
    bool can_comment;
    bool can_run_tests;
    /* Deliberately NOT a bag of strings. A capability set that can grow by
     * configuration is one where nobody can answer "what is this agent allowed
     * to do" by reading the type. */
    bool can_invoke_tools;
} ca_agent_capabilities_t;

typedef struct {
    int max_iterations;
    int64_t timeout_seconds;
} ca_agent_limits_t;

/* Both are hard stops and both are required. An agent with no iteration cap
 * that loses its way costs money until somebody notices; one with no timeout
 * holds a slot forever. */
ca_agent_limits_t ca_agent_limits_default(void);

typedef struct {
    char *name;
    char *email;
} ca_agent_git_identity_t;

void ca_agent_git_identity_free(ca_agent_git_identity_t *identity);

/* Its OWN identity, never a person's. Commits made by an agent must be
 * attributable to the agent - borrowing the operator's name puts a human's
 * signature on work they did not review. */
ca_agent_git_identity_t *ca_agent_git_identity_for(const char *agent_name);

typedef struct {
    bool on_issue_opened;
    bool on_pull_request_opened;
    bool on_mention;
    bool on_schedule;
    char *schedule_cron;
    char **watch_paths;
    size_t watch_path_count;
} ca_agent_triggers_t;

void ca_agent_triggers_free(ca_agent_triggers_t *triggers);

/* -- paca: authentication ------------------------------------------------- */

typedef struct ca_paca_authenticator {
    void *state;
    /* Caller frees the member id. NULL when the credential does not verify. */
    char *(*authenticate)(void *state, const char *credential);
    void (*free_fn)(void *state);
} ca_paca_authenticator_t;

void ca_paca_authenticator_free(ca_paca_authenticator_t *authenticator);

/*
 * HMAC-signed tokens.
 *
 * The signature is compared in CONSTANT TIME and the expiry is checked BEFORE
 * anything else. Both are the boring parts everybody skips: an early-exit
 * compare leaks the signature a byte at a time, and checking claims before
 * expiry means an expired token still tells an attacker whether the rest of it
 * was right.
 */
ca_paca_authenticator_t *ca_hmac_jwt_authenticator_new(const uint8_t *secret,
                                                       size_t secret_len);

/* API keys, stored as salted hashes. The key itself is shown once, at
 * creation, and is not recoverable afterwards - a key a server can read back
 * to you is a key a server can lose on your behalf. */
ca_paca_authenticator_t *ca_paca_api_key_authenticator_new(void);

bool ca_paca_api_key_authenticator_issue(ca_paca_authenticator_t *authenticator,
                                         const char *member_id, char **out_key);

/* -- paca: conversations -------------------------------------------------- */

typedef enum {
    CA_CONVERSATION_QUEUED = 0,
    CA_CONVERSATION_RUNNING,
    CA_CONVERSATION_FINISHED,
    CA_CONVERSATION_FAILED,
    /* Stopped by a person. Kept distinct from FAILED because somebody deciding
     * to stop an agent is not an error, and burying it in the failure count is
     * how a useful signal gets lost. */
    CA_CONVERSATION_STOPPED
} ca_conversation_state_t;

const char *ca_conversation_state_name(ca_conversation_state_t state);

typedef struct {
    char *conversation_id;
    char *project_id;
    char *agent_member_id;
    char *title;
    ca_conversation_state_t state;
    int64_t started_unix;
    int iterations_used;
} ca_agent_conversation_t;

void ca_agent_conversation_free(ca_agent_conversation_t *conversation);

typedef struct ca_paca_conversation_runtime ca_paca_conversation_runtime_t;

ca_paca_conversation_runtime_t *ca_paca_conversation_runtime_new(void);
void ca_paca_conversation_runtime_free(ca_paca_conversation_runtime_t *runtime);

char *ca_paca_conversation_runtime_start(ca_paca_conversation_runtime_t *runtime,
                                         const char *project_id,
                                         const char *agent_member_id,
                                         const char *prompt);

/* Takes effect at the next step boundary, not mid-tool-call. Killing an agent
 * between deciding to write a file and writing it leaves the project in a state
 * nobody chose. */
bool ca_paca_conversation_runtime_stop(ca_paca_conversation_runtime_t *runtime,
                                       const char *conversation_id);

/* -- paca: realtime ------------------------------------------------------- */

/* What everyone in a project sees happening. Agent activity rides the SAME
 * feed as human activity, deliberately: a separate channel for agents is a
 * channel people learn to mute. */
typedef enum {
    CA_REALTIME_TASK_UPDATED = 0,
    CA_REALTIME_QUERY_INVALIDATION,
    CA_REALTIME_DOC_CURSOR_MOVE,
    CA_REALTIME_AGENT_ACTIVITY,
    CA_REALTIME_CONVERSATION_STEP
} ca_realtime_event_kind_t;

const char *ca_realtime_event_kind_name(ca_realtime_event_kind_t kind);

typedef struct {
    ca_realtime_event_kind_t kind;
    char *project_id;
    int64_t at_unix;

    /* CA_REALTIME_TASK_UPDATED */
    int task_number;
    /* CA_REALTIME_QUERY_INVALIDATION */
    char *query_key;
    /* CA_REALTIME_DOC_CURSOR_MOVE */
    char *doc_id;
    char *member_id;
    int cursor_offset;
    /* CA_REALTIME_AGENT_ACTIVITY */
    char *agent_member_id;
    char *action;
    char *detail_json;
    /* CA_REALTIME_CONVERSATION_STEP */
    char *conversation_id;
} ca_realtime_paca_event_t;

void ca_realtime_paca_event_free(ca_realtime_paca_event_t *event);

/* Constructors per kind, for the same reason the speech lifecycle has them:
 * which fields mean anything depends on the kind. */
ca_realtime_paca_event_t *ca_task_updated_event_new(const char *project_id,
                                                    int64_t at_unix, int task_number);

ca_realtime_paca_event_t *ca_doc_cursor_move_event_new(const char *project_id,
                                                       int64_t at_unix,
                                                       const char *doc_id,
                                                       const char *member_id,
                                                       int cursor_offset);

ca_realtime_paca_event_t *ca_agent_activity_event_new(const char *project_id,
                                                      int64_t at_unix,
                                                      const char *agent_member_id,
                                                      const char *action,
                                                      const char *detail_json);

typedef struct ca_realtime_broadcaster {
    void *state;
    void (*broadcast)(void *state, const ca_realtime_paca_event_t *event);
    void (*free_fn)(void *state);
} ca_realtime_broadcaster_t;

void ca_realtime_broadcaster_free(ca_realtime_broadcaster_t *broadcaster);

/* Cursor moves are the highest-volume event by an order of magnitude, so they
 * are coalesced per document per member. Broadcasting each one turns a person
 * scrolling into a denial of service against everybody else in the room. */
ca_realtime_broadcaster_t *ca_paca_realtime_hub_new(int64_t coalesce_ms);

/* -- paca: plugins -------------------------------------------------------- */

typedef enum {
    CA_PLUGIN_EXTENSION_TOOL = 0,
    CA_PLUGIN_EXTENSION_TRIGGER,
    CA_PLUGIN_EXTENSION_VIEW,
    CA_PLUGIN_EXTENSION_FORMATTER,
    CA_PLUGIN_EXTENSION_STORAGE
} ca_plugin_extension_point_t;

const char *ca_plugin_extension_point_name(ca_plugin_extension_point_t point);

/* What a plugin may spend. Defaults 5000 ms and 64 MB.
 *
 * Both are enforced, not advisory. A plugin is somebody else's code running
 * inside a project's process, and the only honest way to host one is to be able
 * to stop it. */
typedef struct {
    int call_timeout_ms;
    int64_t memory_ceiling_bytes;
} ca_plugin_resource_limits_t;

ca_plugin_resource_limits_t ca_plugin_resource_limits_default(void);

/* -- paca: skills --------------------------------------------------------- */

typedef struct ca_paca_skill_installer ca_paca_skill_installer_t;

ca_paca_skill_installer_t *ca_paca_skill_installer_new(const char *skills_root);
void ca_paca_skill_installer_free(ca_paca_skill_installer_t *installer);

/* Installs into the project, not the machine. A skill belongs to the work it
 * was added for; one installed globally starts affecting projects nobody
 * added it to. */
bool ca_paca_skill_installer_install(ca_paca_skill_installer_t *installer,
                                     const char *project_id,
                                     const char *skill_archive_path);

size_t ca_skill_templates_count(void);
const char *ca_skill_templates_at(size_t index);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WORKFLOWS_PACA_H */
