/*
 * net_websocket.c — CircleAI.Networking.WebSocket (C11 port).
 *
 * WebSocketLinkState / WebSocketMessageType, the endpoint/frame records, the
 * InMemoryWebSocketSessionRegistry, the injected IWebSocketAdapter seam + a
 * deterministic loopback adapter, and WebSocketTransport (INetworkTransport).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_websocket.h"

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

static ca_net_metadata_pair_t *dup_pairs(const ca_net_metadata_pair_t *src,
                                         size_t count, bool *ok) {
    *ok = true;
    if (count == 0) return NULL;
    ca_net_metadata_pair_t *out =
        (ca_net_metadata_pair_t *)calloc(count, sizeof(*out));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i].key = dup_or_empty(src ? src[i].key : NULL);
        out[i].value = dup_or_empty(src ? src[i].value : NULL);
        if (!out[i].key || !out[i].value) {
            for (size_t j = 0; j <= i; ++j) { free(out[j].key); free(out[j].value); }
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}
static void free_pairs(ca_net_metadata_pair_t *p, size_t n) {
    if (!p) return;
    for (size_t i = 0; i < n; ++i) { free(p[i].key); free(p[i].value); }
    free(p);
}

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
 * WebSocketEndpointDescriptor
 * =========================================================================== */

ca_ws_endpoint_descriptor_t *ca_ws_endpoint_descriptor_new(
    const char *uri, bool has_headers, const ca_net_metadata_pair_t *headers,
    size_t header_count, int64_t ping_interval_ms,
    const char *const *subprotocols, size_t subprotocol_count) {
    ca_ws_endpoint_descriptor_t *e =
        (ca_ws_endpoint_descriptor_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->uri = dup_or_empty(uri);
    if (!e->uri) { free(e); return NULL; }
    e->has_headers = has_headers;
    if (has_headers) {
        bool ok = true;
        e->headers = dup_pairs(headers, header_count, &ok);
        if (!ok) { ca_ws_endpoint_descriptor_destroy(e); return NULL; }
        e->header_count = header_count;
    }
    e->ping_interval_ms = ping_interval_ms;
    bool ok2 = true;
    e->subprotocols = dup_str_array(subprotocols, subprotocol_count, &ok2);
    if (!ok2) { ca_ws_endpoint_descriptor_destroy(e); return NULL; }
    e->subprotocol_count = subprotocol_count;
    return e;
}
void ca_ws_endpoint_descriptor_destroy(ca_ws_endpoint_descriptor_t *e) {
    if (!e) return;
    free(e->uri);
    free_pairs(e->headers, e->header_count);
    free_str_array(e->subprotocols, e->subprotocol_count);
    free(e);
}
ca_ws_endpoint_descriptor_t *ca_ws_endpoint_descriptor_copy(
    const ca_ws_endpoint_descriptor_t *e) {
    if (!e) return NULL;
    return ca_ws_endpoint_descriptor_new(
        e->uri, e->has_headers, e->headers, e->header_count,
        e->ping_interval_ms, (const char *const *)e->subprotocols,
        e->subprotocol_count);
}

/* ===========================================================================
 * InMemoryWebSocketSessionRegistry
 * =========================================================================== */

typedef struct {
    char              *session_id; /* owned */
    ca_ws_link_state_t state;
} ws_state_entry_t;

struct ca_ws_registry {
    char                        **ep_ids;   /* owned parallel arrays (LWW) */
    ca_ws_endpoint_descriptor_t **ep_vals;
    size_t                        ep_count;
    size_t                        ep_cap;

    ws_state_entry_t             *states;
    size_t                        state_count;
    size_t                        state_cap;

    ca_ws_frame_summary_t        *frames;
    size_t                        frame_count;
    size_t                        frame_cap;
};

ca_ws_registry_t *ca_ws_registry_create(void) {
    return (ca_ws_registry_t *)calloc(1, sizeof(ca_ws_registry_t));
}

void ca_ws_registry_destroy(ca_ws_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->ep_count; ++i) {
        free(r->ep_ids[i]);
        ca_ws_endpoint_descriptor_destroy(r->ep_vals[i]);
    }
    free(r->ep_ids);
    free(r->ep_vals);
    for (size_t i = 0; i < r->state_count; ++i) free(r->states[i].session_id);
    free(r->states);
    for (size_t i = 0; i < r->frame_count; ++i) free(r->frames[i].session_id);
    free(r->frames);
    free(r);
}

static ptrdiff_t ws_ep_index(const ca_ws_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->ep_count; ++i)
        if (strcmp(r->ep_ids[i], id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_ws_registry_register(ca_ws_registry_t *r, const char *session_id,
                            const ca_ws_endpoint_descriptor_t *d) {
    if (!r || !session_id || !d) return -1;
    ca_ws_endpoint_descriptor_t *copy = ca_ws_endpoint_descriptor_copy(d);
    if (!copy) return -1;
    ptrdiff_t idx = ws_ep_index(r, session_id);
    if (idx >= 0) {
        ca_ws_endpoint_descriptor_destroy(r->ep_vals[idx]);
        r->ep_vals[idx] = copy;
        return 0;
    }
    if (r->ep_count == r->ep_cap) {
        size_t nc = r->ep_cap ? r->ep_cap * 2 : 4;
        char **ni = (char **)realloc(r->ep_ids, nc * sizeof(*ni));
        if (!ni) { ca_ws_endpoint_descriptor_destroy(copy); return -1; }
        r->ep_ids = ni;
        ca_ws_endpoint_descriptor_t **nv =
            (ca_ws_endpoint_descriptor_t **)realloc(r->ep_vals,
                                                    nc * sizeof(*nv));
        if (!nv) { ca_ws_endpoint_descriptor_destroy(copy); return -1; }
        r->ep_vals = nv;
        r->ep_cap = nc;
    }
    char *kid = dup_or_empty(session_id);
    if (!kid) { ca_ws_endpoint_descriptor_destroy(copy); return -1; }
    r->ep_ids[r->ep_count] = kid;
    r->ep_vals[r->ep_count] = copy;
    r->ep_count++;
    return 0;
}

ca_ws_endpoint_descriptor_t *ca_ws_registry_get(const ca_ws_registry_t *r,
                                                const char *session_id) {
    if (!r || !session_id) return NULL;
    ptrdiff_t idx = ws_ep_index(r, session_id);
    if (idx < 0) return NULL;
    return ca_ws_endpoint_descriptor_copy(r->ep_vals[idx]);
}

static ptrdiff_t ws_state_index(const ca_ws_registry_t *r, const char *id) {
    for (size_t i = 0; i < r->state_count; ++i)
        if (strcmp(r->states[i].session_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

void ca_ws_registry_set_state(ca_ws_registry_t *r, const char *session_id,
                              ca_ws_link_state_t s) {
    if (!r || !session_id) return;
    ptrdiff_t idx = ws_state_index(r, session_id);
    if (idx >= 0) { r->states[idx].state = s; return; }
    if (r->state_count == r->state_cap) {
        size_t nc = r->state_cap ? r->state_cap * 2 : 4;
        ws_state_entry_t *ns =
            (ws_state_entry_t *)realloc(r->states, nc * sizeof(*ns));
        if (!ns) return;
        r->states = ns;
        r->state_cap = nc;
    }
    char *kid = dup_or_empty(session_id);
    if (!kid) return;
    r->states[r->state_count].session_id = kid;
    r->states[r->state_count].state = s;
    r->state_count++;
}

ca_ws_link_state_t ca_ws_registry_state(const ca_ws_registry_t *r,
                                        const char *session_id) {
    if (!r || !session_id) return CA_WS_STATE_CLOSED;
    ptrdiff_t idx = ws_state_index(r, session_id);
    return idx < 0 ? CA_WS_STATE_CLOSED : r->states[idx].state;
}

int ca_ws_registry_record_frame(ca_ws_registry_t *r, const char *session_id,
                                ca_ws_message_type_t type, int bytes,
                                int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->frame_count == r->frame_cap) {
        size_t nc = r->frame_cap ? r->frame_cap * 2 : 4;
        ca_ws_frame_summary_t *nf =
            (ca_ws_frame_summary_t *)realloc(r->frames, nc * sizeof(*nf));
        if (!nf) return -1;
        r->frames = nf;
        r->frame_cap = nc;
    }
    ca_ws_frame_summary_t *f = &r->frames[r->frame_count];
    f->session_id = dup_or_empty(session_id);
    if (!f->session_id) return -1;
    f->type = type;
    f->bytes = bytes;
    f->at_unix_ms = at_unix_ms;
    r->frame_count++;
    return 0;
}

int64_t ca_ws_registry_total_bytes(const ca_ws_registry_t *r,
                                   const char *session_id) {
    if (!r || !session_id) return 0;
    int64_t sum = 0;
    for (size_t i = 0; i < r->frame_count; ++i)
        if (strcmp(r->frames[i].session_id, session_id) == 0)
            sum += (int64_t)r->frames[i].bytes;
    return sum;
}

int ca_ws_registry_frame_count(const ca_ws_registry_t *r,
                               const char *session_id,
                               ca_ws_message_type_t type) {
    if (!r || !session_id) return 0;
    int n = 0;
    for (size_t i = 0; i < r->frame_count; ++i)
        if (strcmp(r->frames[i].session_id, session_id) == 0 &&
            r->frames[i].type == type)
            n++;
    return n;
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
 * Feed — the seam the adapter uses to push received frames upward.
 * =========================================================================== */

struct ca_ws_feed {
    payload_fifo_t *inbound;  /* borrowed — owned by the transport */
    bool           *open;     /* borrowed open flag */
};

int ca_ws_feed(ca_ws_feed_t *feed, const uint8_t *data, size_t len) {
    if (!feed) return -1;
    if (feed->open && !*feed->open) return -1;
    char id[33];
    ca_net_new_guid_n(id);
    ca_network_payload_t *p = ca_network_payload_create(
        data, len, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, 0, id);
    if (!p) return -1;
    if (!pf_push(feed->inbound, p)) {
        ca_network_payload_destroy(p);
        return -1;
    }
    return 0;
}

int ca_ws_feed_close(ca_ws_feed_t *feed) {
    if (!feed) return -1;
    if (feed->open) *feed->open = false; /* Close frame breaks the pump loop */
    return 0;
}

/* ===========================================================================
 * In-memory IWebSocketAdapter (loopback)
 * =========================================================================== */

struct ca_mem_ws_adapter {
    ca_ws_link_state_t state;
    bool               loopback;
    ca_ws_feed_t      *feed;      /* borrowed, set on connect */
    size_t             send_count;
};

ca_mem_ws_adapter_t *ca_mem_ws_adapter_create(bool start_open, bool loopback) {
    ca_mem_ws_adapter_t *a = (ca_mem_ws_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->state = start_open ? CA_WS_STATE_OPEN : CA_WS_STATE_CLOSED;
    a->loopback = loopback;
    return a;
}
void ca_mem_ws_adapter_destroy(ca_mem_ws_adapter_t *a) { free(a); }
void ca_mem_ws_adapter_set_state(ca_mem_ws_adapter_t *a, ca_ws_link_state_t s) {
    if (a) a->state = s;
}

static ca_ws_link_state_t mwa_state(void *self) {
    return ((ca_mem_ws_adapter_t *)self)->state;
}
static int mwa_connect(void *self, ca_ws_feed_t *feed) {
    ca_mem_ws_adapter_t *a = (ca_mem_ws_adapter_t *)self;
    a->feed = feed;
    a->state = CA_WS_STATE_OPEN;
    return 0;
}
static int mwa_send(void *self, const uint8_t *data, size_t len) {
    ca_mem_ws_adapter_t *a = (ca_mem_ws_adapter_t *)self;
    a->send_count++;
    if (a->loopback && a->feed) return ca_ws_feed(a->feed, data, len);
    return 0;
}
static int mwa_close(void *self) {
    ca_mem_ws_adapter_t *a = (ca_mem_ws_adapter_t *)self;
    a->state = CA_WS_STATE_CLOSE_SENT;
    return 0;
}

ca_ws_adapter_t ca_mem_ws_adapter_as_adapter(ca_mem_ws_adapter_t *a) {
    ca_ws_adapter_t v;
    v.self = a;
    v.state = mwa_state;
    v.connect = mwa_connect;
    v.send = mwa_send;
    v.close = mwa_close;
    return v;
}

int ca_mem_ws_adapter_deliver(ca_mem_ws_adapter_t *a, const uint8_t *data,
                              size_t len) {
    if (!a) return -1;
    if (a->state != CA_WS_STATE_OPEN || !a->feed) return -1;
    return ca_ws_feed(a->feed, data, len);
}
int ca_mem_ws_adapter_deliver_close(ca_mem_ws_adapter_t *a) {
    if (!a || !a->feed) return -1;
    return ca_ws_feed_close(a->feed);
}
size_t ca_mem_ws_adapter_send_count(const ca_mem_ws_adapter_t *a) {
    return a ? a->send_count : 0;
}

/* ===========================================================================
 * WebSocketTransport
 * =========================================================================== */

struct ca_ws_transport {
    ca_ws_adapter_t adapter;       /* borrowed vtable */
    payload_fifo_t  inbound;
    bool            inbound_open;
    ca_ws_feed_t    feed;          /* points at inbound + inbound_open */
    bool            connected;     /* connect() succeeded (ClientWebSocket set) */
};

ca_ws_transport_t *ca_ws_transport_create(ca_ws_adapter_t adapter) {
    ca_ws_transport_t *t = (ca_ws_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->adapter = adapter;
    t->inbound_open = true;
    t->feed.inbound = &t->inbound;
    t->feed.open = &t->inbound_open;
    return t;
}

void ca_ws_transport_destroy(ca_ws_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t);
}

static ca_transport_kind_t ws_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_WEBSOCKET;
}
static bool ws_available(void *self) {
    /* IsAvailable => _ws?.State == Open */
    ca_ws_transport_t *t = (ca_ws_transport_t *)self;
    if (!t->connected) return false;
    if (!t->adapter.state) return false;
    return t->adapter.state(t->adapter.self) == CA_WS_STATE_OPEN;
}
static int ws_start(void *self) {
    /* _ws = new ClientWebSocket(); await ConnectAsync(uri); _ = PumpAsync(); */
    ca_ws_transport_t *t = (ca_ws_transport_t *)self;
    if (!t->adapter.connect) return -1;
    int rc = t->adapter.connect(t->adapter.self, &t->feed);
    if (rc == 0) t->connected = true;
    return rc;
}
static int ws_stop(void *self) {
    /* if (_ws != null) await CloseAsync(NormalClosure, "stop");
       _inbound.Writer.TryComplete(); */
    ca_ws_transport_t *t = (ca_ws_transport_t *)self;
    int rc = 0;
    if (t->connected)
        rc = t->adapter.close ? t->adapter.close(t->adapter.self) : 0;
    t->inbound_open = false;
    return rc;
}
static int ws_send(void *self, const ca_network_payload_t *payload) {
    /* ArgumentNullException.ThrowIfNull(_ws);
       await SendAsync(Data, Binary, endOfMessage:true); */
    ca_ws_transport_t *t = (ca_ws_transport_t *)self;
    if (!payload) return -1;
    if (!t->connected) return -1; /* _ws is null -> ThrowIfNull */
    if (!t->adapter.send) return -1;
    return t->adapter.send(t->adapter.self, payload->data, payload->data_len);
}
static bool ws_receive_next(void *self, ca_network_payload_t **out) {
    ca_ws_transport_t *t = (ca_ws_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_ws_transport_as_transport(ca_ws_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = ws_kind;
    v.is_available = ws_available;
    v.start = ws_start;
    v.stop = ws_stop;
    v.send = ws_send;
    v.receive_next = ws_receive_next;
    return v;
}

size_t ca_ws_transport_pending(const ca_ws_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}
