#ifndef CIRCLE_AI_INTEGRATION_GEO_H
#define CIRCLE_AI_INTEGRATION_GEO_H

/*
 * integration_geo.h — CircleAI.Integration.Geo (C11 port).
 *
 * Deterministic in-memory IWeatherProvider (OpenMeteoWeatherProvider) and
 * IRoutingProvider (OsrmRoutingProvider). The real providers call Open-Meteo /
 * OSRM over an injected HttpClient; here the samples/route are synthesised
 * deterministically from the inputs (the network being the injected dependency),
 * while the load-bearing pure logic is ported faithfully:
 *
 *   Weather  ProviderId "open-meteo".
 *            WmoDecode(code) : Open-Meteo WMO weather-code -> condition string,
 *              ported exactly (see ca_int_open_meteo_wmo_decode).
 *            Current(lat,lon)      : one sample (WindKph = m/s * 3.6).
 *            Hourly(lat,lon,hours) : `hours` samples; hours<=0 || hours>168 ->
 *              ArgumentOutOfRangeException (NULL + SIZE_MAX).
 *   Routing  ProviderId "osrm".
 *            mode -> profile : "bike"/"bicycle" -> bike, "foot"/"walk" -> foot,
 *              else -> driving (ported exactly; see ca_int_osrm_profile).
 *            Route(...) : DistanceKm (metres/1000), Duration (TimeSpan), and a
 *              polyline (endpoints as (Lat,Lon)).
 *
 * Conventions per integration.h. No pthreads. Pure C11 + libc (+ libm).
 */

#include <stdbool.h>

#include "integration.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Open-Meteo WMO weather-code decode (ported from OpenMeteoWeatherProvider.
 * WmoDecode). Returns a borrowed static string; never NULL. */
const char *ca_int_open_meteo_wmo_decode(int code);

/* OSRM mode->profile mapping (ported from OsrmRoutingProvider.RouteAsync).
 * Returns a borrowed static string: "bike", "foot", or "driving". mode NULL ->
 * "driving". */
const char *ca_int_osrm_profile(const char *mode);

/* Create the in-memory Open-Meteo weather provider (ProviderId "open-meteo").
 * NULL on OOM. Destroy with ca_int_weather_provider_destroy. */
ca_int_weather_provider_t *ca_int_open_meteo_create(void);
void ca_int_weather_provider_destroy(ca_int_weather_provider_t *p);

/* Create the in-memory OSRM routing provider (ProviderId "osrm"). host defaults
 * to "https://router.project-osrm.org" when NULL (kept for parity; unused by the
 * in-memory estimate). NULL on OOM. Destroy with ca_int_routing_provider_destroy. */
ca_int_routing_provider_t *ca_int_osrm_create(const char *host);
void ca_int_routing_provider_destroy(ca_int_routing_provider_t *p);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INTEGRATION_GEO_H */
