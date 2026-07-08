#ifndef CIRCLE_AI_HOST_CRON_H
#define CIRCLE_AI_HOST_CRON_H

/*
 * host_cron.h — CircleAI.Hosting scheduling substrate + proactive reasoning
 * (C11 port).
 *
 * Ports (from src/CircleAI.Hosting):
 *   CronScheduleParser.GetNextOccurrence — 5-field cron (min hour dom month dow;
 *                                          0=Sun..6=Sat), lists/steps/ranges,
 *                                          strictly-after, 5-year search cap
 *   CronJob / DeliveryTarget / CronJobState
 *   IScheduledTaskStore + InMemoryScheduledTaskStore
 *   ScheduledAIService                   — the 30 s poll loop, ported as a
 *                                          deterministic tick(now_ms) that runs
 *                                          due jobs via IAIService.AskAsync,
 *                                          recomputes NextRun, and fires an
 *                                          OnJobCompleted callback
 *   ITriggerCondition + ScheduleTrigger + IdleTrigger + ProactiveContext
 *   IProactiveReasoningService + ProactiveReasoningService (+ ProactiveMessage)
 *   IThermalThrottleService + ThermalThrottleService (ThermalState; sampling is
 *                                          an injected probe)
 *   BackgroundInferenceWorker            — start/stop butler + thermal pause gate
 *   IRequestPredictor + HistogramRequestPredictor + ArrivalForecast (Warmup)
 *   PredictiveWarmupController + PredictiveWarmupOptions
 *
 * Distinct namespace from proactive.h's CronExpression (that is
 * CircleAI.Companion.Proactive) — these symbols are ca_host_cron_* /
 * ca_scheduled_* / ca_trigger_* etc.
 *
 * Times are Unix ms UTC. No pthreads — every "background loop" is exposed as a
 * host-driven tick with an explicit now_ms so behaviour is deterministic.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "host_ai.h"      /* ca_ai_service_t */
#include "goal_store.h"   /* ca_goal_record_t, ca_goal_store_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * CronScheduleParser
 * =========================================================================== */

/* Earliest UTC ms strictly after `after_ms` that satisfies the 5-field cron.
 * Writes *out_ms and returns true; returns false on a malformed expression or
 * when nothing matches within 5 years (the C# throws — the port signals false).
 */
bool ca_host_cron_next_occurrence(const char *cron_expression, int64_t after_ms,
                                  int64_t *out_ms);

/* ===========================================================================
 * CronJob + enums
 * =========================================================================== */

typedef enum {
    CA_DELIVERY_LOCAL    = 0,
    CA_DELIVERY_PUSH     = 1,
    CA_DELIVERY_TELEGRAM = 2,
    CA_DELIVERY_EMAIL    = 3,
    CA_DELIVERY_CUSTOM   = 4
} ca_delivery_target_t;

typedef enum {
    CA_CRONJOB_PENDING   = 0,
    CA_CRONJOB_RUNNING   = 1,
    CA_CRONJOB_SUCCEEDED = 2,
    CA_CRONJOB_FAILED    = 3,
    CA_CRONJOB_PAUSED    = 4
} ca_cronjob_state_t;

typedef struct {
    char                *id;              /* owned */
    char                *name;            /* owned */
    char                *prompt;          /* owned */
    char                *cron_expression; /* owned */
    ca_delivery_target_t delivery;
    bool                 has_last_run;
    int64_t              last_run_utc_ms;
    bool                 has_next_run;
    int64_t              next_run_utc_ms;
    ca_cronjob_state_t   state;
    bool                 is_enabled;      /* default true */
} ca_cron_job_t;

void ca_cron_job_free(ca_cron_job_t *j);
void ca_cron_job_free_array(ca_cron_job_t *arr, size_t count);
/* Deep-copy src into dst (dst fields overwritten). Returns dst. */
ca_cron_job_t *ca_cron_job_copy(ca_cron_job_t *dst, const ca_cron_job_t *src);

/* ===========================================================================
 * IScheduledTaskStore + InMemoryScheduledTaskStore
 * =========================================================================== */

typedef struct ca_scheduled_task_store ca_scheduled_task_store_t;

ca_scheduled_task_store_t *ca_scheduled_task_store_create(void);
void ca_scheduled_task_store_destroy(ca_scheduled_task_store_t *s);

/* ListAsync — every job, insertion order. Fresh array (caller frees). NULL +
 * *out_count 0 when empty. */
ca_cron_job_t *ca_scheduled_task_store_list(ca_scheduled_task_store_t *s, size_t *out_count);
/* GetAsync — deep copy into *out (true) or false when absent / blank id. */
bool ca_scheduled_task_store_get(ca_scheduled_task_store_t *s, const char *id, ca_cron_job_t *out);
/* UpsertAsync — insert/replace by Id. Deep-copies in. Returns false on NULL. */
bool ca_scheduled_task_store_upsert(ca_scheduled_task_store_t *s, const ca_cron_job_t *job);
/* DeleteAsync — remove by id; no-op when absent. */
void ca_scheduled_task_store_delete(ca_scheduled_task_store_t *s, const char *id);
/* GetDueJobsAsync — enabled jobs with NextRunUtc <= now_ms. Fresh array. */
ca_cron_job_t *ca_scheduled_task_store_due(ca_scheduled_task_store_t *s, int64_t now_ms,
                                           size_t *out_count);

/* ===========================================================================
 * ScheduledAIService
 * ===========================================================================
 *
 * The 30 s polling loop is exposed as a deterministic tick. Each tick pulls due
 * jobs, marks Running, runs the prompt via IAIService.AskAsync, recomputes
 * NextRun from the cron expression, persists Succeeded/Failed, and invokes the
 * on_completed callback (JobCompletedEventArgs). Poll interval is fixed 30 s
 * (informational; the host drives the tick cadence).
 */

/* on_completed(user, job, response, error_message). job/response/error are
 * borrowed for the call. error_message is NULL on success. */
typedef void (*ca_scheduled_job_completed_fn)(void *user, const ca_cron_job_t *job,
                                              const char *response,
                                              const char *error_message);

typedef struct ca_scheduled_ai_service ca_scheduled_ai_service_t;

/* butler + store borrowed. */
ca_scheduled_ai_service_t *ca_scheduled_ai_service_create(
    ca_ai_service_t *butler, ca_scheduled_task_store_t *store,
    ca_scheduled_job_completed_fn on_completed, void *on_completed_user);
void ca_scheduled_ai_service_destroy(ca_scheduled_ai_service_t *svc);

/* Poll-interval seconds (30). */
double ca_scheduled_ai_service_poll_seconds(const ca_scheduled_ai_service_t *svc);
/* One poll cycle at now_ms: process every due job. Returns jobs executed. */
size_t ca_scheduled_ai_service_tick(ca_scheduled_ai_service_t *svc, int64_t now_ms);

/* ===========================================================================
 * Trigger conditions (ITriggerCondition + ScheduleTrigger + IdleTrigger)
 * =========================================================================== */

/* ProactiveContext snapshot. affect_state may be NULL (has_affect false);
 * active_goals borrowed. */
typedef struct {
    const char             *user_id;
    int64_t                 now_utc_ms;
    int64_t                 time_since_last_interaction_ms;
    bool                    has_affect;
    const ca_goal_record_t *active_goals;
    size_t                  active_goal_count;
} ca_proactive_context_t;

/* A trigger is a name + a stateful is_met predicate. The two concrete triggers
 * (schedule/idle) carry their own state; the vtable seam keeps the reasoning
 * service generic. */
typedef struct ca_trigger ca_trigger_t;

const char *ca_trigger_name(const ca_trigger_t *t);
bool        ca_trigger_is_met(ca_trigger_t *t, const ca_proactive_context_t *ctx);
void        ca_trigger_destroy(ca_trigger_t *t);

/* IdleTrigger — fires when TimeSinceLastInteraction > threshold (default 4 h).
 * idle_threshold_ms <= 0 => 4 hours. */
ca_trigger_t *ca_idle_trigger_create(int64_t idle_threshold_ms);
int64_t       ca_idle_trigger_threshold(const ca_trigger_t *t);

/* ScheduleTrigger — fires at a local time-of-day, within a 5-minute window,
 * once per calendar day. trigger_seconds_of_day in [0, 86400). Because the C
 * port has no ambient local zone, "local time" here is UTC-derived from
 * now_utc_ms (matching the deterministic UTC decomposition used elsewhere).
 * name may be NULL => "schedule". */
ca_trigger_t *ca_schedule_trigger_create(int trigger_seconds_of_day, const char *name);
int           ca_schedule_trigger_seconds_of_day(const ca_trigger_t *t);

/* ===========================================================================
 * ProactiveReasoningService
 * =========================================================================== */

/* on_message(user, user_id, message, trigger_name, generated_utc_ms). */
typedef void (*ca_proactive_message_fn)(void *user, const char *user_id,
                                        const char *message, const char *trigger_name,
                                        int64_t generated_utc_ms);

typedef struct ca_proactive_reasoning_service ca_proactive_reasoning_service_t;

/* butler borrowed. goal_store may be NULL. triggers array is borrowed for the
 * lifetime of the service (evaluated in order; first that fires wins). The
 * affect "last updated" time is supplied per-check (no affect store here). */
ca_proactive_reasoning_service_t *ca_proactive_reasoning_service_create(
    ca_ai_service_t *butler, ca_goal_store_t *goal_store,
    ca_trigger_t *const *triggers, size_t trigger_count,
    ca_proactive_message_fn on_message, void *on_message_user);
void ca_proactive_reasoning_service_destroy(ca_proactive_reasoning_service_t *svc);

/* CheckAsync(userId). now_ms is the current UTC; time_since_last_ms is the gap
 * since the last interaction (the C# derives this from affect.LastUpdatedUtc).
 * Loads active goals, builds the context, evaluates triggers in order, fires
 * one proactive message. Returns true when a message was generated. Blank
 * user_id => false. */
bool ca_proactive_reasoning_service_check(ca_proactive_reasoning_service_t *svc,
                                          const char *user_id, int64_t now_ms,
                                          int64_t time_since_last_ms);

/* Build the proactive prompt (exposed for tests; mirrors BuildProactivePrompt).
 * Returns a freshly-allocated string (caller frees). */
char *ca_proactive_build_prompt(const char *user_id, int64_t time_since_last_ms,
                                const ca_goal_record_t *active_goals, size_t goal_count);

/* ===========================================================================
 * ThermalThrottleService
 * =========================================================================== */

typedef enum {
    CA_HOST_THERMAL_UNKNOWN  = 0,
    CA_HOST_THERMAL_NORMAL   = 1,
    CA_HOST_THERMAL_FAIR     = 2,
    CA_HOST_THERMAL_SERIOUS  = 3,
    CA_HOST_THERMAL_CRITICAL = 4
} ca_host_thermal_state_t;

/* Sampling seam: return the current sampled state. The default probe (NULL)
 * yields Unknown. */
typedef ca_host_thermal_state_t (*ca_thermal_sample_fn)(void *user);

/* on_changed(user, new_state) fired when the sampled state transitions. */
typedef void (*ca_thermal_changed_fn)(void *user, ca_host_thermal_state_t new_state);

typedef struct ca_thermal_throttle_service ca_thermal_throttle_service_t;

ca_thermal_throttle_service_t *ca_thermal_throttle_service_create(
    ca_thermal_sample_fn sample, void *sample_user,
    ca_thermal_changed_fn on_changed, void *on_changed_user);
void ca_thermal_throttle_service_destroy(ca_thermal_throttle_service_t *s);

ca_host_thermal_state_t ca_thermal_throttle_current(const ca_thermal_throttle_service_t *s);
/* True when CurrentState >= Serious. */
bool ca_thermal_throttle_should_pause(const ca_thermal_throttle_service_t *s);
/* StartMonitoring: samples immediately (fires on_changed on transition from
 * Unknown). Idempotent. */
void ca_thermal_throttle_start(ca_thermal_throttle_service_t *s);
void ca_thermal_throttle_stop(ca_thermal_throttle_service_t *s);
/* One poll step (mirrors a PeriodicTimer tick): re-sample + fire on transition.
 */
void ca_thermal_throttle_poll(ca_thermal_throttle_service_t *s);

/* ===========================================================================
 * BackgroundInferenceWorker
 * =========================================================================== */

typedef struct ca_background_inference_worker ca_background_inference_worker_t;

/* butler borrowed; thermal may be NULL. */
ca_background_inference_worker_t *ca_background_inference_worker_create(
    ca_ai_service_t *butler, ca_thermal_throttle_service_t *thermal);
void ca_background_inference_worker_destroy(ca_background_inference_worker_t *w);

/* StartAsync: start thermal monitoring (if any) + start the butler. */
bool ca_background_inference_worker_start(ca_background_inference_worker_t *w);
/* StopAsync: stop thermal + stop the butler. Idempotent. */
bool ca_background_inference_worker_stop(ca_background_inference_worker_t *w);
/* IsPaused — true while thermal >= Serious. Recomputed from the thermal
 * service's current state. */
bool ca_background_inference_worker_is_paused(const ca_background_inference_worker_t *w);

/* ===========================================================================
 * Warmup — IRequestPredictor + HistogramRequestPredictor
 * =========================================================================== */

typedef struct {
    double probability_of_arrival;  /* 0..1 */
    double expected_count;
    double confidence;              /* 0..1 */
} ca_arrival_forecast_t;

typedef struct ca_histogram_request_predictor ca_histogram_request_predictor_t;

/* history_days <= 0 => 7. Returns NULL on OOM. */
ca_histogram_request_predictor_t *ca_histogram_request_predictor_create(int history_days);
void ca_histogram_request_predictor_destroy(ca_histogram_request_predictor_t *p);

void   ca_histogram_request_predictor_record(ca_histogram_request_predictor_t *p, int64_t utc_ms);
int64_t ca_histogram_request_predictor_observed(const ca_histogram_request_predictor_t *p);
ca_arrival_forecast_t ca_histogram_request_predictor_predict(
    const ca_histogram_request_predictor_t *p, int64_t utc_now_ms, int64_t forecast_window_ms);
void ca_histogram_request_predictor_reset(ca_histogram_request_predictor_t *p);

/* ===========================================================================
 * PredictiveWarmupController + options
 * =========================================================================== */

typedef struct {
    bool    enabled;                 /* default false */
    int64_t poll_interval_ms;        /* default 30 s */
    int64_t forecast_window_ms;      /* default 60 s */
    double  warmup_threshold;        /* default 0.5 */
    int64_t min_time_between_warmups_ms; /* default 5 min */
} ca_predictive_warmup_options_t;

void ca_predictive_warmup_options_init(ca_predictive_warmup_options_t *o);

typedef struct ca_predictive_warmup_controller ca_predictive_warmup_controller_t;

/* service + predictor borrowed. options copied. */
ca_predictive_warmup_controller_t *ca_predictive_warmup_controller_create(
    ca_ai_service_t *service, ca_histogram_request_predictor_t *predictor,
    const ca_predictive_warmup_options_t *options);
void ca_predictive_warmup_controller_destroy(ca_predictive_warmup_controller_t *c);

/* NotifyArrival — record an arrival on the predictor at now_ms. */
void ca_predictive_warmup_controller_notify_arrival(ca_predictive_warmup_controller_t *c, int64_t now_ms);
/* TickAsync(now_ms): predict + maybe prewarm. Returns true when warmup fired. */
bool ca_predictive_warmup_controller_tick(ca_predictive_warmup_controller_t *c, int64_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_CRON_H */
