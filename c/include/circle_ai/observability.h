#ifndef CIRCLE_AI_OBSERVABILITY_H
#define CIRCLE_AI_OBSERVABILITY_H

/*
 * observability.h — CircleAI.Observability (C11 port of Contracts.cs +
 * InMemoryObservability.cs + NullImplementations.cs).
 *
 *   Records : MetricSample(Name, double Value, Tags?);
 *             TraceSpan(TraceId, SpanId, ParentSpanId?, Name, StartUtc,
 *                       TimeSpan Duration, Attributes?);
 *             DashboardSpec(DashboardId, Title, JsonBlob).
 *   Metric  : IMetricSink -> InMemoryMetricSink. Emit(sample) appends per Name
 *               (Name required); Read(name) insertion order; MetricNames sorted
 *               asc. BackendId "in-memory".
 *   Trace   : ITraceSink -> InMemoryTraceSink. Emit(span) appends per TraceId
 *               (TraceId required); Read(traceId) ordered by StartUtc asc.
 *               BackendId "in-memory".
 *   Dash    : IDashboardPublisher -> InMemoryDashboardPublisher. Publish(spec)
 *               keyed by DashboardId (replace, DashboardId required); Get(id);
 *               All sorted by DashboardId asc. BackendId "in-memory".
 *   Null variants drop everything and return empty/null.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Times as
 * int64 Unix ms UTC; Duration as int64 ms. Linear arrays, no pthreads. C11+libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Optional tag/attribute (key/value) pair. */
typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_obs_tag_t;

/* MetricSample(Name, Value, Tags?). */
typedef struct {
    char         *name;      /* owned, non-null */
    double        value;
    ca_obs_tag_t *tags;      /* owned; NULL when tag_count == 0 */
    size_t        tag_count;
} ca_metric_sample_t;

void ca_metric_sample_free(ca_metric_sample_t *m);
void ca_metric_sample_free_array(ca_metric_sample_t *arr, size_t count);

/* TraceSpan(TraceId, SpanId, ParentSpanId?, Name, StartUtc, Duration, Attrs?). */
typedef struct {
    char         *trace_id;        /* owned, non-null */
    char         *span_id;         /* owned, non-null */
    char         *parent_span_id;  /* owned, or NULL */
    char         *name;            /* owned, non-null */
    int64_t       start_utc_ms;
    int64_t       duration_ms;
    ca_obs_tag_t *attributes;      /* owned; NULL when attribute_count == 0 */
    size_t        attribute_count;
} ca_trace_span_t;

void ca_trace_span_free(ca_trace_span_t *s);
void ca_trace_span_free_array(ca_trace_span_t *arr, size_t count);

/* DashboardSpec(DashboardId, Title, JsonBlob). */
typedef struct {
    char *dashboard_id; /* owned, non-null */
    char *title;        /* owned, non-null */
    char *json_blob;    /* owned, non-null */
} ca_dashboard_spec_t;

void ca_dashboard_spec_free(ca_dashboard_spec_t *d);
void ca_dashboard_spec_free_array(ca_dashboard_spec_t *arr, size_t count);

/* ── IMetricSink -> InMemoryMetricSink ──────────────────────────────────── */

typedef struct ca_metric_sink ca_metric_sink_t;

ca_metric_sink_t *ca_metric_sink_create(void); /* NULL on OOM */
void ca_metric_sink_destroy(ca_metric_sink_t *s);
const char *ca_metric_sink_backend_id(const ca_metric_sink_t *s); /* "in-memory" */

/* Emit(sample) — appends under Name. 0 / -1 on bad args (null / empty Name) /OOM. */
int ca_metric_sink_emit(ca_metric_sink_t *s, const ca_metric_sample_t *sample);
/* Read(name) in insertion order. NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_metric_sample_t *ca_metric_sink_read(const ca_metric_sink_t *s,
                                        const char *name, size_t *out_count);
/* MetricNames — distinct names sorted asc. NULL + 0 empty; NULL+SIZE_MAX error. */
char **ca_metric_sink_names(const ca_metric_sink_t *s, size_t *out_count);

const char *ca_obs_null_metric_sink_backend_id(void); /* "null" */

/* ── ITraceSink -> InMemoryTraceSink ────────────────────────────────────── */

typedef struct ca_trace_sink ca_trace_sink_t;

ca_trace_sink_t *ca_trace_sink_create(void); /* NULL on OOM */
void ca_trace_sink_destroy(ca_trace_sink_t *s);
const char *ca_trace_sink_backend_id(const ca_trace_sink_t *s); /* "in-memory" */

/* Emit(span) — appends under TraceId. 0 / -1 on bad args (null/empty TraceId). */
int ca_trace_sink_emit(ca_trace_sink_t *s, const ca_trace_span_t *span);
/* Read(traceId) ordered by StartUtc asc. NULL + 0 empty; NULL+SIZE_MAX error. */
ca_trace_span_t *ca_trace_sink_read(const ca_trace_sink_t *s,
                                    const char *trace_id, size_t *out_count);

const char *ca_obs_null_trace_sink_backend_id(void); /* "null" */

/* ── IDashboardPublisher -> InMemoryDashboardPublisher ──────────────────── */

typedef struct ca_dashboard_publisher ca_dashboard_publisher_t;

ca_dashboard_publisher_t *ca_dashboard_publisher_create(void); /* NULL on OOM */
void ca_dashboard_publisher_destroy(ca_dashboard_publisher_t *p);
const char *ca_dashboard_publisher_backend_id(const ca_dashboard_publisher_t *p);

/* Publish(spec) — keyed by DashboardId (replace). 0 / -1 on bad args / OOM. */
int ca_dashboard_publisher_publish(ca_dashboard_publisher_t *p,
                                   const ca_dashboard_spec_t *spec);
/* Get(id) -> fresh copy into *out, true; false on miss / bad args. */
bool ca_dashboard_publisher_get(const ca_dashboard_publisher_t *p,
                                const char *id, ca_dashboard_spec_t *out);
/* All -> fresh owned array sorted by DashboardId asc. NULL+0 empty; SIZE_MAX err. */
ca_dashboard_spec_t *ca_dashboard_publisher_all(const ca_dashboard_publisher_t *p,
                                                size_t *out_count);

const char *ca_obs_null_dashboard_publisher_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_OBSERVABILITY_H */
