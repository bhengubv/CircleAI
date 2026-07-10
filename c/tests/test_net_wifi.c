/*
 * test_net_wifi.c — CircleAI.Networking.WiFi (net_wifi.h).
 *
 * Verifies:
 *   Helper    : ca_wifi_is_ip_address (IPv4 dotted-quad, IPv6 colon, rejects)
 *   Transport : Kind==WiFi, IsAvailable == started, StartAsync opens, SendAsync
 *               unicasts to (ip, DataPort) when the destination is an IP else
 *               broadcasts to DataPort, loopback -> inbound, ReceiveAsync drains,
 *               StopAsync closes + completes
 *   Discovery : deliver a beacon datagram -> PeerInfo (NodeId suffix, DisplayName
 *               "WiFi/{addr}", SupportedTransports=[WiFi], Role=Peer, no signal),
 *               non-beacon ignored, drain order FIFO, AnnounceAsync beacon text
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static void test_is_ip(void) {
    assert(ca_wifi_is_ip_address("192.168.1.10"));
    assert(ca_wifi_is_ip_address("10.0.0.1"));
    assert(ca_wifi_is_ip_address("255.255.255.255"));
    assert(ca_wifi_is_ip_address("::1"));           /* IPv6 */
    assert(ca_wifi_is_ip_address("fe80::1"));
    assert(!ca_wifi_is_ip_address("device-name"));  /* not an IP */
    assert(!ca_wifi_is_ip_address("256.1.1.1"));    /* octet > 255 */
    assert(!ca_wifi_is_ip_address("1.2.3"));        /* too few octets */
    assert(!ca_wifi_is_ip_address("1.2.3.4.5"));    /* too many */
    assert(!ca_wifi_is_ip_address(""));
    assert(!ca_wifi_is_ip_address(NULL));
}

static void test_transport(void) {
    ca_mem_udp_adapter_t *ad = ca_mem_udp_adapter_create(/*loopback*/ true);
    assert(ad);
    ca_wifi_transport_t *t =
        ca_wifi_transport_create(ca_mem_udp_adapter_as_adapter(ad));
    assert(t);
    ca_network_transport_t nt = ca_wifi_transport_as_transport(t);

    assert(nt.kind(nt.self) == CA_TRANSPORT_WIFI);
    assert(nt.is_available(nt.self) == false); /* not started */

    assert(nt.start(nt.self) == 0);
    assert(nt.is_available(nt.self) == true);

    /* Unicast: destination is an IP -> (ip, DataPort). */
    const uint8_t body[] = { 5, 6, 7 };
    ca_network_payload_t *uni = ca_network_payload_create(
        body, 3, "192.168.0.42", CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0,
        NULL);
    assert(uni);
    assert(nt.send(nt.self, uni) == 0);
    assert(ca_mem_udp_adapter_last_was_broadcast(ad) == false);
    assert(strcmp(ca_mem_udp_adapter_last_dest_ip(ad), "192.168.0.42") == 0);
    assert(ca_mem_udp_adapter_last_port(ad) == CA_WIFI_DATA_PORT);
    ca_network_payload_destroy(uni);

    /* loopback fed the unicast bytes back as an inbound payload. */
    assert(ca_wifi_transport_pending(t) == 1);
    ca_network_payload_t *out = NULL;
    assert(nt.receive_next(nt.self, &out) && out->data_len == 3 &&
           out->data[0] == 5);
    ca_network_payload_destroy(out);

    /* Broadcast: destination is not an IP (or empty) -> broadcast, DataPort. */
    ca_network_payload_t *bc = ca_network_payload_create(
        body, 3, "not-an-ip", CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(bc);
    assert(nt.send(nt.self, bc) == 0);
    assert(ca_mem_udp_adapter_last_was_broadcast(ad) == true);
    assert(ca_mem_udp_adapter_last_dest_ip(ad) == NULL);
    assert(ca_mem_udp_adapter_last_port(ad) == CA_WIFI_DATA_PORT);
    ca_network_payload_destroy(bc);

    /* No destination -> broadcast too. */
    ca_network_payload_t *nod = ca_network_payload_create(
        body, 3, NULL, CA_MSG_PRIORITY_NORMAL, NULL, false, 0, T0, NULL);
    assert(nod);
    assert(nt.send(nt.self, nod) == 0);
    assert(ca_mem_udp_adapter_last_was_broadcast(ad) == true);
    ca_network_payload_destroy(nod);

    assert(ca_mem_udp_adapter_send_count(ad) == 3);

    /* Drain everything looped, then stop. */
    while (nt.receive_next(nt.self, &out)) ca_network_payload_destroy(out);

    assert(nt.stop(nt.self) == 0);
    assert(nt.is_available(nt.self) == false);
    assert(ca_mem_udp_adapter_deliver(ad, body, 3) == -1); /* closed */

    ca_wifi_transport_destroy(t);
    ca_mem_udp_adapter_destroy(ad);
}

static void test_discovery(void) {
    ca_wifi_discovery_t *d = ca_wifi_discovery_create();
    assert(d);

    /* Nothing delivered -> drained. */
    ca_peer_info_t *out = NULL;
    assert(ca_wifi_discovery_discover_next(d, &out) == false);

    /* A valid beacon "CIRCLEAI:BEACON:node-abc" from 10.0.0.9. */
    const char *b1 = "CIRCLEAI:BEACON:node-abc";
    assert(ca_wifi_discovery_deliver(d, (const uint8_t *)b1, strlen(b1),
                                     "10.0.0.9", T0) == 0);
    /* A non-beacon datagram is ignored. */
    const char *junk = "HELLO";
    assert(ca_wifi_discovery_deliver(d, (const uint8_t *)junk, strlen(junk),
                                     "10.0.0.10", T0 + 1) == 0);
    /* A second beacon. */
    const char *b2 = "CIRCLEAI:BEACON:peer-2";
    assert(ca_wifi_discovery_deliver(d, (const uint8_t *)b2, strlen(b2),
                                     "10.0.0.11", T0 + 2) == 0);

    /* Drain FIFO: first beacon. */
    assert(ca_wifi_discovery_discover_next(d, &out) && out);
    assert(strcmp(out->node_id, "node-abc") == 0);
    assert(strcmp(out->display_name, "WiFi/10.0.0.9") == 0);
    assert(out->supported_count == 1 &&
           out->supported_transports[0] == CA_TRANSPORT_WIFI);
    assert(out->role == CA_PEER_ROLE_PEER);
    assert(out->has_signal_strength == false);
    assert(out->last_seen_ms == T0);
    ca_peer_info_destroy(out);

    /* Second beacon (junk skipped). */
    assert(ca_wifi_discovery_discover_next(d, &out) && out);
    assert(strcmp(out->node_id, "peer-2") == 0);
    assert(strcmp(out->display_name, "WiFi/10.0.0.11") == 0);
    assert(out->last_seen_ms == T0 + 2);
    ca_peer_info_destroy(out);

    assert(ca_wifi_discovery_discover_next(d, &out) == false); /* drained */

    /* AnnounceAsync builds "{magic}{NodeId}". */
    assert(ca_wifi_discovery_last_announced(d) == NULL);
    ca_transport_kind_t sup[] = { CA_TRANSPORT_WIFI };
    ca_peer_info_t *me =
        ca_peer_info_new("me-node", NULL, sup, 1, CA_PEER_ROLE_PEER, false, 0,
                         T0);
    assert(me);
    assert(ca_wifi_discovery_announce(d, me) == 0);
    assert(strcmp(ca_wifi_discovery_last_announced(d),
                  "CIRCLEAI:BEACON:me-node") == 0);
    ca_peer_info_destroy(me);

    ca_wifi_discovery_destroy(d);
}

int main(void) {
    test_is_ip();
    test_transport();
    test_discovery();
    return 0;
}
