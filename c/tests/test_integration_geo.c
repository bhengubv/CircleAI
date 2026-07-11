/*
 * test_integration_geo.c — CircleAI.Integration.Geo (C11 port) verification of
 * the WMO decode table, the OSRM mode->profile mapping, and the in-memory
 * OpenMeteo weather + Osrm routing providers.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_wmo_decode(void) {
    assert(strcmp(ca_int_open_meteo_wmo_decode(0), "clear sky") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(1), "partly cloudy") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(3), "partly cloudy") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(45), "fog") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(48), "fog") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(51), "drizzle") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(56), "freezing drizzle") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(61), "rain") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(66), "freezing rain") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(71), "snow") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(77), "snow grains") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(80), "rain showers") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(85), "snow showers") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(95), "thunderstorm") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(96), "thunderstorm with hail") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(99), "thunderstorm with hail") == 0);
    assert(strcmp(ca_int_open_meteo_wmo_decode(12345), "unknown") == 0);
    printf("  wmo_decode: ok\n");
}

static void test_osrm_profile(void) {
    assert(strcmp(ca_int_osrm_profile("bike"), "bike") == 0);
    assert(strcmp(ca_int_osrm_profile("bicycle"), "bike") == 0);
    assert(strcmp(ca_int_osrm_profile("foot"), "foot") == 0);
    assert(strcmp(ca_int_osrm_profile("walk"), "foot") == 0);
    assert(strcmp(ca_int_osrm_profile("car"), "driving") == 0);
    assert(strcmp(ca_int_osrm_profile("anything"), "driving") == 0);
    assert(strcmp(ca_int_osrm_profile(NULL), "driving") == 0);
    printf("  osrm_profile: ok\n");
}

static void test_weather_provider(void) {
    ca_int_weather_provider_t *p = ca_int_open_meteo_create();
    assert(p);
    assert(strcmp(p->provider_id(p->impl), "open-meteo") == 0);

    ca_int_weather_sample_t cur;
    assert(p->current(p->impl, 51.5, -0.12, &cur) == 0);
    assert(cur.condition != NULL && cur.condition[0] != '\0');
    ca_int_weather_sample_free(&cur);

    /* hourly bounds: 0 and >168 -> error. */
    size_t n = 0;
    assert(p->hourly(p->impl, 0, 0, 0, &n) == NULL && n == (size_t)-1);
    assert(p->hourly(p->impl, 0, 0, 169, &n) == NULL && n == (size_t)-1);

    ca_int_weather_sample_t *arr = p->hourly(p->impl, 51.5, -0.12, 24, &n);
    assert(n == 24 && arr != NULL);
    for (size_t i = 0; i < n; ++i)
        assert(arr[i].condition != NULL);
    /* determinism: same inputs -> same first condition. */
    ca_int_weather_sample_t cur2;
    assert(p->current(p->impl, 51.5, -0.12, &cur2) == 0);
    assert(strcmp(cur2.condition, arr[0].condition) == 0);
    ca_int_weather_sample_free(&cur2);
    ca_int_weather_sample_free_array(arr, n);

    ca_int_weather_provider_destroy(p);
    printf("  weather_provider: ok\n");
}

static void test_routing_provider(void) {
    ca_int_routing_provider_t *p = ca_int_osrm_create(NULL);
    assert(p);
    assert(strcmp(p->provider_id(p->impl), "osrm") == 0);

    /* London -> Paris-ish; distance > 300 km, polyline endpoints preserved. */
    ca_int_route_estimate_t car;
    assert(p->route(p->impl, 51.5, -0.12, 48.85, 2.35, "car", &car) == 0);
    assert(car.distance_km > 300.0 && car.distance_km < 500.0);
    assert(car.polyline_count == 2);
    assert(car.polyline[0].lat == 51.5 && car.polyline[1].lat == 48.85);
    assert(car.duration_ms > 0);

    /* foot is slower than car over the same leg -> larger duration. */
    ca_int_route_estimate_t foot;
    assert(p->route(p->impl, 51.5, -0.12, 48.85, 2.35, "foot", &foot) == 0);
    assert(foot.duration_ms > car.duration_ms);
    assert(foot.distance_km == car.distance_km); /* same geometry */

    /* NULL mode == car. */
    ca_int_route_estimate_t def;
    assert(p->route(p->impl, 51.5, -0.12, 48.85, 2.35, NULL, &def) == 0);
    assert(def.duration_ms == car.duration_ms);

    ca_int_route_estimate_free(&car);
    ca_int_route_estimate_free(&foot);
    ca_int_route_estimate_free(&def);
    ca_int_routing_provider_destroy(p);
    printf("  routing_provider: ok\n");
}

int main(void) {
    test_wmo_decode();
    test_osrm_profile();
    test_weather_provider();
    test_routing_provider();
    printf("test_integration_geo: all assertions passed\n");
    return 0;
}
