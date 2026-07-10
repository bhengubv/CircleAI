#ifndef CIRCLE_AI_NET_WIFI_H
#define CIRCLE_AI_NET_WIFI_H

/*
 * net_wifi.h — CircleAI.Networking.WiFi (C11 port).
 *
 * The LAN UDP broadcast/unicast network transport + peer discovery. Ports
 * CircleAI.Networking.WiFi 1:1:
 *
 *   Transport : WiFiNetworkTransport — INetworkTransport using LAN UDP.
 *               Constants DiscoveryPort=47890, DataPort=47891. Kind==WiFi,
 *               IsAvailable == receiver started. StartAsync opens sender +
 *               receiver (broadcast) and begins the receive pump. SendAsync:
 *               when DestinationId parses as an IP, unicast the payload bytes to
 *               (ip, DataPort); else broadcast to (255.255.255.255, DataPort).
 *               StopAsync closes both sockets then completes the inbound channel.
 *               ReceiveAsync drains the UNBOUNDED inbound FIFO the UDP seam feeds
 *               (each received datagram becomes NetworkPayload.Create(bytes)).
 *               The real UdpClient is injected behind IUdpSocketAdapter.
 *   Discovery : WiFiPeerDiscovery — IPeerDiscovery over UDP broadcast beacons.
 *               Beacon magic "CIRCLEAI:BEACON:". DiscoverAsync yields a PeerInfo
 *               for each received datagram whose text starts with the magic
 *               (NodeId = the suffix; DisplayName = "WiFi/{remoteAddress}";
 *               SupportedTransports=[WiFi]; Role=Peer; SignalStrength=null;
 *               LastSeen supplied). AnnounceAsync broadcasts "{magic}{NodeId}" on
 *               DiscoveryPort — retained for inspection here.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Timestamps are Unix ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t,
                             ca_peer_info_t */

#ifdef __cplusplus
extern "C" {
#endif

enum {
    CA_WIFI_DISCOVERY_PORT = 47890,
    CA_WIFI_DATA_PORT      = 47891
};

/* ===========================================================================
 * IUdpSocketAdapter — the injected UdpClient seam (vtable).
 *
 *   is_open()                       : whether the receiver socket is open.
 *   start(feed)                     : open sender + receiver, begin pumping; the
 *                                     transport hands the adapter a feed handle
 *                                     the adapter uses to push received datagram
 *                                     bytes upward. 0/-1.
 *   send(data, len, dest_ip, port)  : SendAsync — dest_ip NULL means broadcast
 *                                     to IPAddress.Broadcast; otherwise unicast
 *                                     to (dest_ip, port). 0/-1.
 *   stop()                          : close both sockets. 0/-1.
 * The adapter pushes a received datagram with ca_udp_feed(feed, ...).
 * =========================================================================== */

typedef struct ca_udp_feed ca_udp_feed_t;
/* Push a received datagram (bytes) into the transport's inbound channel.
 * Deep-copied. Returns 0 on success, -1 on OOM / closed / NULL. */
int ca_udp_feed(ca_udp_feed_t *feed, const uint8_t *data, size_t len);

typedef struct {
    void *self;
    bool (*is_open)(void *self);
    int  (*start)(void *self, ca_udp_feed_t *feed);
    int  (*send)(void *self, const uint8_t *data, size_t len,
                 const char *dest_ip, int port);
    int  (*stop)(void *self);
} ca_udp_socket_adapter_t;

/* ===========================================================================
 * Deterministic in-memory IUdpSocketAdapter (loopback).
 *
 * start() opens the receiver and records the feed handle. send() records the
 * last (dest_ip, port, bytes) AND (when looping) feeds the bytes straight back.
 * stop() closes. ca_mem_udp_adapter_deliver lets a host push a datagram upward.
 * =========================================================================== */

typedef struct ca_mem_udp_adapter ca_mem_udp_adapter_t;

ca_mem_udp_adapter_t *ca_mem_udp_adapter_create(bool loopback);
void ca_mem_udp_adapter_destroy(ca_mem_udp_adapter_t *a);
ca_udp_socket_adapter_t ca_mem_udp_adapter_as_adapter(ca_mem_udp_adapter_t *a);
/* Feed a received datagram upward (only while open). Returns 0/-1. */
int ca_mem_udp_adapter_deliver(ca_mem_udp_adapter_t *a, const uint8_t *data,
                               size_t len);
/* Number of send() calls issued so far. */
size_t ca_mem_udp_adapter_send_count(const ca_mem_udp_adapter_t *a);
/* Whether the last send() was a broadcast (dest_ip == NULL). */
bool ca_mem_udp_adapter_last_was_broadcast(const ca_mem_udp_adapter_t *a);
/* The dest IP of the last send() (borrowed) or NULL (broadcast / none). */
const char *ca_mem_udp_adapter_last_dest_ip(const ca_mem_udp_adapter_t *a);
/* The dest port of the last send(). */
int ca_mem_udp_adapter_last_port(const ca_mem_udp_adapter_t *a);

/* ===========================================================================
 * WiFiNetworkTransport
 * =========================================================================== */

typedef struct ca_wifi_transport ca_wifi_transport_t;

ca_wifi_transport_t *ca_wifi_transport_create(ca_udp_socket_adapter_t adapter);
void ca_wifi_transport_destroy(ca_wifi_transport_t *t);
ca_network_transport_t ca_wifi_transport_as_transport(ca_wifi_transport_t *t);
/* Number of inbound payloads currently queued (undrained). */
size_t ca_wifi_transport_pending(const ca_wifi_transport_t *t);

/* True iff `s` parses as an IPv4/IPv6 literal (IPAddress.TryParse analogue).
 * Exposed for testing the unicast-vs-broadcast routing decision. */
bool ca_wifi_is_ip_address(const char *s);

/* ===========================================================================
 * WiFiPeerDiscovery — IPeerDiscovery over UDP broadcast beacons.
 *
 * DiscoverAsync is a drainable cursor: a host delivers received beacon datagrams
 * with ca_wifi_discovery_deliver(magic-prefixed bytes + remote address + seen
 * timestamp); each valid beacon yields a PeerInfo drained by discover_next.
 * AnnounceAsync broadcasts "{magic}{NodeId}" — retained for inspection.
 * =========================================================================== */

/* The beacon magic prefix ("CIRCLEAI:BEACON:"). */
extern const char *const CA_WIFI_BEACON_MAGIC;

typedef struct ca_wifi_discovery ca_wifi_discovery_t;

ca_wifi_discovery_t *ca_wifi_discovery_create(void);
void ca_wifi_discovery_destroy(ca_wifi_discovery_t *d);

/* Deliver a received datagram (`data`/`len`) from `remote_address`, seen at
 * `seen_unix_ms`. If the datagram text starts with the beacon magic, a PeerInfo
 * is enqueued (NodeId = suffix after the magic, DisplayName = "WiFi/{address}",
 * SupportedTransports=[WiFi], Role=Peer, SignalStrength=null, LastSeen=seen).
 * Non-beacon datagrams are ignored. Returns 0 on success (whether or not a
 * beacon matched), -1 on OOM / NULL. */
int ca_wifi_discovery_deliver(ca_wifi_discovery_t *d, const uint8_t *data,
                              size_t len, const char *remote_address,
                              int64_t seen_unix_ms);
/* DiscoverAsync — drain one discovered peer into *out (freshly owned). Returns
 * true if produced, false when currently drained. */
bool ca_wifi_discovery_discover_next(ca_wifi_discovery_t *d,
                                     ca_peer_info_t **out);

/* AnnounceAsync(localInfo) — broadcasts "{magic}{NodeId}"; retains the beacon
 * bytes for inspection. Returns 0/-1. */
int ca_wifi_discovery_announce(ca_wifi_discovery_t *d,
                               const ca_peer_info_t *local_info);
/* The most recently announced beacon text (borrowed) or NULL if never
 * announced. */
const char *ca_wifi_discovery_last_announced(const ca_wifi_discovery_t *d);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_WIFI_H */
