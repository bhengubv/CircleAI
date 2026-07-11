/*
 * workflows.c — CircleAI.Workflows (C11 port).
 *
 * In-memory definition + state stores (keyed dictionaries). Runner is an
 * injected vtable with a Null default. PacaConversationRuntime drives the
 * conversation state machine synchronously: Queued -> Running, the executor
 * emits steps through a sink callback, then Finished / Failed / Stopped.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/workflows.h"
#include "board_common.h"

/* ── byte-blob helper ───────────────────────────────────────────────────── */

static bool bytes_copy(uint8_t **out, const uint8_t *src, size_t n) {
    *out = NULL;
    if (n == 0) return true;
    uint8_t *b = (uint8_t *)malloc(n);
    if (!b) return false;
    if (src) memcpy(b, src, n);
    else memset(b, 0, n);
    *out = b;
    return true;
}

/* ── WorkflowDefinition ─────────────────────────────────────────────────── */

void ca_wf_definition_free(ca_wf_definition_t *d) {
    if (!d) return;
    free(d->definition_id);
    free(d->name);
    free(d->version);
    free(d->description);
    d->definition_id = d->name = d->version = d->description = NULL;
}
static bool definition_copy(ca_wf_definition_t *dst,
                            const ca_wf_definition_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->definition_id = cab_strdup_empty(src->definition_id);
    dst->name          = cab_strdup_empty(src->name);
    dst->version       = cab_strdup_empty(src->version);
    dst->description   = cab_strdup_empty(src->description);
    if (!dst->definition_id || !dst->name || !dst->version || !dst->description) {
        ca_wf_definition_free(dst);
        return false;
    }
    return true;
}

/* ── WorkflowExecution ──────────────────────────────────────────────────── */

void ca_wf_execution_free(ca_wf_execution_t *e) {
    if (!e) return;
    free(e->run_id);
    free(e->definition_id);
    free(e->failure_reason);
    e->run_id = e->definition_id = e->failure_reason = NULL;
    e->has_failure_reason = false;
}
static bool execution_copy(ca_wf_execution_t *dst, const ca_wf_execution_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->phase        = src->phase;
    dst->start_utc_ms = src->start_utc_ms;
    dst->run_id        = cab_strdup_empty(src->run_id);
    dst->definition_id = cab_strdup_empty(src->definition_id);
    bool ok = dst->run_id && dst->definition_id;
    if (ok && src->has_failure_reason) {
        dst->failure_reason = cab_strdup_empty(src->failure_reason);
        ok = dst->failure_reason != NULL;
        dst->has_failure_reason = ok;
    }
    if (!ok) { ca_wf_execution_free(dst); return false; }
    return true;
}

/* ── CheckpointPayload ──────────────────────────────────────────────────── */

void ca_wf_checkpoint_free(ca_wf_checkpoint_t *c) {
    if (!c) return;
    free(c->run_id);
    free(c->step_id);
    free(c->state_blob);
    memset(c, 0, sizeof(*c));
}
static bool checkpoint_copy(ca_wf_checkpoint_t *dst,
                            const ca_wf_checkpoint_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->run_id  = cab_strdup_empty(src->run_id);
    dst->step_id = cab_strdup_empty(src->step_id);
    bool ok = dst->run_id && dst->step_id;
    if (ok) ok = bytes_copy(&dst->state_blob, src->state_blob, src->state_blob_len);
    if (ok) dst->state_blob_len = src->state_blob_len;
    if (!ok) { ca_wf_checkpoint_free(dst); return false; }
    return true;
}

/* ── IWorkflowDefinitionStore ───────────────────────────────────────────── */

struct ca_wf_def_store {
    ca_wf_definition_t *items;
    size_t              count, cap;
};

ca_wf_def_store_t *ca_wf_def_store_create(void) {
    return (ca_wf_def_store_t *)calloc(1, sizeof(ca_wf_def_store_t));
}
void ca_wf_def_store_destroy(ca_wf_def_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_wf_definition_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_wf_def_store_backend_id(const ca_wf_def_store_t *s) {
    (void)s; return "in-memory";
}

int ca_wf_def_store_upsert(ca_wf_def_store_t *s, const ca_wf_definition_t *d) {
    if (!s || !d) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].definition_id, d->definition_id)) {
            ca_wf_definition_t copy;
            if (!definition_copy(&copy, d)) return -1;
            ca_wf_definition_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_wf_definition_t copy;
    if (!definition_copy(&copy, d)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_wf_definition_free(&copy); return -1; }
        s->items = (ca_wf_definition_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_wf_def_store_get(const ca_wf_def_store_t *s, const char *id,
                         ca_wf_definition_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].definition_id, id))
            return definition_copy(out, &s->items[i]);
    return false;
}

const char *ca_wf_null_def_store_backend_id(void) { return "null"; }

/* ── IWorkflowRunner ────────────────────────────────────────────────────── */

int ca_wf_runner_start(const ca_wf_runner_t *r, const char *definition_id,
                       ca_wf_execution_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !r->start || !out) return -1;
    return r->start(r->ctx, definition_id, out);
}
bool ca_wf_runner_get(const ca_wf_runner_t *r, const char *run_id,
                      ca_wf_execution_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !r->get || !out) return false;
    return r->get(r->ctx, run_id, out);
}
int ca_wf_runner_cancel(const ca_wf_runner_t *r, const char *run_id) {
    if (!r || !r->cancel) return -1;
    return r->cancel(r->ctx, run_id);
}

const char *ca_wf_null_runner_backend_id(void) { return "null"; }

int ca_wf_null_runner_start(const char *definition_id, ca_wf_execution_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return -1;
    ca_wf_execution_t e;
    memset(&e, 0, sizeof(e));
    e.run_id = (char *)"00000000-0000-0000-0000-000000000000"; /* Guid.Empty */
    e.definition_id = (char *)(definition_id ? definition_id : "");
    e.phase = CA_WF_PHASE_FAILED;
    e.start_utc_ms = INT64_MIN; /* DateTimeOffset.MinValue surrogate */
    e.has_failure_reason = true;
    e.failure_reason = (char *)"NullWorkflowRunner";
    return execution_copy(out, &e) ? 0 : -1;
}

/* ── IWorkflowState ─────────────────────────────────────────────────────── */

struct ca_wf_state_store {
    ca_wf_checkpoint_t *items;   /* keyed by (RunId, StepId) */
    size_t              count, cap;
};

ca_wf_state_store_t *ca_wf_state_store_create(void) {
    return (ca_wf_state_store_t *)calloc(1, sizeof(ca_wf_state_store_t));
}
void ca_wf_state_store_destroy(ca_wf_state_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->count; ++i) ca_wf_checkpoint_free(&s->items[i]);
    free(s->items);
    free(s);
}
const char *ca_wf_state_store_backend_id(const ca_wf_state_store_t *s) {
    (void)s; return "in-memory";
}

int ca_wf_state_store_checkpoint(ca_wf_state_store_t *s,
                                 const ca_wf_checkpoint_t *payload) {
    if (!s || !payload) return -1;
    for (size_t i = 0; i < s->count; ++i) {
        if (cab_ord_eq(s->items[i].run_id, payload->run_id) &&
            cab_ord_eq(s->items[i].step_id, payload->step_id)) {
            ca_wf_checkpoint_t copy;
            if (!checkpoint_copy(&copy, payload)) return -1;
            ca_wf_checkpoint_free(&s->items[i]);
            s->items[i] = copy;
            return 0;
        }
    }
    ca_wf_checkpoint_t copy;
    if (!checkpoint_copy(&copy, payload)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->items, nc * sizeof(*s->items));
        if (!n) { ca_wf_checkpoint_free(&copy); return -1; }
        s->items = (ca_wf_checkpoint_t *)n;
        s->cap = nc;
    }
    s->items[s->count++] = copy;
    return 0;
}

bool ca_wf_state_store_load(const ca_wf_state_store_t *s, const char *run_id,
                            const char *step_id, ca_wf_checkpoint_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || cab_is_ws(run_id) || cab_is_ws(step_id) || !out) return false;
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->items[i].run_id, run_id) &&
            cab_ord_eq(s->items[i].step_id, step_id))
            return checkpoint_copy(out, &s->items[i]);
    return false;
}

const char *ca_wf_null_state_backend_id(void) { return "null"; }

/* ── Conversations ──────────────────────────────────────────────────────── */

void ca_wf_conversation_free(ca_wf_conversation_t *c) {
    if (!c) return;
    free(c->id);
    free(c->project_id);
    free(c->agent_member_id);
    free(c->human_member_id);
    free(c->opening_prompt);
    free(c->result_json);
    free(c->failure_reason);
    memset(c, 0, sizeof(*c));
}
static bool conversation_copy(ca_wf_conversation_t *dst,
                              const ca_wf_conversation_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->state           = src->state;
    dst->queued_at_ms    = src->queued_at_ms;
    dst->has_started_at  = src->has_started_at;
    dst->started_at_ms   = src->started_at_ms;
    dst->has_finished_at = src->has_finished_at;
    dst->finished_at_ms  = src->finished_at_ms;
    dst->id              = cab_strdup_empty(src->id);
    dst->project_id      = cab_strdup_empty(src->project_id);
    dst->agent_member_id = cab_strdup_empty(src->agent_member_id);
    dst->opening_prompt  = cab_strdup_empty(src->opening_prompt);
    bool ok = dst->id && dst->project_id && dst->agent_member_id &&
              dst->opening_prompt;
    if (ok && src->has_human_member) {
        dst->human_member_id = cab_strdup_empty(src->human_member_id);
        ok = dst->human_member_id != NULL;
        dst->has_human_member = ok;
    }
    if (ok && src->has_result_json) {
        dst->result_json = cab_strdup_empty(src->result_json);
        ok = dst->result_json != NULL;
        dst->has_result_json = ok;
    }
    if (ok && src->has_failure_reason) {
        dst->failure_reason = cab_strdup_empty(src->failure_reason);
        ok = dst->failure_reason != NULL;
        dst->has_failure_reason = ok;
    }
    if (!ok) { ca_wf_conversation_free(dst); return false; }
    return true;
}

void ca_wf_conversation_step_free(ca_wf_conversation_step_t *s) {
    if (!s) return;
    free(s->conversation_id);
    free(s->speaker);
    free(s->content_json);
    s->conversation_id = s->speaker = s->content_json = NULL;
}
void ca_wf_conversation_step_free_array(ca_wf_conversation_step_t *arr,
                                        size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_wf_conversation_step_free(&arr[i]);
    free(arr);
}
static bool step_copy(ca_wf_conversation_step_t *dst,
                      const ca_wf_conversation_step_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->order = src->order;
    dst->at_ms = src->at_ms;
    dst->conversation_id = cab_strdup_empty(src->conversation_id);
    dst->speaker         = cab_strdup_empty(src->speaker);
    dst->content_json    = cab_strdup_empty(src->content_json);
    if (!dst->conversation_id || !dst->speaker || !dst->content_json) {
        ca_wf_conversation_step_free(dst);
        return false;
    }
    return true;
}

typedef struct {
    ca_wf_conversation_t       conversation; /* owned */
    ca_wf_conversation_step_t *steps;        /* owned */
    size_t                     step_count, step_cap;
} conv_entry_t;

struct ca_wf_conversation_runtime {
    conv_entry_t                 *entries;
    size_t                        count, cap;
    ca_wf_conversation_executor_t executor;
};

ca_wf_conversation_runtime_t *ca_wf_conversation_runtime_create(
    const ca_wf_conversation_executor_t *executor) {
    if (!executor || !executor->run) return NULL;
    ca_wf_conversation_runtime_t *rt =
        (ca_wf_conversation_runtime_t *)calloc(1, sizeof(*rt));
    if (!rt) return NULL;
    rt->executor = *executor;
    return rt;
}
void ca_wf_conversation_runtime_destroy(ca_wf_conversation_runtime_t *rt) {
    if (!rt) return;
    for (size_t i = 0; i < rt->count; ++i) {
        ca_wf_conversation_free(&rt->entries[i].conversation);
        ca_wf_conversation_step_free_array(rt->entries[i].steps,
                                           rt->entries[i].step_count);
    }
    free(rt->entries);
    free(rt);
}

static conv_entry_t *conv_find(ca_wf_conversation_runtime_t *rt, const char *id) {
    for (size_t i = 0; i < rt->count; ++i)
        if (cab_ord_eq(rt->entries[i].conversation.id, id))
            return &rt->entries[i];
    return NULL;
}

int ca_wf_conversation_runtime_queue(ca_wf_conversation_runtime_t *rt,
                                     const char *id, const char *project_id,
                                     const char *agent_member_id,
                                     const char *opening_prompt,
                                     const char *human_member_id, int64_t now_ms,
                                     ca_wf_conversation_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!rt || cab_is_ws(id) || !project_id || !agent_member_id || !out)
        return -1;
    if (conv_find(rt, id)) return -1; /* already exists */

    ca_wf_conversation_t c;
    memset(&c, 0, sizeof(c));
    c.id = (char *)id;
    c.project_id = (char *)project_id;
    c.agent_member_id = (char *)agent_member_id;
    if (human_member_id) { c.has_human_member = true; c.human_member_id = (char *)human_member_id; }
    c.opening_prompt = (char *)(opening_prompt ? opening_prompt : "");
    c.state = CA_WF_CONV_QUEUED;
    c.queued_at_ms = now_ms;

    if (rt->count == rt->cap) {
        size_t nc = rt->cap ? rt->cap * 2 : 4;
        void *n = realloc(rt->entries, nc * sizeof(*rt->entries));
        if (!n) return -1;
        rt->entries = (conv_entry_t *)n;
        rt->cap = nc;
    }
    conv_entry_t *e = &rt->entries[rt->count];
    memset(e, 0, sizeof(*e));
    if (!conversation_copy(&e->conversation, &c)) return -1;
    rt->count++;

    return conversation_copy(out, &e->conversation) ? 0 : -1;
}

bool ca_wf_conversation_runtime_get(const ca_wf_conversation_runtime_t *rt,
                                    const char *id, ca_wf_conversation_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!rt || !id || !out) return false;
    conv_entry_t *e = conv_find((ca_wf_conversation_runtime_t *)rt, id);
    if (!e) return false;
    return conversation_copy(out, &e->conversation);
}

ca_wf_conversation_step_t *ca_wf_conversation_runtime_steps(
    const ca_wf_conversation_runtime_t *rt, const char *id, size_t *out_count) {
    if (!out_count) return NULL;
    if (!rt || !id) { *out_count = (size_t)-1; return NULL; }
    conv_entry_t *e = conv_find((ca_wf_conversation_runtime_t *)rt, id);
    if (!e || e->step_count == 0) { *out_count = 0; return NULL; }
    ca_wf_conversation_step_t *out =
        (ca_wf_conversation_step_t *)calloc(e->step_count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < e->step_count; ++i) {
        if (!step_copy(&out[i], &e->steps[i])) {
            ca_wf_conversation_step_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = e->step_count;
    return out;
}

/* Sink closure: append the emitted step into the entry (deep copy). */
typedef struct {
    conv_entry_t *entry;
    bool          oom;
} step_sink_ctx_t;

static void step_sink(void *sink_ctx, const ca_wf_conversation_step_t *step) {
    step_sink_ctx_t *sc = (step_sink_ctx_t *)sink_ctx;
    if (sc->oom || !step) return;
    conv_entry_t *e = sc->entry;
    if (e->step_count == e->step_cap) {
        size_t nc = e->step_cap ? e->step_cap * 2 : 4;
        void *n = realloc(e->steps, nc * sizeof(*e->steps));
        if (!n) { sc->oom = true; return; }
        e->steps = (ca_wf_conversation_step_t *)n;
        e->step_cap = nc;
    }
    ca_wf_conversation_step_t copy;
    if (!step_copy(&copy, step)) { sc->oom = true; return; }
    e->steps[e->step_count++] = copy;
}

int ca_wf_conversation_runtime_run(ca_wf_conversation_runtime_t *rt,
                                   const char *id,
                                   ca_wf_conversation_permissions_t permissions,
                                   int64_t now_ms) {
    if (!rt || !id) return -1;
    conv_entry_t *e = conv_find(rt, id);
    if (!e || e->conversation.state != CA_WF_CONV_QUEUED) return -1;

    /* Queued -> Running */
    e->conversation.state = CA_WF_CONV_RUNNING;
    e->conversation.has_started_at = true;
    e->conversation.started_at_ms = now_ms;

    /* Snapshot the running conversation to hand the executor. */
    ca_wf_conversation_t running;
    if (!conversation_copy(&running, &e->conversation)) return -1;

    step_sink_ctx_t sc; sc.entry = e; sc.oom = false;
    char fail[256]; fail[0] = '\0';
    int rc = rt->executor.run(rt->executor.ctx, &running, permissions,
                              step_sink, &sc, fail, sizeof(fail));
    ca_wf_conversation_free(&running);
    if (sc.oom) return -1;

    e->conversation.has_finished_at = true;
    e->conversation.finished_at_ms = now_ms;

    if (rc == 1) {
        /* stopped (OperationCanceled) */
        e->conversation.state = CA_WF_CONV_STOPPED;
    } else if (rc == 0) {
        e->conversation.state = CA_WF_CONV_FINISHED;
        e->conversation.has_result_json = true;
        free(e->conversation.result_json);
        e->conversation.result_json = cab_strdup("{}");
        if (!e->conversation.result_json) return -1;
    } else {
        e->conversation.state = CA_WF_CONV_FAILED;
        e->conversation.has_failure_reason = true;
        free(e->conversation.failure_reason);
        e->conversation.failure_reason =
            cab_strdup(fail[0] ? fail : "conversation failed");
        if (!e->conversation.failure_reason) return -1;
    }
    return 0;
}
