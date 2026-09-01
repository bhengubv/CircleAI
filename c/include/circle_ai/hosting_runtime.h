#ifndef CIRCLE_AI_HOSTING_RUNTIME_H
#define CIRCLE_AI_HOSTING_RUNTIME_H

/*
 * hosting_runtime.h - CircleAI.Hosting (C11): how a host wires the assistant
 * in, schedules it, and watches what it does.
 *
 * The pieces that surround the model rather than being it: what goes into a
 * system prompt, which endpoint a call takes, when a recurring job fires, which
 * specialist model is allowed to stay resident, and who gets told afterwards.
 *
 * OBSERVERS ARE TOLD, THEY DO NOT DECIDE. Every observer here is downstream of
 * a completed exchange. An observer that could veto would be a policy engine
 * wearing a listener's name, and the first one that threw would silently stop
 * the assistant answering.
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

/* -- the system prompt ---------------------------------------------------- */

/*
 * Whether persona, device context, recall and skills get appended to the
 * caller's own system prompt.
 *
 * ALWAYS is the default, and the reason is stated because it was a change:
 * silently losing memory grounding is worse than receiving grounding you did
 * not explicitly ask for. The caller's instructions still LEAD - enrichment is
 * appended after them, never in front.
 */
typedef enum {
    CA_SYSTEM_PROMPT_ENRICHMENT_ALWAYS = 0,
    /* Only when the caller supplies no system turn at all. Full control of the
     * prompt, accepting that recall and persona will not be injected. */
    CA_SYSTEM_PROMPT_ENRICHMENT_ONLY_WHEN_ABSENT
} ca_system_prompt_enrichment_t;

const char *ca_system_prompt_enrichment_name(ca_system_prompt_enrichment_t enrichment);

/* -- endpoints ------------------------------------------------------------ */

typedef struct ca_ai_endpoint {
    void *state;
    const char *(*endpoint_id)(void *state);
    /* Caller frees. NULL on failure with *out_error set. */
    char *(*send)(void *state, const char *request_json, char **out_error);
    void (*free_fn)(void *state);
} ca_ai_endpoint_t;

void ca_ai_endpoint_free(ca_ai_endpoint_t *endpoint);

/* No socket, no serialisation, no loopback. The default on a device, where the
 * assistant and its caller are the same process and going through HTTP to talk
 * to yourself costs latency for nothing. */
ca_ai_endpoint_t *ca_in_process_endpoint_new(void *service);

typedef struct ca_ai_http_client ca_ai_http_client_t;

/*
 * The HTTP client for a remote host.
 *
 * `transport` is the host's. This module opens no socket and pins no
 * certificate - both are decisions that belong to whoever knows the deployment,
 * and a library that makes them has made them wrong for somebody.
 */
ca_ai_http_client_t *ca_ai_http_client_new(const char *base_url, void *transport);
void ca_ai_http_client_free(ca_ai_http_client_t *client);

ca_ai_endpoint_t *ca_ai_http_client_as_endpoint(ca_ai_http_client_t *client);

/* -- chat runtimes -------------------------------------------------------- */

typedef struct ca_persistable_chat_runtime {
    void *state;
    /* Writes the whole conversation somewhere durable and returns its id.
     * Caller frees. */
    char *(*persist)(void *state, const char *conversation_id);
    /* Restores one. False when it is not there, which is a normal answer after
     * a device has been wiped and must not read as corruption. */
    bool (*restore)(void *state, const char *conversation_id);
    void (*free_fn)(void *state);
} ca_persistable_chat_runtime_t;

void ca_persistable_chat_runtime_free(ca_persistable_chat_runtime_t *runtime);

/* -- scheduled work ------------------------------------------------------- */

typedef enum {
    CA_DELIVERY_TARGET_NOTIFICATION = 0,
    CA_DELIVERY_TARGET_CHAT,
    CA_DELIVERY_TARGET_EMAIL,
    CA_DELIVERY_TARGET_SILENT
} ca_delivery_target_t;

const char *ca_delivery_target_name(ca_delivery_target_t target);

typedef enum {
    /* Never run. */
    CA_CRON_JOB_PENDING = 0,
    CA_CRON_JOB_RUNNING,
    CA_CRON_JOB_SUCCEEDED,
    CA_CRON_JOB_FAILED,
    /* Paused by a person; will not fire until re-enabled. Distinct from FAILED
     * because a paused job is not a broken one, and burying it in a failure
     * count is how somebody stops trusting the list. */
    CA_CRON_JOB_PAUSED
} ca_cron_job_state_t;

const char *ca_cron_job_state_name(ca_cron_job_state_t state);

typedef struct {
    char *id;
    char *name;
    char *prompt;
    /* Five fields: minute hour day-of-month month day-of-week. */
    char *cron_expression;
    ca_delivery_target_t delivery;
    int64_t last_run_unix;   /* negative = never */
    int64_t next_run_unix;
    ca_cron_job_state_t state;
    bool is_enabled;
} ca_cron_job_t;

void ca_cron_job_free(ca_cron_job_t *job);

typedef struct ca_cron_schedule ca_cron_schedule_t;

/*
 * Parses a five-field cron expression. NULL on anything malformed, with
 * *out_error saying which field and why.
 *
 * Refusing loudly matters more here than anywhere else in this file: a schedule
 * that parses but means something other than what was written does not fail -
 * it fires at three in the morning, or never, and the person who wrote it finds
 * out weeks later.
 *
 * DAY-OF-MONTH AND DAY-OF-WEEK ARE OR-ED, NOT AND-ED, when both are restricted.
 * That is genuinely how cron behaves and it surprises everybody: "1 * * 13 5"
 * is the 13th AND every Friday, not only Friday the 13th. Implementing the
 * intuitive reading gives a scheduler that silently disagrees with every other
 * cron on the system.
 */
ca_cron_schedule_t *ca_cron_schedule_parser_parse(const char *expression,
                                                  char **out_error);

void ca_cron_schedule_free(ca_cron_schedule_t *schedule);

/* The next firing strictly after `after_unix`, in UTC. Negative when the
 * expression can never fire again - 30 February parses fine and matches
 * nothing, and a caller looping until the next occurrence would spin forever. */
int64_t ca_cron_schedule_next(const ca_cron_schedule_t *schedule, int64_t after_unix);

bool ca_cron_schedule_matches(const ca_cron_schedule_t *schedule, int64_t at_unix);

/* -- triggers ------------------------------------------------------------- */

typedef struct ca_trigger_condition {
    void *state;
    const char *(*trigger_id)(void *state);
    /* Evaluated cheaply and often. A condition that needs a network call or a
     * model is not a trigger - it is work, and it belongs on the other side of
     * one. */
    bool (*is_met)(void *state, int64_t now_unix);
    void (*free_fn)(void *state);
} ca_trigger_condition_t;

void ca_trigger_condition_free(ca_trigger_condition_t *condition);

/* -- resident specialists ------------------------------------------------- */

typedef enum {
    /* Built and now resident. */
    CA_SLOT_OUTCOME_ADMITTED = 0,
    /* Already resident - not an error, and not a fresh load either. */
    CA_SLOT_OUTCOME_ALREADY_RESIDENT = 1,
    /* The RAM gate denied it. The caller falls back to the generalist. */
    CA_SLOT_OUTCOME_INSUFFICIENT_RAM = 2,
    /* The factory failed. Also falls back, but for a reason worth logging
     * differently: one is the device being small, the other is a bug. */
    CA_SLOT_OUTCOME_BUILD_FAILED = 3
} ca_slot_outcome_t;

const char *ca_slot_outcome_name(ca_slot_outcome_t outcome);

typedef struct {
    ca_slot_outcome_t outcome;
    /* The resident specialist when admitted or already-resident; NULL
     * otherwise. Borrowed - the manager owns residency. */
    void *generator;
    /* Human-readable detail for telemetry. Always populated, including on
     * success: "already resident" and "admitted after evicting X" are the two
     * facts anybody debugging memory pressure actually wants. */
    char *message;
} ca_slot_admission_t;

void ca_slot_admission_free(ca_slot_admission_t *admission);

typedef struct ca_resident_slot_manager ca_resident_slot_manager_t;

/*
 * Decides which specialist models may stay in memory.
 *
 * A GATE RATHER THAN A CACHE. A cache evicts when it feels pressure; this
 * refuses admission BEFORE the load, because on a phone the pressure arrives as
 * the process being killed. By the time an allocator notices, the app is gone
 * and the person sees a crash, not a slower answer.
 */
ca_resident_slot_manager_t *ca_resident_slot_manager_new(size_t max_slots,
                                                         int64_t ram_ceiling_bytes);

void ca_resident_slot_manager_free(ca_resident_slot_manager_t *manager);

ca_slot_admission_t *ca_resident_slot_manager_ensure_specialist(
    ca_resident_slot_manager_t *manager, const char *model_id,
    int64_t approx_ram_bytes,
    void *(*build)(void *build_state, const char *model_id), void *build_state);

void ca_resident_slot_manager_release(ca_resident_slot_manager_t *manager,
                                      const char *model_id);

/* -- generative UI -------------------------------------------------------- */

typedef struct ca_generative_ui_renderer {
    void *state;
    /* Renders one node the model asked for. Returning false REFUSES it, which
     * is how an unknown component becomes a visible gap rather than a blank
     * screen. */
    bool (*render)(void *state, const char *component, const char *props_json);
    void (*free_fn)(void *state);
} ca_generative_ui_renderer_t;

void ca_generative_ui_renderer_free(ca_generative_ui_renderer_t *renderer);

/* Records what it was asked to render instead of rendering it. What tests
 * assert against, and what a host uses to see what a model is trying to draw
 * before granting it a real surface. */
ca_generative_ui_renderer_t *ca_recording_generative_ui_renderer_new(void);

size_t ca_recording_generative_ui_renderer_count(const ca_generative_ui_renderer_t *renderer);

/* Borrowed; NULL when out of range. */
const char *ca_recording_generative_ui_renderer_component_at(
    const ca_generative_ui_renderer_t *renderer, size_t index);

typedef struct ca_json_render_parser ca_json_render_parser_t;

/*
 * Parses a model's render instruction into components.
 *
 * The component name is checked against a CLOSED list before anything is
 * rendered. An open one means a model can name any component the host has, and
 * the prompt that decides what appears on somebody's screen is then text from a
 * language model.
 */
ca_json_render_parser_t *ca_json_render_parser_new(const char **allowed_components,
                                                   size_t count);

void ca_json_render_parser_free(ca_json_render_parser_t *parser);

bool ca_json_render_parser_parse(ca_json_render_parser_t *parser, const char *json,
                                 ca_generative_ui_renderer_t *into,
                                 char **out_error);

/* -- observers ------------------------------------------------------------ */

typedef struct ca_ai_observer {
    void *state;
    /* Called after an exchange completes. Cannot change it and cannot stop it. */
    void (*on_exchange)(void *state, const char *prompt, const char *response,
                        int64_t duration_ms);
    void (*free_fn)(void *state);
} ca_ai_observer_t;

void ca_ai_observer_free(ca_ai_observer_t *observer);

typedef struct ca_circle_aether_transport {
    void *state;
    /* Sends over the mesh. Fire and forget: an observer that waited for a mesh
     * round trip would add the mesh's latency to every reply. */
    bool (*publish)(void *state, const char *topic, const uint8_t *payload,
                    size_t len);
    void (*free_fn)(void *state);
} ca_circle_aether_transport_t;

void ca_circle_aether_transport_free(ca_circle_aether_transport_t *transport);

/* Publishes exchange summaries over the mesh - SUMMARIES, never the text. What
 * somebody said is theirs; that a device was busy for 800 ms is the mesh's
 * business. */
ca_ai_observer_t *ca_aether_ai_observer_new(ca_circle_aether_transport_t *transport);

typedef struct ca_push_notification_sender {
    void *state;
    bool (*send)(void *state, const char *title, const char *body);
    void (*free_fn)(void *state);
} ca_push_notification_sender_t;

void ca_push_notification_sender_free(ca_push_notification_sender_t *sender);

/* Notifies when something finished while nobody was looking. Only for work the
 * person ASKED for and then left - a scheduled job, a long generation. An
 * assistant that notifies on its own initiative is an app that has decided its
 * own thoughts are worth interrupting somebody for. */
ca_ai_observer_t *ca_push_ai_observer_new(ca_push_notification_sender_t *sender);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOSTING_RUNTIME_H */
