/*
 * host_cron.c — CircleAI.Hosting scheduling substrate + proactive reasoning
 * (C11 port). See host_cron.h.
 *
 * CronScheduleParser is ported 1:1 from CronScheduleParser.cs (HashSet<int> →
 * bool sets; AdvanceToNextMonth / AdvanceToNextHour helpers; minute stepping;
 * 5-year search cap; strictly-after). ScheduledAIService / triggers / proactive
 * reasoning / thermal / warmup follow their C# structure with the background
 * loops exposed as deterministic ticks.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/host_cron.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <math.h>

/* ── string helpers ───────────────────────────────────────────────────── */

static char *c_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool c_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

/* ===========================================================================
 * Civil UTC time <-> Unix ms (Hinnant algorithms; matches proactive.c).
 * =========================================================================== */

typedef struct {
    int minute, hour, day, month, dow; /* dow: 0=Sun..6=Sat */
    int64_t year;
} civil_t;

static void civil_from_ms(int64_t unix_ms, civil_t *c) {
    int64_t secs = unix_ms / 1000;
    if (unix_ms % 1000 != 0 && unix_ms < 0) secs -= 1;
    int64_t z = secs / 86400;
    int64_t rem = secs % 86400;
    if (rem < 0) { rem += 86400; z -= 1; }
    int64_t dow = (z % 7 + 4) % 7;
    if (dow < 0) dow += 7;
    c->dow = (int)dow;
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
static int64_t civil_to_ms(int64_t year, int month, int day, int hour, int minute) {
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
static int days_in_month(int64_t year, int month) {
    static const int d[] = { 31,28,31,30,31,30,31,31,30,31,30,31 };
    if (month == 2) {
        bool leap = (year % 4 == 0 && year % 100 != 0) || (year % 400 == 0);
        return leap ? 29 : 28;
    }
    return d[month - 1];
}

/* ===========================================================================
 * CronScheduleParser
 * =========================================================================== */

/* Parse one comma-separated field into a boolean set over [min,max]. Returns
 * false on any malformed part (mirrors ParseField/ParsePart throwing). */
static bool cron_parse_field(const char *field, int min, int max, bool *set) {
    for (int i = min; i <= max; ++i) set[i] = false;
    /* split on ',' */
    const char *p = field;
    while (1) {
        const char *comma = strchr(p, ',');
        size_t len = comma ? (size_t)(comma - p) : strlen(p);
        char buf[64];
        /* trim */
        while (len > 0 && isspace((unsigned char)*p)) { p++; len--; }
        while (len > 0 && isspace((unsigned char)p[len - 1])) len--;
        if (len == 0 || len >= sizeof(buf)) return false;
        memcpy(buf, p, len); buf[len] = '\0';

        /* step */
        int step = 1;
        char *slash = strchr(buf, '/');
        char *core = buf;
        if (slash) {
            char *endp = NULL;
            long s = strtol(slash + 1, &endp, 10);
            if (endp == slash + 1 || *endp != '\0' || s < 1) return false;
            step = (int)s;
            *slash = '\0';
        }

        int rmin, rmax;
        if (strcmp(core, "*") == 0) {
            rmin = min; rmax = max;
        } else {
            char *dash = strchr(core, '-');
            if (dash) {
                *dash = '\0';
                char *e1 = NULL, *e2 = NULL;
                long a = strtol(core, &e1, 10);
                long b = strtol(dash + 1, &e2, 10);
                if (e1 == core || *e1 != '\0' || e2 == dash + 1 || *e2 != '\0') return false;
                rmin = (int)a; rmax = (int)b;
            } else {
                char *e = NULL;
                long v = strtol(core, &e, 10);
                if (e == core || *e != '\0') return false;
                rmin = rmax = (int)v;
            }
        }
        if (rmin < min || rmax > max || rmin > rmax) return false;
        for (int v = rmin; v <= rmax; v += step) set[v] = true;

        if (!comma) break;
        p = comma + 1;
    }
    return true;
}

/* AdvanceToNextMonth: first day of the next valid month (00:00). Returns false
 * if no valid month found in 6 years. */
static bool advance_to_next_month(civil_t *dt, const bool *month_set, int64_t *out_ms) {
    int64_t year = dt->year;
    int month = dt->month + 1;
    if (month > 12) { month = 1; year++; }
    while (year < dt->year + 6) {
        if (month_set[month]) { *out_ms = civil_to_ms(year, month, 1, 0, 0); return true; }
        month++;
        if (month > 12) { month = 1; year++; }
    }
    return false;
}
/* AdvanceToNextHour: next valid hour today (minutes zeroed), else next day's
 * first valid hour. */
static int64_t advance_to_next_hour(civil_t *dt, const bool *hour_set) {
    for (int h = dt->hour + 1; h <= 23; ++h)
        if (hour_set[h]) return civil_to_ms(dt->year, dt->month, dt->day, h, 0);
    /* next day, first valid hour */
    int64_t nd = civil_to_ms(dt->year, dt->month, dt->day, 0, 0) + 86400LL * 1000;
    civil_t ndc; civil_from_ms(nd, &ndc);
    int min_hour = 0;
    for (int h = 0; h <= 23; ++h) if (hour_set[h]) { min_hour = h; break; }
    return civil_to_ms(ndc.year, ndc.month, ndc.day, min_hour, 0);
}

bool ca_host_cron_next_occurrence(const char *cron_expression, int64_t after_ms,
                                  int64_t *out_ms) {
    if (c_blank(cron_expression) || !out_ms) return false;

    /* split into exactly 5 fields on runs of spaces */
    char work[256];
    if (strlen(cron_expression) >= sizeof(work)) return false;
    strcpy(work, cron_expression);
    /* trim leading/trailing */
    char *start = work;
    while (*start && isspace((unsigned char)*start)) start++;
    char *fields[5];
    int nf = 0;
    char *tok = start;
    while (*tok) {
        while (*tok && isspace((unsigned char)*tok)) *tok++ = '\0';
        if (!*tok) break;
        if (nf < 5) fields[nf] = tok;
        nf++;
        while (*tok && !isspace((unsigned char)*tok)) tok++;
    }
    if (nf != 5) return false;

    bool minute_set[60], hour_set[24], dom_set[32], month_set[13], dow_set[7];
    if (!cron_parse_field(fields[0], 0, 59, minute_set)) return false;
    if (!cron_parse_field(fields[1], 0, 23, hour_set))   return false;
    if (!cron_parse_field(fields[2], 1, 31, dom_set))    return false;
    if (!cron_parse_field(fields[3], 1, 12, month_set))  return false;
    if (!cron_parse_field(fields[4], 0, 6,  dow_set))    return false;

    /* candidate = next whole minute after `after`, seconds zeroed. */
    civil_t a; civil_from_ms(after_ms, &a);
    int64_t candidate = civil_to_ms(a.year, a.month, a.day, a.hour, a.minute) + 60LL * 1000;
    int64_t limit;
    { civil_t cc; civil_from_ms(candidate, &cc); limit = civil_to_ms(cc.year + 5, cc.month, cc.day, cc.hour, cc.minute); }

    while (candidate <= limit) {
        civil_t c; civil_from_ms(candidate, &c);

        if (!month_set[c.month]) {
            int64_t nm;
            if (!advance_to_next_month(&c, month_set, &nm)) return false;
            candidate = nm;
            continue;
        }
        /* day-of-month must be valid for the month (skip Feb 31 etc.) */
        if (c.day > days_in_month(c.year, c.month) || !dom_set[c.day]) {
            candidate = civil_to_ms(c.year, c.month, c.day, 0, 0) + 86400LL * 1000; /* AddDays(1).Date() */
            continue;
        }
        if (!dow_set[c.dow]) {
            candidate = civil_to_ms(c.year, c.month, c.day, 0, 0) + 86400LL * 1000;
            continue;
        }
        if (!hour_set[c.hour]) {
            candidate = advance_to_next_hour(&c, hour_set);
            continue;
        }
        if (!minute_set[c.minute]) {
            candidate += 60LL * 1000;
            continue;
        }
        *out_ms = candidate;
        return true;
    }
    return false;
}

/* ===========================================================================
 * CronJob
 * =========================================================================== */

void ca_cron_job_free(ca_cron_job_t *j) {
    if (!j) return;
    free(j->id); free(j->name); free(j->prompt); free(j->cron_expression);
    j->id = j->name = j->prompt = j->cron_expression = NULL;
}
void ca_cron_job_free_array(ca_cron_job_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_cron_job_free(&arr[i]);
    free(arr);
}
ca_cron_job_t *ca_cron_job_copy(ca_cron_job_t *dst, const ca_cron_job_t *src) {
    if (!dst || !src) return dst;
    dst->id              = c_strdup(src->id);
    dst->name            = c_strdup(src->name);
    dst->prompt          = c_strdup(src->prompt);
    dst->cron_expression = c_strdup(src->cron_expression);
    dst->delivery        = src->delivery;
    dst->has_last_run    = src->has_last_run;
    dst->last_run_utc_ms = src->last_run_utc_ms;
    dst->has_next_run    = src->has_next_run;
    dst->next_run_utc_ms = src->next_run_utc_ms;
    dst->state           = src->state;
    dst->is_enabled      = src->is_enabled;
    return dst;
}

/* ===========================================================================
 * InMemoryScheduledTaskStore
 * =========================================================================== */

struct ca_scheduled_task_store {
    ca_cron_job_t *jobs;
    size_t         count, cap;
};

ca_scheduled_task_store_t *ca_scheduled_task_store_create(void) {
    return (ca_scheduled_task_store_t *)calloc(1, sizeof(ca_scheduled_task_store_t));
}
void ca_scheduled_task_store_destroy(ca_scheduled_task_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_cron_job_free(&s->jobs[i]);
    free(s->jobs);
    free(s);
}
static ca_cron_job_t *sts_find(ca_scheduled_task_store_t *s, const char *id) {
    for (size_t i = 0; i < s->count; ++i)
        if (s->jobs[i].id && strcmp(s->jobs[i].id, id) == 0) return &s->jobs[i];
    return NULL;
}
ca_cron_job_t *ca_scheduled_task_store_list(ca_scheduled_task_store_t *s, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s || s->count == 0) return NULL;
    ca_cron_job_t *res = (ca_cron_job_t *)calloc(s->count, sizeof(*res));
    if (!res) return NULL;
    for (size_t i = 0; i < s->count; ++i) ca_cron_job_copy(&res[i], &s->jobs[i]);
    if (out_count) *out_count = s->count;
    return res;
}
bool ca_scheduled_task_store_get(ca_scheduled_task_store_t *s, const char *id, ca_cron_job_t *out) {
    if (!s || c_blank(id) || !out) return false;
    ca_cron_job_t *j = sts_find(s, id);
    if (!j) return false;
    ca_cron_job_copy(out, j);
    return true;
}
bool ca_scheduled_task_store_upsert(ca_scheduled_task_store_t *s, const ca_cron_job_t *job) {
    if (!s || !job || c_blank(job->id)) return false;
    ca_cron_job_t *existing = sts_find(s, job->id);
    if (existing) {
        ca_cron_job_t copy; memset(&copy, 0, sizeof(copy));
        ca_cron_job_copy(&copy, job);
        ca_cron_job_free(existing);
        *existing = copy;
        return true;
    }
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 8;
        void *n = realloc(s->jobs, nc * sizeof(*s->jobs));
        if (!n) return false;
        s->jobs = (ca_cron_job_t *)n; s->cap = nc;
    }
    ca_cron_job_copy(&s->jobs[s->count], job);
    s->count++;
    return true;
}
void ca_scheduled_task_store_delete(ca_scheduled_task_store_t *s, const char *id) {
    if (!s || c_blank(id)) return;
    for (size_t i = 0; i < s->count; ++i)
        if (s->jobs[i].id && strcmp(s->jobs[i].id, id) == 0) {
            ca_cron_job_free(&s->jobs[i]);
            memmove(&s->jobs[i], &s->jobs[i + 1], (s->count - i - 1) * sizeof(*s->jobs));
            s->count--;
            return;
        }
}
ca_cron_job_t *ca_scheduled_task_store_due(ca_scheduled_task_store_t *s, int64_t now_ms,
                                           size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (s->jobs[i].is_enabled && s->jobs[i].has_next_run && s->jobs[i].next_run_utc_ms <= now_ms) n++;
    if (n == 0) return NULL;
    ca_cron_job_t *res = (ca_cron_job_t *)calloc(n, sizeof(*res));
    if (!res) return NULL;
    size_t k = 0;
    for (size_t i = 0; i < s->count; ++i)
        if (s->jobs[i].is_enabled && s->jobs[i].has_next_run && s->jobs[i].next_run_utc_ms <= now_ms)
            ca_cron_job_copy(&res[k++], &s->jobs[i]);
    if (out_count) *out_count = n;
    return res;
}

/* ===========================================================================
 * ScheduledAIService
 * =========================================================================== */

struct ca_scheduled_ai_service {
    ca_ai_service_t              *butler;   /* borrowed */
    ca_scheduled_task_store_t    *store;    /* borrowed */
    ca_scheduled_job_completed_fn on_completed;
    void                         *on_completed_user;
};

ca_scheduled_ai_service_t *ca_scheduled_ai_service_create(
    ca_ai_service_t *butler, ca_scheduled_task_store_t *store,
    ca_scheduled_job_completed_fn on_completed, void *on_completed_user) {
    if (!butler || !store) return NULL;
    ca_scheduled_ai_service_t *svc = (ca_scheduled_ai_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->butler = butler; svc->store = store;
    svc->on_completed = on_completed; svc->on_completed_user = on_completed_user;
    return svc;
}
void ca_scheduled_ai_service_destroy(ca_scheduled_ai_service_t *svc) { free(svc); }
double ca_scheduled_ai_service_poll_seconds(const ca_scheduled_ai_service_t *svc) {
    (void)svc; return 30.0;
}

static void execute_job(ca_scheduled_ai_service_t *svc, const ca_cron_job_t *job, int64_t now_ms) {
    /* mark Running */
    ca_cron_job_t running; memset(&running, 0, sizeof(running));
    ca_cron_job_copy(&running, job);
    running.state = CA_CRONJOB_RUNNING;
    ca_scheduled_task_store_upsert(svc->store, &running);
    ca_cron_job_free(&running);

    char *response = ca_ai_service_ask(svc->butler, job->prompt);
    bool failed = (response == NULL);
    if (!response) response = c_strdup("");

    int64_t next = 0;
    bool has_next = ca_host_cron_next_occurrence(job->cron_expression, now_ms, &next);

    ca_cron_job_t updated; memset(&updated, 0, sizeof(updated));
    ca_cron_job_copy(&updated, job);
    updated.has_last_run = true; updated.last_run_utc_ms = now_ms;
    updated.has_next_run = has_next; updated.next_run_utc_ms = next;
    updated.state = failed ? CA_CRONJOB_FAILED : CA_CRONJOB_SUCCEEDED;
    ca_scheduled_task_store_upsert(svc->store, &updated);

    if (svc->on_completed)
        svc->on_completed(svc->on_completed_user, &updated, response, failed ? "ask failed" : NULL);

    ca_cron_job_free(&updated);
    free(response);
}

size_t ca_scheduled_ai_service_tick(ca_scheduled_ai_service_t *svc, int64_t now_ms) {
    if (!svc) return 0;
    size_t n = 0;
    ca_cron_job_t *due = ca_scheduled_task_store_due(svc->store, now_ms, &n);
    if (n == 0) { ca_cron_job_free_array(due, n); return 0; }
    for (size_t i = 0; i < n; ++i) execute_job(svc, &due[i], now_ms);
    ca_cron_job_free_array(due, n);
    return n;
}

/* ===========================================================================
 * Triggers
 * =========================================================================== */

typedef enum { TRIG_IDLE, TRIG_SCHEDULE } trig_kind;

struct ca_trigger {
    trig_kind kind;
    char     *name;
    /* idle */
    int64_t   idle_threshold_ms;
    /* schedule */
    int       trigger_seconds;   /* seconds of day */
    bool      has_last_fire;
    int64_t   last_fire_day;     /* days since epoch */
};

const char *ca_trigger_name(const ca_trigger_t *t) { return t ? t->name : NULL; }
void ca_trigger_destroy(ca_trigger_t *t) {
    if (!t) return;
    free(t->name);
    free(t);
}

ca_trigger_t *ca_idle_trigger_create(int64_t idle_threshold_ms) {
    ca_trigger_t *t = (ca_trigger_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->kind = TRIG_IDLE;
    t->name = c_strdup("idle");
    t->idle_threshold_ms = idle_threshold_ms > 0 ? idle_threshold_ms : (4LL * 3600 * 1000);
    return t;
}
int64_t ca_idle_trigger_threshold(const ca_trigger_t *t) {
    return (t && t->kind == TRIG_IDLE) ? t->idle_threshold_ms : 0;
}

ca_trigger_t *ca_schedule_trigger_create(int trigger_seconds_of_day, const char *name) {
    ca_trigger_t *t = (ca_trigger_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->kind = TRIG_SCHEDULE;
    t->name = c_strdup(name ? name : "schedule");
    if (trigger_seconds_of_day < 0) trigger_seconds_of_day = 0;
    if (trigger_seconds_of_day >= 86400) trigger_seconds_of_day %= 86400;
    t->trigger_seconds = trigger_seconds_of_day;
    return t;
}
int ca_schedule_trigger_seconds_of_day(const ca_trigger_t *t) {
    return (t && t->kind == TRIG_SCHEDULE) ? t->trigger_seconds : 0;
}

bool ca_trigger_is_met(ca_trigger_t *t, const ca_proactive_context_t *ctx) {
    if (!t || !ctx) return false;
    if (t->kind == TRIG_IDLE)
        return ctx->time_since_last_interaction_ms > t->idle_threshold_ms;

    /* schedule: 5-minute window after trigger time, once per calendar day. */
    int64_t now = ctx->now_utc_ms;
    int64_t day = (now / 1000) / 86400;
    if (now < 0 && (now / 1000) % 86400 != 0) day -= 1;
    int local_seconds = (int)(((now / 1000) % 86400 + 86400) % 86400);

    if (t->has_last_fire && t->last_fire_day == day) return false;

    int window_start = t->trigger_seconds;
    int window_end   = t->trigger_seconds + 300;
    bool in_window;
    if (window_end < 86400) {
        in_window = local_seconds >= window_start && local_seconds < window_end;
    } else {
        int we = window_end - 86400;
        in_window = local_seconds >= window_start || local_seconds < we;
    }
    if (!in_window) return false;
    t->has_last_fire = true; t->last_fire_day = day;
    return true;
}

/* ===========================================================================
 * ProactiveReasoningService
 * =========================================================================== */

struct ca_proactive_reasoning_service {
    ca_ai_service_t         *butler;
    ca_goal_store_t         *goal_store;
    ca_trigger_t           **triggers;   /* borrowed array (copied pointer list) */
    size_t                   trigger_count;
    ca_proactive_message_fn  on_message;
    void                    *on_message_user;
};

ca_proactive_reasoning_service_t *ca_proactive_reasoning_service_create(
    ca_ai_service_t *butler, ca_goal_store_t *goal_store,
    ca_trigger_t *const *triggers, size_t trigger_count,
    ca_proactive_message_fn on_message, void *on_message_user) {
    if (!butler) return NULL;
    ca_proactive_reasoning_service_t *svc = (ca_proactive_reasoning_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->butler = butler; svc->goal_store = goal_store;
    svc->on_message = on_message; svc->on_message_user = on_message_user;
    if (trigger_count) {
        svc->triggers = (ca_trigger_t **)calloc(trigger_count, sizeof(ca_trigger_t *));
        if (!svc->triggers) { free(svc); return NULL; }
        for (size_t i = 0; i < trigger_count; ++i) svc->triggers[i] = triggers[i];
        svc->trigger_count = trigger_count;
    }
    return svc;
}
void ca_proactive_reasoning_service_destroy(ca_proactive_reasoning_service_t *svc) {
    if (!svc) return;
    free(svc->triggers);
    free(svc);
}

char *ca_proactive_build_prompt(const char *user_id, int64_t time_since_last_ms,
                                const ca_goal_record_t *active_goals, size_t goal_count) {
    (void)user_id;
    /* mirror BuildProactivePrompt exactly */
    size_t cap = 256; char *buf = (char *)malloc(cap); size_t len = 0;
    if (!buf) return NULL;
    #define APP(s) do { const char *_s = (s); size_t _n = strlen(_s); \
        while (len + _n + 1 > cap) { cap *= 2; char *n2 = realloc(buf, cap); if (!n2) { free(buf); return NULL; } buf = n2; } \
        memcpy(buf + len, _s, _n); len += _n; buf[len] = '\0'; } while (0)

    APP("You are B!. ");

    double total_minutes = (double)time_since_last_ms / 60000.0;
    if (total_minutes > 5.0) {
        int hours = (int)(time_since_last_ms / 3600000LL);
        int minutes = (int)((time_since_last_ms / 60000LL) % 60);
        char tmp[128];
        if (hours > 0) {
            snprintf(tmp, sizeof(tmp), "The user has been away for approximately %d hour%s. ",
                     hours, hours == 1 ? "" : "s");
        } else {
            snprintf(tmp, sizeof(tmp), "The user has been away for approximately %d minute%s. ",
                     minutes, minutes == 1 ? "" : "s");
        }
        APP(tmp);
    }

    if (goal_count > 0) {
        char tmp[128];
        snprintf(tmp, sizeof(tmp), "They have %zu active goal%s: ",
                 goal_count, goal_count == 1 ? "" : "s");
        APP(tmp);
        for (size_t i = 0; i < goal_count; ++i) {
            APP("\"");
            APP(active_goals[i].title ? active_goals[i].title : "");
            APP("\"");
            if (i < goal_count - 1) APP(", ");
        }
        APP(". ");
    }

    APP("Generate a brief, friendly check-in message (1-2 sentences). ");
    APP("Be warm, specific to their goals if you know them, and not intrusive.");
    #undef APP
    return buf;
}

bool ca_proactive_reasoning_service_check(ca_proactive_reasoning_service_t *svc,
                                          const char *user_id, int64_t now_ms,
                                          int64_t time_since_last_ms) {
    if (!svc || c_blank(user_id)) return false;
    if (svc->trigger_count == 0) return false;

    /* load active goals */
    ca_goal_record_t *goals = NULL;
    size_t goal_count = 0;
    if (svc->goal_store) {
        goals = ca_goal_store_get_active(svc->goal_store, user_id, &goal_count);
        if (goal_count == SIZE_MAX) { goals = NULL; goal_count = 0; }
    }

    ca_proactive_context_t ctx;
    ctx.user_id = user_id;
    ctx.now_utc_ms = now_ms;
    ctx.time_since_last_interaction_ms = time_since_last_ms;
    ctx.has_affect = false;
    ctx.active_goals = goals;
    ctx.active_goal_count = goal_count;

    bool fired = false;
    for (size_t i = 0; i < svc->trigger_count; ++i) {
        if (!ca_trigger_is_met(svc->triggers[i], &ctx)) continue;

        char *prompt = ca_proactive_build_prompt(user_id, time_since_last_ms, goals, goal_count);
        char *message = prompt ? ca_ai_service_ask(svc->butler, prompt) : NULL;
        free(prompt);
        if (message) {
            if (svc->on_message)
                svc->on_message(svc->on_message_user, user_id, message,
                                ca_trigger_name(svc->triggers[i]), now_ms);
            free(message);
            fired = true;
        }
        break; /* only one trigger per call */
    }

    ca_goal_record_free_array(goals, goal_count);
    return fired;
}

/* ===========================================================================
 * ThermalThrottleService
 * =========================================================================== */

struct ca_thermal_throttle_service {
    ca_thermal_sample_fn  sample;
    void                 *sample_user;
    ca_thermal_changed_fn on_changed;
    void                 *on_changed_user;
    ca_host_thermal_state_t    current;
    bool                  running;
};

ca_thermal_throttle_service_t *ca_thermal_throttle_service_create(
    ca_thermal_sample_fn sample, void *sample_user,
    ca_thermal_changed_fn on_changed, void *on_changed_user) {
    ca_thermal_throttle_service_t *s = (ca_thermal_throttle_service_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->sample = sample; s->sample_user = sample_user;
    s->on_changed = on_changed; s->on_changed_user = on_changed_user;
    s->current = CA_HOST_THERMAL_UNKNOWN;
    return s;
}
void ca_thermal_throttle_service_destroy(ca_thermal_throttle_service_t *s) { free(s); }
ca_host_thermal_state_t ca_thermal_throttle_current(const ca_thermal_throttle_service_t *s) {
    return s ? s->current : CA_HOST_THERMAL_UNKNOWN;
}
bool ca_thermal_throttle_should_pause(const ca_thermal_throttle_service_t *s) {
    return s && s->current >= CA_HOST_THERMAL_SERIOUS;
}
static void thermal_apply(ca_thermal_throttle_service_t *s, ca_host_thermal_state_t ns) {
    if (s->current != ns) {
        s->current = ns;
        if (s->on_changed) s->on_changed(s->on_changed_user, ns);
    }
}
static ca_host_thermal_state_t thermal_sample(ca_thermal_throttle_service_t *s) {
    return s->sample ? s->sample(s->sample_user) : CA_HOST_THERMAL_UNKNOWN;
}
void ca_thermal_throttle_start(ca_thermal_throttle_service_t *s) {
    if (!s || s->running) return;
    s->running = true;
    thermal_apply(s, thermal_sample(s)); /* sample immediately */
}
void ca_thermal_throttle_stop(ca_thermal_throttle_service_t *s) {
    if (!s) return;
    s->running = false;
}
void ca_thermal_throttle_poll(ca_thermal_throttle_service_t *s) {
    if (!s || !s->running) return;
    thermal_apply(s, thermal_sample(s));
}

/* ===========================================================================
 * BackgroundInferenceWorker
 * =========================================================================== */

struct ca_background_inference_worker {
    ca_ai_service_t               *butler;
    ca_thermal_throttle_service_t *thermal;
    bool                           stopped;
};

ca_background_inference_worker_t *ca_background_inference_worker_create(
    ca_ai_service_t *butler, ca_thermal_throttle_service_t *thermal) {
    if (!butler) return NULL;
    ca_background_inference_worker_t *w = (ca_background_inference_worker_t *)calloc(1, sizeof(*w));
    if (!w) return NULL;
    w->butler = butler; w->thermal = thermal;
    return w;
}
void ca_background_inference_worker_destroy(ca_background_inference_worker_t *w) { free(w); }
bool ca_background_inference_worker_start(ca_background_inference_worker_t *w) {
    if (!w) return false;
    if (w->thermal) ca_thermal_throttle_start(w->thermal);
    return ca_ai_service_start(w->butler);
}
bool ca_background_inference_worker_stop(ca_background_inference_worker_t *w) {
    if (!w) return false;
    if (w->stopped) return true;
    w->stopped = true;
    if (w->thermal) ca_thermal_throttle_stop(w->thermal);
    return ca_ai_service_stop(w->butler);
}
bool ca_background_inference_worker_is_paused(const ca_background_inference_worker_t *w) {
    if (!w || !w->thermal) return false;
    return ca_thermal_throttle_should_pause(w->thermal);
}

/* ===========================================================================
 * HistogramRequestPredictor
 * =========================================================================== */

#define MINUTES_PER_DAY (24 * 60)
#define MIN_SAMPLES_FOR_FULL_CONFIDENCE 25

struct ca_histogram_request_predictor {
    int      history_days;
    double  *per_minute_rate;   /* [MINUTES_PER_DAY] */
    int     *per_minute_count;  /* [MINUTES_PER_DAY] */
    int64_t  observed;
};

ca_histogram_request_predictor_t *ca_histogram_request_predictor_create(int history_days) {
    if (history_days <= 0) history_days = 7;
    ca_histogram_request_predictor_t *p = (ca_histogram_request_predictor_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->history_days = history_days;
    p->per_minute_rate = (double *)calloc(MINUTES_PER_DAY, sizeof(double));
    p->per_minute_count = (int *)calloc(MINUTES_PER_DAY, sizeof(int));
    if (!p->per_minute_rate || !p->per_minute_count) {
        free(p->per_minute_rate); free(p->per_minute_count); free(p);
        return NULL;
    }
    return p;
}
void ca_histogram_request_predictor_destroy(ca_histogram_request_predictor_t *p) {
    if (!p) return;
    free(p->per_minute_rate); free(p->per_minute_count); free(p);
}
int64_t ca_histogram_request_predictor_observed(const ca_histogram_request_predictor_t *p) {
    return p ? p->observed : 0;
}
void ca_histogram_request_predictor_record(ca_histogram_request_predictor_t *p, int64_t utc_ms) {
    if (!p) return;
    civil_t c; civil_from_ms(utc_ms, &c);
    int minute = c.hour * 60 + c.minute;
    int cnt = ++p->per_minute_count[minute];
    int m = cnt < p->history_days ? cnt : p->history_days;
    double alpha = 2.0 / (m + 1);
    p->per_minute_rate[minute] = (alpha * 1.0) + ((1 - alpha) * p->per_minute_rate[minute]);
    p->observed++;
}
ca_arrival_forecast_t ca_histogram_request_predictor_predict(
    const ca_histogram_request_predictor_t *p, int64_t utc_now_ms, int64_t forecast_window_ms) {
    ca_arrival_forecast_t f = { 0, 0, 0 };
    if (!p || forecast_window_ms <= 0 || p->observed == 0) return f;
    civil_t c; civil_from_ms(utc_now_ms, &c);
    int minute = c.hour * 60 + c.minute;
    int minutes = (int)ceil((double)forecast_window_ms / 60000.0);
    if (minutes < 1) minutes = 1;
    double expected = 0; int covered = 0;
    for (int i = 0; i < minutes; ++i) {
        int idx = (minute + i) % MINUTES_PER_DAY;
        expected += p->per_minute_rate[idx];
        covered  += p->per_minute_count[idx];
    }
    double probability = 1.0 - exp(-expected);
    double confidence = (double)covered / (MIN_SAMPLES_FOR_FULL_CONFIDENCE * minutes);
    if (confidence > 1.0) confidence = 1.0;
    f.probability_of_arrival = probability;
    f.expected_count = expected;
    f.confidence = confidence;
    return f;
}
void ca_histogram_request_predictor_reset(ca_histogram_request_predictor_t *p) {
    if (!p) return;
    for (int i = 0; i < MINUTES_PER_DAY; ++i) { p->per_minute_rate[i] = 0; p->per_minute_count[i] = 0; }
    p->observed = 0;
}

/* ===========================================================================
 * PredictiveWarmupController
 * =========================================================================== */

void ca_predictive_warmup_options_init(ca_predictive_warmup_options_t *o) {
    if (!o) return;
    o->enabled = false;
    o->poll_interval_ms = 30LL * 1000;
    o->forecast_window_ms = 60LL * 1000;
    o->warmup_threshold = 0.5;
    o->min_time_between_warmups_ms = 5LL * 60 * 1000;
}

struct ca_predictive_warmup_controller {
    ca_ai_service_t                   *service;
    ca_histogram_request_predictor_t  *predictor;
    ca_predictive_warmup_options_t     options;
    bool                               has_last_warmup;
    int64_t                            last_warmup_ms;
};

ca_predictive_warmup_controller_t *ca_predictive_warmup_controller_create(
    ca_ai_service_t *service, ca_histogram_request_predictor_t *predictor,
    const ca_predictive_warmup_options_t *options) {
    if (!service || !predictor || !options) return NULL;
    ca_predictive_warmup_controller_t *c = (ca_predictive_warmup_controller_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->service = service; c->predictor = predictor; c->options = *options;
    return c;
}
void ca_predictive_warmup_controller_destroy(ca_predictive_warmup_controller_t *c) { free(c); }
void ca_predictive_warmup_controller_notify_arrival(ca_predictive_warmup_controller_t *c, int64_t now_ms) {
    if (!c) return;
    ca_histogram_request_predictor_record(c->predictor, now_ms);
}
bool ca_predictive_warmup_controller_tick(ca_predictive_warmup_controller_t *c, int64_t now_ms) {
    if (!c) return false;
    ca_arrival_forecast_t f = ca_histogram_request_predictor_predict(
        c->predictor, now_ms, c->options.forecast_window_ms);
    double score = f.probability_of_arrival * f.confidence;
    if (score < c->options.warmup_threshold) return false;
    if (c->has_last_warmup && (now_ms - c->last_warmup_ms) < c->options.min_time_between_warmups_ms)
        return false;
    c->has_last_warmup = true; c->last_warmup_ms = now_ms;
    ca_ai_service_prewarm(c->service);
    return true;
}
