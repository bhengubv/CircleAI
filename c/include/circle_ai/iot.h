#ifndef CIRCLE_AI_IOT_H
#define CIRCLE_AI_IOT_H

/*
 * iot.h — CircleAI.IoT (C11 port of IoTPrimitives.cs).
 *
 *   Records : IoTDevice(DeviceId, Name, Kind, FirmwareVersion,
 *                       DateTimeOffset LastSeenUtc);
 *             IoTTelemetry(DeviceId, Metric, double Value, DateTimeOffset AtUtc);
 *             IoTCommand(CommandId, DeviceId, Action, ArgumentsJson,
 *                        DateTimeOffset SentUtc).
 *   Board   : IIoTBoard -> InMemoryIoTBoard
 *               Register (DeviceId keyed), GetDevice(id) -> device?,
 *               Devices ordered by Name asc, RecordTelemetry (appends),
 *               LatestValue(deviceId, metric) — newest by AtUtc, NaN if none,
 *               History(deviceId, metric, limit=100) newest-first, Take(limit),
 *               SendCommand (appends), CommandsFor(deviceId) newest-first by
 *               SentUtc.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. *Utc as
 * int64 Unix ms UTC. NaN via <math.h>. Linear arrays, no pthreads. Pure C11.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* IoTDevice(DeviceId, Name, Kind, FirmwareVersion, DateTimeOffset LastSeenUtc). */
typedef struct {
    char   *device_id;        /* owned, non-null */
    char   *name;             /* owned, non-null */
    char   *kind;             /* owned, non-null */
    char   *firmware_version; /* owned, non-null */
    int64_t last_seen_utc_ms;
} ca_iot_device_t;

void ca_iot_device_free(ca_iot_device_t *d);
void ca_iot_device_free_array(ca_iot_device_t *arr, size_t count);

/* IoTTelemetry(DeviceId, Metric, double Value, DateTimeOffset AtUtc). */
typedef struct {
    char   *device_id; /* owned, non-null */
    char   *metric;    /* owned, non-null */
    double  value;
    int64_t at_utc_ms;
} ca_iot_telemetry_t;

void ca_iot_telemetry_free(ca_iot_telemetry_t *t);
void ca_iot_telemetry_free_array(ca_iot_telemetry_t *arr, size_t count);

/* IoTCommand(CommandId, DeviceId, Action, ArgumentsJson,
 * DateTimeOffset SentUtc). */
typedef struct {
    char   *command_id;    /* owned, non-null */
    char   *device_id;     /* owned, non-null */
    char   *action;        /* owned, non-null */
    char   *arguments_json;/* owned, non-null */
    int64_t sent_utc_ms;
} ca_iot_command_t;

void ca_iot_command_free(ca_iot_command_t *c);
void ca_iot_command_free_array(ca_iot_command_t *arr, size_t count);

typedef struct ca_iot_board ca_iot_board_t;

ca_iot_board_t *ca_iot_board_create(void); /* NULL on OOM */
void ca_iot_board_destroy(ca_iot_board_t *b);

/* Register(d) — DeviceId keyed set. 0 / -1 on bad args/OOM. */
int ca_iot_board_register(ca_iot_board_t *b, const ca_iot_device_t *d);

/* GetDevice(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_iot_board_get_device(const ca_iot_board_t *b, const char *id,
                             ca_iot_device_t *out);

/* Devices -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_iot_device_t *ca_iot_board_devices(const ca_iot_board_t *b,
                                      size_t *out_count);

/* RecordTelemetry(t) — appends. 0 / -1. */
int ca_iot_board_record_telemetry(ca_iot_board_t *b,
                                  const ca_iot_telemetry_t *t);

/* LatestValue(deviceId, metric) -> newest (by AtUtc) Value; NaN when none. */
double ca_iot_board_latest_value(const ca_iot_board_t *b, const char *device_id,
                                 const char *metric);

/* History(deviceId, metric, limit) -> fresh owned array (*out_count) newest-first
 * by AtUtc, Take(limit). NULL + 0 when empty; NULL + SIZE_MAX on error
 * (limit <= 0). */
ca_iot_telemetry_t *ca_iot_board_history(const ca_iot_board_t *b,
                                         const char *device_id,
                                         const char *metric, int limit,
                                         size_t *out_count);

/* SendCommand(c) — appends. 0 / -1. */
int ca_iot_board_send_command(ca_iot_board_t *b, const ca_iot_command_t *c);

/* CommandsFor(deviceId) -> fresh owned array (*out_count) newest-first by
 * SentUtc. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_iot_command_t *ca_iot_board_commands_for(const ca_iot_board_t *b,
                                            const char *device_id,
                                            size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_IOT_H */
