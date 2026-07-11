#ifndef CIRCLE_AI_OBSERVER_H
#define CIRCLE_AI_OBSERVER_H

/*
 * observer.h — CircleAI.Observer (C11 port of Contracts.cs + InMemoryObserver.cs
 * + NullImplementations.cs). The perceive-reason-act observation loop.
 *
 * The C# loop runs on a background Task.Delay timer; per the C conventions
 * (no pthreads) the loop here is DETERMINISTIC and SYNCHRONOUS: the host drives
 * ticks via ca_observation_loop_tick(), which collects the latest sensor
 * readings, calls the injected reason callback, invokes the returned tools via
 * the toolbox, produces an ObservationTick, and fans it out to subscribers.
 *
 *   Records : SensorReading(SensorId, Kind, CapturedAtUtc, Values{}, Payload?);
 *             ObservationTool(ToolId, Description, Tags[], Invoke);
 *             ObservationTick(AtUtc, Perceived[], Reasoning, ToolsInvoked[]);
 *             ObserverDecision(Reasoning, ToolsToInvoke[], ToolArgs?).
 *   Sensor  : ISensor -> RecordingSensor. Push(reading) stores the latest;
 *               Subscribe fans readings to handlers; Latest exposes the last one.
 *               NullSensor stores nothing. BackendId "recording" / "null".
 *   Toolbox : IObservationToolbox -> InMemoryObservationToolbox. RegisterTool;
 *               TryGet; ListTools. Tool Invoke is a host callback. BackendId
 *               "in-memory".
 *   Loop    : IObservationLoop -> InMemoryObservationLoop over a set of sensors +
 *               a toolbox + a reason callback. Start/Stop toggle a running flag;
 *               Tick() runs one perceive-reason-act cycle (only while started)
 *               and fans an ObservationTick to subscribers. BackendId "in-memory".
 *   Null loop: Start/Stop/Subscribe/Tick no-ops.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Times as
 * int64 Unix ms UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* key/value pair (SensorReading.Values / ObserverDecision.ToolArgs). */
typedef struct { char *key; char *value; } ca_observer_kv_t;

/* SensorReading(SensorId, Kind, CapturedAtUtc, Values{}, Payload?). */
typedef struct {
    char             *sensor_id;      /* owned, non-null */
    char             *kind;           /* owned, non-null */
    int64_t           captured_at_utc_ms;
    ca_observer_kv_t *values;         /* owned; NULL when value_count == 0 */
    size_t            value_count;
    uint8_t          *payload;        /* owned, or NULL */
    size_t            payload_len;
} ca_sensor_reading_t;

void ca_sensor_reading_free(ca_sensor_reading_t *r);
void ca_sensor_reading_free_array(ca_sensor_reading_t *arr, size_t count);

/* ObservationTick(AtUtc, Perceived[], Reasoning, ToolsInvoked[]). */
typedef struct {
    int64_t              at_utc_ms;
    ca_sensor_reading_t *perceived;     /* owned; NULL when perceived_count == 0 */
    size_t               perceived_count;
    char                *reasoning;     /* owned, non-null */
    char               **tools_invoked; /* owned; NULL when tool_count == 0 */
    size_t               tool_count;
} ca_observation_tick_t;

void ca_observation_tick_free(ca_observation_tick_t *t);

/* ── ISensor -> RecordingSensor ─────────────────────────────────────────── */

typedef struct ca_sensor ca_sensor_t;

/* Create a recording sensor with a fixed SensorId + Kind. NULL on OOM. */
ca_sensor_t *ca_sensor_create(const char *sensor_id, const char *kind);
void ca_sensor_destroy(ca_sensor_t *s);
const char *ca_sensor_sensor_id(const ca_sensor_t *s);
const char *ca_sensor_kind(const ca_sensor_t *s);
const char *ca_sensor_backend_id(const ca_sensor_t *s); /* "recording" */

/* Push(reading) — stores the latest and fans out to subscribers. 0 / -1. */
int ca_sensor_push(ca_sensor_t *s, const ca_sensor_reading_t *reading);
/* Latest -> fresh copy of the last reading into *out, true; false when none. */
bool ca_sensor_latest(const ca_sensor_t *s, ca_sensor_reading_t *out);
/* Subscribe a handler (called on each Push). Returns a token id (>= 0) for
 * ca_sensor_unsubscribe, or -1 on bad args / OOM. */
typedef void (*ca_sensor_handler_fn)(void *user, const ca_sensor_reading_t *reading);
int ca_sensor_subscribe(ca_sensor_t *s, ca_sensor_handler_fn handler, void *user);
void ca_sensor_unsubscribe(ca_sensor_t *s, int token);

const char *ca_observer_null_sensor_backend_id(void); /* "null" */

/* ── IObservationToolbox -> InMemoryObservationToolbox ──────────────────── */

/* Tool invocation callback: args are the ToolArgs map; returns 0 on success,
 * non-zero to signal a tool error (the loop skips failed tools). */
typedef int (*ca_observation_tool_fn)(void *user, const ca_observer_kv_t *args,
                                      size_t arg_count);

/* ObservationTool(ToolId, Description, Tags[], Invoke). */
typedef struct {
    char                  *tool_id;     /* owned, non-null */
    char                  *description; /* owned, non-null */
    char                 **tags;        /* owned; NULL when tag_count == 0 */
    size_t                 tag_count;
    ca_observation_tool_fn invoke;      /* borrowed callback */
    void                  *invoke_user; /* borrowed */
} ca_observation_tool_t;

void ca_observation_tool_free(ca_observation_tool_t *t);
void ca_observation_tool_free_array(ca_observation_tool_t *arr, size_t count);

typedef struct ca_observation_toolbox ca_observation_toolbox_t;

ca_observation_toolbox_t *ca_observation_toolbox_create(void); /* NULL on OOM */
void ca_observation_toolbox_destroy(ca_observation_toolbox_t *tb);
const char *ca_observation_toolbox_backend_id(const ca_observation_toolbox_t *tb);

/* RegisterTool(tool) — keyed by ToolId (replace). 0 / -1. */
int ca_observation_toolbox_register(ca_observation_toolbox_t *tb,
                                    const ca_observation_tool_t *tool);
/* TryGet(toolId) -> fresh copy into *out, true; false on miss / bad args. */
bool ca_observation_toolbox_try_get(const ca_observation_toolbox_t *tb,
                                    const char *tool_id, ca_observation_tool_t *out);
/* ListTools() insertion order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_observation_tool_t *ca_observation_toolbox_list(const ca_observation_toolbox_t *tb,
                                                   size_t *out_count);

/* ── IObservationLoop -> InMemoryObservationLoop ────────────────────────── */

/* Reason callback: given the perceived readings, fills *reasoning_out (a heap
 * string the loop takes ownership of) and *tools_out (a heap array of ToolId
 * strings, count in *tools_count_out, loop takes ownership) plus optional
 * *args_out / *args_count_out (loop takes ownership). Returns 0 on success;
 * non-zero to signal a reasoning failure (the loop skips the tick). */
typedef int (*ca_observer_reason_fn)(void *user,
                                     const ca_sensor_reading_t *perceived,
                                     size_t perceived_count,
                                     char **reasoning_out,
                                     char ***tools_out, size_t *tools_count_out,
                                     ca_observer_kv_t **args_out, size_t *args_count_out);

typedef struct ca_observation_loop ca_observation_loop_t;

/* Create a loop over `sensors` (borrowed; must outlive the loop), a toolbox
 * (borrowed), and a reason callback. NULL on bad args / OOM. */
ca_observation_loop_t *ca_observation_loop_create(ca_sensor_t *const *sensors,
                                                  size_t sensor_count,
                                                  ca_observation_toolbox_t *toolbox,
                                                  ca_observer_reason_fn reason,
                                                  void *reason_user);
void ca_observation_loop_destroy(ca_observation_loop_t *loop);
const char *ca_observation_loop_backend_id(const ca_observation_loop_t *loop);

/* Start() / Stop() toggle the running flag. 0 / -1. Start after Start is -1
 * (mirrors the "already started" guard). */
int ca_observation_loop_start(ca_observation_loop_t *loop);
int ca_observation_loop_stop(ca_observation_loop_t *loop);
bool ca_observation_loop_is_running(const ca_observation_loop_t *loop);

/* Subscribe a tick handler. Returns a token (>= 0), or -1 on bad args / OOM. */
typedef void (*ca_observation_tick_fn)(void *user, const ca_observation_tick_t *tick);
int ca_observation_loop_subscribe(ca_observation_loop_t *loop,
                                  ca_observation_tick_fn handler, void *user);
void ca_observation_loop_unsubscribe(ca_observation_loop_t *loop, int token);

/* Tick(atUtcMs) — runs ONE perceive-reason-act cycle (only while running) and
 * fans an ObservationTick to subscribers. Returns 1 when a tick was produced,
 * 0 when skipped (not running / reasoner failed), -1 on bad args / OOM. */
int ca_observation_loop_tick(ca_observation_loop_t *loop, int64_t at_utc_ms);

const char *ca_observer_null_loop_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_OBSERVER_H */
