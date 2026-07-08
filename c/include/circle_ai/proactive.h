#ifndef CIRCLE_AI_PROACTIVE_H
#define CIRCLE_AI_PROACTIVE_H

/*
 * proactive.h — CircleAI.Companion.Proactive (C11 port).
 *
 * The proactive-scheduling substrate, ported 1:1 from the C# project:
 *   CronExpression        — 5-field cron parser (* , - / ; 0=Sun..6=Sat)
 *   ProactiveTask/Trigger — opaque scheduled task + cron/event/manual trigger
 *   IProactiveTaskSource  — where tasks come from (in-memory / null)
 *   IProactiveTaskRunner  — how one executes (delegate / null)
 *   IProactiveScheduler   — the cron tick loop + per-context last-run tracking
 *
 * The three collaborators are function-pointer seams (the C idiom for the C#
 * interfaces). The scheduler owns cron parsing, refresh, tick, event dispatch,
 * and per-(SourceContext, taskId) last-run state exactly as
 * ProactiveScheduler.cs. Times are Unix ms UTC; cron matching decomposes them to
 * UTC civil fields (offset 0), matching DateTimeOffset with TimeSpan.Zero.
 *
 * Task payloads are opaque `void*` the substrate never inspects; the caller's
 * runner reads them. The task's owned string fields (id, cron, event) are
 * strdup'd copies; the payload pointer is borrowed (the caller owns the payload
 * lifetime, as the C# holds an object reference).
 *
 * Ownership: task/trigger/result/error structs own their strdup'd strings with
 * matching *_free. Snapshot arrays (Tasks, LoadErrors) are deep copies the caller
 * frees. Errors on array-returning calls are NULL + count == SIZE_MAX.
 *
 * Pure C11 + libc. No pthreads (the background 1-minute tick is the host's).
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * CronExpression — 5-field parser.
 * ===========================================================================
 *
 * "minute hour day-of-month month day-of-week"; supports *, integers, ranges
 * (1-5), lists (1,15,30), and steps (*​/15). Day-of-week 0=Sunday..6=Saturday.
 * Day-of-month AND day-of-week must both match (AND, not OR).
 */

typedef struct ca_cron_expression ca_cron_expression_t;

/* Parse a 5-field cron. Returns NULL on a malformed expression (wrong field
 * count, out-of-range value, bad step, empty field). */
ca_cron_expression_t *ca_cron_parse(const char *expression);
void ca_cron_destroy(ca_cron_expression_t *expr);

/* Whether the expression matches the given Unix-ms UTC instant. */
bool ca_cron_matches(const ca_cron_expression_t *expr, int64_t moment_ms);

/* Next UTC instant strictly after `after_ms` (rounded up to the next whole
 * minute, seconds zeroed) that matches, searching up to one year forward. Writes
 * *out_ms and returns true; returns false if nothing matches within a year
 * (the C# throws — the port signals it as false). */
bool ca_cron_next_occurrence(const ca_cron_expression_t *expr, int64_t after_ms,
                             int64_t *out_ms);

/* ===========================================================================
 * ProactiveTask / ProactiveTrigger / results
 * =========================================================================== */

/* Exactly one of cron / on_event / manual identifies how the task fires. */
typedef struct {
    char *cron;        /* owned, or NULL */
    char *on_event;    /* owned, or NULL */
    bool  manual;
} ca_proactive_trigger_t;

typedef struct {
    char                   *id;             /* owned */
    ca_proactive_trigger_t  trigger;        /* owned strings */
    void                   *payload;        /* borrowed (caller owns) */
    char                   *source_context; /* owned, or NULL */
} ca_proactive_task_t;

typedef struct {
    char *task_id;          /* owned */
    bool  success;
    char *failure_message;  /* owned, or NULL */
} ca_proactive_run_result_t;

typedef struct {
    char *task_id;          /* owned */
    char *message;          /* owned */
    char *source_context;   /* owned, or NULL */
} ca_proactive_load_error_t;

void ca_proactive_task_free(ca_proactive_task_t *t);
void ca_proactive_task_free_array(ca_proactive_task_t *arr, size_t count);
void ca_proactive_run_result_free(ca_proactive_run_result_t *r);
void ca_proactive_load_error_free(ca_proactive_load_error_t *e);
void ca_proactive_load_error_free_array(ca_proactive_load_error_t *arr, size_t count);

/* ===========================================================================
 * Seams: task source + task runner
 * =========================================================================== */

/* Trigger-time variables (event payload, manual-invoke args) as parallel arrays.
 * Passed to the runner; may be NULL/empty. */
typedef struct {
    const char *const *keys;
    const char *const *values;
    size_t             count;
} ca_proactive_variables_t;

/* Source seam: snapshot tasks + load errors. Both return fresh deep-copied
 * arrays (the scheduler frees them) and set *out_count (0 → NULL is fine). */
typedef ca_proactive_task_t *(*ca_proactive_source_tasks_fn)(void *user, size_t *out_count);
typedef ca_proactive_load_error_t *(*ca_proactive_source_errors_fn)(void *user, size_t *out_count);

/* Runner seam: execute one task with optional variables; fill *out (its strings
 * malloc'd). */
typedef void (*ca_proactive_runner_fn)(void *user, const ca_proactive_task_t *task,
                                       const ca_proactive_variables_t *variables,
                                       ca_proactive_run_result_t *out);

/* ===========================================================================
 * Null/InMemory source + Delegate runner (the safe defaults + test doubles)
 * ===========================================================================
 *
 * These wrap the seams above. NullProactiveTaskSource yields nothing;
 * NullProactiveTaskRunner fails every run ("No IProactiveTaskRunner
 * registered..."). InMemoryProactiveTaskSource is a mutable (context,id)-keyed
 * store. DelegateProactiveTaskRunner forwards to a host delegate.
 */

/* --- Null source --- */
ca_proactive_task_t *ca_null_source_tasks(void *user, size_t *out_count);
ca_proactive_load_error_t *ca_null_source_errors(void *user, size_t *out_count);

/* --- Null runner --- */
void ca_null_runner_run(void *user, const ca_proactive_task_t *task,
                        const ca_proactive_variables_t *variables,
                        ca_proactive_run_result_t *out);

/* --- In-memory source --- */
typedef struct ca_inmemory_source ca_inmemory_source_t;

ca_inmemory_source_t *ca_inmemory_source_create(void);
void ca_inmemory_source_destroy(ca_inmemory_source_t *s);

/* Upsert a copy of the task (keyed by (source_context ?? "", id)). */
void ca_inmemory_source_upsert(ca_inmemory_source_t *s, const ca_proactive_task_t *task);
/* Remove by id + optional context. Returns true if removed. */
bool ca_inmemory_source_remove(ca_inmemory_source_t *s, const char *id,
                               const char *source_context);
void ca_inmemory_source_clear(ca_inmemory_source_t *s);
void ca_inmemory_source_record_error(ca_inmemory_source_t *s,
                                     const ca_proactive_load_error_t *error);

/* Seam adapters (pass the ca_inmemory_source_t* as `user`). */
ca_proactive_task_t *ca_inmemory_source_tasks(void *user, size_t *out_count);
ca_proactive_load_error_t *ca_inmemory_source_errors(void *user, size_t *out_count);

/* ===========================================================================
 * ProactiveScheduler
 * =========================================================================== */

typedef struct ca_proactive_scheduler ca_proactive_scheduler_t;

/* Create over a source (tasks_fn + errors_fn) and a runner. The fn user pointers
 * are borrowed. tasks_fn, errors_fn, runner_fn are required. */
ca_proactive_scheduler_t *ca_proactive_scheduler_create(
    ca_proactive_source_tasks_fn tasks_fn, ca_proactive_source_errors_fn errors_fn,
    void *source_user,
    ca_proactive_runner_fn runner_fn, void *runner_user);
void ca_proactive_scheduler_destroy(ca_proactive_scheduler_t *s);

/* Backend id ("default"). Borrowed. */
const char *ca_proactive_scheduler_backend_id(const ca_proactive_scheduler_t *s);

/* Deep-copied snapshot of the current tasks (caller frees with
 * ca_proactive_task_free_array). *out_count set (0 → NULL). */
ca_proactive_task_t *ca_proactive_scheduler_tasks(const ca_proactive_scheduler_t *s,
                                                  size_t *out_count);
/* Deep-copied snapshot of load errors. */
ca_proactive_load_error_t *ca_proactive_scheduler_load_errors(const ca_proactive_scheduler_t *s,
                                                              size_t *out_count);

/* Next cron firing for a task strictly after after_ms. Writes *out_ms and
 * returns true; returns false for non-cron triggers or unparseable/dead
 * expressions. */
bool ca_proactive_scheduler_next_run(const ca_proactive_scheduler_t *s,
                                     const ca_proactive_task_t *task,
                                     int64_t after_ms, int64_t *out_ms);

/* Re-snapshot from the source; drop last-run state for (context,id) pairs the
 * source no longer reports. */
void ca_proactive_scheduler_refresh(ca_proactive_scheduler_t *s);

/* Tick: run every cron task whose next-run is at-or-before now_ms and that has
 * not already fired for the matching minute; mark each run. */
void ca_proactive_scheduler_tick(ca_proactive_scheduler_t *s, int64_t now_ms);

/* Fire every event-triggered task matching event_name (case-insensitive), in
 * task order; mark each run. now_ms stamps the run time. Blank event_name is a
 * no-op. */
void ca_proactive_scheduler_dispatch_event(ca_proactive_scheduler_t *s,
                                           const char *event_name,
                                           const ca_proactive_variables_t *variables,
                                           int64_t now_ms);

/* One-shot manual run by id. Writes *out (deep copy) and returns true; an
 * unknown id fills (id, false, "No task with id '<id>'.") and still returns
 * true. Returns false only on NULL scheduler/out or a blank id. now_ms stamps
 * the run time. */
bool ca_proactive_scheduler_run_by_id(ca_proactive_scheduler_t *s, const char *id,
                                      const ca_proactive_variables_t *variables,
                                      int64_t now_ms, ca_proactive_run_result_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PROACTIVE_H */
