/*
 * integration_geo.c — CircleAI.Integration.Geo (C11 port).
 *
 * In-memory IWeatherProvider (open-meteo) + IRoutingProvider (osrm). The real
 * providers hit Open-Meteo / OSRM; here samples + route are synthesised
 * deterministically from the inputs. The load-bearing pure logic — the WMO
 * weather-code decode table and the OSRM mode->profile mapping — is ported
 * exactly. Pure C11 + libc + libm. No pthreads.
 */

#include "circle_ai/integration_geo.h"
#include "board_common.h"
#include <math.h>

/* ── ported pure logic ──────────────────────────────────────────────────── */

const char *ca_int_open_meteo_wmo_decode(int code) {
    switch (code) {
        case 0:  return "clear sky";
        case 1: case 2: case 3: return "partly cloudy";
        case 45: case 48: return "fog";
        case 51: case 53: case 55: return "drizzle";
        case 56: case 57: return "freezing drizzle";
        case 61: case 63: case 65: return "rain";
        case 66: case 67: return "freezing rain";
        case 71: case 73: case 75: return "snow";
        case 77: return "snow grains";
        case 80: case 81: case 82: return "rain showers";
        case 85: case 86: return "snow showers";
        case 95: return "thunderstorm";
        case 96: case 99: return "thunderstorm with hail";
        default: return "unknown";
    }
}

const char *ca_int_osrm_profile(const char *mode) {
    if (!mode) return "driving";
    if (cab_ord_eq(mode, "bike") || cab_ord_eq(mode, "bicycle")) return "bike";
    if (cab_ord_eq(mode, "foot") || cab_ord_eq(mode, "walk"))    return "foot";
    return "driving";
}

/* ── weather provider (open-meteo) ──────────────────────────────────────── */

/* Deterministic surrogate weather-code from position (stands in for the network
 * value; stable per (lat,lon,hour) so results are reproducible). */
static int synth_code(double lat, double lon, int hour) {
    long h = (long)(fabs(lat) * 7.0 + fabs(lon) * 3.0) + hour;
    static const int codes[] = {0, 1, 2, 3, 45, 51, 61, 71, 80, 95};
    long i = h % 10;
    if (i < 0) i += 10;
    return codes[i];
}

static void synth_sample(double lat, double lon, int hour,
                         ca_int_weather_sample_t *s) {
    memset(s, 0, sizeof(*s));
    int code = synth_code(lat, lon, hour);
    double base = 15.0 + sin((lat + lon + hour) * 0.1) * 8.0;
    s->at_utc_ms    = (int64_t)hour * 3600000LL;
    s->temp_c       = base;
    s->feels_like_c = base - 1.5;
    s->precip_mm    = (code >= 51) ? 1.0 : 0.0;
    /* m/s surrogate * 3.6 -> km/h, mirroring the C# WindKph conversion. */
    s->wind_kph     = (2.0 + (double)((hour % 5))) * 3.6;
    s->cloud_pct    = (code == 0) ? 0 : (code < 45 ? 40 : 90);
    /* condition set by caller via wmo decode (kept centralised). */
    s->condition    = NULL;
}

static const char *weather_provider_id(void *impl) {
    (void)impl;
    return "open-meteo";
}

static int weather_current(void *impl, double lat, double lon,
                           ca_int_weather_sample_t *out) {
    (void)impl;
    if (!out) return -1;
    synth_sample(lat, lon, 0, out);
    out->condition = cab_strdup_empty(ca_int_open_meteo_wmo_decode(
        synth_code(lat, lon, 0)));
    if (!out->condition) { ca_int_weather_sample_free(out); return -1; }
    return 0;
}

static ca_int_weather_sample_t *weather_hourly(void *impl, double lat, double lon,
                                               int hours, size_t *out_count) {
    (void)impl;
    if (!out_count) return NULL;
    if (hours <= 0 || hours > 168) { *out_count = (size_t)-1; return NULL; }
    ca_int_weather_sample_t *out =
        (ca_int_weather_sample_t *)calloc((size_t)hours, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (int i = 0; i < hours; ++i) {
        synth_sample(lat, lon, i, &out[i]);
        out[i].condition = cab_strdup_empty(ca_int_open_meteo_wmo_decode(
            synth_code(lat, lon, i)));
        if (!out[i].condition) {
            ca_int_weather_sample_free_array(out, (size_t)i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = (size_t)hours;
    return out;
}

ca_int_weather_provider_t *ca_int_open_meteo_create(void) {
    ca_int_weather_provider_t *p =
        (ca_int_weather_provider_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->impl        = NULL; /* stateless */
    p->provider_id = weather_provider_id;
    p->current     = weather_current;
    p->hourly      = weather_hourly;
    return p;
}

void ca_int_weather_provider_destroy(ca_int_weather_provider_t *p) {
    free(p);
}

/* ── routing provider (osrm) ────────────────────────────────────────────── */

typedef struct {
    char *host; /* owned; parity only */
} routing_impl_t;

static const char *routing_provider_id(void *impl) {
    (void)impl;
    return "osrm";
}

/* Great-circle distance in metres (haversine). */
static double haversine_m(double lat1, double lon1, double lat2, double lon2) {
    const double R = 6371000.0;
    const double d2r = 3.14159265358979323846 / 180.0;
    double dlat = (lat2 - lat1) * d2r;
    double dlon = (lon2 - lon1) * d2r;
    double a = sin(dlat / 2) * sin(dlat / 2) +
               cos(lat1 * d2r) * cos(lat2 * d2r) * sin(dlon / 2) * sin(dlon / 2);
    return R * 2.0 * atan2(sqrt(a), sqrt(1.0 - a));
}

static int routing_route(void *impl, double from_lat, double from_lon,
                         double to_lat, double to_lon, const char *mode,
                         ca_int_route_estimate_t *out) {
    (void)impl;
    if (!out) return -1;
    memset(out, 0, sizeof(*out));

    /* Profile speed (m/s): driving ~13.9, bike ~4.2, foot ~1.4. */
    const char *profile = ca_int_osrm_profile(mode);
    double speed_ms = 13.9;
    if (cab_ord_eq(profile, "bike")) speed_ms = 4.2;
    else if (cab_ord_eq(profile, "foot")) speed_ms = 1.4;

    double dist_m = haversine_m(from_lat, from_lon, to_lat, to_lon);
    out->distance_km = dist_m / 1000.0;                    /* metres -> km */
    out->duration_ms = (int64_t)((dist_m / speed_ms) * 1000.0); /* seconds -> ms */

    out->polyline = (ca_int_route_point_t *)malloc(2 * sizeof(ca_int_route_point_t));
    if (!out->polyline) return -1;
    out->polyline[0].lat = from_lat;
    out->polyline[0].lon = from_lon;
    out->polyline[1].lat = to_lat;
    out->polyline[1].lon = to_lon;
    out->polyline_count = 2;
    return 0;
}

ca_int_routing_provider_t *ca_int_osrm_create(const char *host) {
    routing_impl_t *m = (routing_impl_t *)calloc(1, sizeof(routing_impl_t));
    if (!m) return NULL;
    m->host = cab_strdup_empty(host ? host : "https://router.project-osrm.org");
    if (!m->host) { free(m); return NULL; }

    ca_int_routing_provider_t *p =
        (ca_int_routing_provider_t *)calloc(1, sizeof(*p));
    if (!p) { free(m->host); free(m); return NULL; }
    p->impl        = m;
    p->provider_id = routing_provider_id;
    p->route       = routing_route;
    return p;
}

void ca_int_routing_provider_destroy(ca_int_routing_provider_t *p) {
    if (!p) return;
    routing_impl_t *m = (routing_impl_t *)p->impl;
    if (m) { free(m->host); free(m); }
    free(p);
}
