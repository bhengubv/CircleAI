#ifndef CIRCLE_AI_AMBIENT_H
#define CIRCLE_AI_AMBIENT_H

/*
 * ambient.h — CircleAI.Ambient (C11 port of AmbientPrimitives.cs).
 *
 *   Records : AmbientReading(DeviceId, double TemperatureC, double Humidity,
 *                       double LuxLight, double DbNoise, DateTimeOffset AtUtc);
 *             AmbientPreference(Location, double TargetTempC, double
 *                       TargetHumidity, double MaxNoiseDb).
 *   Board   : IAmbientBoard -> InMemoryAmbientBoard
 *               Record (appends), Latest(deviceId) — newest reading (nullable),
 *               History(deviceId, limit=50) newest-first by AtUtc top-limit,
 *               SetPreference (Location keyed), GetPreference(location),
 *               IsComfortable(deviceId, location) — true when a preference and a
 *               latest reading exist and |Temp - Target| <= 2, |Humidity -
 *               Target| <= 10, and DbNoise <= MaxNoiseDb.
 *
 * DateTimeOffset as Unix ms UTC.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* AmbientReading(DeviceId, double TemperatureC, double Humidity, double LuxLight,
 * double DbNoise, DateTimeOffset AtUtc). */
typedef struct {
    char   *device_id; /* owned, non-null */
    double  temperature_c;
    double  humidity;
    double  lux_light;
    double  db_noise;
    int64_t at_utc_ms;
} ca_ambient_reading_t;

void ca_ambient_reading_free(ca_ambient_reading_t *r);
void ca_ambient_reading_free_array(ca_ambient_reading_t *arr, size_t count);

/* AmbientPreference(Location, double TargetTempC, double TargetHumidity,
 * double MaxNoiseDb). */
typedef struct {
    char   *location; /* owned, non-null */
    double  target_temp_c;
    double  target_humidity;
    double  max_noise_db;
} ca_ambient_preference_t;

void ca_ambient_preference_free(ca_ambient_preference_t *p);

typedef struct ca_ambient_board ca_ambient_board_t;

ca_ambient_board_t *ca_ambient_board_create(void); /* NULL on OOM */
void ca_ambient_board_destroy(ca_ambient_board_t *b);

/* Record(r) — appends. 0 / -1. */
int ca_ambient_board_record(ca_ambient_board_t *b,
                            const ca_ambient_reading_t *r);

/* Latest(deviceId) -> writes the newest reading into *out and returns true; false
 * (the C# null) when the device has no readings / bad args. */
bool ca_ambient_board_latest(const ca_ambient_board_t *b, const char *device_id,
                             ca_ambient_reading_t *out);

/* History(deviceId, limit) -> fresh owned array newest-first by AtUtc, first
 * `limit`. limit must be > 0 (SIZE_MAX on limit<=0 / bad args). Use 50 for the C#
 * default. NULL + 0 empty. */
ca_ambient_reading_t *ca_ambient_board_history(const ca_ambient_board_t *b,
                                               const char *device_id, int limit,
                                               size_t *out_count);

/* SetPreference(p) — Location keyed set. 0 / -1. */
int ca_ambient_board_set_preference(ca_ambient_board_t *b,
                                    const ca_ambient_preference_t *p);

/* GetPreference(location) -> fresh owned copy into *out, true; false on miss. */
bool ca_ambient_board_get_preference(const ca_ambient_board_t *b,
                                     const char *location,
                                     ca_ambient_preference_t *out);

/* IsComfortable(deviceId, location) — true when a preference + latest reading
 * exist and all comfort bounds hold; false otherwise. */
bool ca_ambient_board_is_comfortable(const ca_ambient_board_t *b,
                                     const char *device_id,
                                     const char *location);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AMBIENT_H */
