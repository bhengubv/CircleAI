/*
 * pipelines.c — CircleAI.Pipelines (C11 port).
 *
 * Source: per-stream unbounded FIFO with a completed flag; ReadAsync is a
 * drain cursor. Sink: append list. Executor: registered runner table + run
 * store, RunAsync mints "run-{n}" and captures rows/failure. DB: tables of
 * rows (tagged-value fields) with a tiny "SELECT * FROM <table>" parser.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/pipelines.h"
#include "board_common.h"

#include <stdio.h>

/* ── field (tagged value) copy / free ───────────────────────────────────── */

static void field_free(ca_pipe_field_t *f) {
    if (!f) return;
    free(f->key);
    free(f->s);
    f->key = f->s = NULL;
}
static void fields_free(ca_pipe_field_t *fields, size_t n) {
    if (!fields) return;
    for (size_t i = 0; i < n; ++i) field_free(&fields[i]);
    free(fields);
}
static bool field_copy(ca_pipe_field_t *dst, const ca_pipe_field_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->kind = src->kind;
    dst->i = src->i; dst->d = src->d; dst->b = src->b;
    dst->key = cab_strdup_empty(src->key);
    if (!dst->key) return false;
    if (src->kind == CA_PIPE_VAL_STRING) {
        dst->s = cab_strdup_empty(src->s);
        if (!dst->s) { field_free(dst); return false; }
    }
    return true;
}
/* Deep-copy a field array. *out set (NULL when n==0). false on OOM. */
static bool fields_copy(ca_pipe_field_t **out, const ca_pipe_field_t *src,
                        size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_pipe_field_t *v = (ca_pipe_field_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        if (!field_copy(&v[i], &src[i])) { fields_free(v, i); return false; }
    }
    *out = v;
    return true;
}

/* ── PipelineRecord ─────────────────────────────────────────────────────── */

void ca_pipe_record_free(ca_pipe_record_t *r) {
    if (!r) return;
    free(r->stream);
    fields_free(r->fields, r->field_count);
    r->stream = NULL;
    r->fields = NULL;
    r->field_count = 0;
}
void ca_pipe_record_free_array(ca_pipe_record_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_pipe_record_free(&arr[i]);
    free(arr);
}
static bool record_copy(ca_pipe_record_t *dst, const ca_pipe_record_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->stream = cab_strdup_empty(src->stream);
    if (!dst->stream) return false;
    if (!fields_copy(&dst->fields, src->fields, src->field_count)) {
        free(dst->stream);
        dst->stream = NULL;
        return false;
    }
    dst->field_count = src->field_count;
    return true;
}

/* ── PipelineRun ────────────────────────────────────────────────────────── */

void ca_pipe_run_free(ca_pipe_run_t *r) {
    if (!r) return;
    free(r->run_id);
    free(r->pipeline_id);
    free(r->failure_reason);
    r->run_id = r->pipeline_id = r->failure_reason = NULL;
    r->has_end_utc = r->has_failure_reason = false;
}
static bool run_copy(ca_pipe_run_t *dst, const ca_pipe_run_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->start_utc_ms   = src->start_utc_ms;
    dst->has_end_utc    = src->has_end_utc;
    dst->end_utc_ms     = src->end_utc_ms;
    dst->rows_processed = src->rows_processed;
    dst->run_id      = cab_strdup_empty(src->run_id);
    dst->pipeline_id = cab_strdup_empty(src->pipeline_id);
    bool ok = dst->run_id && dst->pipeline_id;
    if (ok && src->has_failure_reason) {
        dst->failure_reason = cab_strdup_empty(src->failure_reason);
        ok = dst->failure_reason != NULL;
        dst->has_failure_reason = ok;
    }
    if (!ok) { ca_pipe_run_free(dst); return false; }
    return true;
}

/* ── IPipelineSource ────────────────────────────────────────────────────── */

typedef struct {
    char             *name;      /* owned */
    ca_pipe_record_t *buf;       /* FIFO of owned records */
    size_t            head, count, cap;
    bool              completed;
} source_stream_t;

struct ca_pipe_source {
    source_stream_t *streams;
    size_t           count, cap;
};

ca_pipe_source_t *ca_pipe_source_create(void) {
    return (ca_pipe_source_t *)calloc(1, sizeof(ca_pipe_source_t));
}
static void source_stream_free(source_stream_t *st) {
    for (size_t i = 0; i < st->count; ++i)
        ca_pipe_record_free(&st->buf[(st->head + i) % st->cap]);
    free(st->buf);
    free(st->name);
}
void ca_pipe_source_destroy(ca_pipe_source_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) source_stream_free(&s->streams[i]);
    free(s->streams);
    free(s);
}
const char *ca_pipe_source_backend_id(const ca_pipe_source_t *s) {
    (void)s; return "in-memory";
}

/* GetOrAdd stream. NULL on OOM. */
static source_stream_t *source_get_or_add(ca_pipe_source_t *s,
                                          const char *stream) {
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->streams[i].name, stream)) return &s->streams[i];
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->streams, nc * sizeof(*s->streams));
        if (!n) return NULL;
        s->streams = (source_stream_t *)n;
        s->cap = nc;
    }
    source_stream_t *st = &s->streams[s->count];
    memset(st, 0, sizeof(*st));
    st->name = cab_strdup(stream);
    if (!st->name) return NULL;
    s->count++;
    return st;
}
static source_stream_t *source_find(const ca_pipe_source_t *s,
                                    const char *stream) {
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->streams[i].name, stream)) return &s->streams[i];
    return NULL;
}

int ca_pipe_source_push(ca_pipe_source_t *s, const char *stream,
                        const ca_pipe_record_t *record) {
    if (!s || cab_is_ws(stream) || !record) return -1;
    source_stream_t *st = source_get_or_add(s, stream);
    if (!st) return -1;
    if (st->completed) return -1; /* writer completed -> TryWrite would fail */
    if (st->count == st->cap) {
        size_t nc = st->cap ? st->cap * 2 : 4;
        ca_pipe_record_t *nb = (ca_pipe_record_t *)calloc(nc, sizeof(*nb));
        if (!nb) return -1;
        for (size_t i = 0; i < st->count; ++i)
            nb[i] = st->buf[(st->head + i) % st->cap];
        free(st->buf);
        st->buf = nb;
        st->cap = nc;
        st->head = 0;
    }
    ca_pipe_record_t copy;
    if (!record_copy(&copy, record)) return -1;
    st->buf[(st->head + st->count) % st->cap] = copy;
    st->count++;
    return 0;
}

int ca_pipe_source_complete(ca_pipe_source_t *s, const char *stream) {
    if (!s || !stream) return -1;
    source_stream_t *st = source_find(s, stream);
    if (st) st->completed = true; /* TryComplete no-ops on an unknown stream */
    return 0;
}

bool ca_pipe_source_read_next(ca_pipe_source_t *s, const char *stream,
                              ca_pipe_record_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(stream) || !out) return false;
    source_stream_t *st = source_get_or_add(s, stream);
    if (!st || st->count == 0) return false;
    *out = st->buf[st->head];
    st->head = (st->head + 1) % st->cap;
    st->count--;
    return true;
}

bool ca_pipe_source_is_drained(const ca_pipe_source_t *s, const char *stream) {
    if (!s || !stream) return false;
    source_stream_t *st = source_find(s, stream);
    if (!st) return false; /* never created -> ReadAsync would block, not end */
    return st->completed && st->count == 0;
}

const char *ca_pipe_null_source_backend_id(void) { return "null"; }

/* ── IPipelineSink ──────────────────────────────────────────────────────── */

struct ca_pipe_sink {
    ca_pipe_record_t *records;
    size_t            count, cap;
};

ca_pipe_sink_t *ca_pipe_sink_create(void) {
    return (ca_pipe_sink_t *)calloc(1, sizeof(ca_pipe_sink_t));
}
void ca_pipe_sink_destroy(ca_pipe_sink_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_pipe_record_free(&s->records[i]);
    free(s->records);
    free(s);
}
const char *ca_pipe_sink_backend_id(const ca_pipe_sink_t *s) {
    (void)s; return "in-memory";
}

int ca_pipe_sink_write(ca_pipe_sink_t *s, const ca_pipe_record_t *record) {
    if (!s || !record) return -1;
    ca_pipe_record_t copy;
    if (!record_copy(&copy, record)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->records, nc * sizeof(*s->records));
        if (!n) { ca_pipe_record_free(&copy); return -1; }
        s->records = (ca_pipe_record_t *)n;
        s->cap = nc;
    }
    s->records[s->count++] = copy;
    return 0;
}
int ca_pipe_sink_flush(ca_pipe_sink_t *s) { return s ? 0 : -1; }

ca_pipe_record_t *ca_pipe_sink_records(const ca_pipe_sink_t *s,
                                       size_t *out_count) {
    if (!out_count) return NULL;
    if (!s) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    ca_pipe_record_t *out = (ca_pipe_record_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!record_copy(&out[i], &s->records[i])) {
            ca_pipe_record_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

const char *ca_pipe_null_sink_backend_id(void) { return "null"; }

/* ── IPipelineExecutor ──────────────────────────────────────────────────── */

typedef struct {
    char             *id;       /* owned */
    ca_pipe_runner_fn runner;
    void             *ctx;
} pipeline_entry_t;

struct ca_pipe_executor {
    pipeline_entry_t *pipelines;
    size_t            p_count, p_cap;
    ca_pipe_run_t    *runs;      /* owned */
    size_t            r_count, r_cap;
    long              run_seq;
};

ca_pipe_executor_t *ca_pipe_executor_create(void) {
    return (ca_pipe_executor_t *)calloc(1, sizeof(ca_pipe_executor_t));
}
void ca_pipe_executor_destroy(ca_pipe_executor_t *e) {
    if (!e) return;
    for (size_t i = 0; i < e->p_count; ++i) free(e->pipelines[i].id);
    for (size_t i = 0; i < e->r_count; ++i) ca_pipe_run_free(&e->runs[i]);
    free(e->pipelines);
    free(e->runs);
    free(e);
}
const char *ca_pipe_executor_backend_id(const ca_pipe_executor_t *e) {
    (void)e; return "in-memory";
}

int ca_pipe_executor_register(ca_pipe_executor_t *e, const char *pipeline_id,
                              ca_pipe_runner_fn runner, void *ctx) {
    if (!e || cab_is_ws(pipeline_id) || !runner) return -1;
    for (size_t i = 0; i < e->p_count; ++i) {
        if (cab_ord_eq(e->pipelines[i].id, pipeline_id)) {
            e->pipelines[i].runner = runner;
            e->pipelines[i].ctx = ctx;
            return 0;
        }
    }
    if (e->p_count == e->p_cap) {
        size_t nc = e->p_cap ? e->p_cap * 2 : 4;
        void *n = realloc(e->pipelines, nc * sizeof(*e->pipelines));
        if (!n) return -1;
        e->pipelines = (pipeline_entry_t *)n;
        e->p_cap = nc;
    }
    pipeline_entry_t *pe = &e->pipelines[e->p_count];
    pe->id = cab_strdup(pipeline_id);
    if (!pe->id) return -1;
    pe->runner = runner;
    pe->ctx = ctx;
    e->p_count++;
    return 0;
}

static int executor_store_run(ca_pipe_executor_t *e, const ca_pipe_run_t *run) {
    if (e->r_count == e->r_cap) {
        size_t nc = e->r_cap ? e->r_cap * 2 : 4;
        void *n = realloc(e->runs, nc * sizeof(*e->runs));
        if (!n) return -1;
        e->runs = (ca_pipe_run_t *)n;
        e->r_cap = nc;
    }
    ca_pipe_run_t copy;
    if (!run_copy(&copy, run)) return -1;
    e->runs[e->r_count++] = copy;
    return 0;
}

int ca_pipe_executor_run(ca_pipe_executor_t *e, const char *pipeline_id,
                         int64_t now_ms, ca_pipe_run_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!e || cab_is_ws(pipeline_id) || !out) return -1;
    pipeline_entry_t *pe = NULL;
    for (size_t i = 0; i < e->p_count; ++i)
        if (cab_ord_eq(e->pipelines[i].id, pipeline_id)) { pe = &e->pipelines[i]; break; }
    if (!pe) return -1; /* InvalidOperationException: unknown pipeline */

    int64_t rows = 0;
    char fail[256]; fail[0] = '\0';
    int rc = pe->runner(pe->ctx, &rows, fail, sizeof(fail));

    char run_id[32];
    snprintf(run_id, sizeof(run_id), "run-%ld", ++e->run_seq);

    ca_pipe_run_t run;
    memset(&run, 0, sizeof(run));
    run.run_id = run_id;
    run.pipeline_id = (char *)pipeline_id;
    run.start_utc_ms = now_ms;
    run.has_end_utc = true;
    run.end_utc_ms = now_ms;
    run.rows_processed = (rc == 0) ? rows : 0;
    if (rc != 0) {
        run.has_failure_reason = true;
        run.failure_reason = fail[0] ? fail : (char *)"pipeline failed";
    }

    if (executor_store_run(e, &run) != 0) return -1;
    return run_copy(out, &run) ? 0 : -1;
}

bool ca_pipe_executor_get_run(const ca_pipe_executor_t *e, const char *run_id,
                              ca_pipe_run_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!e || cab_is_ws(run_id) || !out) return false;
    for (size_t i = 0; i < e->r_count; ++i)
        if (cab_ord_eq(e->runs[i].run_id, run_id))
            return run_copy(out, &e->runs[i]);
    return false;
}

const char *ca_pipe_null_executor_backend_id(void) { return "null"; }

int ca_pipe_null_executor_run(const char *pipeline_id, ca_pipe_run_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return -1;
    ca_pipe_run_t run;
    memset(&run, 0, sizeof(run));
    run.run_id = (char *)"00000000-0000-0000-0000-000000000000"; /* Guid.Empty */
    run.pipeline_id = (char *)(pipeline_id ? pipeline_id : "");
    run.start_utc_ms = INT64_MIN; /* DateTimeOffset.MinValue surrogate */
    run.has_end_utc = true;
    run.end_utc_ms = INT64_MIN;
    run.rows_processed = 0;
    run.has_failure_reason = true;
    run.failure_reason = (char *)"NullPipelineExecutor";
    return run_copy(out, &run) ? 0 : -1;
}

/* ── IDatabaseQueryTool ─────────────────────────────────────────────────── */

void ca_pipe_query_result_free(ca_pipe_query_result_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->row_count; ++i)
        fields_free(r->rows[i].fields, r->rows[i].field_count);
    free(r->rows);
    r->rows = NULL;
    r->row_count = 0;
}

typedef struct {
    ca_pipe_field_t *fields;
    size_t           field_count;
} db_row_t;

typedef struct {
    char     *name;   /* owned */
    db_row_t *rows;    /* owned */
    size_t    count, cap;
} db_table_t;

struct ca_pipe_db {
    db_table_t *tables;
    size_t      count, cap;
};

ca_pipe_db_t *ca_pipe_db_create(void) {
    return (ca_pipe_db_t *)calloc(1, sizeof(ca_pipe_db_t));
}
void ca_pipe_db_destroy(ca_pipe_db_t *db) {
    if (!db) return;
    for (size_t i = 0; i < db->count; ++i) {
        db_table_t *t = &db->tables[i];
        for (size_t r = 0; r < t->count; ++r)
            fields_free(t->rows[r].fields, t->rows[r].field_count);
        free(t->rows);
        free(t->name);
    }
    free(db->tables);
    free(db);
}
const char *ca_pipe_db_backend_id(const ca_pipe_db_t *db) {
    (void)db; return "in-memory";
}

/* GetOrAdd table (case-insensitive). NULL on OOM. */
static db_table_t *db_get_or_add(ca_pipe_db_t *db, const char *name) {
    for (size_t i = 0; i < db->count; ++i)
        if (cab_ci_eq(db->tables[i].name, name)) return &db->tables[i];
    if (db->count == db->cap) {
        size_t nc = db->cap ? db->cap * 2 : 4;
        void *n = realloc(db->tables, nc * sizeof(*db->tables));
        if (!n) return NULL;
        db->tables = (db_table_t *)n;
        db->cap = nc;
    }
    db_table_t *t = &db->tables[db->count];
    memset(t, 0, sizeof(*t));
    t->name = cab_strdup(name);
    if (!t->name) return NULL;
    db->count++;
    return t;
}
/* `name` is a slice of length name_len (not NUL-terminated). Case-insensitive. */
static const db_table_t *db_find(const ca_pipe_db_t *db, const char *name,
                                 size_t name_len) {
    for (size_t i = 0; i < db->count; ++i) {
        const char *tn = db->tables[i].name;
        if (strlen(tn) != name_len) continue;
        bool eq = true;
        for (size_t k = 0; k < name_len; ++k)
            if (tolower((unsigned char)tn[k]) != tolower((unsigned char)name[k])) { eq = false; break; }
        if (eq) return &db->tables[i];
    }
    return NULL;
}

int ca_pipe_db_insert(ca_pipe_db_t *db, const char *table_name,
                      const ca_pipe_field_t *fields, size_t field_count) {
    if (!db || cab_is_ws(table_name) || (field_count > 0 && !fields)) return -1;
    db_table_t *t = db_get_or_add(db, table_name);
    if (!t) return -1;
    ca_pipe_field_t *row_fields = NULL;
    if (!fields_copy(&row_fields, fields, field_count)) return -1;
    if (t->count == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 4;
        void *n = realloc(t->rows, nc * sizeof(*t->rows));
        if (!n) { fields_free(row_fields, field_count); return -1; }
        t->rows = (db_row_t *)n;
        t->cap = nc;
    }
    t->rows[t->count].fields = row_fields;
    t->rows[t->count].field_count = field_count;
    t->count++;
    return 0;
}

/* Skip leading ASCII whitespace. */
static const char *skip_ws(const char *p) {
    while (*p && isspace((unsigned char)*p)) p++;
    return p;
}

int ca_pipe_db_query(const ca_pipe_db_t *db, const char *sql,
                     ca_pipe_query_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!db || cab_is_ws(sql) || !out) return -1;

    const char *p = skip_ws(sql);
    /* Must start with "SELECT " (case-insensitive). */
    static const char *SELECT = "SELECT ";
    for (size_t i = 0; SELECT[i]; ++i) {
        if (p[i] == '\0' ||
            tolower((unsigned char)p[i]) != tolower((unsigned char)SELECT[i]))
            return -1;
    }

    /* Find "FROM " (case-insensitive) anywhere after. */
    const char *from = NULL;
    for (const char *q = p; *q; ++q) {
        if ((q[0]=='F'||q[0]=='f') && (q[1]=='R'||q[1]=='r') &&
            (q[2]=='O'||q[2]=='o') && (q[3]=='M'||q[3]=='m') && q[4]==' ') {
            from = q; break;
        }
    }
    if (!from) return -1; /* SELECT requires a FROM clause */

    const char *rest = skip_ws(from + 5); /* after "FROM " */
    /* table name = up to first ' ' or ';'. */
    size_t name_len = 0;
    while (rest[name_len] && rest[name_len] != ' ' && rest[name_len] != ';')
        name_len++;
    if (name_len == 0) return -1;

    const db_table_t *t = db_find(db, rest, name_len);
    if (!t || t->count == 0) {
        /* unknown table -> empty result (RowCount 0) */
        out->rows = NULL;
        out->row_count = 0;
        return 0;
    }

    ca_pipe_row_t *rows = (ca_pipe_row_t *)calloc(t->count, sizeof(*rows));
    if (!rows) return -1;
    for (size_t i = 0; i < t->count; ++i) {
        if (!fields_copy(&rows[i].fields, t->rows[i].fields, t->rows[i].field_count)) {
            for (size_t k = 0; k < i; ++k) fields_free(rows[k].fields, rows[k].field_count);
            free(rows);
            return -1;
        }
        rows[i].field_count = t->rows[i].field_count;
    }
    out->rows = rows;
    out->row_count = t->count;
    return 0;
}

const char *ca_pipe_null_db_backend_id(void) { return "null"; }
