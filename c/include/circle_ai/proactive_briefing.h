#ifndef CIRCLE_AI_PROACTIVE_BRIEFING_H
#define CIRCLE_AI_PROACTIVE_BRIEFING_H

/*
 * proactive_briefing.h — CircleAI ProactiveBriefingService (C11 port).
 *
 * Assembles a "what's happening" briefing from registered calendar / email /
 * news / weather connectors, optionally summarises it through an LLM, and pushes
 * the result through any registered notifier. Ported from ProactiveBriefingService.cs.
 *
 * The scheduling loop in the C# is a hosted background service driven by a
 * System.Threading.Timer; the port exposes the two pure pieces that carry the
 * logic — the fire-time computation (TimeUntilNextFire) and the one-shot fire
 * (FireOnceAsync) — plus the connectors/notifier/AI as INJECTED function-pointer
 * seams. The host drives the loop from its own clock.
 *
 * The C# connector interfaces (ICalendarConnector, IEmailConnector, INewsSource,
 * IWeatherProvider) and IBriefingNotifier become seams here; the briefing
 * assembles exactly the same context text (### headers, bullet formats, 8/5/5
 * caps, weather line) so the summariser prompt is byte-for-byte the C#'s.
 *
 * Ownership: connector seams return fresh deep-copied arrays the briefing frees;
 * value structs own their strdup'd strings with matching *_free. The assembled
 * context string is malloc'd and returned to the caller / handed to the AI +
 * notifiers. Times are Unix ms UTC.
 *
 * Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Connector value types (subset the briefing reads)
 * =========================================================================== */

/* CalendarEvent — the briefing reads StartUtc/Title/Location (ordered by start,
 * top 8). start_local_hhmm is the "HH:mm" the C# renders via ToLocalTime(); the
 * port takes the already-formatted "HH:mm" so the local-time policy stays the
 * host's. */
typedef struct {
    char   *title;              /* owned */
    char   *location;           /* owned, or NULL/empty */
    int64_t start_utc_ms;
    char   *start_hhmm;         /* owned; "HH:mm" for display */
} ca_briefing_calendar_event_t;

/* EmailMessage — the briefing reads From/Subject (top 5 unread). */
typedef struct {
    char *from;                 /* owned */
    char *subject;              /* owned */
} ca_briefing_email_t;

/* NewsItem — the briefing reads Title (top 5). */
typedef struct {
    char *title;                /* owned */
} ca_briefing_news_t;

/* WeatherSample — the briefing reads TempC/Condition/FeelsLikeC/WindKph. */
typedef struct {
    double temp_c;
    double feels_like_c;
    double wind_kph;
    char  *condition;           /* owned */
} ca_briefing_weather_t;

/* ===========================================================================
 * Connector seams
 * ===========================================================================
 *
 * Each connector exposes a provider id, a configured flag, and a fetch. The
 * briefing skips unconfigured connectors (Where(c => c.IsConfigured)). Fetches
 * return fresh arrays (the briefing frees them) and set *out_count; a fetch may
 * fail by returning NULL with *out_count == SIZE_MAX (the C# try/catch skips it).
 */

typedef struct {
    const char *provider_id;
    bool        is_configured;
    /* List events in [from_ms, to_ms]. */
    ca_briefing_calendar_event_t *(*list_events)(void *user, int64_t from_ms, int64_t to_ms,
                                                 size_t *out_count);
    void       *user;
} ca_briefing_calendar_connector_t;

typedef struct {
    const char *provider_id;
    bool        is_configured;
    ca_briefing_email_t *(*list_unread)(void *user, int max, size_t *out_count);
    void       *user;
} ca_briefing_email_connector_t;

typedef struct {
    const char *source_id;
    bool        is_configured;
    ca_briefing_news_t *(*fetch_latest)(void *user, int max, size_t *out_count);
    void       *user;
} ca_briefing_news_source_t;

typedef struct {
    const char *provider_id;
    /* CurrentAsync(lat, lon) → fills *out (its strings malloc'd). Return true on
     * success, false to skip (the C# try/catch). */
    bool (*current)(void *user, double lat, double lon, ca_briefing_weather_t *out);
    void *user;
} ca_briefing_weather_provider_t;

/* Notifier seam: deliver(headline, body, address). address may be NULL. */
typedef void (*ca_briefing_notifier_fn)(void *user, const char *headline,
                                        const char *body, const char *address);

/* AI summariser seam: given the prompt, return a malloc'd summary, or NULL to
 * fall back to the raw context (the C# catch → sends raw context). */
typedef char *(*ca_briefing_ai_fn)(void *user, const char *prompt);

/* ===========================================================================
 * Options
 * =========================================================================== */

typedef struct {
    /* UTC times-of-day (minutes past midnight) at which to fire. Default: 06:30
     * (390) and 18:00 (1080). */
    const int *fire_times_min;
    size_t     fire_times_count;

    bool   has_lat; double latitude;    /* skip weather when has_lat/has_lon false */
    bool   has_lon; double longitude;

    const char *headline;               /* default "Your briefing" */
    const char *delivery_address;       /* may be NULL */
} ca_briefing_options_t;

/* ===========================================================================
 * ProactiveBriefingService
 * =========================================================================== */

typedef struct ca_proactive_briefing_service ca_proactive_briefing_service_t;

/* Create the service. All connector/notifier/AI arrays are borrowed (their user
 * pointers too) and must outlive the service. Any array may be NULL/empty. opts
 * is copied (fire_times default applies when fire_times_count == 0). weather may
 * be NULL. ai may be NULL (→ raw context is delivered). */
ca_proactive_briefing_service_t *ca_proactive_briefing_service_create(
    const ca_briefing_options_t *opts,
    const ca_briefing_calendar_connector_t *calendars, size_t calendar_count,
    const ca_briefing_email_connector_t    *emails,    size_t email_count,
    const ca_briefing_news_source_t        *news,      size_t news_count,
    const ca_briefing_weather_provider_t   *weather,   /* NULL to skip */
    const ca_briefing_notifier_fn          *notifiers, void *const *notifier_users,
    size_t notifier_count,
    ca_briefing_ai_fn ai, void *ai_user);
void ca_proactive_briefing_service_destroy(ca_proactive_briefing_service_t *s);

/* Milliseconds until the next configured fire moment, given now_ms. Mirrors
 * TimeUntilNextFire: candidates <= now+30s roll to the next day; the smallest
 * positive gap wins; empty fire-times → 1 hour. */
int64_t ca_proactive_briefing_time_until_next_fire(
    const ca_proactive_briefing_service_t *s, int64_t now_ms);

/* Assemble + summarise + deliver one briefing at now_ms. Returns true if a
 * briefing was delivered (>=1 signal present), false if no signals (skipped).
 * If out_body is non-NULL it receives the delivered body (malloc'd; caller
 * frees) — NULL is written when skipped. */
bool ca_proactive_briefing_fire_once(ca_proactive_briefing_service_t *s, int64_t now_ms,
                                     char **out_body);

/* --- value frees (for connector implementations) --- */
void ca_briefing_calendar_event_free(ca_briefing_calendar_event_t *e);
void ca_briefing_calendar_event_free_array(ca_briefing_calendar_event_t *arr, size_t count);
void ca_briefing_email_free(ca_briefing_email_t *e);
void ca_briefing_email_free_array(ca_briefing_email_t *arr, size_t count);
void ca_briefing_news_free(ca_briefing_news_t *n);
void ca_briefing_news_free_array(ca_briefing_news_t *arr, size_t count);
void ca_briefing_weather_free(ca_briefing_weather_t *w);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PROACTIVE_BRIEFING_H */
