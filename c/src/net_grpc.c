/*
 * net_grpc.c — CircleAI.Networking.Grpc (C11 port).
 *
 * GrpcChannelDescriptor / GrpcRetryPolicy / GrpcCallSummary records, the retry
 * policy presets, InMemoryGrpcCallMetrics, and GrpcNetworkTransport
 * (INetworkTransport; Send is the not-supported path).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_grpc.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- helpers ---- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *dup_or_empty(const char *s) { return dup_or_null(s ? s : ""); }

static char **dup_str_array(const char *const *src, size_t count, bool *ok) {
    *ok = true;
    if (count == 0) return NULL;
    char **out = (char **)calloc(count, sizeof(*out));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i] = dup_or_empty(src ? src[i] : NULL);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}
static void free_str_array(char **a, size_t n) {
    if (!a) return;
    for (size_t i = 0; i < n; ++i) free(a[i]);
    free(a);
}

/* ===========================================================================
 * GrpcChannelDescriptor
 * =========================================================================== */

ca_grpc_channel_descriptor_t *ca_grpc_channel_descriptor_new(
    const char *target, bool use_tls, int max_receive_bytes,
    int max_send_bytes, int64_t keep_alive_interval_ms) {
    ca_grpc_channel_descriptor_t *d =
        (ca_grpc_channel_descriptor_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->target = dup_or_empty(target);
    if (!d->target) { free(d); return NULL; }
    d->use_tls = use_tls;
    d->max_receive_bytes = max_receive_bytes;
    d->max_send_bytes = max_send_bytes;
    d->keep_alive_interval_ms = keep_alive_interval_ms;
    return d;
}

void ca_grpc_channel_descriptor_destroy(ca_grpc_channel_descriptor_t *d) {
    if (!d) return;
    free(d->target);
    free(d);
}

ca_grpc_channel_descriptor_t *ca_grpc_channel_descriptor_copy(
    const ca_grpc_channel_descriptor_t *d) {
    if (!d) return NULL;
    return ca_grpc_channel_descriptor_new(d->target, d->use_tls,
                                          d->max_receive_bytes,
                                          d->max_send_bytes,
                                          d->keep_alive_interval_ms);
}

/* ===========================================================================
 * GrpcRetryPolicy
 * =========================================================================== */

ca_grpc_retry_policy_t *ca_grpc_retry_policy_new(
    int max_attempts, int64_t initial_backoff_ms, int64_t max_backoff_ms,
    double multiplier, const char *const *retryable_status_codes,
    size_t retryable_count) {
    ca_grpc_retry_policy_t *p =
        (ca_grpc_retry_policy_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->max_attempts = max_attempts;
    p->initial_backoff_ms = initial_backoff_ms;
    p->max_backoff_ms = max_backoff_ms;
    p->multiplier = multiplier;
    bool ok = true;
    p->retryable_status_codes =
        dup_str_array(retryable_status_codes, retryable_count, &ok);
    if (!ok) { free(p); return NULL; }
    p->retryable_count = retryable_count;
    return p;
}

void ca_grpc_retry_policy_destroy(ca_grpc_retry_policy_t *p) {
    if (!p) return;
    free_str_array(p->retryable_status_codes, p->retryable_count);
    free(p);
}

ca_grpc_retry_policy_t *ca_grpc_retry_policy_copy(
    const ca_grpc_retry_policy_t *p) {
    if (!p) return NULL;
    return ca_grpc_retry_policy_new(
        p->max_attempts, p->initial_backoff_ms, p->max_backoff_ms,
        p->multiplier, (const char *const *)p->retryable_status_codes,
        p->retryable_count);
}

ca_grpc_retry_policy_t *ca_grpc_retry_policies_default(void) {
    const char *codes[] = { "UNAVAILABLE", "DEADLINE_EXCEEDED" };
    return ca_grpc_retry_policy_new(3, 100, 2000, 2.0, codes, 2);
}
ca_grpc_retry_policy_t *ca_grpc_retry_policies_aggressive(void) {
    const char *codes[] = { "UNAVAILABLE", "DEADLINE_EXCEEDED",
                            "RESOURCE_EXHAUSTED" };
    return ca_grpc_retry_policy_new(6, 50, 5000, 2.0, codes, 3);
}
ca_grpc_retry_policy_t *ca_grpc_retry_policies_no_retry(void) {
    return ca_grpc_retry_policy_new(1, 0, 0, 1.0, NULL, 0);
}

/* ===========================================================================
 * GrpcReconnectPolicy
 * =========================================================================== */

ca_grpc_reconnect_policy_t ca_grpc_reconnect_policy_default(void) {
    /* new(5, TimeSpan.FromMilliseconds(200), 2.0, TimeSpan.FromSeconds(30)) */
    ca_grpc_reconnect_policy_t p;
    p.max_attempts       = 5;
    p.initial_backoff_ms = 200;
    p.backoff_multiplier = 2.0;
    p.max_backoff_ms     = 30000;
    return p;
}

int64_t ca_grpc_reconnect_policy_backoff_for(
    const ca_grpc_reconnect_policy_t *p, int attempt) {
    if (!p) return -1;
    /* if (attempt < 1) throw ArgumentOutOfRangeException. */
    if (attempt < 1) return -1;
    /* InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt-1) */
    double scaled = (double)p->initial_backoff_ms *
                    pow(p->backoff_multiplier, (double)(attempt - 1));
    double cap_ms = (double)p->max_backoff_ms;
    if (isinf(scaled) || scaled > cap_ms) return p->max_backoff_ms;
    /* TimeSpan.FromMilliseconds(scaled) — whole-ms durations in this port. */
    return (int64_t)scaled;
}

bool ca_grpc_reconnect_policy_should_retry(
    const ca_grpc_reconnect_policy_t *p, int attempt) {
    if (!p) return false;
    return attempt < p->max_attempts;
}

/* ===========================================================================
 * GrpcDeadline
 * =========================================================================== */

bool ca_grpc_deadline_from_timeout(int64_t timeout_ms, int64_t now_utc_ms,
                                   int64_t *out_deadline_ms) {
    /* if (timeout < TimeSpan.Zero) throw ArgumentOutOfRangeException. */
    if (timeout_ms < 0) return false;
    if (out_deadline_ms) *out_deadline_ms = now_utc_ms + timeout_ms;
    return true;
}

int64_t ca_grpc_deadline_remaining(int64_t deadline_utc_ms, int64_t now_utc_ms) {
    int64_t left = deadline_utc_ms - now_utc_ms;
    return left > 0 ? left : 0;   /* clamp to zero once passed */
}

bool ca_grpc_deadline_is_expired(int64_t deadline_utc_ms, int64_t now_utc_ms) {
    return now_utc_ms >= deadline_utc_ms;
}

/* ===========================================================================
 * GrpcCallSummary free helpers
 * =========================================================================== */

void ca_grpc_call_summary_free(ca_grpc_call_summary_t *c) {
    if (!c) return;
    free(c->method);
    free(c->status_code);
    c->method = c->status_code = NULL;
}

void ca_grpc_call_summary_free_array(ca_grpc_call_summary_t *arr,
                                     size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_grpc_call_summary_free(&arr[i]);
    free(arr);
}

/* ===========================================================================
 * InMemoryGrpcCallMetrics
 * =========================================================================== */

typedef struct {
    char                        *id;   /* owned */
    ca_grpc_channel_descriptor_t *desc; /* owned */
} grpc_chan_entry_t;

typedef struct {
    char                   *id;    /* owned */
    ca_grpc_channel_state_t state;
} grpc_state_entry_t;

struct ca_grpc_metrics {
    grpc_chan_entry_t      *channels;
    size_t                  chan_count, chan_cap;
    grpc_state_entry_t     *states;
    size_t                  state_count, state_cap;
    ca_grpc_call_summary_t *calls;
    size_t                  call_count, call_cap;
    long                    seq;   /* Interlocked _seq */
};

ca_grpc_metrics_t *ca_grpc_metrics_create(void) {
    return (ca_grpc_metrics_t *)calloc(1, sizeof(ca_grpc_metrics_t));
}

void ca_grpc_metrics_destroy(ca_grpc_metrics_t *m) {
    if (!m) return;
    for (size_t i = 0; i < m->chan_count; ++i) {
        free(m->channels[i].id);
        ca_grpc_channel_descriptor_destroy(m->channels[i].desc);
    }
    free(m->channels);
    for (size_t i = 0; i < m->state_count; ++i) free(m->states[i].id);
    free(m->states);
    for (size_t i = 0; i < m->call_count; ++i)
        ca_grpc_call_summary_free(&m->calls[i]);
    free(m->calls);
    free(m);
}

static ptrdiff_t chan_index(const ca_grpc_metrics_t *m, const char *id) {
    for (size_t i = 0; i < m->chan_count; ++i)
        if (strcmp(m->channels[i].id, id) == 0) return (ptrdiff_t)i;
    return -1;
}
static ptrdiff_t state_index(const ca_grpc_metrics_t *m, const char *id) {
    for (size_t i = 0; i < m->state_count; ++i)
        if (strcmp(m->states[i].id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_grpc_metrics_register_channel(ca_grpc_metrics_t *m, const char *id,
                                     const ca_grpc_channel_descriptor_t *d) {
    if (!m || !id || !d) return -1;
    ca_grpc_channel_descriptor_t *copy = ca_grpc_channel_descriptor_copy(d);
    if (!copy) return -1;
    ptrdiff_t idx = chan_index(m, id);
    if (idx >= 0) {
        ca_grpc_channel_descriptor_destroy(m->channels[idx].desc);
        m->channels[idx].desc = copy;
        return 0;
    }
    if (m->chan_count == m->chan_cap) {
        size_t nc = m->chan_cap ? m->chan_cap * 2 : 4;
        grpc_chan_entry_t *ne =
            (grpc_chan_entry_t *)realloc(m->channels, nc * sizeof(*ne));
        if (!ne) { ca_grpc_channel_descriptor_destroy(copy); return -1; }
        m->channels = ne;
        m->chan_cap = nc;
    }
    char *kid = dup_or_empty(id);
    if (!kid) { ca_grpc_channel_descriptor_destroy(copy); return -1; }
    m->channels[m->chan_count].id = kid;
    m->channels[m->chan_count].desc = copy;
    m->chan_count++;
    return 0;
}

ca_grpc_channel_descriptor_t *ca_grpc_metrics_get_channel(
    const ca_grpc_metrics_t *m, const char *id) {
    if (!m || !id) return NULL;
    ptrdiff_t idx = chan_index(m, id);
    return idx < 0 ? NULL : ca_grpc_channel_descriptor_copy(m->channels[idx].desc);
}

void ca_grpc_metrics_set_state(ca_grpc_metrics_t *m, const char *id,
                               ca_grpc_channel_state_t s) {
    if (!m || !id) return;
    ptrdiff_t idx = state_index(m, id);
    if (idx >= 0) { m->states[idx].state = s; return; }
    if (m->state_count == m->state_cap) {
        size_t nc = m->state_cap ? m->state_cap * 2 : 4;
        grpc_state_entry_t *ne =
            (grpc_state_entry_t *)realloc(m->states, nc * sizeof(*ne));
        if (!ne) return;
        m->states = ne;
        m->state_cap = nc;
    }
    char *kid = dup_or_empty(id);
    if (!kid) return;
    m->states[m->state_count].id = kid;
    m->states[m->state_count].state = s;
    m->state_count++;
}

ca_grpc_channel_state_t ca_grpc_metrics_state(const ca_grpc_metrics_t *m,
                                              const char *id) {
    if (!m || !id) return CA_GRPC_STATE_IDLE;
    ptrdiff_t idx = state_index(m, id);
    return idx < 0 ? CA_GRPC_STATE_IDLE : m->states[idx].state;
}

char *ca_grpc_metrics_log_call(ca_grpc_metrics_t *m,
                               const char *method, int attempts,
                               int64_t latency_ms, const char *status_code,
                               int64_t at_unix_ms, char *out_id,
                               size_t out_id_size) {
    if (!m || !out_id || out_id_size == 0) return NULL;
    if (m->call_count == m->call_cap) {
        size_t nc = m->call_cap ? m->call_cap * 2 : 4;
        ca_grpc_call_summary_t *ne =
            (ca_grpc_call_summary_t *)realloc(m->calls, nc * sizeof(*ne));
        if (!ne) return NULL;
        m->calls = ne;
        m->call_cap = nc;
    }
    ca_grpc_call_summary_t *c = &m->calls[m->call_count];
    memset(c, 0, sizeof(*c));
    c->method = dup_or_empty(method);
    c->status_code = dup_or_empty(status_code);
    if (!c->method || !c->status_code) {
        ca_grpc_call_summary_free(c);
        return NULL;
    }
    c->attempts = attempts;
    c->latency_ms = latency_ms;
    c->at_unix_ms = at_unix_ms;
    m->call_count++;
    /* Interlocked.Increment(ref _seq) => pre-incremented, starts at 1. */
    long n = ++m->seq;
    snprintf(out_id, out_id_size, "grpc-%ld", n);
    return out_id;
}

typedef struct { const ca_grpc_call_summary_t *c; size_t ord; } call_ref_t;
static int cmp_call_desc(const void *a, const void *b) {
    const call_ref_t *ra = (const call_ref_t *)a;
    const call_ref_t *rb = (const call_ref_t *)b;
    if (ra->c->at_unix_ms > rb->c->at_unix_ms) return -1;
    if (ra->c->at_unix_ms < rb->c->at_unix_ms) return 1;
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

ca_grpc_call_summary_t *ca_grpc_metrics_recent_calls(
    const ca_grpc_metrics_t *m, int limit, size_t *count) {
    if (!m || !count) { if (count) *count = SIZE_MAX; return NULL; }
    if (limit < 0) limit = 0;
    size_t take = (size_t)limit < m->call_count ? (size_t)limit
                                                : m->call_count;
    if (take == 0) { *count = 0; return NULL; }
    call_ref_t *refs = (call_ref_t *)calloc(m->call_count, sizeof(*refs));
    if (!refs) { *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < m->call_count; ++i) {
        refs[i].c = &m->calls[i];
        refs[i].ord = i;
    }
    qsort(refs, m->call_count, sizeof(*refs), cmp_call_desc);
    ca_grpc_call_summary_t *out =
        (ca_grpc_call_summary_t *)calloc(take, sizeof(*out));
    if (!out) { free(refs); *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        const ca_grpc_call_summary_t *s = refs[i].c;
        out[i].method = dup_or_empty(s->method);
        out[i].status_code = dup_or_empty(s->status_code);
        out[i].attempts = s->attempts;
        out[i].latency_ms = s->latency_ms;
        out[i].at_unix_ms = s->at_unix_ms;
        if (!out[i].method || !out[i].status_code) {
            ca_grpc_call_summary_free_array(out, i + 1);
            free(refs);
            *count = SIZE_MAX;
            return NULL;
        }
    }
    free(refs);
    *count = take;
    return out;
}

/* ===========================================================================
 * GrpcNetworkTransport
 * =========================================================================== */

struct ca_grpc_transport {
    char                         *address;    /* owned */
    ca_grpc_channel_descriptor_t *descriptor; /* owned, may be NULL */
    bool                          running;
};

ca_grpc_transport_t *ca_grpc_transport_create(
    const char *address, const ca_grpc_channel_descriptor_t *descriptor) {
    ca_grpc_transport_t *t = (ca_grpc_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->address = dup_or_empty(address);
    if (!t->address) { free(t); return NULL; }
    if (descriptor) {
        t->descriptor = ca_grpc_channel_descriptor_copy(descriptor);
        if (!t->descriptor) { free(t->address); free(t); return NULL; }
    }
    return t;
}

void ca_grpc_transport_destroy(ca_grpc_transport_t *t) {
    if (!t) return;
    free(t->address);
    ca_grpc_channel_descriptor_destroy(t->descriptor);
    free(t);
}

static ca_transport_kind_t gt_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_GRPC;
}
static bool gt_available(void *self) {
    return ((ca_grpc_transport_t *)self)->running;
}
static int gt_start(void *self) {
    ((ca_grpc_transport_t *)self)->running = true;
    return 0;
}
static int gt_stop(void *self) {
    ((ca_grpc_transport_t *)self)->running = false;
    return 0;
}
static int gt_send(void *self, const ca_network_payload_t *payload) {
    /* throw new NotSupportedException(...) — no generic send path. */
    (void)self; (void)payload;
    return CA_GRPC_SEND_NOT_SUPPORTED;
}
static bool gt_receive_next(void *self, ca_network_payload_t **out) {
    (void)self;
    if (out) *out = NULL;
    return false; /* yield break; */
}

ca_network_transport_t ca_grpc_transport_as_transport(ca_grpc_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = gt_kind;
    v.is_available = gt_available;
    v.start = gt_start;
    v.stop = gt_stop;
    v.send = gt_send;
    v.receive_next = gt_receive_next;
    return v;
}

const char *ca_grpc_transport_address(const ca_grpc_transport_t *t) {
    return t ? t->address : NULL;
}

ca_grpc_channel_descriptor_t *ca_grpc_transport_channel(
    const ca_grpc_transport_t *t) {
    if (!t || !t->descriptor) return NULL;
    return ca_grpc_channel_descriptor_copy(t->descriptor);
}
