/*
 * net_nearlink.c — CircleAI.Networking.NearLink (C11 port).
 *
 * NearLink pairing/power enums, the device/session/throughput records, the
 * InMemoryNearLinkRegistry, the injected INearLinkAdapter seam + a deterministic
 * in-memory adapter, and NearLinkTransport (INetworkTransport).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_nearlink.h"

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

/* ===========================================================================
 * NearLinkDevice
 * =========================================================================== */

ca_nearlink_device_t *ca_nearlink_device_new(const char *device_id,
                                             const char *friendly_name,
                                             const char *manufacturer_id,
                                             const char *firmware_version) {
    ca_nearlink_device_t *d = (ca_nearlink_device_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->device_id = dup_or_empty(device_id);
    d->friendly_name = dup_or_empty(friendly_name);
    d->manufacturer_id = dup_or_empty(manufacturer_id);
    d->firmware_version = dup_or_empty(firmware_version);
    if (!d->device_id || !d->friendly_name || !d->manufacturer_id ||
        !d->firmware_version) {
        ca_nearlink_device_destroy(d);
        return NULL;
    }
    return d;
}
void ca_nearlink_device_destroy(ca_nearlink_device_t *d) {
    if (!d) return;
    free(d->device_id);
    free(d->friendly_name);
    free(d->manufacturer_id);
    free(d->firmware_version);
    free(d);
}
ca_nearlink_device_t *ca_nearlink_device_copy(const ca_nearlink_device_t *d) {
    if (!d) return NULL;
    return ca_nearlink_device_new(d->device_id, d->friendly_name,
                                  d->manufacturer_id, d->firmware_version);
}

/* ===========================================================================
 * NearLinkSession
 * =========================================================================== */

ca_nearlink_session_t *ca_nearlink_session_new(
    const char *session_id, const char *device_id,
    ca_nearlink_power_profile_t power_profile, int64_t started_unix_ms) {
    ca_nearlink_session_t *s = (ca_nearlink_session_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->session_id = dup_or_empty(session_id);
    s->device_id = dup_or_empty(device_id);
    if (!s->session_id || !s->device_id) {
        ca_nearlink_session_destroy(s);
        return NULL;
    }
    s->power_profile = power_profile;
    s->started_unix_ms = started_unix_ms;
    return s;
}
void ca_nearlink_session_destroy(ca_nearlink_session_t *s) {
    if (!s) return;
    free(s->session_id);
    free(s->device_id);
    free(s);
}
ca_nearlink_session_t *ca_nearlink_session_copy(
    const ca_nearlink_session_t *s) {
    if (!s) return NULL;
    return ca_nearlink_session_new(s->session_id, s->device_id,
                                   s->power_profile, s->started_unix_ms);
}

/* ===========================================================================
 * InMemoryNearLinkRegistry
 * =========================================================================== */

typedef struct {
    char                       *device_id; /* owned */
    ca_nearlink_pairing_state_t state;
} nl_pair_entry_t;

struct ca_nearlink_registry {
    ca_nearlink_device_t **devices;  /* owned array (LWW by DeviceId) */
    size_t                 dev_count;
    size_t                 dev_cap;

    nl_pair_entry_t       *states;   /* owned array */
    size_t                 state_count;
    size_t                 state_cap;

    ca_nearlink_session_t **sessions; /* owned array (LWW by SessionId) */
    size_t                  sess_count;
    size_t                  sess_cap;

    ca_nearlink_throughput_sample_t *tp; /* owned array */
    size_t                           tp_count;
    size_t                           tp_cap;
};

ca_nearlink_registry_t *ca_nearlink_registry_create(void) {
    return (ca_nearlink_registry_t *)calloc(1, sizeof(ca_nearlink_registry_t));
}

void ca_nearlink_registry_destroy(ca_nearlink_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->dev_count; ++i)
        ca_nearlink_device_destroy(r->devices[i]);
    free(r->devices);
    for (size_t i = 0; i < r->state_count; ++i) free(r->states[i].device_id);
    free(r->states);
    for (size_t i = 0; i < r->sess_count; ++i)
        ca_nearlink_session_destroy(r->sessions[i]);
    free(r->sessions);
    for (size_t i = 0; i < r->tp_count; ++i) free(r->tp[i].device_id);
    free(r->tp);
    free(r);
}

static ptrdiff_t nl_dev_index(const ca_nearlink_registry_t *r,
                              const char *id) {
    for (size_t i = 0; i < r->dev_count; ++i)
        if (strcmp(r->devices[i]->device_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_nearlink_registry_register(ca_nearlink_registry_t *r,
                                  const ca_nearlink_device_t *d) {
    if (!r || !d) return -1;
    ca_nearlink_device_t *copy = ca_nearlink_device_copy(d);
    if (!copy) return -1;
    ptrdiff_t idx = nl_dev_index(r, d->device_id);
    if (idx >= 0) {
        ca_nearlink_device_destroy(r->devices[idx]);
        r->devices[idx] = copy;
        return 0;
    }
    if (r->dev_count == r->dev_cap) {
        size_t nc = r->dev_cap ? r->dev_cap * 2 : 4;
        ca_nearlink_device_t **np =
            (ca_nearlink_device_t **)realloc(r->devices, nc * sizeof(*np));
        if (!np) { ca_nearlink_device_destroy(copy); return -1; }
        r->devices = np;
        r->dev_cap = nc;
    }
    r->devices[r->dev_count++] = copy;
    return 0;
}

ca_nearlink_device_t *ca_nearlink_registry_get_device(
    const ca_nearlink_registry_t *r, const char *device_id) {
    if (!r || !device_id) return NULL;
    ptrdiff_t idx = nl_dev_index(r, device_id);
    if (idx < 0) return NULL;
    return ca_nearlink_device_copy(r->devices[idx]);
}

typedef struct { const ca_nearlink_device_t *d; size_t ord; } dev_ref_t;
static int cmp_dev_by_name(const void *a, const void *b) {
    const dev_ref_t *ra = (const dev_ref_t *)a;
    const dev_ref_t *rb = (const dev_ref_t *)b;
    int c = strcmp(ra->d->friendly_name, rb->d->friendly_name);
    if (c != 0) return c;
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

int ca_nearlink_registry_devices(const ca_nearlink_registry_t *r,
                                 ca_nearlink_device_t ***out, size_t *count) {
    if (!r || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    if (r->dev_count == 0) { *out = NULL; *count = 0; return 0; }
    dev_ref_t *refs = (dev_ref_t *)calloc(r->dev_count, sizeof(*refs));
    if (!refs) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->dev_count; ++i) {
        refs[i].d = r->devices[i];
        refs[i].ord = i;
    }
    qsort(refs, r->dev_count, sizeof(*refs), cmp_dev_by_name);
    ca_nearlink_device_t **arr =
        (ca_nearlink_device_t **)calloc(r->dev_count, sizeof(*arr));
    if (!arr) { free(refs); *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->dev_count; ++i) {
        arr[i] = ca_nearlink_device_copy(refs[i].d);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j)
                ca_nearlink_device_destroy(arr[j]);
            free(arr); free(refs);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    free(refs);
    *out = arr;
    *count = r->dev_count;
    return 0;
}

static ptrdiff_t nl_state_index(const ca_nearlink_registry_t *r,
                                const char *id) {
    for (size_t i = 0; i < r->state_count; ++i)
        if (strcmp(r->states[i].device_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

void ca_nearlink_registry_set_pairing_state(ca_nearlink_registry_t *r,
                                            const char *device_id,
                                            ca_nearlink_pairing_state_t s) {
    if (!r || !device_id) return;
    ptrdiff_t idx = nl_state_index(r, device_id);
    if (idx >= 0) { r->states[idx].state = s; return; }
    if (r->state_count == r->state_cap) {
        size_t nc = r->state_cap ? r->state_cap * 2 : 4;
        nl_pair_entry_t *ns =
            (nl_pair_entry_t *)realloc(r->states, nc * sizeof(*ns));
        if (!ns) return;
        r->states = ns;
        r->state_cap = nc;
    }
    char *id = dup_or_empty(device_id);
    if (!id) return;
    r->states[r->state_count].device_id = id;
    r->states[r->state_count].state = s;
    r->state_count++;
}

ca_nearlink_pairing_state_t ca_nearlink_registry_pairing_state(
    const ca_nearlink_registry_t *r, const char *device_id) {
    if (!r || !device_id) return CA_NEARLINK_PAIRING_UNPAIRED;
    ptrdiff_t idx = nl_state_index(r, device_id);
    return idx < 0 ? CA_NEARLINK_PAIRING_UNPAIRED : r->states[idx].state;
}

static ptrdiff_t nl_sess_index(const ca_nearlink_registry_t *r,
                               const char *id) {
    for (size_t i = 0; i < r->sess_count; ++i)
        if (strcmp(r->sessions[i]->session_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_nearlink_registry_open_session(ca_nearlink_registry_t *r,
                                      const ca_nearlink_session_t *s) {
    if (!r || !s) return -1;
    ca_nearlink_session_t *copy = ca_nearlink_session_copy(s);
    if (!copy) return -1;
    ptrdiff_t idx = nl_sess_index(r, s->session_id);
    if (idx >= 0) {
        ca_nearlink_session_destroy(r->sessions[idx]);
        r->sessions[idx] = copy;
        return 0;
    }
    if (r->sess_count == r->sess_cap) {
        size_t nc = r->sess_cap ? r->sess_cap * 2 : 4;
        ca_nearlink_session_t **np =
            (ca_nearlink_session_t **)realloc(r->sessions, nc * sizeof(*np));
        if (!np) { ca_nearlink_session_destroy(copy); return -1; }
        r->sessions = np;
        r->sess_cap = nc;
    }
    r->sessions[r->sess_count++] = copy;
    return 0;
}

ca_nearlink_session_t *ca_nearlink_registry_get_session(
    const ca_nearlink_registry_t *r, const char *session_id) {
    if (!r || !session_id) return NULL;
    ptrdiff_t idx = nl_sess_index(r, session_id);
    if (idx < 0) return NULL;
    return ca_nearlink_session_copy(r->sessions[idx]);
}

void ca_nearlink_registry_close_session(ca_nearlink_registry_t *r,
                                        const char *session_id) {
    if (!r || !session_id) return;
    ptrdiff_t idx = nl_sess_index(r, session_id);
    if (idx < 0) return;
    ca_nearlink_session_destroy(r->sessions[idx]);
    for (size_t i = (size_t)idx; i + 1 < r->sess_count; ++i)
        r->sessions[i] = r->sessions[i + 1];
    r->sess_count--;
}

int ca_nearlink_registry_active_sessions(const ca_nearlink_registry_t *r,
                                         ca_nearlink_session_t ***out,
                                         size_t *count) {
    if (!r || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    if (r->sess_count == 0) { *out = NULL; *count = 0; return 0; }
    ca_nearlink_session_t **arr =
        (ca_nearlink_session_t **)calloc(r->sess_count, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->sess_count; ++i) {
        arr[i] = ca_nearlink_session_copy(r->sessions[i]);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j)
                ca_nearlink_session_destroy(arr[j]);
            free(arr);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    *out = arr;
    *count = r->sess_count;
    return 0;
}

int ca_nearlink_registry_record_throughput(ca_nearlink_registry_t *r,
                                            const char *device_id,
                                            double kbps_read, double kbps_write,
                                            int rssi_dbm, int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->tp_count == r->tp_cap) {
        size_t nc = r->tp_cap ? r->tp_cap * 2 : 4;
        ca_nearlink_throughput_sample_t *nt =
            (ca_nearlink_throughput_sample_t *)realloc(r->tp,
                                                       nc * sizeof(*nt));
        if (!nt) return -1;
        r->tp = nt;
        r->tp_cap = nc;
    }
    ca_nearlink_throughput_sample_t *s = &r->tp[r->tp_count];
    s->device_id = dup_or_empty(device_id);
    if (!s->device_id) return -1;
    s->kbps_read = kbps_read;
    s->kbps_write = kbps_write;
    s->rssi_dbm = rssi_dbm;
    s->at_unix_ms = at_unix_ms;
    r->tp_count++;
    return 0;
}

double ca_nearlink_registry_avg_rssi(const ca_nearlink_registry_t *r,
                                     const char *device_id) {
    if (!r || !device_id) return -127.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < r->tp_count; ++i) {
        if (strcmp(r->tp[i].device_id, device_id) == 0) {
            sum += (double)r->tp[i].rssi_dbm;
            n++;
        }
    }
    /* .Select(RssiDbm).DefaultIfEmpty(-127).Average() */
    return n == 0 ? -127.0 : sum / (double)n;
}

double ca_nearlink_registry_avg_kbps_read(const ca_nearlink_registry_t *r,
                                          const char *device_id) {
    if (!r || !device_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < r->tp_count; ++i) {
        if (strcmp(r->tp[i].device_id, device_id) == 0) {
            sum += r->tp[i].kbps_read;
            n++;
        }
    }
    /* .Select(KbpsRead).DefaultIfEmpty(0.0).Average() */
    return n == 0 ? 0.0 : sum / (double)n;
}

double ca_nearlink_registry_avg_kbps_write(const ca_nearlink_registry_t *r,
                                           const char *device_id) {
    if (!r || !device_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < r->tp_count; ++i) {
        if (strcmp(r->tp[i].device_id, device_id) == 0) {
            sum += r->tp[i].kbps_write;
            n++;
        }
    }
    /* .Select(KbpsWrite).DefaultIfEmpty(0.0).Average() */
    return n == 0 ? 0.0 : sum / (double)n;
}

bool ca_nearlink_registry_unregister(ca_nearlink_registry_t *r,
                                     const char *device_id) {
    /* string.IsNullOrEmpty(deviceId) -> false. */
    if (!r || !device_id || device_id[0] == '\0') return false;
    ptrdiff_t di = nl_dev_index(r, device_id);
    bool removed = false;
    if (di >= 0) {
        ca_nearlink_device_destroy(r->devices[(size_t)di]);
        for (size_t i = (size_t)di; i + 1 < r->dev_count; ++i)
            r->devices[i] = r->devices[i + 1];
        r->dev_count--;
        removed = true;
    }
    /* _states.TryRemove(deviceId, out _) — drop cached pairing state. Open
     * sessions are intentionally left untouched. */
    ptrdiff_t si = nl_state_index(r, device_id);
    if (si >= 0) {
        free(r->states[(size_t)si].device_id);
        for (size_t i = (size_t)si; i + 1 < r->state_count; ++i)
            r->states[i] = r->states[i + 1];
        r->state_count--;
    }
    return removed;
}

typedef struct { const ca_nearlink_session_t *s; size_t ord; } sess_ref_t;
static int cmp_sess_by_started(const void *a, const void *b) {
    const sess_ref_t *ra = (const sess_ref_t *)a;
    const sess_ref_t *rb = (const sess_ref_t *)b;
    if (ra->s->started_unix_ms < rb->s->started_unix_ms) return -1;
    if (ra->s->started_unix_ms > rb->s->started_unix_ms) return 1;
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

int ca_nearlink_registry_sessions_for_device(const ca_nearlink_registry_t *r,
                                             const char *device_id,
                                             ca_nearlink_session_t ***out,
                                             size_t *count) {
    if (!r || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    /* string.IsNullOrEmpty(deviceId) -> Array.Empty. */
    if (!device_id || device_id[0] == '\0') { *out = NULL; *count = 0; return 0; }

    sess_ref_t *refs = (sess_ref_t *)calloc(r->sess_count ? r->sess_count : 1,
                                            sizeof(*refs));
    if (!refs) { *out = NULL; *count = SIZE_MAX; return -1; }
    size_t n = 0;
    for (size_t i = 0; i < r->sess_count; ++i) {
        if (strcmp(r->sessions[i]->device_id, device_id) == 0) {
            refs[n].s = r->sessions[i];
            refs[n].ord = i;
            n++;
        }
    }
    if (n == 0) { free(refs); *out = NULL; *count = 0; return 0; }
    qsort(refs, n, sizeof(*refs), cmp_sess_by_started);
    ca_nearlink_session_t **arr =
        (ca_nearlink_session_t **)calloc(n, sizeof(*arr));
    if (!arr) { free(refs); *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < n; ++i) {
        arr[i] = ca_nearlink_session_copy(refs[i].s);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j)
                ca_nearlink_session_destroy(arr[j]);
            free(arr); free(refs);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    free(refs);
    *out = arr;
    *count = n;
    return 0;
}

/* ===========================================================================
 * Unbounded FIFO of NetworkPayload* (transport inbound channel)
 * =========================================================================== */

typedef struct {
    ca_network_payload_t **items;
    size_t head, count, cap;
} payload_fifo_t;

static bool pf_push(payload_fifo_t *q, ca_network_payload_t *owned) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_network_payload_t **ni =
                (ca_network_payload_t **)realloc(q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}
static ca_network_payload_t *pf_pop(payload_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_network_payload_t *p = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return p;
}
static void pf_free(payload_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_network_payload_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ===========================================================================
 * Inbound writer — the ChannelWriter<NetworkPayload> seam
 * =========================================================================== */

struct ca_nearlink_inbound_writer {
    payload_fifo_t *queue;
    bool           *open;
};

int ca_nearlink_inbound_write(ca_nearlink_inbound_writer_t *writer,
                              const ca_network_payload_t *payload) {
    if (!writer || !payload) return -1;
    if (writer->open && !*writer->open) return -1;
    ca_network_payload_t *copy = ca_network_payload_copy(payload);
    if (!copy) return -1;
    if (!pf_push(writer->queue, copy)) {
        ca_network_payload_destroy(copy);
        return -1;
    }
    return 0;
}

/* ===========================================================================
 * In-memory INearLinkAdapter
 * =========================================================================== */

struct ca_mem_nearlink_adapter {
    bool                          is_available;
    bool                          started;
    ca_nearlink_inbound_writer_t *writer; /* borrowed, set on start */
    size_t                        sent_count;
};

ca_mem_nearlink_adapter_t *ca_mem_nearlink_adapter_create(bool is_available) {
    ca_mem_nearlink_adapter_t *a =
        (ca_mem_nearlink_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->is_available = is_available;
    return a;
}
void ca_mem_nearlink_adapter_destroy(ca_mem_nearlink_adapter_t *a) { free(a); }
void ca_mem_nearlink_adapter_set_available(ca_mem_nearlink_adapter_t *a,
                                           bool v) {
    if (a) a->is_available = v;
}

static bool mna_is_available(void *self) {
    return ((ca_mem_nearlink_adapter_t *)self)->is_available;
}
static int mna_start(void *self, ca_nearlink_inbound_writer_t *writer) {
    ca_mem_nearlink_adapter_t *a = (ca_mem_nearlink_adapter_t *)self;
    a->writer = writer;
    a->started = true;
    return 0;
}
static int mna_stop(void *self) {
    ca_mem_nearlink_adapter_t *a = (ca_mem_nearlink_adapter_t *)self;
    a->started = false;
    a->writer = NULL;
    return 0;
}
static int mna_send(void *self, const ca_network_payload_t *payload) {
    ca_mem_nearlink_adapter_t *a = (ca_mem_nearlink_adapter_t *)self;
    if (!payload) return -1;
    a->sent_count++;
    return 0;
}

ca_nearlink_adapter_t ca_mem_nearlink_adapter_as_adapter(
    ca_mem_nearlink_adapter_t *a) {
    ca_nearlink_adapter_t v;
    v.self = a;
    v.is_available = mna_is_available;
    v.start = mna_start;
    v.stop = mna_stop;
    v.send = mna_send;
    return v;
}

int ca_mem_nearlink_adapter_deliver(ca_mem_nearlink_adapter_t *a,
                                    const ca_network_payload_t *payload) {
    if (!a || !payload) return -1;
    if (!a->started || !a->writer) return -1;
    return ca_nearlink_inbound_write(a->writer, payload);
}
size_t ca_mem_nearlink_adapter_sent_count(const ca_mem_nearlink_adapter_t *a) {
    return a ? a->sent_count : 0;
}

/* ===========================================================================
 * NearLinkTransport
 * =========================================================================== */

struct ca_nearlink_transport {
    ca_nearlink_adapter_t        adapter;      /* borrowed vtable */
    payload_fifo_t               inbound;
    bool                         inbound_open;
    ca_nearlink_inbound_writer_t writer;
};

ca_nearlink_transport_t *ca_nearlink_transport_create(
    ca_nearlink_adapter_t adapter) {
    ca_nearlink_transport_t *t =
        (ca_nearlink_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->adapter = adapter;
    t->inbound_open = true;
    t->writer.queue = &t->inbound;
    t->writer.open = &t->inbound_open;
    return t;
}

void ca_nearlink_transport_destroy(ca_nearlink_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t);
}

static ca_transport_kind_t nl_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_NEARLINK;
}
static bool nl_available(void *self) {
    ca_nearlink_transport_t *t = (ca_nearlink_transport_t *)self;
    return t->adapter.is_available
               ? t->adapter.is_available(t->adapter.self)
               : false;
}
static int nl_start(void *self) {
    /* await _adapter.StartAsync(_inbound.Writer, ct) */
    ca_nearlink_transport_t *t = (ca_nearlink_transport_t *)self;
    if (!t->adapter.start) return -1;
    return t->adapter.start(t->adapter.self, &t->writer);
}
static int nl_stop(void *self) {
    /* await _adapter.StopAsync(ct); _inbound.Writer.TryComplete(); */
    ca_nearlink_transport_t *t = (ca_nearlink_transport_t *)self;
    int rc = t->adapter.stop ? t->adapter.stop(t->adapter.self) : 0;
    t->inbound_open = false;
    return rc;
}
static int nl_send(void *self, const ca_network_payload_t *payload) {
    /* return _adapter.SendAsync(payload, ct); */
    ca_nearlink_transport_t *t = (ca_nearlink_transport_t *)self;
    if (!payload) return -1;
    if (!t->adapter.send) return -1;
    return t->adapter.send(t->adapter.self, payload);
}
static bool nl_receive_next(void *self, ca_network_payload_t **out) {
    ca_nearlink_transport_t *t = (ca_nearlink_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_nearlink_transport_as_transport(
    ca_nearlink_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = nl_kind;
    v.is_available = nl_available;
    v.start = nl_start;
    v.stop = nl_stop;
    v.send = nl_send;
    v.receive_next = nl_receive_next;
    return v;
}

size_t ca_nearlink_transport_pending(const ca_nearlink_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}
