#ifndef CIRCLE_AI_WEARABLE_H
#define CIRCLE_AI_WEARABLE_H

/*
 * wearable.h — CircleAI.Wearable (C11 port of WearablePrimitives.cs).
 *
 *   Enums   : WearableKind { Smartwatch, FitnessBand, ChestStrap, Patch, Headset };
 *             WearableTelemetryKind { HeartRate, Steps, Calories, SleepStage,
 *                       SkinTempC, Stress, OxygenPct }.
 *   Records : WearableDevice(DeviceId, WearableKind Kind, Vendor, FirmwareVersion,
 *                       double BatteryPct);
 *             WearableSample(DeviceId, WearableTelemetryKind Kind, double Value,
 *                       DateTimeOffset AtUtc).
 *   Board   : IWearableBoard -> InMemoryWearableBoard
 *               Add (DeviceId keyed), GetDevice(id), Devices ordered by Vendor asc,
 *               Record (appends; unknown DeviceId throws), ReadSince(deviceId,
 *               kind, since) ascending by AtUtc, LatestValue(deviceId, kind) —
 *               newest Value (nullable), AverageValue(deviceId, kind, since) —
 *               mean Value over ReadSince (NaN when none).
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

typedef enum {
    CA_WEARABLE_KIND_SMARTWATCH = 0,
    CA_WEARABLE_KIND_FITNESS_BAND = 1,
    CA_WEARABLE_KIND_CHEST_STRAP = 2,
    CA_WEARABLE_KIND_PATCH = 3,
    CA_WEARABLE_KIND_HEADSET = 4
} ca_wearable_kind_t;

typedef enum {
    CA_WEARABLE_TELEMETRY_HEART_RATE = 0,
    CA_WEARABLE_TELEMETRY_STEPS = 1,
    CA_WEARABLE_TELEMETRY_CALORIES = 2,
    CA_WEARABLE_TELEMETRY_SLEEP_STAGE = 3,
    CA_WEARABLE_TELEMETRY_SKIN_TEMP_C = 4,
    CA_WEARABLE_TELEMETRY_STRESS = 5,
    CA_WEARABLE_TELEMETRY_OXYGEN_PCT = 6
} ca_wearable_telemetry_kind_t;

/* WearableDevice(DeviceId, WearableKind Kind, Vendor, FirmwareVersion,
 * double BatteryPct). */
typedef struct {
    char   *device_id;        /* owned, non-null */
    ca_wearable_kind_t kind;
    char   *vendor;           /* owned, non-null */
    char   *firmware_version; /* owned, non-null */
    double  battery_pct;
} ca_wearable_device_t;

void ca_wearable_device_free(ca_wearable_device_t *d);
void ca_wearable_device_free_array(ca_wearable_device_t *arr, size_t count);

/* WearableSample(DeviceId, WearableTelemetryKind Kind, double Value,
 * DateTimeOffset AtUtc). */
typedef struct {
    char   *device_id; /* owned, non-null */
    ca_wearable_telemetry_kind_t kind;
    double  value;
    int64_t at_utc_ms;
} ca_wearable_sample_t;

void ca_wearable_sample_free(ca_wearable_sample_t *s);
void ca_wearable_sample_free_array(ca_wearable_sample_t *arr, size_t count);

typedef struct ca_wearable_board ca_wearable_board_t;

ca_wearable_board_t *ca_wearable_board_create(void); /* NULL on OOM */
void ca_wearable_board_destroy(ca_wearable_board_t *b);

/* Add(d) — DeviceId keyed set. 0 / -1. */
int ca_wearable_board_add(ca_wearable_board_t *b, const ca_wearable_device_t *d);

/* GetDevice(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_wearable_board_get_device(const ca_wearable_board_t *b, const char *id,
                                  ca_wearable_device_t *out);

/* Devices -> fresh owned array ordered by Vendor asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_wearable_device_t *ca_wearable_board_devices(const ca_wearable_board_t *b,
                                                size_t *out_count);

/* Record(s) — appends. 0 on success, -1 on bad args, -2 when the DeviceId is
 * unknown (C# InvalidOperationException). */
int ca_wearable_board_record(ca_wearable_board_t *b,
                             const ca_wearable_sample_t *s);

/* ReadSince(deviceId, kind, since_ms) -> fresh owned array ascending by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_wearable_sample_t *ca_wearable_board_read_since(
    const ca_wearable_board_t *b, const char *device_id,
    ca_wearable_telemetry_kind_t kind, int64_t since_ms, size_t *out_count);

/* LatestValue(deviceId, kind) -> writes the newest Value into *out_value and
 * returns true; false (the C# null) when no such sample / bad args. */
bool ca_wearable_board_latest_value(const ca_wearable_board_t *b,
                                    const char *device_id,
                                    ca_wearable_telemetry_kind_t kind,
                                    double *out_value);

/* AverageValue(deviceId, kind, since_ms) — mean Value over ReadSince; NaN when
 * there are none (mirrors items.Count == 0 ? double.NaN). */
double ca_wearable_board_average_value(const ca_wearable_board_t *b,
                                       const char *device_id,
                                       ca_wearable_telemetry_kind_t kind,
                                       int64_t since_ms);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WEARABLE_H */
