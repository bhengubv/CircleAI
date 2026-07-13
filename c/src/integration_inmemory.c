/*
 * integration_inmemory.c — CircleAI.Integration InMemoryIntegrationConnectors.cs
 * (C11 port).
 *
 * The six canonical in-memory reference connectors (ProviderId/SourceId
 * "in-memory"), distinct from the provider-specific doubles. Numeric formulas —
 * the weather pseudo-model and the haversine route estimate — are ported to match
 * the C# byte-for-byte: Math.Round(x, n) is reproduced with round-half-to-even
 * (rint under the default FE_TONEAREST mode), and the expressions are written
 * straight (the build uses -ffp-contract=off). Pure C11 + libc + libm. No pthreads.
 */

#include "circle_ai/integration_inmemory.h"
#include "board_common.h"

#include <math.h>

/* Math.PI as the exact IEEE-754 double (matches the C# constant). Avoids relying
 * on M_PI, which is not standard C11 and is absent on some toolchains. */
#define IM_PI 3.14159265358979323846

/* Math.Round(value, decimals) with MidpointRounding.ToEven: scale, round-half-to-
 * even (rint honours the default rounding mode), unscale. decimals in {2,3} here. */
static double round_even(double value, int decimals) {
    double p = 1.0;
    for (int i = 0; i < decimals; ++i) p *= 10.0;
    return rint(value * p) / p;
}

/* ── shared string helper: Contains(query, OrdinalIgnoreCase) ─────────────────
 * board_common's cab_ci_contains already implements this (empty needle matches,
 * mirroring C# string.Contains("")). */

/* =========================================================================
 * InMemoryCalendarConnector
 * ========================================================================= */

typedef struct {
    ca_int_calendar_event_t *items;
    size_t                   count, cap;
} imcal_impl_t;

static const char *imcal_provider_id(void *impl) { (void)impl; return "in-memory"; }
static bool imcal_is_configured(void *impl) { (void)impl; return true; }

/* Stable ascending sort of collected indices by StartUtc. */
static void imcal_sort_start_asc(const imcal_impl_t *m, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].start_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].start_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

static ca_int_calendar_event_t *imcal_list_events(void *impl, int64_t from_utc_ms,
                                                  int64_t to_utc_ms,
                                                  size_t *out_count) {
    if (!out_count) return NULL;
    imcal_impl_t *m = (imcal_impl_t *)impl;
    if (m->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < m->count; ++i) {
        /* Where(e => e.StartUtc < toUtc && e.EndUtc > fromUtc). */
        if (m->items[i].start_utc_ms < to_utc_ms &&
            m->items[i].end_utc_ms > from_utc_ms)
            idx[n++] = i;
    }
    imcal_sort_start_asc(m, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_int_calendar_event_t *out =
        (ca_int_calendar_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ca_int_calendar_event_copy(&out[i], &m->items[idx[i]])) {
            ca_int_calendar_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* Find an event by EventId (Ordinal). SIZE_MAX when absent. */
static size_t imcal_index_of(const imcal_impl_t *m, const char *event_id) {
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].event_id, event_id)) return i;
    return (size_t)-1;
}

static int imcal_create_event(void *impl, const ca_int_calendar_event_t *ev,
                              ca_int_calendar_event_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    imcal_impl_t *m = (imcal_impl_t *)impl;
    if (!m || !ev) return -1;   /* ArgumentNullException.ThrowIfNull(ev) */

    /* _events[ev.EventId] = ev — store by EventId (last-write-wins), then return
     * a copy. No UID assignment (unlike the CalDAV double). */
    ca_int_calendar_event_t stored;
    if (!ca_int_calendar_event_copy(&stored, ev)) return -1;
    if (out && !ca_int_calendar_event_copy(out, &stored)) {
        ca_int_calendar_event_free(&stored);
        return -1;
    }
    size_t at = imcal_index_of(m, stored.event_id);
    if (at != (size_t)-1) {
        ca_int_calendar_event_free(&m->items[at]);
        m->items[at] = stored;
        return 0;
    }
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) {
            ca_int_calendar_event_free(&stored);
            if (out) ca_int_calendar_event_free(out);
            return -1;
        }
        m->items = (ca_int_calendar_event_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = stored;
    return 0;
}

static int imcal_delete_event(void *impl, const char *calendar_id,
                              const char *event_id) {
    (void)calendar_id;   /* _events.TryRemove(eventId, out _) ignores calendarId */
    imcal_impl_t *m = (imcal_impl_t *)impl;
    if (!m || !event_id) return 0;   /* no throw; NULL guarded as a no-op */
    size_t at = imcal_index_of(m, event_id);
    if (at == (size_t)-1) return 0;  /* not-found swallowed */
    ca_int_calendar_event_free(&m->items[at]);
    for (size_t i = at; i + 1 < m->count; ++i) m->items[i] = m->items[i + 1];
    m->count--;
    return 0;
}

ca_int_calendar_connector_t *ca_int_inmemory_calendar_create(void) {
    imcal_impl_t *m = (imcal_impl_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    ca_int_calendar_connector_t *c =
        (ca_int_calendar_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = imcal_provider_id;
    c->is_configured = imcal_is_configured;
    c->list_events   = imcal_list_events;
    c->create_event  = imcal_create_event;
    c->delete_event  = imcal_delete_event;
    return c;
}

void ca_int_inmemory_calendar_destroy(ca_int_calendar_connector_t *c) {
    if (!c) return;
    imcal_impl_t *m = (imcal_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i)
            ca_int_calendar_event_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}

/* =========================================================================
 * InMemoryEmailConnector
 * ========================================================================= */

typedef struct {
    ca_int_email_message_t *items;
    size_t                  count, cap;
} imemail_impl_t;

static const char *imemail_provider_id(void *impl) { (void)impl; return "in-memory"; }
static bool imemail_is_configured(void *impl) { (void)impl; return true; }

/* Stable descending sort of collected indices by ReceivedUtc. */
static void imemail_sort_recv_desc(const imemail_impl_t *m, size_t *idx,
                                   size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].received_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].received_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

/* Materialise the first `take` of `idx` (already ordered) into a fresh array. */
static ca_int_email_message_t *imemail_take(const imemail_impl_t *m,
                                            const size_t *idx, size_t n,
                                            size_t take, size_t *out_count) {
    if (take > n) take = n;
    if (take == 0) { *out_count = 0; return NULL; }
    ca_int_email_message_t *out =
        (ca_int_email_message_t *)calloc(take, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        if (!ca_int_email_message_copy(&out[i], &m->items[idx[i]])) {
            ca_int_email_message_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = take;
    return out;
}

static ca_int_email_message_t *imemail_list_unread(void *impl, int max,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    imemail_impl_t *m = (imemail_impl_t *)impl;
    /* Take(Math.Max(0, max)) — max<=0 yields empty (no throw). */
    size_t take = max > 0 ? (size_t)max : 0;
    if (m->count == 0 || take == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < m->count; ++i)
        if (m->items[i].unread) idx[n++] = i;
    imemail_sort_recv_desc(m, idx, n);
    ca_int_email_message_t *out = imemail_take(m, idx, n, take, out_count);
    free(idx);
    return out;
}

static ca_int_email_message_t *imemail_search(void *impl, const char *query,
                                              int max, size_t *out_count) {
    if (!out_count) return NULL;
    imemail_impl_t *m = (imemail_impl_t *)impl;
    if (!query) query = "";   /* query ??= "" */
    size_t take = max > 0 ? (size_t)max : 0;
    if (m->count == 0 || take == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ci_contains(m->items[i].subject, query) ||
            cab_ci_contains(m->items[i].body_text, query))
            idx[n++] = i;
    imemail_sort_recv_desc(m, idx, n);
    ca_int_email_message_t *out = imemail_take(m, idx, n, take, out_count);
    free(idx);
    return out;
}

static int imemail_mark_read(void *impl, const char *message_id) {
    imemail_impl_t *m = (imemail_impl_t *)impl;
    if (!m || !message_id) return 0;   /* no throw; NULL guarded */
    /* if (_messages.TryGetValue(id, out var msg)) _messages[id] = msg with {Unread=false} */
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].message_id, message_id)) {
            m->items[i].unread = false;
            break;
        }
    return 0;
}

ca_int_email_connector_t *ca_int_inmemory_email_create(void) {
    imemail_impl_t *m = (imemail_impl_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    ca_int_email_connector_t *c =
        (ca_int_email_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = imemail_provider_id;
    c->is_configured = imemail_is_configured;
    c->list_unread   = imemail_list_unread;
    c->search        = imemail_search;
    c->mark_read     = imemail_mark_read;
    return c;
}

int ca_int_inmemory_email_seed(ca_int_email_connector_t *c,
                               const ca_int_email_message_t *msg) {
    if (!c || !msg) return -1;
    imemail_impl_t *m = (imemail_impl_t *)c->impl;
    ca_int_email_message_t copy;
    if (!ca_int_email_message_copy(&copy, msg)) return -1;
    /* _messages[m.MessageId] = m — last-write-wins by MessageId. */
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].message_id, msg->message_id)) {
            ca_int_email_message_free(&m->items[i]);
            m->items[i] = copy;
            return 0;
        }
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_email_message_free(&copy); return -1; }
        m->items = (ca_int_email_message_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

void ca_int_inmemory_email_destroy(ca_int_email_connector_t *c) {
    if (!c) return;
    imemail_impl_t *m = (imemail_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i)
            ca_int_email_message_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}

/* =========================================================================
 * InMemoryNewsSource
 * ========================================================================= */

typedef struct {
    ca_int_news_item_t *items;
    size_t              count, cap;
} imnews_impl_t;

static const char *imnews_source_id(void *impl) { (void)impl; return "in-memory"; }
static bool imnews_is_configured(void *impl) { (void)impl; return true; }

static ca_int_news_item_t *imnews_fetch_latest(void *impl, int max,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    imnews_impl_t *m = (imnews_impl_t *)impl;
    /* Take(Math.Max(0, max)) — max<=0 yields empty (no throw). */
    size_t take = max > 0 ? (size_t)max : 0;
    if (m->count == 0 || take == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m->count; ++i) idx[i] = i;
    /* OrderByDescending(PublishedUtc), stable insertion sort. */
    for (size_t i = 1; i < m->count; ++i) {
        size_t key = idx[i];
        int64_t kt = m->items[key].published_utc_ms;
        size_t j = i;
        while (j > 0 && m->items[idx[j - 1]].published_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
    size_t n = m->count;
    if (take < n) n = take;

    ca_int_news_item_t *out = (ca_int_news_item_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!ca_int_news_item_copy(&out[i], &m->items[idx[i]])) {
            ca_int_news_item_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

ca_int_news_source_t *ca_int_inmemory_news_create(void) {
    imnews_impl_t *m = (imnews_impl_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    ca_int_news_source_t *s = (ca_int_news_source_t *)calloc(1, sizeof(*s));
    if (!s) { free(m); return NULL; }
    s->impl          = m;
    s->source_id     = imnews_source_id;
    s->is_configured = imnews_is_configured;
    s->fetch_latest  = imnews_fetch_latest;
    return s;
}

int ca_int_inmemory_news_seed(ca_int_news_source_t *s,
                              const ca_int_news_item_t *item) {
    if (!s || !item) return -1;
    imnews_impl_t *m = (imnews_impl_t *)s->impl;
    ca_int_news_item_t copy;
    if (!ca_int_news_item_copy(&copy, item)) return -1;
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].item_id, item->item_id)) {
            ca_int_news_item_free(&m->items[i]);
            m->items[i] = copy;
            return 0;
        }
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_news_item_free(&copy); return -1; }
        m->items = (ca_int_news_item_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

void ca_int_inmemory_news_destroy(ca_int_news_source_t *s) {
    if (!s) return;
    imnews_impl_t *m = (imnews_impl_t *)s->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i) ca_int_news_item_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(s);
}

/* =========================================================================
 * InMemoryWeatherProvider
 * ========================================================================= */

/* Sample(lat, lon, hourOffset). */
static void imweather_sample(double lat, double lon, int hour_offset,
                             ca_int_weather_sample_t *s) {
    memset(s, 0, sizeof(*s));
    /* tempC = Round(15 + 10*cos((lat + hourOffset) * PI / 12), 2). */
    double temp_c = round_even(
        15.0 + 10.0 * cos(((double)lat + (double)hour_offset) * IM_PI / 12.0), 2);
    s->at_utc_ms    = (int64_t)hour_offset * 3600000LL; /* UnixEpoch.AddHours */
    s->temp_c       = temp_c;
    s->feels_like_c = round_even(temp_c - 1.5, 2);
    s->precip_mm    = 0.0;
    s->wind_kph     = 12.0;
    s->cloud_pct    = 40;
    (void)lon; /* lon is not used by the C# formula */
}

static const char *imweather_provider_id(void *impl) {
    (void)impl; return "in-memory";
}

static int imweather_current(void *impl, double lat, double lon,
                             ca_int_weather_sample_t *out) {
    (void)impl;
    if (!out) return -1;
    imweather_sample(lat, lon, 0, out);
    out->condition = cab_strdup_empty("Clear");
    if (!out->condition) { ca_int_weather_sample_free(out); return -1; }
    return 0;
}

static ca_int_weather_sample_t *imweather_hourly(void *impl, double lat,
                                                 double lon, int hours,
                                                 size_t *out_count) {
    (void)impl;
    if (!out_count) return NULL;
    /* Enumerable.Range(0, Math.Max(0, hours)) — hours<=0 yields empty (no throw). */
    if (hours <= 0) { *out_count = 0; return NULL; }
    ca_int_weather_sample_t *out =
        (ca_int_weather_sample_t *)calloc((size_t)hours, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (int h = 0; h < hours; ++h) {
        imweather_sample(lat, lon, h, &out[h]);
        out[h].condition = cab_strdup_empty("Clear");
        if (!out[h].condition) {
            ca_int_weather_sample_free_array(out, (size_t)h);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = (size_t)hours;
    return out;
}

ca_int_weather_provider_t *ca_int_inmemory_weather_create(void) {
    ca_int_weather_provider_t *p =
        (ca_int_weather_provider_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->impl        = NULL; /* stateless */
    p->provider_id = imweather_provider_id;
    p->current     = imweather_current;
    p->hourly      = imweather_hourly;
    return p;
}

void ca_int_inmemory_weather_destroy(ca_int_weather_provider_t *p) {
    free(p);
}

/* =========================================================================
 * InMemoryRoutingProvider
 * ========================================================================= */

/* Haversine great-circle distance in KILOMETRES (r = 6371). */
static double imroute_haversine(double lat1, double lon1, double lat2,
                                double lon2) {
    const double r = 6371.0;
    double d_lat = (lat2 - lat1) * IM_PI / 180.0;
    double d_lon = (lon2 - lon1) * IM_PI / 180.0;
    double a = sin(d_lat / 2) * sin(d_lat / 2) +
               cos(lat1 * IM_PI / 180.0) * cos(lat2 * IM_PI / 180.0) *
               sin(d_lon / 2) * sin(d_lon / 2);
    return r * 2 * atan2(sqrt(a), sqrt(1 - a));
}

static const char *imroute_provider_id(void *impl) {
    (void)impl; return "in-memory";
}

static int imroute_route(void *impl, double from_lat, double from_lon,
                         double to_lat, double to_lon, const char *mode,
                         ca_int_route_estimate_t *out) {
    (void)impl;
    if (!out) return -1;
    memset(out, 0, sizeof(*out));

    double km = imroute_haversine(from_lat, from_lon, to_lat, to_lon);
    /* mode switch { "walk"=>5, "bike"=>18, "transit"=>30, _=>60 }. mode NULL is
     * the default arm (60) — the C# default parameter is "car". */
    double kph = 60.0;
    if (mode) {
        if (cab_ord_eq(mode, "walk"))         kph = 5.0;
        else if (cab_ord_eq(mode, "bike"))    kph = 18.0;
        else if (cab_ord_eq(mode, "transit")) kph = 30.0;
    }
    /* Duration = FromHours(kph <= 0 ? 0 : km / kph). Whole-ms in this port. */
    double hours = kph <= 0.0 ? 0.0 : km / kph;
    out->distance_km = round_even(km, 3);          /* Round(km, 3) */
    out->duration_ms = (int64_t)(hours * 3600000.0);

    out->polyline = (ca_int_route_point_t *)malloc(2 * sizeof(ca_int_route_point_t));
    if (!out->polyline) return -1;
    out->polyline[0].lat = from_lat;
    out->polyline[0].lon = from_lon;
    out->polyline[1].lat = to_lat;
    out->polyline[1].lon = to_lon;
    out->polyline_count = 2;
    return 0;
}

ca_int_routing_provider_t *ca_int_inmemory_routing_create(void) {
    ca_int_routing_provider_t *p =
        (ca_int_routing_provider_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->impl        = NULL; /* stateless */
    p->provider_id = imroute_provider_id;
    p->route       = imroute_route;
    return p;
}

void ca_int_inmemory_routing_destroy(ca_int_routing_provider_t *p) {
    free(p);
}

/* =========================================================================
 * InMemoryHomeAutomationConnector
 * ========================================================================= */

typedef struct {
    ca_int_ha_entity_t *items;
    size_t              count, cap;
} imhome_impl_t;

static const char *imhome_provider_id(void *impl) { (void)impl; return "in-memory"; }
static bool imhome_is_configured(void *impl) { (void)impl; return true; }

static ca_int_ha_entity_t *imhome_list_entities(void *impl, size_t *out_count) {
    if (!out_count) return NULL;
    imhome_impl_t *m = (imhome_impl_t *)impl;
    if (m->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(m->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m->count; ++i) idx[i] = i;
    /* OrderBy(EntityId), stable insertion sort (Ordinal — deterministic). */
    for (size_t i = 1; i < m->count; ++i) {
        size_t key = idx[i];
        const char *ke = m->items[key].entity_id;
        size_t j = i;
        while (j > 0 && strcmp(m->items[idx[j - 1]].entity_id, ke) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }

    ca_int_ha_entity_t *out =
        (ca_int_ha_entity_t *)calloc(m->count, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < m->count; ++i) {
        if (!ca_int_ha_entity_copy(&out[i], &m->items[idx[i]])) {
            ca_int_ha_entity_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = m->count;
    return out;
}

static int imhome_call_service(void *impl, const char *domain, const char *service,
                               const ca_int_service_data_pair_t *data,
                               size_t data_count) {
    (void)data; (void)data_count;   /* data unused by the reference impl */
    imhome_impl_t *m = (imhome_impl_t *)impl;
    if (!m || !domain || !service) return 0;   /* no throw; NULL guarded */

    /* For each entity in the matching domain (OrdinalIgnoreCase), pick the new
     * state by service; unknown services leave State unchanged. */
    for (size_t i = 0; i < m->count; ++i) {
        if (!cab_ci_eq(m->items[i].domain, domain)) continue;
        const char *new_state = NULL;
        if (cab_ord_eq(service, "turn_on")) new_state = "on";
        else if (cab_ord_eq(service, "turn_off")) new_state = "off";
        else if (cab_ord_eq(service, "toggle"))
            new_state = cab_ord_eq(m->items[i].state, "on") ? "off" : "on";
        if (!new_state) continue;   /* _ => e.State (no change) */
        char *ns = cab_strdup_empty(new_state);
        if (!ns) continue;          /* leave prior state on OOM */
        free(m->items[i].state);
        m->items[i].state = ns;
    }
    return 0;
}

ca_int_home_connector_t *ca_int_inmemory_home_create(void) {
    imhome_impl_t *m = (imhome_impl_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    ca_int_home_connector_t *c =
        (ca_int_home_connector_t *)calloc(1, sizeof(*c));
    if (!c) { free(m); return NULL; }
    c->impl          = m;
    c->provider_id   = imhome_provider_id;
    c->is_configured = imhome_is_configured;
    c->list_entities = imhome_list_entities;
    c->call_service  = imhome_call_service;
    return c;
}

int ca_int_inmemory_home_seed(ca_int_home_connector_t *c,
                              const ca_int_ha_entity_t *entity) {
    if (!c || !entity) return -1;
    imhome_impl_t *m = (imhome_impl_t *)c->impl;
    ca_int_ha_entity_t copy;
    if (!ca_int_ha_entity_copy(&copy, entity)) return -1;
    for (size_t i = 0; i < m->count; ++i)
        if (cab_ord_eq(m->items[i].entity_id, entity->entity_id)) {
            ca_int_ha_entity_free(&m->items[i]);
            m->items[i] = copy;
            return 0;
        }
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        void *nb = realloc(m->items, nc * sizeof(*m->items));
        if (!nb) { ca_int_ha_entity_free(&copy); return -1; }
        m->items = (ca_int_ha_entity_t *)nb;
        m->cap = nc;
    }
    m->items[m->count++] = copy;
    return 0;
}

void ca_int_inmemory_home_destroy(ca_int_home_connector_t *c) {
    if (!c) return;
    imhome_impl_t *m = (imhome_impl_t *)c->impl;
    if (m) {
        for (size_t i = 0; i < m->count; ++i) ca_int_ha_entity_free(&m->items[i]);
        free(m->items);
        free(m);
    }
    free(c);
}
