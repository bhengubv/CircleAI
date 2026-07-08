/*
 * test_proactive.c — CircleAI.Companion.Proactive (C11).
 *
 * CronExpression + ProactiveScheduler + In-memory source + Null/Delegate runner.
 * Cron reference instants are computed against the C# DateTimeOffset semantics
 * (UTC, 0=Sunday day-of-week, AND of day-of-month & day-of-week).
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

/* 2021-06-14T13:00:00Z (Monday). Unix ms. */
#define MON_1300Z 1623675600000LL
#define MIN_MS    60000LL
#define HOUR_MS   3600000LL
#define DAY_MS    86400000LL

/* ========================================================================= */
static void test_cron_parse_errors(void) {
    assert(ca_cron_parse(NULL) == NULL);
    assert(ca_cron_parse("* * * *") == NULL);          /* 4 fields */
    assert(ca_cron_parse("* * * * * *") == NULL);      /* 6 fields */
    assert(ca_cron_parse("60 * * * *") == NULL);       /* minute out of range */
    assert(ca_cron_parse("* 24 * * *") == NULL);       /* hour out of range */
    assert(ca_cron_parse("* * 0 * *") == NULL);        /* dom min is 1 */
    assert(ca_cron_parse("* * * 13 *") == NULL);       /* month max 12 */
    assert(ca_cron_parse("* * * * 7") == NULL);        /* dow max 6 */
    assert(ca_cron_parse("*/0 * * * *") == NULL);      /* step must be positive */
    assert(ca_cron_parse("5-2 * * * *") == NULL);      /* inverted range */
    printf("  cron_parse_errors: ok\n");
}

static void test_cron_matches(void) {
    /* every minute */
    ca_cron_expression_t *e = ca_cron_parse("* * * * *");
    assert(e);
    assert(ca_cron_matches(e, MON_1300Z));
    ca_cron_destroy(e);

    /* 0 13 * * 1 → minute 0, hour 13, Monday. MON_1300Z matches. */
    e = ca_cron_parse("0 13 * * 1");
    assert(e);
    assert(ca_cron_matches(e, MON_1300Z));
    assert(!ca_cron_matches(e, MON_1300Z + MIN_MS));      /* 13:01 no */
    assert(!ca_cron_matches(e, MON_1300Z + DAY_MS));      /* Tuesday no */
    ca_cron_destroy(e);

    /* AND semantics: "0 0 14 * 0" needs day-of-month 14 AND Sunday. 2021-06-14
     * is a Monday → never matches on the 14th unless it also falls on Sunday. */
    e = ca_cron_parse("0 0 14 * 0");
    assert(e);
    int64_t midnight14 = MON_1300Z - 13 * HOUR_MS;    /* 2021-06-14T00:00Z (Mon) */
    assert(!ca_cron_matches(e, midnight14));           /* 14th but Monday, not Sunday */
    ca_cron_destroy(e);

    /* step + list + range */
    e = ca_cron_parse("*/15 9-17 * * 1,3,5");
    assert(e);
    /* 13:00 Monday: minute 0 (0 %15==0 ✓), hour 13 in 9-17 ✓, Monday in {1,3,5} ✓ */
    assert(ca_cron_matches(e, MON_1300Z));
    assert(!ca_cron_matches(e, MON_1300Z + 5 * MIN_MS));   /* 13:05 not a /15 */
    assert(ca_cron_matches(e, MON_1300Z + 15 * MIN_MS));   /* 13:15 ✓ */
    ca_cron_destroy(e);

    printf("  cron_matches: ok\n");
}

static void test_cron_next(void) {
    /* 0 13 * * 1: next after 13:00 Monday is next Monday 13:00 (+7 days). */
    ca_cron_expression_t *e = ca_cron_parse("0 13 * * 1");
    assert(e);
    int64_t out;
    assert(ca_cron_next_occurrence(e, MON_1300Z, &out));
    assert(out == MON_1300Z + 7 * DAY_MS);
    /* just before → same day 13:00 */
    assert(ca_cron_next_occurrence(e, MON_1300Z - MIN_MS, &out));
    assert(out == MON_1300Z);
    ca_cron_destroy(e);

    /* every minute: next after t is t rounded up +1 min */
    e = ca_cron_parse("* * * * *");
    assert(e);
    assert(ca_cron_next_occurrence(e, MON_1300Z, &out));
    assert(out == MON_1300Z + MIN_MS);
    /* seconds inside a minute round up to the next whole minute */
    assert(ca_cron_next_occurrence(e, MON_1300Z + 30000, &out));
    assert(out == MON_1300Z + MIN_MS);
    ca_cron_destroy(e);

    printf("  cron_next: ok\n");
}

/* ========================================================================= */
/* Delegate runner that records how many times each task id ran. */
typedef struct { char ids[32][64]; int count; } run_log;
static run_log g_log;
static void logging_runner(void *user, const ca_proactive_task_t *task,
                           const ca_proactive_variables_t *variables,
                           ca_proactive_run_result_t *out) {
    (void)user; (void)variables;
    if (g_log.count < 32) {
        strncpy(g_log.ids[g_log.count], task->id, 63);
        g_log.ids[g_log.count][63] = '\0';
        g_log.count++;
    }
    out->task_id = strdup(task->id);
    out->success = true;
    out->failure_message = NULL;
}
static int count_runs(const char *id) {
    int c = 0;
    for (int i = 0; i < g_log.count; ++i) if (strcmp(g_log.ids[i], id) == 0) c++;
    return c;
}

static ca_proactive_task_t make_cron_task(const char *id, const char *cron) {
    ca_proactive_task_t t; memset(&t, 0, sizeof(t));
    t.id = (char *)id;
    t.trigger.cron = (char *)cron;
    return t;
}
static ca_proactive_task_t make_event_task(const char *id, const char *ev) {
    ca_proactive_task_t t; memset(&t, 0, sizeof(t));
    t.id = (char *)id;
    t.trigger.on_event = (char *)ev;
    return t;
}

static void test_scheduler(void) {
    ca_inmemory_source_t *src = ca_inmemory_source_create();
    assert(src);

    ca_proactive_scheduler_t *s = ca_proactive_scheduler_create(
        ca_inmemory_source_tasks, ca_inmemory_source_errors, src,
        logging_runner, NULL);
    assert(s);
    assert(strcmp(ca_proactive_scheduler_backend_id(s), "default") == 0);

    /* empty until refresh */
    size_t n;
    ca_proactive_task_t *snap = ca_proactive_scheduler_tasks(s, &n);
    assert(n == 0 && snap == NULL);

    /* add a cron task firing every minute + an event task */
    ca_proactive_task_t ct = make_cron_task("cronjob", "* * * * *");
    ca_proactive_task_t et = make_event_task("onsave", "note-saved");
    ca_inmemory_source_upsert(src, &ct);
    ca_inmemory_source_upsert(src, &et);

    ca_proactive_scheduler_refresh(s);
    snap = ca_proactive_scheduler_tasks(s, &n);
    assert(n == 2);
    ca_proactive_task_free_array(snap, n);

    /* GetNextRun for the cron task */
    int64_t next;
    ca_proactive_task_t probe = make_cron_task("cronjob", "* * * * *");
    assert(ca_proactive_scheduler_next_run(s, &probe, MON_1300Z, &next));
    assert(next == MON_1300Z + MIN_MS);
    /* event task → no cron next-run */
    ca_proactive_task_t probe2 = make_event_task("onsave", "note-saved");
    assert(!ca_proactive_scheduler_next_run(s, &probe2, MON_1300Z, &next));

    /* tick fires the cron task once (never-run anchor = now-1min → next <= now) */
    memset(&g_log, 0, sizeof(g_log));
    ca_proactive_scheduler_tick(s, MON_1300Z);
    assert(count_runs("cronjob") == 1);
    assert(count_runs("onsave") == 0);   /* event task not fired by tick */

    /* same-minute re-tick does NOT re-run (last-run tracking) */
    ca_proactive_scheduler_tick(s, MON_1300Z);
    assert(count_runs("cronjob") == 1);

    /* a minute later it fires again */
    ca_proactive_scheduler_tick(s, MON_1300Z + MIN_MS);
    assert(count_runs("cronjob") == 2);

    /* dispatch event fires the matching event task (case-insensitive) */
    memset(&g_log, 0, sizeof(g_log));
    ca_proactive_scheduler_dispatch_event(s, "NOTE-SAVED", NULL, MON_1300Z);
    assert(count_runs("onsave") == 1);
    ca_proactive_scheduler_dispatch_event(s, "unrelated", NULL, MON_1300Z);
    assert(count_runs("onsave") == 1);
    /* blank event → no-op */
    ca_proactive_scheduler_dispatch_event(s, "  ", NULL, MON_1300Z);
    assert(count_runs("onsave") == 1);

    /* RunById */
    memset(&g_log, 0, sizeof(g_log));
    ca_proactive_run_result_t res;
    assert(ca_proactive_scheduler_run_by_id(s, "cronjob", NULL, MON_1300Z, &res));
    assert(res.success && count_runs("cronjob") == 1);
    ca_proactive_run_result_free(&res);
    /* unknown id → success=false, message */
    assert(ca_proactive_scheduler_run_by_id(s, "ghost", NULL, MON_1300Z, &res));
    assert(!res.success && strstr(res.failure_message, "No task with id 'ghost'") != NULL);
    ca_proactive_run_result_free(&res);
    /* blank id → false */
    assert(!ca_proactive_scheduler_run_by_id(s, "  ", NULL, MON_1300Z, &res));

    ca_proactive_scheduler_destroy(s);
    ca_inmemory_source_destroy(src);
    printf("  scheduler: ok\n");
}

static void test_null_runner(void) {
    /* NullRunner fails every run */
    ca_inmemory_source_t *src = ca_inmemory_source_create();
    ca_proactive_task_t ct = make_cron_task("j", "* * * * *");
    ca_inmemory_source_upsert(src, &ct);

    ca_proactive_scheduler_t *s = ca_proactive_scheduler_create(
        ca_inmemory_source_tasks, ca_inmemory_source_errors, src,
        ca_null_runner_run, NULL);
    assert(s);
    ca_proactive_scheduler_refresh(s);

    ca_proactive_run_result_t res;
    assert(ca_proactive_scheduler_run_by_id(s, "j", NULL, MON_1300Z, &res));
    assert(!res.success);
    assert(strstr(res.failure_message, "No IProactiveTaskRunner registered") != NULL);
    ca_proactive_run_result_free(&res);

    ca_proactive_scheduler_destroy(s);
    ca_inmemory_source_destroy(src);
    printf("  null_runner: ok\n");
}

static void test_source_lifecycle(void) {
    ca_inmemory_source_t *src = ca_inmemory_source_create();

    /* upsert same id twice → replaced, not duplicated */
    ca_proactive_task_t a1 = make_cron_task("dup", "* * * * *");
    ca_proactive_task_t a2 = make_cron_task("dup", "0 9 * * *");
    ca_inmemory_source_upsert(src, &a1);
    ca_inmemory_source_upsert(src, &a2);
    size_t n;
    ca_proactive_task_t *tasks = ca_inmemory_source_tasks(src, &n);
    assert(n == 1);
    assert(strcmp(tasks[0].trigger.cron, "0 9 * * *") == 0);   /* the second wins */
    ca_proactive_task_free_array(tasks, n);

    /* same id, different context → distinct entries */
    ca_proactive_task_t ctx1 = make_cron_task("t", "* * * * *"); ctx1.source_context = (char *)"tenantA";
    ca_proactive_task_t ctx2 = make_cron_task("t", "* * * * *"); ctx2.source_context = (char *)"tenantB";
    ca_inmemory_source_upsert(src, &ctx1);
    ca_inmemory_source_upsert(src, &ctx2);
    tasks = ca_inmemory_source_tasks(src, &n);
    assert(n == 3);   /* dup + t@A + t@B */
    ca_proactive_task_free_array(tasks, n);

    /* remove by id + context */
    assert(ca_inmemory_source_remove(src, "t", "tenantA"));
    tasks = ca_inmemory_source_tasks(src, &n);
    assert(n == 2);
    ca_proactive_task_free_array(tasks, n);
    assert(!ca_inmemory_source_remove(src, "t", "tenantA"));   /* already gone */
    assert(!ca_inmemory_source_remove(src, "  ", NULL));        /* blank id */

    /* record + read a load error */
    ca_proactive_load_error_t err;
    memset(&err, 0, sizeof(err));
    err.task_id = (char *)"bad"; err.message = (char *)"parse failed";
    ca_inmemory_source_record_error(src, &err);
    size_t ec;
    ca_proactive_load_error_t *errs = ca_inmemory_source_errors(src, &ec);
    assert(ec == 1 && strcmp(errs[0].message, "parse failed") == 0);
    ca_proactive_load_error_free_array(errs, ec);

    /* clear wipes tasks + errors */
    ca_inmemory_source_clear(src);
    tasks = ca_inmemory_source_tasks(src, &n);
    assert(n == 0 && tasks == NULL);
    errs = ca_inmemory_source_errors(src, &ec);
    assert(ec == 0 && errs == NULL);

    ca_inmemory_source_destroy(src);
    printf("  source_lifecycle: ok\n");
}

static void test_refresh_drops_lastrun(void) {
    /* After a task fires then disappears from the source, refresh drops its
     * last-run state — a re-added task fires immediately again. */
    ca_inmemory_source_t *src = ca_inmemory_source_create();
    ca_proactive_task_t ct = make_cron_task("recur", "* * * * *");
    ca_inmemory_source_upsert(src, &ct);

    ca_proactive_scheduler_t *s = ca_proactive_scheduler_create(
        ca_inmemory_source_tasks, ca_inmemory_source_errors, src,
        logging_runner, NULL);
    ca_proactive_scheduler_refresh(s);

    memset(&g_log, 0, sizeof(g_log));
    ca_proactive_scheduler_tick(s, MON_1300Z);
    assert(count_runs("recur") == 1);

    /* remove + refresh → last-run dropped */
    ca_inmemory_source_remove(src, "recur", NULL);
    ca_proactive_scheduler_refresh(s);
    /* re-add + refresh → fires again on the same minute (fresh last-run) */
    ca_inmemory_source_upsert(src, &ct);
    ca_proactive_scheduler_refresh(s);
    ca_proactive_scheduler_tick(s, MON_1300Z);
    assert(count_runs("recur") == 2);

    ca_proactive_scheduler_destroy(s);
    ca_inmemory_source_destroy(src);
    printf("  refresh_drops_lastrun: ok\n");
}

int main(void) {
    test_cron_parse_errors();
    test_cron_matches();
    test_cron_next();
    test_scheduler();
    test_null_runner();
    test_source_lifecycle();
    test_refresh_drops_lastrun();
    printf("test_proactive: all assertions passed\n");
    return 0;
}
