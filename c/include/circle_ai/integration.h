#ifndef CIRCLE_AI_INTEGRATION_H
#define CIRCLE_AI_INTEGRATION_H

/*
 * integration.h — CircleAI.Integration (C11 port of Contracts.cs).
 *
 * Shared abstractions for the external-integration layer. Calendar, email,
 * news, weather and home-automation providers implement these vtable
 * interfaces so the Companion's ProactiveBriefingService can stitch a coherent
 * "what's happening" picture without coupling to specific providers.
 *
 * C# interfaces become function-pointer vtables carrying an opaque impl handle
 * (mirrors the pattern used by CircleAI.Telephony carriers + CircleAI.Networking
 * transports). The real HTTP connectors (Google/MsGraph/CalDav/Imap/Gmail/
 * OpenMeteo/Osrm/Bluesky/Mastodon/NewsApi/Rss/HomeAssistant) are injected
 * dependencies; each ships a deterministic in-memory implementation (in the
 * integration_calendar / _email / _geo / _home / _news modules) that carries the
 * provider's ProviderId/SourceId and behaviour with no real network.
 *
 *   Records : CalendarEvent, EmailMessage, NewsItem, WeatherSample,
 *             RouteEstimate (+ RoutePoint), HaEntity (+ attribute pairs).
 *   Vtables : ICalendarConnector, IEmailConnector, INewsSource,
 *             IWeatherProvider, IRoutingProvider, IHomeAutomationConnector.
 *
 * Conventions: ca_ prefix, _t types, strdup-owning fields with matching *_free,
 * deep-copy getters, list results as fresh owned arrays (*out_count), errors via
 * NULL + count SIZE_MAX. Nullable C# string fields carried as has_* flag + owned
 * buffer. DateTimeOffset carried as int64 Unix ms UTC; TimeSpan as int64 ms.
 * Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── Calendar ─────────────────────────────────────────────────────────────
 * CalendarEvent(EventId, CalendarId, Title, string? Description,
 *               string? Location, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
 *               bool IsAllDay, IReadOnlyList<string> Attendees). */
typedef struct {
    char   *event_id;      /* owned, non-null */
    char   *calendar_id;   /* owned, non-null */
    char   *title;         /* owned, non-null */
    bool    has_description;
    char   *description;   /* owned, valid only when has_description */
    bool    has_location;
    char   *location;      /* owned, valid only when has_location */
    int64_t start_utc_ms;  /* DateTimeOffset StartUtc as Unix ms UTC */
    int64_t end_utc_ms;    /* DateTimeOffset EndUtc as Unix ms UTC */
    bool    is_all_day;
    char  **attendees;     /* owned array (may be NULL when count==0) */
    size_t  attendees_count;
} ca_int_calendar_event_t;

void ca_int_calendar_event_free(ca_int_calendar_event_t *e);
void ca_int_calendar_event_free_array(ca_int_calendar_event_t *arr, size_t count);
/* Deep-copy src into a zeroed dst. false on OOM (dst left freed/zeroed). */
bool ca_int_calendar_event_copy(ca_int_calendar_event_t *dst,
                                const ca_int_calendar_event_t *src);

/* ── Email ─────────────────────────────────────────────────────────────────
 * EmailMessage(MessageId, From, IReadOnlyList<string> To, Subject, BodyText,
 *              DateTimeOffset ReceivedUtc, bool Unread,
 *              IReadOnlyList<string> Labels). */
typedef struct {
    char   *message_id;    /* owned, non-null */
    char   *from;          /* owned, non-null */
    char  **to;            /* owned array (may be NULL when count==0) */
    size_t  to_count;
    char   *subject;       /* owned, non-null */
    char   *body_text;     /* owned, non-null */
    int64_t received_utc_ms;
    bool    unread;
    char  **labels;        /* owned array (may be NULL when count==0) */
    size_t  labels_count;
} ca_int_email_message_t;

void ca_int_email_message_free(ca_int_email_message_t *m);
void ca_int_email_message_free_array(ca_int_email_message_t *arr, size_t count);
bool ca_int_email_message_copy(ca_int_email_message_t *dst,
                               const ca_int_email_message_t *src);

/* ── News + social feeds ────────────────────────────────────────────────────
 * NewsItem(ItemId, SourceId, Title, Summary, Uri Url, DateTimeOffset PublishedUtc,
 *          IReadOnlyList<string> Tags). Uri carried as an owned string. */
typedef struct {
    char   *item_id;       /* owned, non-null */
    char   *source_id;     /* owned, non-null */
    char   *title;         /* owned, non-null */
    char   *summary;       /* owned, non-null */
    char   *url;           /* owned, non-null (C# Uri.ToString()) */
    int64_t published_utc_ms;
    char  **tags;          /* owned array (may be NULL when count==0) */
    size_t  tags_count;
} ca_int_news_item_t;

void ca_int_news_item_free(ca_int_news_item_t *n);
void ca_int_news_item_free_array(ca_int_news_item_t *arr, size_t count);
bool ca_int_news_item_copy(ca_int_news_item_t *dst, const ca_int_news_item_t *src);

/* ── Weather ────────────────────────────────────────────────────────────────
 * WeatherSample(DateTimeOffset AtUtc, double TempC, double FeelsLikeC,
 *               double PrecipMm, double WindKph, int CloudPct, string Condition). */
typedef struct {
    int64_t at_utc_ms;
    double  temp_c;
    double  feels_like_c;
    double  precip_mm;
    double  wind_kph;
    int     cloud_pct;
    char   *condition;     /* owned, non-null */
} ca_int_weather_sample_t;

void ca_int_weather_sample_free(ca_int_weather_sample_t *w);
void ca_int_weather_sample_free_array(ca_int_weather_sample_t *arr, size_t count);
bool ca_int_weather_sample_copy(ca_int_weather_sample_t *dst,
                                const ca_int_weather_sample_t *src);

/* ── Routing / traffic ──────────────────────────────────────────────────────
 * RouteEstimate(double DistanceKm, TimeSpan Duration,
 *               IReadOnlyList<(double Lat, double Lon)> Polyline). */
typedef struct {
    double lat;
    double lon;
} ca_int_route_point_t;

typedef struct {
    double                distance_km;
    int64_t               duration_ms;   /* TimeSpan Duration as ms */
    ca_int_route_point_t *polyline;      /* owned array (may be NULL when count==0) */
    size_t                polyline_count;
} ca_int_route_estimate_t;

void ca_int_route_estimate_free(ca_int_route_estimate_t *r);
bool ca_int_route_estimate_copy(ca_int_route_estimate_t *dst,
                                const ca_int_route_estimate_t *src);

/* ── Home automation ────────────────────────────────────────────────────────
 * HaEntity(EntityId, FriendlyName, Domain, State,
 *          IReadOnlyDictionary<string,string> Attributes). */
typedef struct {
    char *key;   /* owned, non-null */
    char *value; /* owned, non-null */
} ca_int_attr_pair_t;

typedef struct {
    char               *entity_id;      /* owned, non-null */
    char               *friendly_name;  /* owned, non-null */
    char               *domain;         /* owned, non-null */
    char               *state;          /* owned, non-null */
    ca_int_attr_pair_t *attributes;     /* owned array (may be NULL when count==0) */
    size_t              attributes_count;
} ca_int_ha_entity_t;

void ca_int_ha_entity_free(ca_int_ha_entity_t *e);
void ca_int_ha_entity_free_array(ca_int_ha_entity_t *arr, size_t count);
bool ca_int_ha_entity_copy(ca_int_ha_entity_t *dst, const ca_int_ha_entity_t *src);
/* Lookup Attributes[key] (Ordinal). Returns borrowed value or NULL when absent. */
const char *ca_int_ha_entity_attr(const ca_int_ha_entity_t *e, const char *key);

/* ── Service-call data (IHomeAutomationConnector.CallServiceAsync data arg) ──
 * IReadOnlyDictionary<string, object?>? carried as an owned string-map (object?
 * rendered to its string form by callers; NULL data == empty map). */
typedef ca_int_attr_pair_t ca_int_service_data_pair_t;

/* =========================================================================
 * Interface vtables. Each carries an opaque impl handle + function pointers.
 * A vtable value is owned by whoever created it (the in-memory sub-modules
 * expose *_create returning one of these + a matching *_destroy).
 * ========================================================================= */

/* ── ICalendarConnector ─────────────────────────────────────────────────── */
typedef struct ca_int_calendar_connector {
    void       *impl;
    const char *(*provider_id)(void *impl);
    bool        (*is_configured)(void *impl);
    /* ListEventsAsync(fromUtc, toUtc) -> fresh owned array (*out_count).
     * NULL+0 empty; NULL+SIZE_MAX on error. */
    ca_int_calendar_event_t *(*list_events)(void *impl, int64_t from_utc_ms,
                                            int64_t to_utc_ms, size_t *out_count);
    /* CreateEventAsync(ev) -> fresh owned copy of the stored event into *out.
     * 0 success; -1 bad args/OOM (mirrors ArgumentNullException). */
    int (*create_event)(void *impl, const ca_int_calendar_event_t *ev,
                        ca_int_calendar_event_t *out);
    /* DeleteEventAsync(calendarId, eventId). 0 success (incl. not-found, which
     * C# swallows); -1 bad args (eventId NULL/whitespace -> ArgumentException). */
    int (*delete_event)(void *impl, const char *calendar_id, const char *event_id);
} ca_int_calendar_connector_t;

/* ── IEmailConnector ────────────────────────────────────────────────────── */
typedef struct ca_int_email_connector {
    void       *impl;
    const char *(*provider_id)(void *impl);
    bool        (*is_configured)(void *impl);
    /* ListUnreadAsync(max) -> fresh owned array. NULL+SIZE_MAX on error
     * (max<=0 -> ArgumentOutOfRangeException). */
    ca_int_email_message_t *(*list_unread)(void *impl, int max, size_t *out_count);
    /* SearchAsync(query, max) -> fresh owned array. NULL+SIZE_MAX on error
     * (query NULL/whitespace or max<=0). */
    ca_int_email_message_t *(*search)(void *impl, const char *query, int max,
                                      size_t *out_count);
    /* MarkReadAsync(messageId). 0 success (incl. unknown id); -1 bad args
     * (messageId NULL/whitespace -> ArgumentException). */
    int (*mark_read)(void *impl, const char *message_id);
} ca_int_email_connector_t;

/* ── INewsSource ────────────────────────────────────────────────────────── */
typedef struct ca_int_news_source {
    void       *impl;
    const char *(*source_id)(void *impl);
    bool        (*is_configured)(void *impl);
    /* FetchLatestAsync(max) -> fresh owned array. NULL+SIZE_MAX on error
     * (max<=0 -> ArgumentOutOfRangeException; NewsApi also when !IsConfigured). */
    ca_int_news_item_t *(*fetch_latest)(void *impl, int max, size_t *out_count);
} ca_int_news_source_t;

/* ── IWeatherProvider ───────────────────────────────────────────────────── */
typedef struct ca_int_weather_provider {
    void       *impl;
    const char *(*provider_id)(void *impl);
    /* CurrentAsync(lat, lon) -> owned sample into *out. 0 success; -1 error. */
    int (*current)(void *impl, double lat, double lon, ca_int_weather_sample_t *out);
    /* HourlyAsync(lat, lon, hours) -> fresh owned array. NULL+SIZE_MAX on error
     * (hours<=0 || hours>168 -> ArgumentOutOfRangeException). */
    ca_int_weather_sample_t *(*hourly)(void *impl, double lat, double lon,
                                       int hours, size_t *out_count);
} ca_int_weather_provider_t;

/* ── IRoutingProvider ───────────────────────────────────────────────────── */
typedef struct ca_int_routing_provider {
    void       *impl;
    const char *(*provider_id)(void *impl);
    /* RouteAsync(fromLat, fromLon, toLat, toLon, mode) -> owned estimate into
     * *out. mode NULL treated as "car". 0 success; -1 error. */
    int (*route)(void *impl, double from_lat, double from_lon, double to_lat,
                double to_lon, const char *mode, ca_int_route_estimate_t *out);
} ca_int_routing_provider_t;

/* ── IHomeAutomationConnector ───────────────────────────────────────────── */
typedef struct ca_int_home_connector {
    void       *impl;
    const char *(*provider_id)(void *impl);
    bool        (*is_configured)(void *impl);
    /* ListEntitiesAsync() -> fresh owned array. NULL+0 empty; NULL+SIZE_MAX
     * on error. */
    ca_int_ha_entity_t *(*list_entities)(void *impl, size_t *out_count);
    /* CallServiceAsync(domain, service, data[]). 0 success; -1 bad args
     * (domain/service NULL/whitespace -> ArgumentException). data may be NULL. */
    int (*call_service)(void *impl, const char *domain, const char *service,
                       const ca_int_service_data_pair_t *data, size_t data_count);
} ca_int_home_connector_t;

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_H */
