/*
 * proactive.c — CircleAI.Companion.Proactive (C11 port).
 *
 * CronExpression + ProactiveTask/Trigger + the ProactiveScheduler with
 * per-(SourceContext, taskId) last-run tracking, plus the Null/InMemory source
 * and Null/Delegate runner adapters. Ported 1:1 from the C# project.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/proactive.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

/* ===========================================================================
 * Shared helpers
 * =========================================================================== */

static char *pv_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool pv_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

static bool pv_ieq(const char *a, const char *b) {
    if (!a || !b) return a == b;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return false;
        a++; b++;
    }
    return *a == *b;
}

/* Civil UTC fields from Unix ms. Minute/hour/day/month/year/day-of-week (0=Sun).
 * Truncated toward negative infinity for negatives. */
typedef struct {
    int minute, hour, day, month, dow;   /* dow: 0=Sunday .. 6=Saturday */
    int64_t year;
} pv_civil_t;

static void pv_from_ms(int64_t unix_ms, pv_civil_t *c) {
    int64_t secs = unix_ms / 1000;
    if (unix_ms % 1000 != 0 && unix_ms < 0) secs -= 1;   /* floor */
    int64_t z = secs / 86400;                            /* days since 1970-01-01 */
    int64_t rem = secs % 86400;
    if (rem < 0) { rem += 86400; z -= 1; }
    /* day of week from the un-shifted day number: 1970-01-01 was Thursday (=4). */
    int64_t dow = (z % 7 + 4) % 7;
    if (dow < 0) dow += 7;
    c->dow = (int)dow;
    /* Hinnant civil_from_days works in the 0000-03-01 era; shift the epoch in. */
    int64_t zc = z + 719468;
    int64_t era = (zc >= 0 ? zc : zc - 146096) / 146097;
    unsigned doe = (unsigned)(zc - era * 146097);
    unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t y = (int64_t)yoe + era * 400;
    unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    unsigned mp = (5 * doy + 2) / 153;
    unsigned d = doy - (153 * mp + 2) / 5 + 1;
    unsigned m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) y += 1;
    c->year = y; c->month = (int)m; c->day = (int)d;
    c->hour = (int)(rem / 3600);
    c->minute = (int)((rem % 3600) / 60);
}

/* Unix ms for a UTC minute (seconds=0) given civil y/m/d/h/min. */
static int64_t pv_to_ms(int64_t year, int month, int day, int hour, int minute) {
    int64_t y = year;
    if (month <= 2) y -= 1;
    int64_t era = (y >= 0 ? y : y - 399) / 400;
    unsigned yoe = (unsigned)(y - era * 400);
    unsigned doy = (153 * (unsigned)(month + (month > 2 ? -3 : 9)) + 2) / 5 + (unsigned)day - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = era * 146097 + (int64_t)doe - 719468;
    int64_t secs = days * 86400 + (int64_t)hour * 3600 + (int64_t)minute * 60;
    return secs * 1000;
}

/* ===========================================================================
 * CronExpression
 * =========================================================================== */

struct ca_cron_expression {
    bool minutes[60];
    bool hours[24];
    bool days_of_month[32];   /* 1..31 */
    bool months[13];          /* 1..12 */
    bool days_of_week[7];     /* 0..6 */
};

/* Expand one comma-part into `set` (indices min..max). Returns false on error. */
static bool cron_expand_part(const char *part, int min, int max, bool *set) {
    /* trim */
    while (*part && isspace((unsigned char)*part)) part++;
    size_t len = strlen(part);
    while (len > 0 && isspace((unsigned char)part[len - 1])) len--;
    char buf[64];
    if (len >= sizeof(buf)) return false;
    memcpy(buf, part, len); buf[len] = '\0';

    int step = 1;
    char *slash = strchr(buf, '/');
    if (slash) {
        char *endp = NULL;
        long s = strtol(slash + 1, &endp, 10);
        if (endp == slash + 1 || *endp != '\0' || s <= 0) return false;
        step = (int)s;
        *slash = '\0';
    }

    int range_start, range_end;
    if (strcmp(buf, "*") == 0) {
        range_start = min; range_end = max;
    } else if (strchr(buf, '-')) {
        char *dash = strchr(buf, '-');
        *dash = '\0';
        char *e1 = NULL, *e2 = NULL;
        long a = strtol(buf, &e1, 10);
        long b = strtol(dash + 1, &e2, 10);
        if (*e1 != '\0' || *e2 != '\0') return false;
        range_start = (int)a; range_end = (int)b;
    } else {
        char *e = NULL;
        long v = strtol(buf, &e, 10);
        if (e == buf || *e != '\0') return false;
        range_start = range_end = (int)v;
    }

    if (range_start < min || range_end > max || range_start > range_end) return false;
    for (int v = range_start; v <= range_end; v += step) set[v - min] = true;
    return true;
}

/* Parse one whitespace field (comma-list of parts) into `set`. */
static bool cron_parse_field(const char *field, int min, int max, bool *set) {
    bool any = false;
    char buf[128];
    if (strlen(field) >= sizeof(buf)) return false;
    strcpy(buf, field);
    char *save = NULL;
    char *tok = strtok_r(buf, ",", &save);
    if (!tok) return false;
    while (tok) {
        if (!cron_expand_part(tok, min, max, set)) return false;
        tok = strtok_r(NULL, ",", &save);
    }
    for (int i = 0; i <= max - min; ++i) if (set[i]) { any = true; break; }
    return any;   /* "resolved to no values" → error */
}

ca_cron_expression_t *ca_cron_parse(const char *expression) {
    if (!expression) return NULL;
    /* split on whitespace, TrimEntries + RemoveEmpty */
    char buf[256];
    if (strlen(expression) >= sizeof(buf)) return NULL;
    strcpy(buf, expression);
    char *fields[8];
    int nf = 0;
    char *save = NULL;
    char *tok = strtok_r(buf, " \t\r\n", &save);
    while (tok && nf < 8) { fields[nf++] = tok; tok = strtok_r(NULL, " \t\r\n", &save); }
    if (nf != 5) return NULL;

    ca_cron_expression_t *e = (ca_cron_expression_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    /* minutes[0..59], hours[0..23], dom[index 1..31], months[index 1..12], dow[0..6] */
    bool ok = cron_parse_field(fields[0], 0, 59, e->minutes)
           && cron_parse_field(fields[1], 0, 23, e->hours)
           && cron_parse_field(fields[2], 1, 31, e->days_of_month + 1)
           && cron_parse_field(fields[3], 1, 12, e->months + 1)
           && cron_parse_field(fields[4], 0, 6,  e->days_of_week);
    if (!ok) { free(e); return NULL; }
    return e;
}

void ca_cron_destroy(ca_cron_expression_t *expr) { free(expr); }

bool ca_cron_matches(const ca_cron_expression_t *expr, int64_t moment_ms) {
    if (!expr) return false;
    pv_civil_t c;
    pv_from_ms(moment_ms, &c);
    if (!expr->minutes[c.minute]) return false;
    if (!expr->hours[c.hour]) return false;
    if (c.day < 1 || c.day > 31 || !expr->days_of_month[c.day]) return false;
    if (c.month < 1 || c.month > 12 || !expr->months[c.month]) return false;
    if (!expr->days_of_week[c.dow]) return false;   /* AND semantics */
    return true;
}

bool ca_cron_next_occurrence(const ca_cron_expression_t *expr, int64_t after_ms,
                             int64_t *out_ms) {
    if (!expr || !out_ms) return false;
    /* t = after.AddMinutes(1) truncated to the minute (seconds zeroed). */
    int64_t t = after_ms + 60000;
    pv_civil_t c;
    pv_from_ms(t, &c);
    int64_t cur = pv_to_ms(c.year, c.month, c.day, c.hour, c.minute);
    /* limit = t.AddYears(1); approximate with 366 days of minutes to bound the
     * search (the C# uses calendar +1yr; 366d ≥ any 1yr span so we never stop
     * early, and a dead expression still terminates). */
    int64_t limit = cur + 366LL * 24 * 60 * 60000;
    while (cur <= limit) {
        if (ca_cron_matches(expr, cur)) { *out_ms = cur; return true; }
        cur += 60000;
    }
    return false;
}

/* ===========================================================================
 * Task / trigger / result frees
 * =========================================================================== */

void ca_proactive_task_free(ca_proactive_task_t *t) {
    if (!t) return;
    free(t->id);
    free(t->trigger.cron);
    free(t->trigger.on_event);
    free(t->source_context);
    t->id = t->source_context = NULL;
    t->trigger.cron = t->trigger.on_event = NULL;
    t->payload = NULL;
}
void ca_proactive_task_free_array(ca_proactive_task_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_proactive_task_free(&arr[i]);
    free(arr);
}
void ca_proactive_run_result_free(ca_proactive_run_result_t *r) {
    if (!r) return;
    free(r->task_id); free(r->failure_message);
    r->task_id = r->failure_message = NULL;
}
void ca_proactive_load_error_free(ca_proactive_load_error_t *e) {
    if (!e) return;
    free(e->task_id); free(e->message); free(e->source_context);
    e->task_id = e->message = e->source_context = NULL;
}
void ca_proactive_load_error_free_array(ca_proactive_load_error_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_proactive_load_error_free(&arr[i]);
    free(arr);
}

static void pv_task_copy(ca_proactive_task_t *dst, const ca_proactive_task_t *src) {
    dst->id = pv_strdup(src->id);
    dst->trigger.cron = pv_strdup(src->trigger.cron);
    dst->trigger.on_event = pv_strdup(src->trigger.on_event);
    dst->trigger.manual = src->trigger.manual;
    dst->payload = src->payload;   /* borrowed */
    dst->source_context = pv_strdup(src->source_context);
}
static void pv_error_copy(ca_proactive_load_error_t *dst, const ca_proactive_load_error_t *src) {
    dst->task_id = pv_strdup(src->task_id);
    dst->message = pv_strdup(src->message);
    dst->source_context = pv_strdup(src->source_context);
}

/* ===========================================================================
 * Null source + Null runner
 * =========================================================================== */

ca_proactive_task_t *ca_null_source_tasks(void *user, size_t *out_count) {
    (void)user;
    if (out_count) *out_count = 0;
    return NULL;
}
ca_proactive_load_error_t *ca_null_source_errors(void *user, size_t *out_count) {
    (void)user;
    if (out_count) *out_count = 0;
    return NULL;
}
void ca_null_runner_run(void *user, const ca_proactive_task_t *task,
                        const ca_proactive_variables_t *variables,
                        ca_proactive_run_result_t *out) {
    (void)user; (void)variables;
    if (!out) return;
    out->task_id = pv_strdup(task ? task->id : "");
    out->success = false;
    out->failure_message =
        pv_strdup("No IProactiveTaskRunner registered; using NullProactiveTaskRunner.");
}

/* ===========================================================================
 * In-memory source
 * =========================================================================== */

struct ca_inmemory_source {
    ca_proactive_task_t       *tasks;
    size_t                     task_count, task_cap;
    ca_proactive_load_error_t *errors;
    size_t                     err_count, err_cap;
};

ca_inmemory_source_t *ca_inmemory_source_create(void) {
    return (ca_inmemory_source_t *)calloc(1, sizeof(ca_inmemory_source_t));
}
void ca_inmemory_source_destroy(ca_inmemory_source_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->task_count; ++i) ca_proactive_task_free(&s->tasks[i]);
    free(s->tasks);
    for (size_t i = 0; i < s->err_count; ++i) ca_proactive_load_error_free(&s->errors[i]);
    free(s->errors);
    free(s);
}
static const char *pv_ctx(const char *c) { return c ? c : ""; }
static size_t pv_find_task(ca_inmemory_source_t *s, const char *ctx, const char *id) {
    for (size_t i = 0; i < s->task_count; ++i)
        if (pv_ieq(pv_ctx(s->tasks[i].source_context), ctx) && pv_ieq(s->tasks[i].id, id))
            return i;
    return (size_t)-1;
}
void ca_inmemory_source_upsert(ca_inmemory_source_t *s, const ca_proactive_task_t *task) {
    if (!s || !task) return;
    size_t idx = pv_find_task(s, pv_ctx(task->source_context), task->id ? task->id : "");
    if (idx != (size_t)-1) {
        ca_proactive_task_free(&s->tasks[idx]);
        pv_task_copy(&s->tasks[idx], task);
        return;
    }
    if (s->task_count == s->task_cap) {
        size_t nc = s->task_cap ? s->task_cap * 2 : 8;
        ca_proactive_task_t *nt = (ca_proactive_task_t *)realloc(s->tasks, nc * sizeof(*nt));
        if (!nt) return;
        s->tasks = nt; s->task_cap = nc;
    }
    pv_task_copy(&s->tasks[s->task_count++], task);
}
bool ca_inmemory_source_remove(ca_inmemory_source_t *s, const char *id,
                               const char *source_context) {
    if (!s || pv_blank(id)) return false;
    size_t idx = pv_find_task(s, pv_ctx(source_context), id);
    if (idx == (size_t)-1) return false;
    ca_proactive_task_free(&s->tasks[idx]);
    memmove(&s->tasks[idx], &s->tasks[idx + 1],
            (s->task_count - idx - 1) * sizeof(ca_proactive_task_t));
    s->task_count--;
    return true;
}
void ca_inmemory_source_clear(ca_inmemory_source_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->task_count; ++i) ca_proactive_task_free(&s->tasks[i]);
    s->task_count = 0;
    for (size_t i = 0; i < s->err_count; ++i) ca_proactive_load_error_free(&s->errors[i]);
    s->err_count = 0;
}
void ca_inmemory_source_record_error(ca_inmemory_source_t *s,
                                     const ca_proactive_load_error_t *error) {
    if (!s || !error) return;
    if (s->err_count == s->err_cap) {
        size_t nc = s->err_cap ? s->err_cap * 2 : 8;
        ca_proactive_load_error_t *ne =
            (ca_proactive_load_error_t *)realloc(s->errors, nc * sizeof(*ne));
        if (!ne) return;
        s->errors = ne; s->err_cap = nc;
    }
    pv_error_copy(&s->errors[s->err_count++], error);
}
ca_proactive_task_t *ca_inmemory_source_tasks(void *user, size_t *out_count) {
    ca_inmemory_source_t *s = (ca_inmemory_source_t *)user;
    if (out_count) *out_count = 0;
    if (!s || s->task_count == 0) return NULL;
    ca_proactive_task_t *arr = (ca_proactive_task_t *)calloc(s->task_count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->task_count; ++i) pv_task_copy(&arr[i], &s->tasks[i]);
    if (out_count) *out_count = s->task_count;
    return arr;
}
ca_proactive_load_error_t *ca_inmemory_source_errors(void *user, size_t *out_count) {
    ca_inmemory_source_t *s = (ca_inmemory_source_t *)user;
    if (out_count) *out_count = 0;
    if (!s || s->err_count == 0) return NULL;
    ca_proactive_load_error_t *arr =
        (ca_proactive_load_error_t *)calloc(s->err_count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->err_count; ++i) pv_error_copy(&arr[i], &s->errors[i]);
    if (out_count) *out_count = s->err_count;
    return arr;
}

/* ===========================================================================
 * ProactiveScheduler
 * =========================================================================== */

/* Per-context last-run: a flat list of (ctx, id, when_ms). */
typedef struct { char *ctx; char *id; int64_t when_ms; } pv_lastrun;

struct ca_proactive_scheduler {
    ca_proactive_source_tasks_fn  tasks_fn;
    ca_proactive_source_errors_fn errors_fn;
    void                         *source_user;
    ca_proactive_runner_fn        runner_fn;
    void                         *runner_user;

    ca_proactive_task_t       *tasks;  size_t task_count;
    ca_proactive_load_error_t *errors; size_t err_count;

    pv_lastrun *last_runs; size_t lr_count, lr_cap;
};

ca_proactive_scheduler_t *ca_proactive_scheduler_create(
    ca_proactive_source_tasks_fn tasks_fn, ca_proactive_source_errors_fn errors_fn,
    void *source_user,
    ca_proactive_runner_fn runner_fn, void *runner_user) {
    if (!tasks_fn || !errors_fn || !runner_fn) return NULL;
    ca_proactive_scheduler_t *s = (ca_proactive_scheduler_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->tasks_fn = tasks_fn; s->errors_fn = errors_fn; s->source_user = source_user;
    s->runner_fn = runner_fn; s->runner_user = runner_user;
    return s;
}
static void pv_free_tasks(ca_proactive_scheduler_t *s) {
    for (size_t i = 0; i < s->task_count; ++i) ca_proactive_task_free(&s->tasks[i]);
    free(s->tasks); s->tasks = NULL; s->task_count = 0;
}
static void pv_free_errors(ca_proactive_scheduler_t *s) {
    for (size_t i = 0; i < s->err_count; ++i) ca_proactive_load_error_free(&s->errors[i]);
    free(s->errors); s->errors = NULL; s->err_count = 0;
}
void ca_proactive_scheduler_destroy(ca_proactive_scheduler_t *s) {
    if (!s) return;
    pv_free_tasks(s);
    pv_free_errors(s);
    for (size_t i = 0; i < s->lr_count; ++i) { free(s->last_runs[i].ctx); free(s->last_runs[i].id); }
    free(s->last_runs);
    free(s);
}
const char *ca_proactive_scheduler_backend_id(const ca_proactive_scheduler_t *s) {
    (void)s; return "default";
}

ca_proactive_task_t *ca_proactive_scheduler_tasks(const ca_proactive_scheduler_t *s,
                                                  size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || s->task_count == 0) return NULL;
    ca_proactive_task_t *arr = (ca_proactive_task_t *)calloc(s->task_count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->task_count; ++i) pv_task_copy(&arr[i], &s->tasks[i]);
    if (out_count) *out_count = s->task_count;
    return arr;
}
ca_proactive_load_error_t *ca_proactive_scheduler_load_errors(const ca_proactive_scheduler_t *s,
                                                              size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || s->err_count == 0) return NULL;
    ca_proactive_load_error_t *arr =
        (ca_proactive_load_error_t *)calloc(s->err_count, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->err_count; ++i) pv_error_copy(&arr[i], &s->errors[i]);
    if (out_count) *out_count = s->err_count;
    return arr;
}

bool ca_proactive_scheduler_next_run(const ca_proactive_scheduler_t *s,
                                     const ca_proactive_task_t *task,
                                     int64_t after_ms, int64_t *out_ms) {
    (void)s;
    if (!task || !out_ms || task->trigger.cron == NULL) return false;
    ca_cron_expression_t *expr = ca_cron_parse(task->trigger.cron);
    if (!expr) return false;
    bool ok = ca_cron_next_occurrence(expr, after_ms, out_ms);
    ca_cron_destroy(expr);
    return ok;
}

static pv_lastrun *pv_lr_find(ca_proactive_scheduler_t *s, const char *ctx, const char *id) {
    for (size_t i = 0; i < s->lr_count; ++i)
        if (pv_ieq(s->last_runs[i].ctx, ctx) && pv_ieq(s->last_runs[i].id, id))
            return &s->last_runs[i];
    return NULL;
}
static void pv_lr_mark(ca_proactive_scheduler_t *s, const ca_proactive_task_t *t, int64_t when) {
    const char *ctx = pv_ctx(t->source_context);
    pv_lastrun *lr = pv_lr_find(s, ctx, t->id ? t->id : "");
    if (lr) { lr->when_ms = when; return; }
    if (s->lr_count == s->lr_cap) {
        size_t nc = s->lr_cap ? s->lr_cap * 2 : 8;
        pv_lastrun *nl = (pv_lastrun *)realloc(s->last_runs, nc * sizeof(*nl));
        if (!nl) return;
        s->last_runs = nl; s->lr_cap = nc;
    }
    s->last_runs[s->lr_count].ctx = pv_strdup(ctx);
    s->last_runs[s->lr_count].id = pv_strdup(t->id ? t->id : "");
    s->last_runs[s->lr_count].when_ms = when;
    s->lr_count++;
}

void ca_proactive_scheduler_refresh(ca_proactive_scheduler_t *s) {
    if (!s) return;
    size_t tc = 0, ec = 0;
    ca_proactive_task_t *nt = s->tasks_fn(s->source_user, &tc);
    ca_proactive_load_error_t *ne = s->errors_fn(s->source_user, &ec);
    if (tc == (size_t)-1) { tc = 0; nt = NULL; }
    if (ec == (size_t)-1) { ec = 0; ne = NULL; }

    pv_free_tasks(s);
    pv_free_errors(s);
    s->tasks = nt; s->task_count = tc;
    s->errors = ne; s->err_count = ec;

    /* Drop last-run state for (ctx,id) pairs no longer present. */
    for (size_t i = 0; i < s->lr_count; ) {
        bool live = false;
        for (size_t j = 0; j < s->task_count; ++j)
            if (pv_ieq(s->last_runs[i].ctx, pv_ctx(s->tasks[j].source_context))
                && pv_ieq(s->last_runs[i].id, s->tasks[j].id ? s->tasks[j].id : "")) {
                live = true; break;
            }
        if (!live) {
            free(s->last_runs[i].ctx); free(s->last_runs[i].id);
            memmove(&s->last_runs[i], &s->last_runs[i + 1],
                    (s->lr_count - i - 1) * sizeof(pv_lastrun));
            s->lr_count--;
        } else {
            ++i;
        }
    }
}

void ca_proactive_scheduler_tick(ca_proactive_scheduler_t *s, int64_t now_ms) {
    if (!s) return;
    for (size_t i = 0; i < s->task_count; ++i) {
        ca_proactive_task_t *t = &s->tasks[i];
        if (t->trigger.cron == NULL) continue;
        const char *ctx = pv_ctx(t->source_context);
        pv_lastrun *lr = pv_lr_find(s, ctx, t->id ? t->id : "");
        int64_t last_run = lr ? lr->when_ms : INT64_MIN;   /* DateTimeOffset.MinValue */

        ca_cron_expression_t *expr = ca_cron_parse(t->trigger.cron);
        if (!expr) continue;   /* parse error: skip, don't crash the tick */
        /* anchor = lastRun==MinValue ? now-1min : lastRun */
        int64_t anchor = (last_run == INT64_MIN) ? (now_ms - 60000) : last_run;
        int64_t next;
        if (ca_cron_next_occurrence(expr, anchor, &next) && next <= now_ms) {
            ca_cron_destroy(expr);
            ca_proactive_run_result_t res; memset(&res, 0, sizeof(res));
            s->runner_fn(s->runner_user, t, NULL, &res);
            ca_proactive_run_result_free(&res);
            pv_lr_mark(s, t, now_ms);
        } else {
            ca_cron_destroy(expr);
        }
    }
}

void ca_proactive_scheduler_dispatch_event(ca_proactive_scheduler_t *s,
                                           const char *event_name,
                                           const ca_proactive_variables_t *variables,
                                           int64_t now_ms) {
    if (!s || pv_blank(event_name)) return;
    for (size_t i = 0; i < s->task_count; ++i) {
        ca_proactive_task_t *t = &s->tasks[i];
        if (t->trigger.on_event && pv_ieq(t->trigger.on_event, event_name)) {
            ca_proactive_run_result_t res; memset(&res, 0, sizeof(res));
            s->runner_fn(s->runner_user, t, variables, &res);
            ca_proactive_run_result_free(&res);
            pv_lr_mark(s, t, now_ms);
        }
    }
}

bool ca_proactive_scheduler_run_by_id(ca_proactive_scheduler_t *s, const char *id,
                                      const ca_proactive_variables_t *variables,
                                      int64_t now_ms, ca_proactive_run_result_t *out) {
    if (!s || !out || pv_blank(id)) return false;
    ca_proactive_task_t *task = NULL;
    for (size_t i = 0; i < s->task_count; ++i)
        if (pv_ieq(s->tasks[i].id, id)) { task = &s->tasks[i]; break; }
    if (!task) {
        char buf[128];
        snprintf(buf, sizeof(buf), "No task with id '%s'.", id);
        out->task_id = pv_strdup(id);
        out->success = false;
        out->failure_message = pv_strdup(buf);
        return true;
    }
    s->runner_fn(s->runner_user, task, variables, out);
    pv_lr_mark(s, task, now_ms);
    return true;
}
