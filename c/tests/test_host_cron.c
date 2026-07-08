/*
 * test_host_cron.c — CircleAI.Hosting scheduling substrate (C11 port).
 *
 * Verifies CronScheduleParser.GetNextOccurrence, CronJob + store, the
 * ScheduledAIService deterministic tick, triggers (idle/schedule),
 * ProactiveReasoningService, ThermalThrottleService, BackgroundInferenceWorker,
 * HistogramRequestPredictor, and PredictiveWarmupController.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* civil ms helper for building known instants (UTC). */
static int64_t ms(int64_t y, int mo, int d, int h, int mi) {
    /* reuse the same Hinnant formula the parser uses so we test civil round-trip */
    int64_t yy = y; if (mo <= 2) yy -= 1;
    int64_t era = (yy >= 0 ? yy : yy - 399) / 400;
    unsigned yoe = (unsigned)(yy - era * 400);
    unsigned doy = (153 * (unsigned)(mo + (mo > 2 ? -3 : 9)) + 2) / 5 + (unsigned)d - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = era * 146097 + (int64_t)doe - 719468;
    return (days * 86400 + (int64_t)h * 3600 + (int64_t)mi * 60) * 1000;
}

static void test_cron_parser(void) {
    int64_t out = 0;

    /* every minute: strictly after -> next whole minute */
    int64_t base = ms(2026, 3, 15, 10, 30); /* 10:30:00 */
    assert(ca_host_cron_next_occurrence("* * * * *", base, &out));
    assert(out == ms(2026, 3, 15, 10, 31));

    /* fixed 0 9 * * * -> next 09:00 */
    assert(ca_host_cron_next_occurrence("0 9 * * *", ms(2026, 3, 15, 10, 30), &out));
    assert(out == ms(2026, 3, 16, 9, 0));
    /* if before 9am same day, that day's 9am */
    assert(ca_host_cron_next_occurrence("0 9 * * *", ms(2026, 3, 15, 8, 0), &out));
    assert(out == ms(2026, 3, 15, 9, 0));

    /* step: every 15 minutes */
    assert(ca_host_cron_next_occurrence("*/15 * * * *", ms(2026, 3, 15, 10, 7), &out));
    assert(out == ms(2026, 3, 15, 10, 15));
    assert(ca_host_cron_next_occurrence("*/15 * * * *", ms(2026, 3, 15, 10, 15), &out));
    assert(out == ms(2026, 3, 15, 10, 30));

    /* list of minutes */
    assert(ca_host_cron_next_occurrence("5,35 * * * *", ms(2026, 3, 15, 10, 10), &out));
    assert(out == ms(2026, 3, 15, 10, 35));

    /* range of hours 9-17 at minute 0 */
    assert(ca_host_cron_next_occurrence("0 9-17 * * *", ms(2026, 3, 15, 17, 30), &out));
    assert(out == ms(2026, 3, 16, 9, 0));

    /* day-of-week: 0 0 * * 0 (Sunday). 2026-03-15 is a Sunday. */
    assert(ca_host_cron_next_occurrence("0 0 * * 0", ms(2026, 3, 15, 1, 0), &out));
    /* next Sunday 00:00 is 2026-03-22 */
    assert(out == ms(2026, 3, 22, 0, 0));

    /* specific day-of-month */
    assert(ca_host_cron_next_occurrence("0 0 1 * *", ms(2026, 3, 15, 0, 0), &out));
    assert(out == ms(2026, 4, 1, 0, 0));

    /* impossible: Feb 31 -> no occurrence within 5 years -> false */
    assert(ca_host_cron_next_occurrence("0 9 31 2 *", ms(2026, 1, 1, 0, 0), &out) == false);

    /* malformed */
    assert(ca_host_cron_next_occurrence("* * * *", base, &out) == false);   /* 4 fields */
    assert(ca_host_cron_next_occurrence("60 * * * *", base, &out) == false); /* minute out of range */
    assert(ca_host_cron_next_occurrence("* 24 * * *", base, &out) == false); /* hour out of range */
    assert(ca_host_cron_next_occurrence("", base, &out) == false);
    assert(ca_host_cron_next_occurrence("*/0 * * * *", base, &out) == false); /* zero step */

    printf("  cron parser: ok\n");
}

static void test_store(void) {
    ca_scheduled_task_store_t *s = ca_scheduled_task_store_create();
    assert(s);

    ca_cron_job_t j; memset(&j, 0, sizeof(j));
    j.id = strdup("j1"); j.name = strdup("Daily"); j.prompt = strdup("brief me");
    j.cron_expression = strdup("0 9 * * *");
    j.delivery = CA_DELIVERY_LOCAL; j.state = CA_CRONJOB_PENDING; j.is_enabled = true;
    j.has_next_run = true; j.next_run_utc_ms = 1000;
    assert(ca_scheduled_task_store_upsert(s, &j));
    ca_cron_job_free(&j);

    size_t n = 0;
    ca_cron_job_t *list = ca_scheduled_task_store_list(s, &n);
    assert(n == 1 && strcmp(list[0].name, "Daily") == 0);
    ca_cron_job_free_array(list, n);

    ca_cron_job_t got; memset(&got, 0, sizeof(got));
    assert(ca_scheduled_task_store_get(s, "j1", &got));
    assert(got.delivery == CA_DELIVERY_LOCAL && got.is_enabled);
    ca_cron_job_free(&got);

    /* due: next_run 1000 <= now 5000 */
    ca_cron_job_t *due = ca_scheduled_task_store_due(s, 5000, &n);
    assert(n == 1);
    ca_cron_job_free_array(due, n);
    /* not due before next_run */
    due = ca_scheduled_task_store_due(s, 500, &n);
    assert(n == 0 && due == NULL);

    /* disabled -> not due */
    assert(ca_scheduled_task_store_get(s, "j1", &got));
    got.is_enabled = false;
    ca_scheduled_task_store_upsert(s, &got);
    ca_cron_job_free(&got);
    due = ca_scheduled_task_store_due(s, 5000, &n);
    assert(n == 0);
    ca_cron_job_free_array(due, n);

    ca_scheduled_task_store_delete(s, "j1");
    list = ca_scheduled_task_store_list(s, &n);
    assert(n == 0);
    ca_cron_job_free_array(list, n);

    ca_scheduled_task_store_destroy(s);
    printf("  store: ok\n");
}

/* --- ScheduledAIService tick over a real AIService --- */
static int g_completed_count = 0;
static char g_last_response[256];
static void on_job_done(void *user, const ca_cron_job_t *job, const char *response, const char *err) {
    (void)user; (void)err;
    g_completed_count++;
    snprintf(g_last_response, sizeof(g_last_response), "%s", response ? response : "");
    assert(job->state == CA_CRONJOB_SUCCEEDED || job->state == CA_CRONJOB_FAILED);
}

static void test_scheduled_service(void) {
    ca_ai_options_t2 opts; assert(ca_ai_options_init(&opts));
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    assert(impl);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    assert(ca_ai_service_start(svc));

    ca_scheduled_task_store_t *store = ca_scheduled_task_store_create();
    ca_scheduled_ai_service_t *sched = ca_scheduled_ai_service_create(svc, store, on_job_done, NULL);
    assert(sched);
    assert(ca_scheduled_ai_service_poll_seconds(sched) == 30.0);

    ca_cron_job_t j; memset(&j, 0, sizeof(j));
    j.id = strdup("d1"); j.name = strdup("d"); j.prompt = strdup("hello world");
    j.cron_expression = strdup("*/5 * * * *");
    j.is_enabled = true; j.has_next_run = true;
    j.next_run_utc_ms = ms(2026, 3, 15, 10, 0);
    ca_scheduled_task_store_upsert(store, &j);
    ca_cron_job_free(&j);

    g_completed_count = 0;
    /* tick before due -> nothing */
    size_t ran = ca_scheduled_ai_service_tick(sched, ms(2026, 3, 15, 9, 0));
    assert(ran == 0 && g_completed_count == 0);

    /* tick after due -> runs, recomputes next-run */
    ran = ca_scheduled_ai_service_tick(sched, ms(2026, 3, 15, 10, 1));
    assert(ran == 1 && g_completed_count == 1);
    assert(strlen(g_last_response) > 0);   /* butler produced a reply */

    /* job now has state Succeeded + a future next-run -> not due at same instant */
    ca_cron_job_t got; memset(&got, 0, sizeof(got));
    assert(ca_scheduled_task_store_get(store, "d1", &got));
    assert(got.state == CA_CRONJOB_SUCCEEDED);
    assert(got.has_last_run && got.has_next_run);
    assert(got.next_run_utc_ms > ms(2026, 3, 15, 10, 1));
    ca_cron_job_free(&got);

    ran = ca_scheduled_ai_service_tick(sched, ms(2026, 3, 15, 10, 1));
    assert(ran == 0);

    ca_scheduled_ai_service_destroy(sched);
    ca_scheduled_task_store_destroy(store);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  scheduled service: ok\n");
}

static void test_triggers(void) {
    /* idle: default 4h */
    ca_trigger_t *idle = ca_idle_trigger_create(0);
    assert(strcmp(ca_trigger_name(idle), "idle") == 0);
    assert(ca_idle_trigger_threshold(idle) == 4LL * 3600 * 1000);
    ca_proactive_context_t ctx;
    memset(&ctx, 0, sizeof(ctx));
    ctx.user_id = "u1"; ctx.now_utc_ms = ms(2026, 3, 15, 12, 0);
    ctx.time_since_last_interaction_ms = 3LL * 3600 * 1000; /* 3h < 4h */
    assert(ca_trigger_is_met(idle, &ctx) == false);
    ctx.time_since_last_interaction_ms = 5LL * 3600 * 1000; /* 5h > 4h */
    assert(ca_trigger_is_met(idle, &ctx) == true);
    ca_trigger_destroy(idle);

    /* schedule: fires within 5-min window, once per day */
    int nine_am = 9 * 3600;
    ca_trigger_t *sch = ca_schedule_trigger_create(nine_am, NULL);
    assert(strcmp(ca_trigger_name(sch), "schedule") == 0);
    ctx.now_utc_ms = ms(2026, 3, 15, 8, 59); /* before window */
    assert(ca_trigger_is_met(sch, &ctx) == false);
    ctx.now_utc_ms = ms(2026, 3, 15, 9, 2);  /* in window */
    assert(ca_trigger_is_met(sch, &ctx) == true);
    /* already fired today */
    ctx.now_utc_ms = ms(2026, 3, 15, 9, 3);
    assert(ca_trigger_is_met(sch, &ctx) == false);
    /* next day fires again */
    ctx.now_utc_ms = ms(2026, 3, 16, 9, 1);
    assert(ca_trigger_is_met(sch, &ctx) == true);
    ca_trigger_destroy(sch);
    printf("  triggers: ok\n");
}

static int g_proactive_fired = 0;
static char g_proactive_trigger[32];
static void on_proactive(void *user, const char *uid, const char *msg, const char *trig, int64_t when) {
    (void)user; (void)uid; (void)msg; (void)when;
    g_proactive_fired++;
    snprintf(g_proactive_trigger, sizeof(g_proactive_trigger), "%s", trig);
}

static void test_proactive_service(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(svc);

    ca_goal_store_t *goals = ca_goal_store_create();
    ca_goal_record_t g; memset(&g, 0, sizeof(g));
    g.id = strdup("g1"); g.user_id = strdup("u1"); g.title = strdup("Ship v1");
    g.description = strdup("d"); g.status = CA_GOAL_STATUS_ACTIVE; g.created_utc_ms = 1;
    ca_goal_store_upsert(goals, &g);
    ca_goal_record_free(&g);

    ca_trigger_t *idle = ca_idle_trigger_create(4LL * 3600 * 1000);
    ca_trigger_t *triggers[1] = { idle };
    ca_proactive_reasoning_service_t *svc2 = ca_proactive_reasoning_service_create(
        svc, goals, triggers, 1, on_proactive, NULL);
    assert(svc2);

    g_proactive_fired = 0;
    /* not idle enough -> no fire */
    assert(ca_proactive_reasoning_service_check(svc2, "u1", ms(2026, 3, 15, 12, 0), 1LL * 3600 * 1000) == false);
    assert(g_proactive_fired == 0);
    /* idle 5h -> fires */
    assert(ca_proactive_reasoning_service_check(svc2, "u1", ms(2026, 3, 15, 12, 0), 5LL * 3600 * 1000) == true);
    assert(g_proactive_fired == 1 && strcmp(g_proactive_trigger, "idle") == 0);
    /* blank user -> false */
    assert(ca_proactive_reasoning_service_check(svc2, "  ", ms(2026, 3, 15, 12, 0), 5LL * 3600 * 1000) == false);

    /* prompt builder */
    ca_goal_record_t *active = NULL; size_t nn = 0;
    active = ca_goal_store_get_active(goals, "u1", &nn);
    char *p = ca_proactive_build_prompt("u1", 2LL * 3600 * 1000, active, nn);
    assert(strstr(p, "You are B!"));
    assert(strstr(p, "2 hours"));
    assert(strstr(p, "1 active goal"));
    assert(strstr(p, "Ship v1"));
    free(p);
    ca_goal_record_free_array(active, nn);

    ca_proactive_reasoning_service_destroy(svc2);
    ca_trigger_destroy(idle);
    ca_goal_store_destroy(goals);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  proactive service: ok\n");
}

/* --- thermal --- */
static ca_host_thermal_state_t g_thermal = CA_HOST_THERMAL_NORMAL;
static ca_host_thermal_state_t sample_thermal(void *user) { (void)user; return g_thermal; }
static int g_thermal_changes = 0;
static ca_host_thermal_state_t g_thermal_last;
static void on_thermal(void *user, ca_host_thermal_state_t s) { (void)user; g_thermal_changes++; g_thermal_last = s; }

static void test_thermal(void) {
    g_thermal = CA_HOST_THERMAL_NORMAL; g_thermal_changes = 0;
    ca_thermal_throttle_service_t *t = ca_thermal_throttle_service_create(sample_thermal, NULL, on_thermal, NULL);
    assert(ca_thermal_throttle_current(t) == CA_HOST_THERMAL_UNKNOWN);
    ca_thermal_throttle_start(t); /* samples immediately -> Normal, fires change Unknown->Normal */
    assert(ca_thermal_throttle_current(t) == CA_HOST_THERMAL_NORMAL);
    assert(g_thermal_changes == 1 && g_thermal_last == CA_HOST_THERMAL_NORMAL);
    assert(ca_thermal_throttle_should_pause(t) == false);

    g_thermal = CA_HOST_THERMAL_SERIOUS;
    ca_thermal_throttle_poll(t);
    assert(ca_thermal_throttle_current(t) == CA_HOST_THERMAL_SERIOUS);
    assert(ca_thermal_throttle_should_pause(t) == true);
    assert(g_thermal_changes == 2);

    /* same state -> no new change */
    ca_thermal_throttle_poll(t);
    assert(g_thermal_changes == 2);

    ca_thermal_throttle_service_destroy(t);
    printf("  thermal: ok\n");
}

static void test_background_worker(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);

    g_thermal = CA_HOST_THERMAL_NORMAL;
    ca_thermal_throttle_service_t *t = ca_thermal_throttle_service_create(sample_thermal, NULL, NULL, NULL);
    ca_background_inference_worker_t *w = ca_background_inference_worker_create(svc, t);
    assert(ca_background_inference_worker_start(w));
    assert(ca_ai_service_is_ready(svc));
    assert(ca_background_inference_worker_is_paused(w) == false);

    g_thermal = CA_HOST_THERMAL_CRITICAL;
    ca_thermal_throttle_poll(t);
    assert(ca_background_inference_worker_is_paused(w) == true);

    assert(ca_background_inference_worker_stop(w));
    assert(ca_background_inference_worker_stop(w)); /* idempotent */
    ca_background_inference_worker_destroy(w);
    ca_thermal_throttle_service_destroy(t);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  background worker: ok\n");
}

static void test_predictor(void) {
    ca_histogram_request_predictor_t *p = ca_histogram_request_predictor_create(7);
    assert(ca_histogram_request_predictor_observed(p) == 0);

    /* cold: zero confidence */
    ca_arrival_forecast_t f = ca_histogram_request_predictor_predict(p, ms(2026, 3, 15, 10, 0), 60LL * 1000);
    assert(f.confidence == 0.0 && f.probability_of_arrival == 0.0);

    /* record many arrivals at 10:00 across days */
    for (int day = 0; day < 30; ++day)
        ca_histogram_request_predictor_record(p, ms(2026, 3, 15 + day, 10, 0));
    assert(ca_histogram_request_predictor_observed(p) == 30);

    f = ca_histogram_request_predictor_predict(p, ms(2026, 4, 20, 10, 0), 60LL * 1000);
    assert(f.expected_count > 0.0);
    assert(f.probability_of_arrival > 0.0 && f.probability_of_arrival < 1.0);
    assert(f.confidence > 0.0);

    /* window at a cold time -> low expected */
    f = ca_histogram_request_predictor_predict(p, ms(2026, 4, 20, 3, 0), 60LL * 1000);
    assert(f.expected_count == 0.0);

    ca_histogram_request_predictor_reset(p);
    assert(ca_histogram_request_predictor_observed(p) == 0);
    ca_histogram_request_predictor_destroy(p);
    printf("  predictor: ok\n");
}

static void test_warmup_controller(void) {
    ca_ai_options_t2 opts; ca_ai_options_init(&opts);
    ca_ai_service_impl_t *impl = ca_ai_service_impl_create(&opts);
    ca_ai_service_t *svc = ca_ai_service_impl_as_service(impl);
    ca_ai_service_start(svc);

    ca_histogram_request_predictor_t *p = ca_histogram_request_predictor_create(7);
    ca_predictive_warmup_options_t wo; ca_predictive_warmup_options_init(&wo);
    assert(wo.enabled == false && wo.warmup_threshold == 0.5);
    wo.enabled = true;
    ca_predictive_warmup_controller_t *c = ca_predictive_warmup_controller_create(svc, p, &wo);
    assert(c);

    /* no history -> no warmup */
    assert(ca_predictive_warmup_controller_tick(c, ms(2026, 3, 15, 10, 0)) == false);

    /* saturate the 10:00 slot so probability*confidence >= 0.5 */
    for (int day = 0; day < 60; ++day)
        for (int k = 0; k < 3; ++k)
            ca_predictive_warmup_controller_notify_arrival(c, ms(2026, 3, 15 + day, 10, 0));

    bool fired = ca_predictive_warmup_controller_tick(c, ms(2026, 6, 1, 10, 0));
    assert(fired == true);
    /* immediate re-tick blocked by min-time-between-warmups */
    assert(ca_predictive_warmup_controller_tick(c, ms(2026, 6, 1, 10, 0)) == false);

    ca_predictive_warmup_controller_destroy(c);
    ca_histogram_request_predictor_destroy(p);
    ca_ai_service_impl_destroy(impl);
    ca_ai_options_free(&opts);
    printf("  warmup controller: ok\n");
}

int main(void) {
    test_cron_parser();
    test_store();
    test_scheduled_service();
    test_triggers();
    test_proactive_service();
    test_thermal();
    test_background_worker();
    test_predictor();
    test_warmup_controller();
    printf("test_host_cron: all assertions passed\n");
    return 0;
}
