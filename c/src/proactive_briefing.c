/*
 * proactive_briefing.c — CircleAI ProactiveBriefingService (C11 port).
 *
 * Assembles the briefing context exactly as ProactiveBriefingService.cs
 * (### headers, bullet formats, 8/5/5 caps, weather line), optionally runs it
 * through an AI summariser seam, and delivers via notifier seams. The fire-time
 * computation mirrors TimeUntilNextFire.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/proactive_briefing.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <math.h>

/* ---- small helpers ---- */

static char *pb_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

typedef struct { char *buf; size_t len, cap; } pb_sb;
static void pb_sb_append(pb_sb *b, const char *s) {
    if (!s) return;
    size_t n = strlen(s);
    if (b->len + n + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 128;
        while (b->len + n + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(b->buf, nc);
        if (!nb) return;
        b->buf = nb; b->cap = nc;
    }
    if (!b->buf) return;
    memcpy(b->buf + b->len, s, n);
    b->len += n;
    b->buf[b->len] = '\0';
}

/* Round-half-to-even to 0 decimals (matches .NET "F0" banker's rounding). */
static void pb_f0(double v, char *out, size_t cap) {
    double r = rint(v);   /* rint uses round-half-to-even under the default mode */
    snprintf(out, cap, "%.0f", r);
}

/* ---- value frees ---- */

void ca_briefing_calendar_event_free(ca_briefing_calendar_event_t *e) {
    if (!e) return;
    free(e->title); free(e->location); free(e->start_hhmm);
    e->title = e->location = e->start_hhmm = NULL;
}
void ca_briefing_calendar_event_free_array(ca_briefing_calendar_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_briefing_calendar_event_free(&arr[i]);
    free(arr);
}
void ca_briefing_email_free(ca_briefing_email_t *e) {
    if (!e) return;
    free(e->from); free(e->subject);
    e->from = e->subject = NULL;
}
void ca_briefing_email_free_array(ca_briefing_email_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_briefing_email_free(&arr[i]);
    free(arr);
}
void ca_briefing_news_free(ca_briefing_news_t *n) {
    if (!n) return;
    free(n->title); n->title = NULL;
}
void ca_briefing_news_free_array(ca_briefing_news_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_briefing_news_free(&arr[i]);
    free(arr);
}
void ca_briefing_weather_free(ca_briefing_weather_t *w) {
    if (!w) return;
    free(w->condition); w->condition = NULL;
}

/* ---- service ---- */

#define PB_DEFAULT_FIRE0 390   /* 06:30 */
#define PB_DEFAULT_FIRE1 1080  /* 18:00 */

struct ca_proactive_briefing_service {
    int   *fire_times; size_t fire_count;
    bool   has_lat; double lat;
    bool   has_lon; double lon;
    char  *headline;
    char  *delivery_address;

    const ca_briefing_calendar_connector_t *calendars; size_t calendar_count;
    const ca_briefing_email_connector_t    *emails;    size_t email_count;
    const ca_briefing_news_source_t        *news;      size_t news_count;
    const ca_briefing_weather_provider_t   *weather;

    const ca_briefing_notifier_fn *notifiers; void *const *notifier_users;
    size_t notifier_count;

    ca_briefing_ai_fn ai; void *ai_user;
};

ca_proactive_briefing_service_t *ca_proactive_briefing_service_create(
    const ca_briefing_options_t *opts,
    const ca_briefing_calendar_connector_t *calendars, size_t calendar_count,
    const ca_briefing_email_connector_t    *emails,    size_t email_count,
    const ca_briefing_news_source_t        *news,      size_t news_count,
    const ca_briefing_weather_provider_t   *weather,
    const ca_briefing_notifier_fn          *notifiers, void *const *notifier_users,
    size_t notifier_count,
    ca_briefing_ai_fn ai, void *ai_user) {
    if (!opts) return NULL;
    ca_proactive_briefing_service_t *s =
        (ca_proactive_briefing_service_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;

    if (opts->fire_times_count > 0 && opts->fire_times_min) {
        s->fire_times = (int *)malloc(opts->fire_times_count * sizeof(int));
        if (!s->fire_times) { free(s); return NULL; }
        memcpy(s->fire_times, opts->fire_times_min, opts->fire_times_count * sizeof(int));
        s->fire_count = opts->fire_times_count;
    } else {
        s->fire_times = (int *)malloc(2 * sizeof(int));
        if (!s->fire_times) { free(s); return NULL; }
        s->fire_times[0] = PB_DEFAULT_FIRE0;
        s->fire_times[1] = PB_DEFAULT_FIRE1;
        s->fire_count = 2;
    }
    s->has_lat = opts->has_lat; s->lat = opts->latitude;
    s->has_lon = opts->has_lon; s->lon = opts->longitude;
    s->headline = pb_strdup(opts->headline ? opts->headline : "Your briefing");
    s->delivery_address = opts->delivery_address ? pb_strdup(opts->delivery_address) : NULL;

    s->calendars = calendars; s->calendar_count = calendar_count;
    s->emails = emails; s->email_count = email_count;
    s->news = news; s->news_count = news_count;
    s->weather = weather;
    s->notifiers = notifiers; s->notifier_users = notifier_users;
    s->notifier_count = notifier_count;
    s->ai = ai; s->ai_user = ai_user;
    return s;
}
void ca_proactive_briefing_service_destroy(ca_proactive_briefing_service_t *s) {
    if (!s) return;
    free(s->fire_times);
    free(s->headline);
    free(s->delivery_address);
    free(s);
}

int64_t ca_proactive_briefing_time_until_next_fire(
    const ca_proactive_briefing_service_t *s, int64_t now_ms) {
    if (!s || s->fire_count == 0) return 3600LL * 1000;   /* 1 hour */
    /* todayBase = start-of-UTC-day for now */
    int64_t day_ms = now_ms - ((now_ms % 86400000LL + 86400000LL) % 86400000LL);
    bool have = false;
    int64_t best = 0;
    for (size_t i = 0; i < s->fire_count; ++i) {
        int64_t candidate = day_ms + (int64_t)s->fire_times[i] * 60000;
        if (candidate <= now_ms + 30000) candidate += 86400000LL;   /* AddDays(1) */
        int64_t gap = candidate - now_ms;
        if (!have || gap < best) { best = gap; have = true; }
    }
    return have ? best : 3600LL * 1000;
}

/* insertion-sort calendar events ascending by start_utc_ms (stable) */
static void pb_sort_events(ca_briefing_calendar_event_t *ev, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_briefing_calendar_event_t key = ev[i];
        size_t j = i;
        while (j > 0 && ev[j - 1].start_utc_ms > key.start_utc_ms) { ev[j] = ev[j - 1]; j--; }
        ev[j] = key;
    }
}

bool ca_proactive_briefing_fire_once(ca_proactive_briefing_service_t *s, int64_t now_ms,
                                     char **out_body) {
    if (out_body) *out_body = NULL;
    if (!s) return false;

    pb_sb ctx = {0};
    int parts = 0;
    #define PB_LINE(str) do { if (parts++ > 0) pb_sb_append(&ctx, "\n"); pb_sb_append(&ctx, (str)); } while (0)

    /* Calendar — next 24h, configured only, ordered by start, top 8. */
    for (size_t ci = 0; ci < s->calendar_count; ++ci) {
        const ca_briefing_calendar_connector_t *cal = &s->calendars[ci];
        if (!cal->is_configured || !cal->list_events) continue;
        size_t n = 0;
        ca_briefing_calendar_event_t *ev =
            cal->list_events(cal->user, now_ms, now_ms + 24LL * 3600 * 1000, &n);
        if (n == (size_t)-1) continue;   /* fetch failed → skip */
        if (n > 0) {
            char hdr[256];
            snprintf(hdr, sizeof(hdr), "### Calendar (%s)", cal->provider_id ? cal->provider_id : "");
            PB_LINE(hdr);
            pb_sort_events(ev, n);
            size_t take = n < 8 ? n : 8;
            for (size_t i = 0; i < take; ++i) {
                pb_sb line = {0};
                pb_sb_append(&line, "- ");
                pb_sb_append(&line, ev[i].start_hhmm ? ev[i].start_hhmm : "");
                pb_sb_append(&line, " ");
                pb_sb_append(&line, ev[i].title ? ev[i].title : "");
                if (ev[i].location && ev[i].location[0]) {
                    pb_sb_append(&line, " @ ");
                    pb_sb_append(&line, ev[i].location);
                }
                PB_LINE(line.buf ? line.buf : "");
                free(line.buf);
            }
        }
        ca_briefing_calendar_event_free_array(ev, n);
    }

    /* Email — unread, top 5. */
    for (size_t ei = 0; ei < s->email_count; ++ei) {
        const ca_briefing_email_connector_t *em = &s->emails[ei];
        if (!em->is_configured || !em->list_unread) continue;
        size_t n = 0;
        ca_briefing_email_t *msgs = em->list_unread(em->user, 5, &n);
        if (n == (size_t)-1) continue;
        if (n > 0) {
            char hdr[256];
            snprintf(hdr, sizeof(hdr), "### Unread email (%s)", em->provider_id ? em->provider_id : "");
            PB_LINE(hdr);
            for (size_t i = 0; i < n; ++i) {
                pb_sb line = {0};
                pb_sb_append(&line, "- ");
                pb_sb_append(&line, msgs[i].from ? msgs[i].from : "");
                pb_sb_append(&line, ": ");
                pb_sb_append(&line, msgs[i].subject ? msgs[i].subject : "");
                PB_LINE(line.buf ? line.buf : "");
                free(line.buf);
            }
        }
        ca_briefing_email_free_array(msgs, n);
    }

    /* News — latest, top 5 per source. */
    for (size_t ni = 0; ni < s->news_count; ++ni) {
        const ca_briefing_news_source_t *src = &s->news[ni];
        if (!src->is_configured || !src->fetch_latest) continue;
        size_t n = 0;
        ca_briefing_news_t *items = src->fetch_latest(src->user, 5, &n);
        if (n == (size_t)-1) continue;
        if (n > 0) {
            char hdr[256];
            snprintf(hdr, sizeof(hdr), "### News (%s)", src->source_id ? src->source_id : "");
            PB_LINE(hdr);
            for (size_t i = 0; i < n; ++i) {
                pb_sb line = {0};
                pb_sb_append(&line, "- ");
                pb_sb_append(&line, items[i].title ? items[i].title : "");
                PB_LINE(line.buf ? line.buf : "");
                free(line.buf);
            }
        }
        ca_briefing_news_free_array(items, n);
    }

    /* Weather — if provider + location configured. */
    if (s->weather && s->weather->current && s->has_lat && s->has_lon) {
        ca_briefing_weather_t w; memset(&w, 0, sizeof(w));
        if (s->weather->current(s->weather->user, s->lat, s->lon, &w)) {
            char hdr[256];
            snprintf(hdr, sizeof(hdr), "### Weather (%s)",
                     s->weather->provider_id ? s->weather->provider_id : "");
            PB_LINE(hdr);
            char t[16], f[16], wind[16];
            pb_f0(w.temp_c, t, sizeof(t));
            pb_f0(w.feels_like_c, f, sizeof(f));
            pb_f0(w.wind_kph, wind, sizeof(wind));
            pb_sb line = {0};
            pb_sb_append(&line, "- ");
            pb_sb_append(&line, t);
            pb_sb_append(&line, "\xc2\xb0""C ");          /* °C */
            pb_sb_append(&line, w.condition ? w.condition : "");
            pb_sb_append(&line, ", feels ");
            pb_sb_append(&line, f);
            pb_sb_append(&line, "\xc2\xb0""C, wind ");     /* °C, wind */
            pb_sb_append(&line, wind);
            pb_sb_append(&line, " km/h");
            PB_LINE(line.buf ? line.buf : "");
            free(line.buf);
        }
        ca_briefing_weather_free(&w);
    }

    if (parts == 0) {
        free(ctx.buf);
        return false;   /* no signals; skipping fire */
    }

    const char *context = ctx.buf ? ctx.buf : "";
    /* prompt = prefix + context */
    pb_sb prompt = {0};
    pb_sb_append(&prompt,
        "Summarise the user's morning briefing in 80 words or less. Warm but factual. "
        "End with the one thing they should do first today.\n\n");
    pb_sb_append(&prompt, context);

    char *summary;
    if (s->ai) {
        char *r = s->ai(s->ai_user, prompt.buf ? prompt.buf : "");
        summary = r ? r : pb_strdup(context);   /* AI failure → raw context */
    } else {
        summary = pb_strdup(context);
    }
    free(prompt.buf);

    for (size_t i = 0; i < s->notifier_count; ++i) {
        if (s->notifiers[i]) {
            void *nu = s->notifier_users ? s->notifier_users[i] : NULL;
            s->notifiers[i](nu, s->headline, summary, s->delivery_address);
        }
    }

    if (out_body) *out_body = pb_strdup(summary);
    free(summary);
    free(ctx.buf);
    return true;
    #undef PB_LINE
}
