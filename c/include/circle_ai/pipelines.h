#ifndef CIRCLE_AI_PIPELINES_H
#define CIRCLE_AI_PIPELINES_H

/*
 * pipelines.h — CircleAI.Pipelines (C11 port of Contracts.cs +
 * InMemoryPipelines.cs + NullImplementations.cs). Data-pipeline surface.
 *
 *   Records : PipelineRecord(Stream, IReadOnlyDictionary<string,object?> Values);
 *             PipelineRun(RunId, PipelineId, DateTimeOffset StartUtc,
 *                         DateTimeOffset? EndUtc, long RowsProcessed,
 *                         string? FailureReason);
 *             DatabaseQueryResult(rows, RowCount).
 *   object?  : tagged value (null/string/int/double/bool), as host_tools_ui.
 *   Sources  : IPipelineSource -> InMemoryPipelineSource — per-stream unbounded
 *                FIFO: Push(stream,record), Complete(stream), ReadAsync(stream)
 *                modelled as a drain cursor (read_next until completed & empty).
 *                BackendId "in-memory". Null source yields nothing.
 *   Sinks    : IPipelineSink -> InMemoryPipelineSink — Write appends, Flush
 *                no-op, Records snapshot. BackendId "in-memory". Null sink drops.
 *   Executor : IPipelineExecutor -> InMemoryPipelineExecutor — Register(id,fn),
 *                RunAsync(id) mints "run-{n}", runs the fn (capturing rows +
 *                failure message), stores + returns the PipelineRun; unknown id
 *                is an error. GetRunAsync(runId) -> run?. BackendId "in-memory".
 *                Null executor -> failed run "NullPipelineExecutor".
 *   DB       : IDatabaseQueryTool -> InMemoryDatabaseQueryTool — Insert(table,
 *                row); QueryAsync only supports "SELECT * FROM <table>"
 *                (case-insensitive), returning the table's rows (empty when the
 *                table is unknown). Non-SELECT / missing-FROM are errors.
 *                BackendId "in-memory". Null DB -> empty result.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Nullable
 * via has_*. Start/End as int64 Unix ms UTC. Linear arrays, no pthreads.
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── tagged object? value ───────────────────────────────────────────────── */

typedef enum {
    CA_PIPE_VAL_NULL   = 0,
    CA_PIPE_VAL_STRING = 1,
    CA_PIPE_VAL_INT    = 2,
    CA_PIPE_VAL_DOUBLE = 3,
    CA_PIPE_VAL_BOOL   = 4
} ca_pipe_value_kind_t;

typedef struct {
    char                *key;  /* owned, non-null */
    ca_pipe_value_kind_t kind;
    char                *s;    /* owned when STRING */
    int64_t              i;    /* when INT */
    double               d;    /* when DOUBLE */
    bool                 b;    /* when BOOL */
} ca_pipe_field_t;

/* PipelineRecord(Stream, Values). Values is a parallel field array. */
typedef struct {
    char            *stream;      /* owned, non-null */
    ca_pipe_field_t *fields;      /* owned array (field_count) */
    size_t           field_count;
} ca_pipe_record_t;

void ca_pipe_record_free(ca_pipe_record_t *r);
void ca_pipe_record_free_array(ca_pipe_record_t *arr, size_t count);

/* PipelineRun(RunId, PipelineId, StartUtc, EndUtc?, RowsProcessed,
 * FailureReason?). */
typedef struct {
    char   *run_id;              /* owned, non-null */
    char   *pipeline_id;         /* owned, non-null */
    int64_t start_utc_ms;
    bool    has_end_utc;         /* false == C# null EndUtc */
    int64_t end_utc_ms;
    int64_t rows_processed;
    bool    has_failure_reason;  /* false == C# null FailureReason */
    char   *failure_reason;      /* owned, valid only when has_* */
} ca_pipe_run_t;

void ca_pipe_run_free(ca_pipe_run_t *r);

/* ── IPipelineSource ────────────────────────────────────────────────────── */

typedef struct ca_pipe_source ca_pipe_source_t;

ca_pipe_source_t *ca_pipe_source_create(void); /* NULL on OOM */
void ca_pipe_source_destroy(ca_pipe_source_t *s);
const char *ca_pipe_source_backend_id(const ca_pipe_source_t *s); /* "in-memory" */

/* Push(stream, record) — appends to the stream's FIFO. 0 / -1 (bad args/OOM;
 * pushing to a completed stream is rejected -> -1). */
int ca_pipe_source_push(ca_pipe_source_t *s, const char *stream,
                        const ca_pipe_record_t *record);
/* Complete(stream) — marks the stream done (no more records). 0 / -1. */
int ca_pipe_source_complete(ca_pipe_source_t *s, const char *stream);
/* ReadAsync drain: pop the next record from the stream into *out (freshly
 * owned; free with ca_pipe_record_free). true if produced. When false, check
 * ca_pipe_source_is_drained to distinguish "empty, more coming" from "done". */
bool ca_pipe_source_read_next(ca_pipe_source_t *s, const char *stream,
                              ca_pipe_record_t *out);
/* True once the stream is Completed AND its FIFO is empty (ReadAsync ends). */
bool ca_pipe_source_is_drained(const ca_pipe_source_t *s, const char *stream);

/* Null source: read yields nothing (always drained). */
const char *ca_pipe_null_source_backend_id(void); /* "null" */

/* ── IPipelineSink ──────────────────────────────────────────────────────── */

typedef struct ca_pipe_sink ca_pipe_sink_t;

ca_pipe_sink_t *ca_pipe_sink_create(void); /* NULL on OOM */
void ca_pipe_sink_destroy(ca_pipe_sink_t *s);
const char *ca_pipe_sink_backend_id(const ca_pipe_sink_t *s); /* "in-memory" */

/* Write(record) — appends. 0 / -1. */
int ca_pipe_sink_write(ca_pipe_sink_t *s, const ca_pipe_record_t *record);
/* Flush — no-op. 0. */
int ca_pipe_sink_flush(ca_pipe_sink_t *s);
/* Records -> fresh owned array (*out_count) in write order. NULL + 0 empty;
 * NULL + SIZE_MAX on error. */
ca_pipe_record_t *ca_pipe_sink_records(const ca_pipe_sink_t *s, size_t *out_count);

const char *ca_pipe_null_sink_backend_id(void); /* "null" */

/* ── IPipelineExecutor ──────────────────────────────────────────────────── */

/* Pipeline runner: does the work, writes rows-processed into *rows_out; returns
 * 0 on success, -1 to fail the run (with the message copied into fail_msg, at
 * most fail_msg_cap-1 chars). */
typedef int (*ca_pipe_runner_fn)(void *ctx, int64_t *rows_out, char *fail_msg,
                                 size_t fail_msg_cap);

typedef struct ca_pipe_executor ca_pipe_executor_t;

ca_pipe_executor_t *ca_pipe_executor_create(void); /* NULL on OOM */
void ca_pipe_executor_destroy(ca_pipe_executor_t *e);
const char *ca_pipe_executor_backend_id(const ca_pipe_executor_t *e); /* "in-memory" */

/* Register(pipelineId, runner). 0 / -1 on bad args/OOM. */
int ca_pipe_executor_register(ca_pipe_executor_t *e, const char *pipeline_id,
                              ca_pipe_runner_fn runner, void *ctx);
/* RunAsync(pipelineId) -> fill *out (owned; free with ca_pipe_run_free). 0 on
 * success (even when the runner fails — the failure is captured in the run),
 * -1 on bad args / unknown pipeline / OOM. now_ms is the deterministic clock
 * used for StartUtc/EndUtc. */
int ca_pipe_executor_run(ca_pipe_executor_t *e, const char *pipeline_id,
                         int64_t now_ms, ca_pipe_run_t *out);
/* GetRunAsync(runId) -> fresh copy into *out, true; false on miss/bad args. */
bool ca_pipe_executor_get_run(const ca_pipe_executor_t *e, const char *run_id,
                              ca_pipe_run_t *out);

/* Null executor: run -> failed run {RunId "00000000-0000-0000-0000-000000000000",
 * FailureReason "NullPipelineExecutor"}. 0 / -1 on bad args/OOM. */
const char *ca_pipe_null_executor_backend_id(void); /* "null" */
int  ca_pipe_null_executor_run(const char *pipeline_id, ca_pipe_run_t *out);

/* ── IDatabaseQueryTool ─────────────────────────────────────────────────── */

/* DatabaseQueryResult(Rows, RowCount) — rows as a parallel field-array table. */
typedef struct {
    ca_pipe_field_t *fields;   /* owned array (field_count) */
    size_t           field_count;
} ca_pipe_row_t;

typedef struct {
    ca_pipe_row_t *rows;     /* owned array (row_count) */
    size_t         row_count;
} ca_pipe_query_result_t;

void ca_pipe_query_result_free(ca_pipe_query_result_t *r);

typedef struct ca_pipe_db ca_pipe_db_t;

ca_pipe_db_t *ca_pipe_db_create(void); /* NULL on OOM */
void ca_pipe_db_destroy(ca_pipe_db_t *db);
const char *ca_pipe_db_backend_id(const ca_pipe_db_t *db); /* "in-memory" */

/* Insert(tableName, row) — table names are case-insensitive. 0 / -1. row is a
 * parallel field array of length field_count. */
int ca_pipe_db_insert(ca_pipe_db_t *db, const char *table_name,
                      const ca_pipe_field_t *fields, size_t field_count);
/* QueryAsync(sql) -> fill *out. Only "SELECT * FROM <table>" is supported
 * (case-insensitive). Unknown table -> empty result (RowCount 0). 0 on success,
 * -1 on bad args (null/empty sql, non-SELECT, missing FROM) or OOM. */
int ca_pipe_db_query(const ca_pipe_db_t *db, const char *sql,
                     ca_pipe_query_result_t *out);

/* Null DB: query -> empty result. */
const char *ca_pipe_null_db_backend_id(void); /* "null" */

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PIPELINES_H */
