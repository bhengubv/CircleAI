/*
 * net_bluetooth.c — CircleAI.Networking.Bluetooth (C11 port).
 *
 * BLE endpoint/capability/throughput records, the well-known capability presets,
 * the InMemoryBluetoothTransportRegistry, the injected IBleGattAdapter seam + a
 * deterministic in-memory adapter, and BluetoothNetworkTransport (INetworkTransport).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_bluetooth.h"

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
 * BluetoothEndpointDescriptor
 * =========================================================================== */

ca_bt_endpoint_descriptor_t *ca_bt_endpoint_descriptor_new(
    const char *device_id, const char *name, const char *mac_address,
    const char *const *advertised_services, size_t advertised_count) {
    ca_bt_endpoint_descriptor_t *e =
        (ca_bt_endpoint_descriptor_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->device_id = dup_or_empty(device_id);
    e->name = dup_or_empty(name);
    e->mac_address = dup_or_empty(mac_address);
    if (!e->device_id || !e->name || !e->mac_address) {
        ca_bt_endpoint_descriptor_destroy(e);
        return NULL;
    }
    bool ok = true;
    e->advertised_services =
        dup_str_array(advertised_services, advertised_count, &ok);
    if (!ok) { ca_bt_endpoint_descriptor_destroy(e); return NULL; }
    e->advertised_count = advertised_count;
    return e;
}

void ca_bt_endpoint_descriptor_destroy(ca_bt_endpoint_descriptor_t *e) {
    if (!e) return;
    free(e->device_id);
    free(e->name);
    free(e->mac_address);
    free_str_array(e->advertised_services, e->advertised_count);
    free(e);
}

ca_bt_endpoint_descriptor_t *ca_bt_endpoint_descriptor_copy(
    const ca_bt_endpoint_descriptor_t *e) {
    if (!e) return NULL;
    return ca_bt_endpoint_descriptor_new(
        e->device_id, e->name, e->mac_address,
        (const char *const *)e->advertised_services, e->advertised_count);
}

/* ===========================================================================
 * BluetoothCapabilityProfile
 * =========================================================================== */

ca_bt_capability_profile_t *ca_bt_capability_profile_new(
    int max_mtu_bytes, bool supports_secure_connections,
    bool supports_high_speed, const char *const *compatible_profiles,
    size_t compatible_count) {
    ca_bt_capability_profile_t *p =
        (ca_bt_capability_profile_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->max_mtu_bytes = max_mtu_bytes;
    p->supports_secure_connections = supports_secure_connections;
    p->supports_high_speed = supports_high_speed;
    bool ok = true;
    p->compatible_profiles =
        dup_str_array(compatible_profiles, compatible_count, &ok);
    if (!ok) { free(p); return NULL; }
    p->compatible_count = compatible_count;
    return p;
}

void ca_bt_capability_profile_destroy(ca_bt_capability_profile_t *p) {
    if (!p) return;
    free_str_array(p->compatible_profiles, p->compatible_count);
    free(p);
}

ca_bt_capability_profile_t *ca_bt_capability_profile_copy(
    const ca_bt_capability_profile_t *p) {
    if (!p) return NULL;
    return ca_bt_capability_profile_new(
        p->max_mtu_bytes, p->supports_secure_connections,
        p->supports_high_speed,
        (const char *const *)p->compatible_profiles, p->compatible_count);
}

ca_bt_capability_profile_t *ca_bt_capability_profiles_le5(void) {
    const char *svc[] = { "GATT", "L2CAP" };
    return ca_bt_capability_profile_new(247, true, true, svc, 2);
}
ca_bt_capability_profile_t *ca_bt_capability_profiles_le4(void) {
    const char *svc[] = { "GATT" };
    return ca_bt_capability_profile_new(23, true, false, svc, 1);
}
ca_bt_capability_profile_t *ca_bt_capability_profiles_classic(void) {
    const char *svc[] = { "SPP", "RFCOMM" };
    return ca_bt_capability_profile_new(1024, true, false, svc, 2);
}

/* ===========================================================================
 * InMemoryBluetoothTransportRegistry
 * =========================================================================== */

typedef struct {
    char                    *device_id; /* owned */
    ca_bt_connection_state_t state;
} bt_state_entry_t;

struct ca_bt_registry {
    ca_bt_endpoint_descriptor_t **endpoints; /* owned array (LWW by device_id) */
    size_t                        ep_count;
    size_t                        ep_cap;

    bt_state_entry_t             *states;     /* owned array */
    size_t                        state_count;
    size_t                        state_cap;

    ca_bt_throughput_sample_t    *tp;         /* owned array */
    size_t                        tp_count;
    size_t                        tp_cap;
};

ca_bt_registry_t *ca_bt_registry_create(void) {
    return (ca_bt_registry_t *)calloc(1, sizeof(ca_bt_registry_t));
}

void ca_bt_registry_destroy(ca_bt_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->ep_count; ++i)
        ca_bt_endpoint_descriptor_destroy(r->endpoints[i]);
    free(r->endpoints);
    for (size_t i = 0; i < r->state_count; ++i) free(r->states[i].device_id);
    free(r->states);
    for (size_t i = 0; i < r->tp_count; ++i) free(r->tp[i].device_id);
    free(r->tp);
    free(r);
}

static ptrdiff_t reg_ep_index(const ca_bt_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->ep_count; ++i)
        if (strcmp(r->endpoints[i]->device_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_bt_registry_register(ca_bt_registry_t *r,
                            const ca_bt_endpoint_descriptor_t *e) {
    if (!r || !e) return -1;
    ca_bt_endpoint_descriptor_t *copy = ca_bt_endpoint_descriptor_copy(e);
    if (!copy) return -1;
    ptrdiff_t idx = reg_ep_index(r, e->device_id);
    if (idx >= 0) {
        ca_bt_endpoint_descriptor_destroy(r->endpoints[idx]);
        r->endpoints[idx] = copy;
        return 0;
    }
    if (r->ep_count == r->ep_cap) {
        size_t nc = r->ep_cap ? r->ep_cap * 2 : 4;
        ca_bt_endpoint_descriptor_t **np =
            (ca_bt_endpoint_descriptor_t **)realloc(r->endpoints,
                                                    nc * sizeof(*np));
        if (!np) { ca_bt_endpoint_descriptor_destroy(copy); return -1; }
        r->endpoints = np;
        r->ep_cap = nc;
    }
    r->endpoints[r->ep_count++] = copy;
    return 0;
}

ca_bt_endpoint_descriptor_t *ca_bt_registry_get_endpoint(
    const ca_bt_registry_t *r, const char *device_id) {
    if (!r || !device_id) return NULL;
    ptrdiff_t idx = reg_ep_index(r, device_id);
    if (idx < 0) return NULL;
    return ca_bt_endpoint_descriptor_copy(r->endpoints[idx]);
}

typedef struct { const ca_bt_endpoint_descriptor_t *e; size_t ord; } ep_ref_t;
static int cmp_ep_by_name(const void *a, const void *b) {
    const ep_ref_t *ra = (const ep_ref_t *)a;
    const ep_ref_t *rb = (const ep_ref_t *)b;
    int c = strcmp(ra->e->name, rb->e->name);
    if (c != 0) return c;
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

int ca_bt_registry_all_endpoints(const ca_bt_registry_t *r,
                                 ca_bt_endpoint_descriptor_t ***out,
                                 size_t *count) {
    if (!r || !out || !count) { if (out) *out = NULL; if (count) *count = SIZE_MAX; return -1; }
    if (r->ep_count == 0) { *out = NULL; *count = 0; return 0; }
    ep_ref_t *refs = (ep_ref_t *)calloc(r->ep_count, sizeof(*refs));
    if (!refs) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->ep_count; ++i) {
        refs[i].e = r->endpoints[i];
        refs[i].ord = i;
    }
    qsort(refs, r->ep_count, sizeof(*refs), cmp_ep_by_name);
    ca_bt_endpoint_descriptor_t **arr =
        (ca_bt_endpoint_descriptor_t **)calloc(r->ep_count, sizeof(*arr));
    if (!arr) { free(refs); *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->ep_count; ++i) {
        arr[i] = ca_bt_endpoint_descriptor_copy(refs[i].e);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j)
                ca_bt_endpoint_descriptor_destroy(arr[j]);
            free(arr); free(refs);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    free(refs);
    *out = arr;
    *count = r->ep_count;
    return 0;
}

static ptrdiff_t reg_state_index(const ca_bt_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->state_count; ++i)
        if (strcmp(r->states[i].device_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

void ca_bt_registry_set_state(ca_bt_registry_t *r, const char *device_id,
                              ca_bt_connection_state_t s) {
    if (!r || !device_id) return;
    ptrdiff_t idx = reg_state_index(r, device_id);
    if (idx >= 0) { r->states[idx].state = s; return; }
    if (r->state_count == r->state_cap) {
        size_t nc = r->state_cap ? r->state_cap * 2 : 4;
        bt_state_entry_t *ns =
            (bt_state_entry_t *)realloc(r->states, nc * sizeof(*ns));
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

ca_bt_connection_state_t ca_bt_registry_state(const ca_bt_registry_t *r,
                                              const char *device_id) {
    if (!r || !device_id) return CA_BT_STATE_DISCONNECTED;
    ptrdiff_t idx = reg_state_index(r, device_id);
    return idx < 0 ? CA_BT_STATE_DISCONNECTED : r->states[idx].state;
}

int ca_bt_registry_record_throughput(ca_bt_registry_t *r, const char *device_id,
                                     double kbps_read, double kbps_write,
                                     int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->tp_count == r->tp_cap) {
        size_t nc = r->tp_cap ? r->tp_cap * 2 : 4;
        ca_bt_throughput_sample_t *nt =
            (ca_bt_throughput_sample_t *)realloc(r->tp, nc * sizeof(*nt));
        if (!nt) return -1;
        r->tp = nt;
        r->tp_cap = nc;
    }
    ca_bt_throughput_sample_t *s = &r->tp[r->tp_count];
    s->device_id = dup_or_empty(device_id);
    if (!s->device_id) return -1;
    s->kbps_read = kbps_read;
    s->kbps_write = kbps_write;
    s->at_unix_ms = at_unix_ms;
    r->tp_count++;
    return 0;
}

double ca_bt_registry_avg_kbps_read(const ca_bt_registry_t *r,
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
    return n == 0 ? 0.0 : sum / (double)n;
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
 * Inbound writer — the ChannelWriter<NetworkPayload> seam handed to the adapter
 * =========================================================================== */

struct ca_bt_inbound_writer {
    payload_fifo_t *queue;   /* borrowed — owned by the transport */
    bool           *open;    /* borrowed open flag */
};

int ca_bt_inbound_write(ca_bt_inbound_writer_t *writer,
                        const ca_network_payload_t *payload) {
    if (!writer || !payload) return -1;
    if (writer->open && !*writer->open) return -1; /* completed */
    ca_network_payload_t *copy = ca_network_payload_copy(payload);
    if (!copy) return -1;
    if (!pf_push(writer->queue, copy)) {
        ca_network_payload_destroy(copy);
        return -1;
    }
    return 0;
}

/* ===========================================================================
 * In-memory IBleGattAdapter
 * =========================================================================== */

struct ca_mem_ble_adapter {
    bool                    is_available;
    bool                    started;
    ca_bt_inbound_writer_t *writer;  /* borrowed, set on start */
    size_t                  sent_count;
};

ca_mem_ble_adapter_t *ca_mem_ble_adapter_create(bool is_available) {
    ca_mem_ble_adapter_t *a =
        (ca_mem_ble_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->is_available = is_available;
    return a;
}

void ca_mem_ble_adapter_destroy(ca_mem_ble_adapter_t *a) { free(a); }

void ca_mem_ble_adapter_set_available(ca_mem_ble_adapter_t *a, bool v) {
    if (a) a->is_available = v;
}

static bool mba_is_available(void *self) {
    return ((ca_mem_ble_adapter_t *)self)->is_available;
}
static int mba_start(void *self, ca_bt_inbound_writer_t *writer) {
    ca_mem_ble_adapter_t *a = (ca_mem_ble_adapter_t *)self;
    a->writer = writer;
    a->started = true;
    return 0;
}
static int mba_stop(void *self) {
    ca_mem_ble_adapter_t *a = (ca_mem_ble_adapter_t *)self;
    a->started = false;
    a->writer = NULL;
    return 0;
}
static int mba_write(void *self, const ca_network_payload_t *payload) {
    ca_mem_ble_adapter_t *a = (ca_mem_ble_adapter_t *)self;
    if (!payload) return -1;
    a->sent_count++;
    return 0;
}

ca_ble_gatt_adapter_t ca_mem_ble_adapter_as_adapter(ca_mem_ble_adapter_t *a) {
    ca_ble_gatt_adapter_t v;
    v.self = a;
    v.is_available = mba_is_available;
    v.start = mba_start;
    v.stop = mba_stop;
    v.write = mba_write;
    return v;
}

int ca_mem_ble_adapter_deliver(ca_mem_ble_adapter_t *a,
                               const ca_network_payload_t *payload) {
    if (!a || !payload) return -1;
    if (!a->started || !a->writer) return -1;
    return ca_bt_inbound_write(a->writer, payload);
}

size_t ca_mem_ble_adapter_sent_count(const ca_mem_ble_adapter_t *a) {
    return a ? a->sent_count : 0;
}

/* ===========================================================================
 * BluetoothNetworkTransport
 * =========================================================================== */

struct ca_bt_transport {
    ca_ble_gatt_adapter_t  adapter;      /* borrowed vtable */
    payload_fifo_t         inbound;
    bool                   inbound_open;
    ca_bt_inbound_writer_t writer;       /* points at inbound + inbound_open */
};

ca_bt_transport_t *ca_bt_transport_create(ca_ble_gatt_adapter_t adapter) {
    ca_bt_transport_t *t = (ca_bt_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->adapter = adapter;
    t->inbound_open = true;
    t->writer.queue = &t->inbound;
    t->writer.open = &t->inbound_open;
    return t;
}

void ca_bt_transport_destroy(ca_bt_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t);
}

static ca_transport_kind_t bt_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_BLUETOOTH;
}
static bool bt_available(void *self) {
    ca_bt_transport_t *t = (ca_bt_transport_t *)self;
    return t->adapter.is_available ? t->adapter.is_available(t->adapter.self)
                                   : false;
}
static int bt_start(void *self) {
    /* await _adapter.StartAsync(_inbound.Writer, ct) */
    ca_bt_transport_t *t = (ca_bt_transport_t *)self;
    if (!t->adapter.start) return -1;
    return t->adapter.start(t->adapter.self, &t->writer);
}
static int bt_stop(void *self) {
    /* await _adapter.StopAsync(ct); _inbound.Writer.TryComplete(); */
    ca_bt_transport_t *t = (ca_bt_transport_t *)self;
    int rc = t->adapter.stop ? t->adapter.stop(t->adapter.self) : 0;
    t->inbound_open = false;
    return rc;
}
static int bt_send(void *self, const ca_network_payload_t *payload) {
    /* _adapter.WriteAsync(payload, ct) */
    ca_bt_transport_t *t = (ca_bt_transport_t *)self;
    if (!payload) return -1;
    if (!t->adapter.write) return -1;
    return t->adapter.write(t->adapter.self, payload);
}
static bool bt_receive_next(void *self, ca_network_payload_t **out) {
    ca_bt_transport_t *t = (ca_bt_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_bt_transport_as_transport(ca_bt_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = bt_kind;
    v.is_available = bt_available;
    v.start = bt_start;
    v.stop = bt_stop;
    v.send = bt_send;
    v.receive_next = bt_receive_next;
    return v;
}

size_t ca_bt_transport_pending(const ca_bt_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}
