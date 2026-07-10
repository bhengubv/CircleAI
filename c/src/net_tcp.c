/*
 * net_tcp.c — CircleAI.Networking.Tcp (C11 port).
 *
 * TcpConnectionState, the endpoint/throughput records, TcpKnownPorts, the
 * InMemoryTcpConnectionRegistry, the injected ITcpStreamAdapter seam + a
 * deterministic loopback adapter, and TcpNetworkTransport (INetworkTransport)
 * with 4-byte little-endian length-prefix framing.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_tcp.h"

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
 * TcpEndpointDescriptor
 * =========================================================================== */

ca_tcp_endpoint_descriptor_t *ca_tcp_endpoint_descriptor_new(
    const char *host, int port, bool no_delay, bool keep_alive,
    int64_t connect_timeout_ms) {
    ca_tcp_endpoint_descriptor_t *e =
        (ca_tcp_endpoint_descriptor_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->host = dup_or_empty(host);
    if (!e->host) { free(e); return NULL; }
    e->port = port;
    e->no_delay = no_delay;
    e->keep_alive = keep_alive;
    e->connect_timeout_ms = connect_timeout_ms;
    return e;
}
void ca_tcp_endpoint_descriptor_destroy(ca_tcp_endpoint_descriptor_t *e) {
    if (!e) return;
    free(e->host);
    free(e);
}
ca_tcp_endpoint_descriptor_t *ca_tcp_endpoint_descriptor_copy(
    const ca_tcp_endpoint_descriptor_t *e) {
    if (!e) return NULL;
    return ca_tcp_endpoint_descriptor_new(e->host, e->port, e->no_delay,
                                          e->keep_alive, e->connect_timeout_ms);
}

/* ===========================================================================
 * InMemoryTcpConnectionRegistry
 * =========================================================================== */

typedef struct {
    char                     *id;    /* owned */
    ca_tcp_connection_state_t state;
} tcp_state_entry_t;

struct ca_tcp_registry {
    char                         **ep_ids;   /* owned parallel arrays (LWW by id) */
    ca_tcp_endpoint_descriptor_t **ep_vals;
    size_t                         ep_count;
    size_t                         ep_cap;

    tcp_state_entry_t             *states;
    size_t                         state_count;
    size_t                         state_cap;

    ca_tcp_throughput_sample_t    *tp;
    size_t                         tp_count;
    size_t                         tp_cap;
};

ca_tcp_registry_t *ca_tcp_registry_create(void) {
    return (ca_tcp_registry_t *)calloc(1, sizeof(ca_tcp_registry_t));
}

void ca_tcp_registry_destroy(ca_tcp_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->ep_count; ++i) {
        free(r->ep_ids[i]);
        ca_tcp_endpoint_descriptor_destroy(r->ep_vals[i]);
    }
    free(r->ep_ids);
    free(r->ep_vals);
    for (size_t i = 0; i < r->state_count; ++i) free(r->states[i].id);
    free(r->states);
    for (size_t i = 0; i < r->tp_count; ++i) free(r->tp[i].endpoint_id);
    free(r->tp);
    free(r);
}

static ptrdiff_t tcp_ep_index(const ca_tcp_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->ep_count; ++i)
        if (strcmp(r->ep_ids[i], id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_tcp_registry_register(ca_tcp_registry_t *r, const char *id,
                             const ca_tcp_endpoint_descriptor_t *d) {
    if (!r || !id || !d) return -1;
    ca_tcp_endpoint_descriptor_t *copy = ca_tcp_endpoint_descriptor_copy(d);
    if (!copy) return -1;
    ptrdiff_t idx = tcp_ep_index(r, id);
    if (idx >= 0) {
        ca_tcp_endpoint_descriptor_destroy(r->ep_vals[idx]);
        r->ep_vals[idx] = copy;
        return 0;
    }
    if (r->ep_count == r->ep_cap) {
        size_t nc = r->ep_cap ? r->ep_cap * 2 : 4;
        char **ni = (char **)realloc(r->ep_ids, nc * sizeof(*ni));
        if (!ni) { ca_tcp_endpoint_descriptor_destroy(copy); return -1; }
        r->ep_ids = ni;
        ca_tcp_endpoint_descriptor_t **nv =
            (ca_tcp_endpoint_descriptor_t **)realloc(r->ep_vals,
                                                     nc * sizeof(*nv));
        if (!nv) { ca_tcp_endpoint_descriptor_destroy(copy); return -1; }
        r->ep_vals = nv;
        r->ep_cap = nc;
    }
    char *kid = dup_or_empty(id);
    if (!kid) { ca_tcp_endpoint_descriptor_destroy(copy); return -1; }
    r->ep_ids[r->ep_count] = kid;
    r->ep_vals[r->ep_count] = copy;
    r->ep_count++;
    return 0;
}

ca_tcp_endpoint_descriptor_t *ca_tcp_registry_get(const ca_tcp_registry_t *r,
                                                  const char *id) {
    if (!r || !id) return NULL;
    ptrdiff_t idx = tcp_ep_index(r, id);
    if (idx < 0) return NULL;
    return ca_tcp_endpoint_descriptor_copy(r->ep_vals[idx]);
}

static ptrdiff_t tcp_state_index(const ca_tcp_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->state_count; ++i)
        if (strcmp(r->states[i].id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

void ca_tcp_registry_set_state(ca_tcp_registry_t *r, const char *id,
                               ca_tcp_connection_state_t s) {
    if (!r || !id) return;
    ptrdiff_t idx = tcp_state_index(r, id);
    if (idx >= 0) { r->states[idx].state = s; return; }
    if (r->state_count == r->state_cap) {
        size_t nc = r->state_cap ? r->state_cap * 2 : 4;
        tcp_state_entry_t *ns =
            (tcp_state_entry_t *)realloc(r->states, nc * sizeof(*ns));
        if (!ns) return;
        r->states = ns;
        r->state_cap = nc;
    }
    char *kid = dup_or_empty(id);
    if (!kid) return;
    r->states[r->state_count].id = kid;
    r->states[r->state_count].state = s;
    r->state_count++;
}

ca_tcp_connection_state_t ca_tcp_registry_state(const ca_tcp_registry_t *r,
                                                const char *id) {
    if (!r || !id) return CA_TCP_STATE_DISCONNECTED;
    ptrdiff_t idx = tcp_state_index(r, id);
    return idx < 0 ? CA_TCP_STATE_DISCONNECTED : r->states[idx].state;
}

int ca_tcp_registry_record_sample(ca_tcp_registry_t *r, const char *endpoint_id,
                                  int64_t bytes_sent, int64_t bytes_received,
                                  int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->tp_count == r->tp_cap) {
        size_t nc = r->tp_cap ? r->tp_cap * 2 : 4;
        ca_tcp_throughput_sample_t *nt =
            (ca_tcp_throughput_sample_t *)realloc(r->tp, nc * sizeof(*nt));
        if (!nt) return -1;
        r->tp = nt;
        r->tp_cap = nc;
    }
    ca_tcp_throughput_sample_t *s = &r->tp[r->tp_count];
    s->endpoint_id = dup_or_empty(endpoint_id);
    if (!s->endpoint_id) return -1;
    s->bytes_sent = bytes_sent;
    s->bytes_received = bytes_received;
    s->at_unix_ms = at_unix_ms;
    r->tp_count++;
    return 0;
}

int64_t ca_tcp_registry_total_bytes_sent(const ca_tcp_registry_t *r,
                                         const char *id) {
    if (!r || !id) return 0;
    int64_t sum = 0;
    for (size_t i = 0; i < r->tp_count; ++i)
        if (strcmp(r->tp[i].endpoint_id, id) == 0) sum += r->tp[i].bytes_sent;
    return sum;
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
 * Receive de-framer — accumulates fed wire bytes and extracts whole
 * [4-byte LE length][length bytes] frames into inbound payloads. Mirrors the
 * C# PumpAsync: ReadExactly(4) -> len (BitConverter little-endian) ->
 * ReadExactly(len) -> NetworkPayload.Create(bytes).
 * =========================================================================== */

typedef struct {
    uint8_t *buf;    /* accumulated undecoded bytes */
    size_t   len;
    size_t   cap;
} recv_accum_t;

static bool ra_append(recv_accum_t *a, const uint8_t *data, size_t len) {
    if (len == 0) return true;
    if (a->len + len > a->cap) {
        size_t nc = a->cap ? a->cap : 64;
        while (nc < a->len + len) nc *= 2;
        uint8_t *nb = (uint8_t *)realloc(a->buf, nc);
        if (!nb) return false;
        a->buf = nb;
        a->cap = nc;
    }
    memcpy(a->buf + a->len, data, len);
    a->len += len;
    return true;
}
/* Drop the first `n` consumed bytes from the front. */
static void ra_consume(recv_accum_t *a, size_t n) {
    if (n >= a->len) { a->len = 0; return; }
    memmove(a->buf, a->buf + n, a->len - n);
    a->len -= n;
}
static void ra_free(recv_accum_t *a) {
    free(a->buf);
    a->buf = NULL;
    a->len = a->cap = 0;
}

/* ===========================================================================
 * Stream feed — the seam the adapter uses to push received wire bytes upward.
 * =========================================================================== */

struct ca_tcp_stream_feed {
    recv_accum_t   *accum;    /* borrowed — owned by the transport */
    payload_fifo_t *inbound;  /* borrowed — owned by the transport */
    bool           *open;     /* borrowed open flag */
};

/* De-frame as many complete frames as are available in `accum` into `inbound`.
 * Returns 0 on success, -1 on OOM. */
static int deframe(recv_accum_t *accum, payload_fifo_t *inbound) {
    for (;;) {
        if (accum->len < 4) return 0;
        /* little-endian int32 length (BitConverter on LE host) */
        uint32_t ulen = (uint32_t)accum->buf[0] |
                        ((uint32_t)accum->buf[1] << 8) |
                        ((uint32_t)accum->buf[2] << 16) |
                        ((uint32_t)accum->buf[3] << 24);
        size_t len = (size_t)ulen;
        if (accum->len < 4 + len) return 0; /* wait for the full body */
        const uint8_t *body = accum->buf + 4;
        char id[33];
        ca_net_new_guid_n(id);
        ca_network_payload_t *p = ca_network_payload_create(
            body, len, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, 0, id);
        if (!p) return -1;
        if (!pf_push(inbound, p)) {
            ca_network_payload_destroy(p);
            return -1;
        }
        ra_consume(accum, 4 + len);
    }
}

int ca_tcp_stream_feed(ca_tcp_stream_feed_t *feed, const uint8_t *data,
                       size_t len) {
    if (!feed) return -1;
    if (feed->open && !*feed->open) return -1;
    if (!ra_append(feed->accum, data, len)) return -1;
    return deframe(feed->accum, feed->inbound);
}

/* ===========================================================================
 * In-memory ITcpStreamAdapter (loopback)
 * =========================================================================== */

struct ca_mem_tcp_adapter {
    bool                  connected;
    bool                  loopback;
    ca_tcp_stream_feed_t *feed;         /* borrowed, set on connect */
    size_t                bytes_written;
};

ca_mem_tcp_adapter_t *ca_mem_tcp_adapter_create(bool start_connected,
                                                bool loopback) {
    ca_mem_tcp_adapter_t *a = (ca_mem_tcp_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->connected = start_connected;
    a->loopback = loopback;
    return a;
}
void ca_mem_tcp_adapter_destroy(ca_mem_tcp_adapter_t *a) { free(a); }
void ca_mem_tcp_adapter_set_connected(ca_mem_tcp_adapter_t *a, bool v) {
    if (a) a->connected = v;
}

static bool mta_is_connected(void *self) {
    return ((ca_mem_tcp_adapter_t *)self)->connected;
}
static int mta_connect(void *self, ca_tcp_stream_feed_t *feed) {
    ca_mem_tcp_adapter_t *a = (ca_mem_tcp_adapter_t *)self;
    a->feed = feed;
    a->connected = true;
    return 0;
}
static int mta_write(void *self, const uint8_t *data, size_t len) {
    ca_mem_tcp_adapter_t *a = (ca_mem_tcp_adapter_t *)self;
    a->bytes_written += len;
    if (a->loopback && a->feed) return ca_tcp_stream_feed(a->feed, data, len);
    return 0;
}
static int mta_close(void *self) {
    ca_mem_tcp_adapter_t *a = (ca_mem_tcp_adapter_t *)self;
    a->connected = false;
    a->feed = NULL;
    return 0;
}

ca_tcp_stream_adapter_t ca_mem_tcp_adapter_as_adapter(ca_mem_tcp_adapter_t *a) {
    ca_tcp_stream_adapter_t v;
    v.self = a;
    v.is_connected = mta_is_connected;
    v.connect = mta_connect;
    v.write = mta_write;
    v.close = mta_close;
    return v;
}

int ca_mem_tcp_adapter_deliver(ca_mem_tcp_adapter_t *a, const uint8_t *data,
                               size_t len) {
    if (!a) return -1;
    if (!a->connected || !a->feed) return -1;
    return ca_tcp_stream_feed(a->feed, data, len);
}
size_t ca_mem_tcp_adapter_bytes_written(const ca_mem_tcp_adapter_t *a) {
    return a ? a->bytes_written : 0;
}

/* ===========================================================================
 * TcpNetworkTransport
 * =========================================================================== */

struct ca_tcp_transport {
    bool                    is_listener;
    bool                    has_adapter;
    ca_tcp_stream_adapter_t adapter;      /* borrowed vtable (client mode) */
    char                   *remote_host;  /* owned (client mode), may be NULL */
    int                     remote_port;
    int                     listen_port;
    bool                    listening;    /* listener started */

    recv_accum_t            accum;
    payload_fifo_t          inbound;
    bool                    inbound_open;
    ca_tcp_stream_feed_t    feed;         /* points at accum + inbound + open */
    bool                    connected;    /* client connect() succeeded */
};

ca_tcp_transport_t *ca_tcp_transport_create_client(
    ca_tcp_stream_adapter_t adapter, const char *remote_host, int remote_port) {
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->is_listener = false;
    t->has_adapter = true;
    t->adapter = adapter;
    t->remote_host = dup_or_null(remote_host);
    t->remote_port = remote_port;
    t->inbound_open = true;
    t->feed.accum = &t->accum;
    t->feed.inbound = &t->inbound;
    t->feed.open = &t->inbound_open;
    return t;
}

ca_tcp_transport_t *ca_tcp_transport_create_listener(int listen_port) {
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->is_listener = true;
    t->has_adapter = false;
    t->listen_port = listen_port;
    t->inbound_open = true;
    t->feed.accum = &t->accum;
    t->feed.inbound = &t->inbound;
    t->feed.open = &t->inbound_open;
    return t;
}

void ca_tcp_transport_destroy(ca_tcp_transport_t *t) {
    if (!t) return;
    ra_free(&t->accum);
    pf_free(&t->inbound);
    free(t->remote_host);
    free(t);
}

static ca_transport_kind_t tcp_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_TCP;
}
static bool tcp_available(void *self) {
    /* IsAvailable => _client?.Connected ?? false. A listener has no client. */
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)self;
    if (t->is_listener || !t->has_adapter) return false;
    if (!t->connected) return false;
    return t->adapter.is_connected ? t->adapter.is_connected(t->adapter.self)
                                   : false;
}
static int tcp_start(void *self) {
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)self;
    if (t->is_listener) {
        /* new TcpListener(IPAddress.Any, port).Start(); */
        t->listening = true;
        return 0;
    }
    if (t->remote_host) {
        /* new TcpClient(); await ConnectAsync(remote); _ = PumpAsync(); */
        if (!t->adapter.connect) return -1;
        int rc = t->adapter.connect(t->adapter.self, &t->feed);
        if (rc == 0) t->connected = true;
        return rc;
    }
    return 0; /* neither remote nor listen — no-op */
}
static int tcp_stop(void *self) {
    /* _stream?.Close(); _client?.Close(); _listener?.Stop();
       _inbound.Writer.TryComplete(); */
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)self;
    int rc = 0;
    if (!t->is_listener && t->has_adapter && t->connected) {
        rc = t->adapter.close ? t->adapter.close(t->adapter.self) : 0;
        t->connected = false;
    }
    t->listening = false;
    t->inbound_open = false;
    return rc;
}
static int tcp_send(void *self, const ca_network_payload_t *payload) {
    /* if (_stream is null) throw InvalidOperationException("Not connected.");
       write 4-byte LE length prefix then the body. */
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)self;
    if (!payload) return -1;
    if (t->is_listener || !t->has_adapter || !t->connected) return -1;
    if (!t->adapter.write) return -1;

    uint32_t n = (uint32_t)payload->data_len;
    uint8_t lenbuf[4];
    lenbuf[0] = (uint8_t)(n & 0xFF);
    lenbuf[1] = (uint8_t)((n >> 8) & 0xFF);
    lenbuf[2] = (uint8_t)((n >> 16) & 0xFF);
    lenbuf[3] = (uint8_t)((n >> 24) & 0xFF);
    /* two separate WriteAsync calls in the C#; mirror that ordering. */
    int rc = t->adapter.write(t->adapter.self, lenbuf, 4);
    if (rc != 0) return rc;
    return t->adapter.write(t->adapter.self, payload->data, payload->data_len);
}
static bool tcp_receive_next(void *self, ca_network_payload_t **out) {
    ca_tcp_transport_t *t = (ca_tcp_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_tcp_transport_as_transport(ca_tcp_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = tcp_kind;
    v.is_available = tcp_available;
    v.start = tcp_start;
    v.stop = tcp_stop;
    v.send = tcp_send;
    v.receive_next = tcp_receive_next;
    return v;
}

size_t ca_tcp_transport_pending(const ca_tcp_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}
bool ca_tcp_transport_is_listener(const ca_tcp_transport_t *t) {
    return t ? t->is_listener : false;
}
