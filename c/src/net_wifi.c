/*
 * net_wifi.c — CircleAI.Networking.WiFi (C11 port).
 *
 * The injected IUdpSocketAdapter seam + a deterministic loopback adapter,
 * WiFiNetworkTransport (INetworkTransport over LAN UDP), and WiFiPeerDiscovery
 * (IPeerDiscovery over UDP broadcast beacons).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_wifi.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

const char *const CA_WIFI_BEACON_MAGIC = "CIRCLEAI:BEACON:";

/* ---- helpers ---- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* IPAddress.TryParse analogue: dotted-quad IPv4 (each octet 0..255, exactly 4)
 * or anything containing ':' (IPv6). Empty / NULL => false. */
bool ca_wifi_is_ip_address(const char *s) {
    if (!s || !*s) return false;
    /* IPv6 if it contains a colon. */
    for (const char *p = s; *p; ++p)
        if (*p == ':') return true;
    /* IPv4 dotted quad. */
    int octets = 0;
    const char *p = s;
    while (*p) {
        if (*p < '0' || *p > '9') return false;
        int val = 0, digits = 0;
        while (*p >= '0' && *p <= '9') {
            val = val * 10 + (*p - '0');
            digits++;
            if (digits > 3) return false;
            p++;
        }
        if (val > 255) return false;
        octets++;
        if (*p == '.') {
            p++;
            if (!*p) return false; /* trailing dot */
        } else if (*p != '\0') {
            return false;
        }
    }
    return octets == 4;
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
 * UDP feed — the seam the adapter uses to push received datagrams upward.
 * =========================================================================== */

struct ca_udp_feed {
    payload_fifo_t *inbound;  /* borrowed — owned by the transport */
    bool           *open;     /* borrowed open flag */
};

int ca_udp_feed(ca_udp_feed_t *feed, const uint8_t *data, size_t len) {
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

/* ===========================================================================
 * In-memory IUdpSocketAdapter (loopback)
 * =========================================================================== */

struct ca_mem_udp_adapter {
    bool           loopback;
    bool           open;
    ca_udp_feed_t *feed;         /* borrowed, set on start */
    size_t         send_count;
    bool           last_broadcast;
    char          *last_dest_ip; /* owned, may be NULL */
    int            last_port;
};

ca_mem_udp_adapter_t *ca_mem_udp_adapter_create(bool loopback) {
    ca_mem_udp_adapter_t *a = (ca_mem_udp_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->loopback = loopback;
    return a;
}
void ca_mem_udp_adapter_destroy(ca_mem_udp_adapter_t *a) {
    if (!a) return;
    free(a->last_dest_ip);
    free(a);
}

static bool mua_is_open(void *self) {
    return ((ca_mem_udp_adapter_t *)self)->open;
}
static int mua_start(void *self, ca_udp_feed_t *feed) {
    ca_mem_udp_adapter_t *a = (ca_mem_udp_adapter_t *)self;
    a->feed = feed;
    a->open = true;
    return 0;
}
static int mua_send(void *self, const uint8_t *data, size_t len,
                    const char *dest_ip, int port) {
    ca_mem_udp_adapter_t *a = (ca_mem_udp_adapter_t *)self;
    char *copy = dup_or_null(dest_ip);
    if (dest_ip && !copy) return -1;
    free(a->last_dest_ip);
    a->last_dest_ip = copy;
    a->last_broadcast = (dest_ip == NULL);
    a->last_port = port;
    a->send_count++;
    if (a->loopback && a->feed) return ca_udp_feed(a->feed, data, len);
    return 0;
}
static int mua_stop(void *self) {
    ca_mem_udp_adapter_t *a = (ca_mem_udp_adapter_t *)self;
    a->open = false;
    a->feed = NULL;
    return 0;
}

ca_udp_socket_adapter_t ca_mem_udp_adapter_as_adapter(ca_mem_udp_adapter_t *a) {
    ca_udp_socket_adapter_t v;
    v.self = a;
    v.is_open = mua_is_open;
    v.start = mua_start;
    v.send = mua_send;
    v.stop = mua_stop;
    return v;
}

int ca_mem_udp_adapter_deliver(ca_mem_udp_adapter_t *a, const uint8_t *data,
                               size_t len) {
    if (!a) return -1;
    if (!a->open || !a->feed) return -1;
    return ca_udp_feed(a->feed, data, len);
}
size_t ca_mem_udp_adapter_send_count(const ca_mem_udp_adapter_t *a) {
    return a ? a->send_count : 0;
}
bool ca_mem_udp_adapter_last_was_broadcast(const ca_mem_udp_adapter_t *a) {
    return a ? a->last_broadcast : false;
}
const char *ca_mem_udp_adapter_last_dest_ip(const ca_mem_udp_adapter_t *a) {
    return a ? a->last_dest_ip : NULL;
}
int ca_mem_udp_adapter_last_port(const ca_mem_udp_adapter_t *a) {
    return a ? a->last_port : 0;
}

/* ===========================================================================
 * WiFiNetworkTransport
 * =========================================================================== */

struct ca_wifi_transport {
    ca_udp_socket_adapter_t adapter;      /* borrowed vtable */
    payload_fifo_t          inbound;
    bool                    inbound_open;
    ca_udp_feed_t           feed;         /* points at inbound + inbound_open */
    bool                    started;
};

ca_wifi_transport_t *ca_wifi_transport_create(ca_udp_socket_adapter_t adapter) {
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->adapter = adapter;
    t->inbound_open = true;
    t->feed.inbound = &t->inbound;
    t->feed.open = &t->inbound_open;
    return t;
}

void ca_wifi_transport_destroy(ca_wifi_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t);
}

static ca_transport_kind_t wifi_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_WIFI;
}
static bool wifi_available(void *self) {
    /* IsAvailable => _receiver is not null (started). */
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)self;
    if (!t->started) return false;
    return t->adapter.is_open ? t->adapter.is_open(t->adapter.self) : false;
}
static int wifi_start(void *self) {
    /* _sender = new UdpClient(); _receiver = new UdpClient(DataPort){Broadcast};
       _ = PumpAsync(); */
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)self;
    if (!t->adapter.start) return -1;
    int rc = t->adapter.start(t->adapter.self, &t->feed);
    if (rc == 0) t->started = true;
    return rc;
}
static int wifi_stop(void *self) {
    /* _receiver?.Close(); _sender?.Close(); _inbound.Writer.TryComplete(); */
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)self;
    int rc = t->adapter.stop ? t->adapter.stop(t->adapter.self) : 0;
    t->started = false;
    t->inbound_open = false;
    return rc;
}
static int wifi_send(void *self, const ca_network_payload_t *payload) {
    /* if (DestinationId is {Length>0} dest && IPAddress.TryParse(dest, out ip))
     *     SendAsync(data, (ip, DataPort));
     * else { EnableBroadcast; SendAsync(data, (Broadcast, DataPort)); } */
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)self;
    if (!payload) return -1;
    if (!t->adapter.send) return -1;
    const char *dest = payload->destination_id;
    if (dest && dest[0] != '\0' && ca_wifi_is_ip_address(dest)) {
        return t->adapter.send(t->adapter.self, payload->data,
                               payload->data_len, dest, CA_WIFI_DATA_PORT);
    }
    /* broadcast: dest_ip NULL */
    return t->adapter.send(t->adapter.self, payload->data, payload->data_len,
                           NULL, CA_WIFI_DATA_PORT);
}
static bool wifi_receive_next(void *self, ca_network_payload_t **out) {
    ca_wifi_transport_t *t = (ca_wifi_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_wifi_transport_as_transport(ca_wifi_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = wifi_kind;
    v.is_available = wifi_available;
    v.start = wifi_start;
    v.stop = wifi_stop;
    v.send = wifi_send;
    v.receive_next = wifi_receive_next;
    return v;
}

size_t ca_wifi_transport_pending(const ca_wifi_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}

/* ===========================================================================
 * WiFiPeerDiscovery — IPeerDiscovery over UDP broadcast beacons.
 * =========================================================================== */

struct ca_wifi_discovery {
    ca_peer_info_t **queue;   /* owned array of owned PeerInfo (FIFO) */
    size_t           head, count, cap;
    char            *last_announced; /* owned, may be NULL */
};

ca_wifi_discovery_t *ca_wifi_discovery_create(void) {
    return (ca_wifi_discovery_t *)calloc(1, sizeof(ca_wifi_discovery_t));
}

void ca_wifi_discovery_destroy(ca_wifi_discovery_t *d) {
    if (!d) return;
    for (size_t i = d->head; i < d->count; ++i)
        ca_peer_info_destroy(d->queue[i]);
    free(d->queue);
    free(d->last_announced);
    free(d);
}

static bool disc_push(ca_wifi_discovery_t *d, ca_peer_info_t *owned) {
    if (d->count == d->cap) {
        if (d->head > 0) {
            size_t live = d->count - d->head;
            memmove(d->queue, d->queue + d->head, live * sizeof(*d->queue));
            d->count = live;
            d->head = 0;
        }
        if (d->count == d->cap) {
            size_t nc = d->cap ? d->cap * 2 : 4;
            ca_peer_info_t **nq =
                (ca_peer_info_t **)realloc(d->queue, nc * sizeof(*nq));
            if (!nq) return false;
            d->queue = nq;
            d->cap = nc;
        }
    }
    d->queue[d->count++] = owned;
    return true;
}

int ca_wifi_discovery_deliver(ca_wifi_discovery_t *d, const uint8_t *data,
                              size_t len, const char *remote_address,
                              int64_t seen_unix_ms) {
    if (!d) return -1;
    /* Encoding.UTF8.GetString(buffer); msg.StartsWith(BeaconMagic, Ordinal) */
    size_t magic_len = strlen(CA_WIFI_BEACON_MAGIC);
    if (len < magic_len) return 0; /* too short to carry the magic — ignore */
    if (memcmp(data, CA_WIFI_BEACON_MAGIC, magic_len) != 0) return 0; /* not a beacon */

    /* nodeId = msg[BeaconMagic.Length..] */
    size_t id_len = len - magic_len;
    char *node_id = (char *)malloc(id_len + 1);
    if (!node_id) return -1;
    if (id_len) memcpy(node_id, data + magic_len, id_len);
    node_id[id_len] = '\0';

    /* DisplayName = $"WiFi/{result.RemoteEndPoint.Address}" */
    const char *addr = remote_address ? remote_address : "";
    size_t dn_len = strlen("WiFi/") + strlen(addr) + 1;
    char *display = (char *)malloc(dn_len);
    if (!display) { free(node_id); return -1; }
    snprintf(display, dn_len, "WiFi/%s", addr);

    ca_transport_kind_t supported[] = { CA_TRANSPORT_WIFI };
    ca_peer_info_t *peer = ca_peer_info_new(
        node_id, display, supported, 1, CA_PEER_ROLE_PEER,
        /*has_signal*/ false, 0, seen_unix_ms);
    free(node_id);
    free(display);
    if (!peer) return -1;
    if (!disc_push(d, peer)) {
        ca_peer_info_destroy(peer);
        return -1;
    }
    return 0;
}

bool ca_wifi_discovery_discover_next(ca_wifi_discovery_t *d,
                                     ca_peer_info_t **out) {
    if (!d || !out) return false;
    if (d->head >= d->count) return false;
    ca_peer_info_t *p = d->queue[d->head];
    d->queue[d->head++] = NULL;
    if (d->head == d->count) { d->head = 0; d->count = 0; }
    *out = p;
    return true;
}

int ca_wifi_discovery_announce(ca_wifi_discovery_t *d,
                               const ca_peer_info_t *local_info) {
    if (!d || !local_info) return -1;
    /* beacon = $"{BeaconMagic}{localInfo.NodeId}" */
    const char *node_id = local_info->node_id ? local_info->node_id : "";
    size_t need = strlen(CA_WIFI_BEACON_MAGIC) + strlen(node_id) + 1;
    char *beacon = (char *)malloc(need);
    if (!beacon) return -1;
    snprintf(beacon, need, "%s%s", CA_WIFI_BEACON_MAGIC, node_id);
    free(d->last_announced);
    d->last_announced = beacon;
    return 0;
}

const char *ca_wifi_discovery_last_announced(const ca_wifi_discovery_t *d) {
    return d ? d->last_announced : NULL;
}
