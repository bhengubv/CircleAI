/*
 * observer.c — CircleAI.Observer (C11 port).
 *
 * Deterministic synchronous perceive-reason-act loop (no background thread — the
 * host drives ticks). Sensors record their latest reading and fan out to
 * subscribers; the toolbox is a keyed tool registry; the loop collects the latest
 * readings, calls the reason callback, invokes the returned tools, and fans an
 * ObservationTick to subscribers. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/observer.h"
#include "board_common.h"

/* ── shared kv helpers ──────────────────────────────────────────────────── */

static void okv_free(ca_observer_kv_t *kv, size_t n) {
    if (!kv) return;
    for (size_t i = 0; i < n; ++i) { free(kv[i].key); free(kv[i].value); }
    free(kv);
}
static bool okv_copy(ca_observer_kv_t **out, const ca_observer_kv_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    ca_observer_kv_t *v = (ca_observer_kv_t *)calloc(n, sizeof(*v));
    if (!v) return false;
    for (size_t i = 0; i < n; ++i) {
        v[i].key   = cab_strdup_empty(src ? src[i].key : NULL);
        v[i].value = cab_strdup_empty(src ? src[i].value : NULL);
        if (!v[i].key || !v[i].value) { okv_free(v, i + 1); return false; }
    }
    *out = v;
    return true;
}

/* ── SensorReading ──────────────────────────────────────────────────────── */

void ca_sensor_reading_free(ca_sensor_reading_t *r) {
    if (!r) return;
    free(r->sensor_id);
    free(r->kind);
    okv_free(r->values, r->value_count);
    free(r->payload);
    memset(r, 0, sizeof(*r));
}
void ca_sensor_reading_free_array(ca_sensor_reading_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_sensor_reading_free(&arr[i]);
    free(arr);
}
static bool reading_copy(ca_sensor_reading_t *dst, const ca_sensor_reading_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->captured_at_utc_ms = src->captured_at_utc_ms;
    dst->sensor_id = cab_strdup_empty(src->sensor_id);
    dst->kind      = cab_strdup_empty(src->kind);
    if (!dst->sensor_id || !dst->kind) { ca_sensor_reading_free(dst); return false; }
    if (!okv_copy(&dst->values, src->values, src->value_count)) {
        ca_sensor_reading_free(dst); return false;
    }
    dst->value_count = src->value_count;
    if (src->payload && src->payload_len > 0) {
        dst->payload = (uint8_t *)malloc(src->payload_len);
        if (!dst->payload) { ca_sensor_reading_free(dst); return false; }
        memcpy(dst->payload, src->payload, src->payload_len);
        dst->payload_len = src->payload_len;
    }
    return true;
}

/* ── ObservationTick ────────────────────────────────────────────────────── */

void ca_observation_tick_free(ca_observation_tick_t *t) {
    if (!t) return;
    ca_sensor_reading_free_array(t->perceived, t->perceived_count);
    free(t->reasoning);
    cab_strv_free(t->tools_invoked, t->tool_count);
    memset(t, 0, sizeof(*t));
}

/* ── ObservationTool ────────────────────────────────────────────────────── */

void ca_observation_tool_free(ca_observation_tool_t *t) {
    if (!t) return;
    free(t->tool_id);
    free(t->description);
    cab_strv_free(t->tags, t->tag_count);
    t->tool_id = t->description = NULL; t->tags = NULL; t->tag_count = 0;
    t->invoke = NULL; t->invoke_user = NULL;
}
void ca_observation_tool_free_array(ca_observation_tool_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_observation_tool_free(&arr[i]);
    free(arr);
}
static bool tool_copy(ca_observation_tool_t *dst, const ca_observation_tool_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->invoke = src->invoke;
    dst->invoke_user = src->invoke_user;
    dst->tool_id = cab_strdup_empty(src->tool_id);
    dst->description = cab_strdup_empty(src->description);
    if (!dst->tool_id || !dst->description) { ca_observation_tool_free(dst); return false; }
    if (!cab_strv_copy(&dst->tags, src->tags, src->tag_count)) {
        ca_observation_tool_free(dst); return false;
    }
    dst->tag_count = src->tag_count;
    return true;
}

/* ── RecordingSensor ────────────────────────────────────────────────────── */

typedef struct { int token; ca_sensor_handler_fn fn; void *user; } sensor_sub_t;

struct ca_sensor {
    char               *sensor_id;
    char               *kind;
    ca_sensor_reading_t latest;
    bool                has_latest;
    sensor_sub_t       *subs;
    size_t              sub_count, sub_cap;
    int                 next_token;
};

ca_sensor_t *ca_sensor_create(const char *sensor_id, const char *kind) {
    ca_sensor_t *s = (ca_sensor_t *)calloc(1, sizeof(ca_sensor_t));
    if (!s) return NULL;
    s->sensor_id = cab_strdup_empty(sensor_id ? sensor_id : "");
    s->kind = cab_strdup_empty(kind ? kind : "");
    if (!s->sensor_id || !s->kind) { ca_sensor_destroy(s); return NULL; }
    return s;
}
void ca_sensor_destroy(ca_sensor_t *s) {
    if (!s) return;
    free(s->sensor_id);
    free(s->kind);
    if (s->has_latest) ca_sensor_reading_free(&s->latest);
    free(s->subs);
    free(s);
}
const char *ca_sensor_sensor_id(const ca_sensor_t *s) { return s ? s->sensor_id : NULL; }
const char *ca_sensor_kind(const ca_sensor_t *s) { return s ? s->kind : NULL; }
const char *ca_sensor_backend_id(const ca_sensor_t *s) { (void)s; return "recording"; }

int ca_sensor_push(ca_sensor_t *s, const ca_sensor_reading_t *reading) {
    if (!s || !reading) return -1;
    ca_sensor_reading_t copy;
    if (!reading_copy(&copy, reading)) return -1;
    if (s->has_latest) ca_sensor_reading_free(&s->latest);
    s->latest = copy;
    s->has_latest = true;
    /* fan out (subscribers get a borrowed view of the stored latest) */
    for (size_t i = 0; i < s->sub_count; ++i)
        if (s->subs[i].fn) s->subs[i].fn(s->subs[i].user, &s->latest);
    return 0;
}
bool ca_sensor_latest(const ca_sensor_t *s, ca_sensor_reading_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || !out || !s->has_latest) return false;
    return reading_copy(out, &s->latest);
}
int ca_sensor_subscribe(ca_sensor_t *s, ca_sensor_handler_fn handler, void *user) {
    if (!s || !handler) return -1;
    if (s->sub_count == s->sub_cap) {
        size_t nc = s->sub_cap ? s->sub_cap * 2 : 4;
        void *n = realloc(s->subs, nc * sizeof(sensor_sub_t));
        if (!n) return -1;
        s->subs = (sensor_sub_t *)n; s->sub_cap = nc;
    }
    int token = s->next_token++;
    s->subs[s->sub_count].token = token;
    s->subs[s->sub_count].fn = handler;
    s->subs[s->sub_count].user = user;
    s->sub_count++;
    return token;
}
void ca_sensor_unsubscribe(ca_sensor_t *s, int token) {
    if (!s) return;
    for (size_t i = 0; i < s->sub_count; ++i) {
        if (s->subs[i].token == token) {
            s->subs[i] = s->subs[s->sub_count - 1];
            s->sub_count--;
            return;
        }
    }
}

const char *ca_observer_null_sensor_backend_id(void) { return "null"; }

/* ── InMemoryObservationToolbox ─────────────────────────────────────────── */

struct ca_observation_toolbox {
    ca_observation_tool_t *items; size_t count, cap;
};

ca_observation_toolbox_t *ca_observation_toolbox_create(void) {
    return (ca_observation_toolbox_t *)calloc(1, sizeof(ca_observation_toolbox_t));
}
void ca_observation_toolbox_destroy(ca_observation_toolbox_t *tb) {
    if (!tb) return;
    for (size_t i = 0; i < tb->count; ++i) ca_observation_tool_free(&tb->items[i]);
    free(tb->items);
    free(tb);
}
const char *ca_observation_toolbox_backend_id(const ca_observation_toolbox_t *tb) {
    (void)tb; return "in-memory";
}
int ca_observation_toolbox_register(ca_observation_toolbox_t *tb,
                                    const ca_observation_tool_t *tool) {
    if (!tb || !tool || cab_is_ws(tool->tool_id)) return -1;
    for (size_t i = 0; i < tb->count; ++i) {
        if (cab_ord_eq(tb->items[i].tool_id, tool->tool_id)) {
            ca_observation_tool_t copy;
            if (!tool_copy(&copy, tool)) return -1;
            ca_observation_tool_free(&tb->items[i]);
            tb->items[i] = copy;
            return 0;
        }
    }
    ca_observation_tool_t copy;
    if (!tool_copy(&copy, tool)) return -1;
    if (tb->count == tb->cap) {
        size_t nc = tb->cap ? tb->cap * 2 : 4;
        void *n = realloc(tb->items, nc * sizeof(*tb->items));
        if (!n) { ca_observation_tool_free(&copy); return -1; }
        tb->items = (ca_observation_tool_t *)n; tb->cap = nc;
    }
    tb->items[tb->count++] = copy;
    return 0;
}
bool ca_observation_toolbox_try_get(const ca_observation_toolbox_t *tb,
                                    const char *tool_id, ca_observation_tool_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!tb || cab_is_ws(tool_id) || !out) return false;
    for (size_t i = 0; i < tb->count; ++i)
        if (cab_ord_eq(tb->items[i].tool_id, tool_id))
            return tool_copy(out, &tb->items[i]);
    return false;
}
ca_observation_tool_t *ca_observation_toolbox_list(const ca_observation_toolbox_t *tb,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!tb) { *out_count = (size_t)-1; return NULL; }
    if (tb->count == 0) { *out_count = 0; return NULL; }
    ca_observation_tool_t *out = (ca_observation_tool_t *)calloc(tb->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < tb->count; ++i) {
        if (!tool_copy(&out[i], &tb->items[i])) {
            ca_observation_tool_free_array(out, i);
            *out_count = (size_t)-1; return NULL;
        }
    }
    *out_count = tb->count;
    return out;
}

/* Find a tool by id (borrowed pointer into the toolbox), or NULL. */
static const ca_observation_tool_t *toolbox_find(const ca_observation_toolbox_t *tb,
                                                 const char *tool_id) {
    for (size_t i = 0; i < tb->count; ++i)
        if (cab_ord_eq(tb->items[i].tool_id, tool_id)) return &tb->items[i];
    return NULL;
}

/* ── InMemoryObservationLoop ────────────────────────────────────────────── */

typedef struct { int token; ca_observation_tick_fn fn; void *user; } tick_sub_t;

struct ca_observation_loop {
    ca_sensor_t             **sensors;   /* borrowed array (copied pointers) */
    size_t                    sensor_count;
    ca_observation_toolbox_t *toolbox;   /* borrowed */
    ca_observer_reason_fn     reason;
    void                     *reason_user;
    bool                      running;
    tick_sub_t               *subs;
    size_t                    sub_count, sub_cap;
    int                       next_token;
};

ca_observation_loop_t *ca_observation_loop_create(ca_sensor_t *const *sensors,
                                                  size_t sensor_count,
                                                  ca_observation_toolbox_t *toolbox,
                                                  ca_observer_reason_fn reason,
                                                  void *reason_user) {
    if (!toolbox || !reason || (!sensors && sensor_count > 0)) return NULL;
    ca_observation_loop_t *loop = (ca_observation_loop_t *)calloc(1, sizeof(*loop));
    if (!loop) return NULL;
    if (sensor_count > 0) {
        loop->sensors = (ca_sensor_t **)calloc(sensor_count, sizeof(ca_sensor_t *));
        if (!loop->sensors) { free(loop); return NULL; }
        for (size_t i = 0; i < sensor_count; ++i) loop->sensors[i] = sensors[i];
    }
    loop->sensor_count = sensor_count;
    loop->toolbox = toolbox;
    loop->reason = reason;
    loop->reason_user = reason_user;
    return loop;
}
void ca_observation_loop_destroy(ca_observation_loop_t *loop) {
    if (!loop) return;
    free(loop->sensors);
    free(loop->subs);
    free(loop);
}
const char *ca_observation_loop_backend_id(const ca_observation_loop_t *loop) {
    (void)loop; return "in-memory";
}

int ca_observation_loop_start(ca_observation_loop_t *loop) {
    if (!loop) return -1;
    if (loop->running) return -1; /* already started */
    loop->running = true;
    return 0;
}
int ca_observation_loop_stop(ca_observation_loop_t *loop) {
    if (!loop) return -1;
    loop->running = false;
    return 0;
}
bool ca_observation_loop_is_running(const ca_observation_loop_t *loop) {
    return loop && loop->running;
}

int ca_observation_loop_subscribe(ca_observation_loop_t *loop,
                                  ca_observation_tick_fn handler, void *user) {
    if (!loop || !handler) return -1;
    if (loop->sub_count == loop->sub_cap) {
        size_t nc = loop->sub_cap ? loop->sub_cap * 2 : 4;
        void *n = realloc(loop->subs, nc * sizeof(tick_sub_t));
        if (!n) return -1;
        loop->subs = (tick_sub_t *)n; loop->sub_cap = nc;
    }
    int token = loop->next_token++;
    loop->subs[loop->sub_count].token = token;
    loop->subs[loop->sub_count].fn = handler;
    loop->subs[loop->sub_count].user = user;
    loop->sub_count++;
    return token;
}
void ca_observation_loop_unsubscribe(ca_observation_loop_t *loop, int token) {
    if (!loop) return;
    for (size_t i = 0; i < loop->sub_count; ++i) {
        if (loop->subs[i].token == token) {
            loop->subs[i] = loop->subs[loop->sub_count - 1];
            loop->sub_count--;
            return;
        }
    }
}

int ca_observation_loop_tick(ca_observation_loop_t *loop, int64_t at_utc_ms) {
    if (!loop) return -1;
    if (!loop->running) return 0;

    /* Perceive: collect latest reading from each sensor that has one. */
    ca_sensor_reading_t *readings = NULL;
    size_t rc = 0;
    if (loop->sensor_count > 0) {
        readings = (ca_sensor_reading_t *)calloc(loop->sensor_count, sizeof(*readings));
        if (!readings) return -1;
        for (size_t i = 0; i < loop->sensor_count; ++i) {
            ca_sensor_reading_t r;
            if (ca_sensor_latest(loop->sensors[i], &r)) {
                readings[rc++] = r;
            }
        }
    }

    /* Reason. */
    char  *reasoning = NULL;
    char **tools = NULL; size_t tools_n = 0;
    ca_observer_kv_t *args = NULL; size_t args_n = 0;
    int rr = loop->reason(loop->reason_user, readings, rc, &reasoning,
                          &tools, &tools_n, &args, &args_n);
    if (rr != 0) {
        /* reasoner failed — skip the tick */
        ca_sensor_reading_free_array(readings, rc);
        free(reasoning);
        cab_strv_free(tools, tools_n);
        okv_free(args, args_n);
        return 0;
    }
    if (!reasoning) reasoning = cab_strdup_empty("");

    /* Act: invoke each tool that resolves; record successes. */
    char **invoked = NULL; size_t inv_n = 0;
    if (tools_n > 0) {
        invoked = (char **)calloc(tools_n, sizeof(char *));
        if (!invoked) {
            ca_sensor_reading_free_array(readings, rc);
            free(reasoning); cab_strv_free(tools, tools_n); okv_free(args, args_n);
            return -1;
        }
        for (size_t i = 0; i < tools_n; ++i) {
            const ca_observation_tool_t *tool = toolbox_find(loop->toolbox, tools[i]);
            if (!tool || !tool->invoke) continue;
            int trc = tool->invoke(tool->invoke_user, args, args_n);
            if (trc == 0) {
                invoked[inv_n] = cab_strdup_empty(tools[i]);
                if (!invoked[inv_n]) {
                    cab_strv_free(invoked, inv_n);
                    ca_sensor_reading_free_array(readings, rc);
                    free(reasoning); cab_strv_free(tools, tools_n); okv_free(args, args_n);
                    return -1;
                }
                inv_n++;
            }
        }
    }
    cab_strv_free(tools, tools_n);
    okv_free(args, args_n);

    /* Build the tick + fan out. */
    ca_observation_tick_t tick;
    memset(&tick, 0, sizeof(tick));
    tick.at_utc_ms = at_utc_ms;
    tick.perceived = readings;      /* transfer ownership */
    tick.perceived_count = rc;
    tick.reasoning = reasoning;     /* transfer ownership */
    tick.tools_invoked = inv_n ? invoked : (free(invoked), NULL);
    tick.tool_count = inv_n;

    for (size_t i = 0; i < loop->sub_count; ++i)
        if (loop->subs[i].fn) loop->subs[i].fn(loop->subs[i].user, &tick);

    ca_observation_tick_free(&tick);
    return 1;
}

const char *ca_observer_null_loop_backend_id(void) { return "null"; }
